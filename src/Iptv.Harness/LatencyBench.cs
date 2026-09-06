using System.Diagnostics;
using Iptv.Core.Sources;
using Iptv.Mpv;

namespace Iptv.Harness;

/// <summary>One named set of mpv options to measure.</summary>
/// <param name="Name">Short label for the results table.</param>
/// <param name="Rationale">Why this profile might be faster than the baseline.</param>
/// <param name="Options">Options applied before <c>mpv_initialize</c>.</param>
internal sealed record LatencyProfile(
    string Name,
    string Rationale,
    Dictionary<string, string> Options);

/// <summary>
/// Measures time-to-first-frame across mpv option profiles against a real stream.
/// </summary>
/// <remarks>
/// <para>
/// Phase 7's headline target is a p50 channel change under 800ms. Measured against the
/// reference provider the baseline is 1.2-2.3s, and because that account permits a single
/// connection the dual-handle prebuffering the phase was designed around cannot be used at
/// all. Every millisecond has to come from the cold path, so the cold path gets measured
/// rather than tuned by intuition.
/// </para>
/// <para>
/// Trials run strictly one at a time with a pause between them. Exceeding the account's
/// connection limit gets it temporarily blocked, which a user would blame on this
/// application.
/// </para>
/// </remarks>
internal static class LatencyBench
{
    /// <summary>
    /// The PRD's live profile, unchanged. Everything else is measured against this.
    /// </summary>
    private static Dictionary<string, string> Baseline() => new()
    {
        ["vo"] = "libmpv",
        ["idle"] = "yes",
        ["keep-open"] = "yes",
        ["audio"] = "no",
        ["hwdec"] = "auto-safe",
        ["profile"] = "low-latency",
        ["cache"] = "yes",
        ["demuxer-lavf-o"] = "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2",
        ["demuxer-max-bytes"] = "32MiB",
        ["demuxer-readahead-secs"] = "2",
        ["deinterlace"] = "auto",
    };

    private static Dictionary<string, string> With(
        Action<Dictionary<string, string>> mutate)
    {
        var options = Baseline();
        mutate(options);
        return options;
    }

    /// <summary>The profiles to compare, in the order they are reported.</summary>
    internal static IReadOnlyList<LatencyProfile> Profiles =>
    [
        new("baseline",
            "the PRD live profile as written",
            Baseline()),

        new("probe-1s",
            "FFmpeg probes 5MB / 5s by default before reporting a stream; a TS mux needs far less",
            With(o => o["demuxer-lavf-o"] =
                "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2," +
                "probesize=1000000,analyzeduration=1000000")),

        new("probe-250ms",
            "aggressive probe: enough for one keyframe on a typical TS",
            With(o => o["demuxer-lavf-o"] =
                "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2," +
                "probesize=250000,analyzeduration=250000")),

        new("no-initial-pause",
            "mpv waits for the cache to fill before starting; live TV does not need that",
            With(o => o["cache-pause-initial"] = "no")),

        new("combined",
            "aggressive probe, no initial cache pause, minimal readahead",
            With(o =>
            {
                o["demuxer-lavf-o"] =
                    "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2," +
                    "probesize=250000,analyzeduration=250000";
                o["cache-pause-initial"] = "no";
                o["demuxer-readahead-secs"] = "0.2";
                o["cache-secs"] = "1";
            })),

        new("combined+lowlatency",
            "as combined, plus dropping the untimed-frame wait and forcing fast decode",
            With(o =>
            {
                o["demuxer-lavf-o"] =
                    "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2," +
                    "probesize=250000,analyzeduration=250000";
                o["cache-pause-initial"] = "no";
                o["demuxer-readahead-secs"] = "0.2";
                o["cache-secs"] = "1";
                o["vd-lavc-threads"] = "0";
                o["video-latency-hacks"] = "yes";
            })),
    ];

    /// <summary>
    /// Guards the provider against this benchmark.
    /// </summary>
    /// <remarks>
    /// An earlier version of this benchmark opened 30 streams in quick succession against
    /// an account permitting one, and the provider blocked it for hours. The limiter makes
    /// that impossible rather than relying on whoever runs it to remember. Five seconds
    /// between opens is deliberately conservative: the measurement is of the open itself,
    /// so waiting longer costs only wall-clock time.
    /// </remarks>
    private static readonly ProviderConnectionLimiter Limiter =
        new(maxConnections: 1, minimumInterval: TimeSpan.FromSeconds(5));

    /// <summary>Runs one trial and returns time to first rendered frame, or null on failure.</summary>
    /// <remarks>
    /// Measures to the first frame this application can actually present, not to
    /// <c>FILE_LOADED</c>. A channel change ends, for the user, when a picture appears.
    /// </remarks>
    internal static Timing? Measure(LatencyProfile profile, string url, TimeSpan timeout)
    {
        // Blocks rather than failing: a refused trial would silently skew the median, and
        // the benchmark is not in a hurry.
        ConnectionLease? lease = null;
        var waited = Stopwatch.StartNew();
        while (!Limiter.TryAcquire(out lease))
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(1))
            {
                throw new InvalidOperationException(
                    "Could not acquire a provider connection slot within a minute.");
            }

            Thread.Sleep(250);
        }

        using var connection = lease;
        using var handle = MpvHandle.Create(profile.Options);
        using var renderer = MpvOpenGlRenderer.Create(handle);
        using var target = SharedVideoTarget.Create(1280, 720);
        using var events = new MpvEventLoop(handle);
        events.Start();

        var stopwatch = Stopwatch.StartNew();
        handle.Command("loadfile", url);

        long? loaded = null;

        while (stopwatch.Elapsed < timeout)
        {
            while (events.Events.TryRead(out var evt))
            {
                switch (evt)
                {
                    // An unopenable stream must not be reported as a slow one; it would
                    // drag the profile's median toward the timeout and look like a tuning
                    // result.
                    case MpvEndFile { Reason: 4 }:
                        return null;

                    // Everything before this is connect, probe and demux - the provider's
                    // latency plus FFmpeg's analysis. Everything after is decode and
                    // present, which is ours. Only the second half is tunable from here,
                    // so the split decides whether more tuning is worth attempting.
                    case MpvSimpleEvent { Kind: MpvEventKind.FileLoaded }:
                        loaded ??= stopwatch.ElapsedMilliseconds;
                        break;

                    default:
                        break;
                }
            }

            if (renderer.HasFrameReady())
            {
                target.RenderFrame(renderer);
                stopwatch.Stop();
                return new Timing(stopwatch.ElapsedMilliseconds, loaded);
            }

            Thread.Sleep(1);
        }

        return null;
    }

    /// <summary>One trial's timings.</summary>
    /// <param name="FirstFrameMs">Load command to first presentable frame.</param>
    /// <param name="FileLoadedMs">
    /// Load command to <c>FILE_LOADED</c>: connect, probe and demux. Null when the event
    /// did not arrive before the first frame.
    /// </param>
    internal readonly record struct Timing(long FirstFrameMs, long? FileLoadedMs);
}
