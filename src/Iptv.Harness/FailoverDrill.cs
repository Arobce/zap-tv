using System.Diagnostics;
using Iptv.Core.Data;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Drives a real failover with real mpv and no provider.
/// </summary>
/// <remarks>
/// <para>
/// The PRD's exit criterion is recovery within 10s after a stream is killed mid-playback.
/// Proving that against the reference account means deliberately breaking a stream on a
/// single-connection subscription, which is not something to do casually. This does the
/// same thing offline: a scratch database with two candidates for one channel, the first
/// pointing at a port nothing is listening on and the second at mpv's built-in generator.
/// </para>
/// <para>
/// It exercises the parts that are easy to get wrong and impossible to unit test together
/// - detection, the connection lease across a retry, and the health rows - against the
/// real player rather than a stub of it.
/// </para>
/// </remarks>
internal static class FailoverDrill
{
    /// <summary>Nothing listens here, so mpv fails to connect rather than hanging.</summary>
    private const string DeadUrl = "http://127.0.0.1:1/live/dead.ts";

    private const string WorkingUrl = "av://lavfi:testsrc=size=640x360:rate=30";

    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (!MpvLibrary.IsAvailable())
        {
            Console.Error.WriteLine("libmpv-2.dll not found; cannot run the drill.");
            return 1;
        }

        var path = Path.Combine(Path.GetTempPath(), $"iptv-drill-{Guid.NewGuid():N}.db");

        try
        {
            await using var connection = await new SqliteConnectionFactory(path)
                .OpenAsync(cancellationToken).ConfigureAwait(false);

            await SeedAsync(connection, cancellationToken).ConfigureAwait(false);

            var session = await FailoverSession.StartAsync(
                connection, "tvg:drill", StreamKind.Live, DateTimeOffset.UtcNow, QualityPreference.Highest,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine("== plan ==");
            Console.WriteLine($"  candidates {session.Remaining + 1}, refused by guard {session.Excluded.Count}");
            Console.WriteLine();

            var recovered = await DriveAsync(connection, session, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("== stream_health ==");
            await PrintHealthAsync(connection, cancellationToken).ConfigureAwait(false);

            return recovered ? 0 : 5;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    private static async Task SeedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // Same title on both, and no country prefix on either, so the safety guard has no
        // reason to refuse the fallback. A drill that the guard silently blocked would
        // report "no recovery" for the wrong reason.
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url, priority) VALUES
              (1, 'Dead',    'm3u', 'http://127.0.0.1:1', 0),
              (2, 'Working', 'm3u', 'http://127.0.0.1:1', 1);

            INSERT INTO channels (channel_key, display_name) VALUES ('tvg:drill', 'Drill');

            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, quality, is_active, is_separator, last_seen_utc)
            VALUES
              (1, 'dead',    'live', 'Drill', 'drill', @dead,    'tvg:drill', 'Hd', 1, 0, 0),
              (2, 'working', 'live', 'Drill', 'drill', @working, 'tvg:drill', 'Hd', 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@dead", DeadUrl);
        command.Parameters.AddWithValue("@working", WorkingUrl);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the attempt loop on an STA thread, as mpv's GL path requires.</summary>
    private static Task<bool> DriveAsync(
        SqliteConnection connection,
        FailoverSession session,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Drive(connection, session, cancellationToken));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"drill failed: {exception.Message}");
                completion.SetResult(false);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool Drive(
        SqliteConnection connection,
        FailoverSession session,
        CancellationToken cancellationToken)
    {
        using var glContext = WglContext.Create();
        glContext.MakeCurrent();

        using var handle = MpvHandle.Create(new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["idle"] = "yes",
            ["keep-open"] = "yes",
            ["audio"] = "no",
            ["hwdec"] = "auto-safe",
            ["profile"] = "low-latency",
            ["cache"] = "yes",
            ["cache-pause-initial"] = "no",

            // Reconnect is deliberately off here. The app enables it so a brief provider
            // hiccup does not become a channel change; in the drill it would keep retrying
            // a port nothing is listening on and mask the failure the drill is measuring.
            ["demuxer-max-bytes"] = "32MiB",
            ["deinterlace"] = "auto",
        });

        using var renderer = MpvOpenGlRenderer.Create(handle);
        using var target = SharedVideoTarget.Create(640, 360);
        using var events = new MpvEventLoop(handle);
        events.Start();

        // The same limiter the app uses, with the same floor, so the drill exercises the
        // interaction that motivated AcquireAsync: a stream failing inside the interval.
        var limiter = new ProviderConnectionLimiter(1, TimeSpan.FromSeconds(2));
        ConnectionLease? lease = null;

        var overall = Stopwatch.StartNew();

        try
        {
            while (session.Current is { } candidate)
            {
                lease?.Dispose();
                lease = limiter.AcquireAsync(cancellationToken).GetAwaiter().GetResult();

                Console.WriteLine($"  attempt {session.AttemptNumber}: {candidate.ProviderName}");

                var attempt = Stopwatch.StartNew();
                handle.Command("loadfile", candidate.Url);

                var outcome = Watch(events, renderer, target, attempt, out var firstFrameMs);

                if (outcome == PlaybackOutcome.Ok)
                {
                    Console.WriteLine($"    playing after {firstFrameMs}ms");

                    Report(connection, session, PlaybackOutcome.Ok, firstFrameMs, "drill", cancellationToken);

                    Console.WriteLine();
                    Console.WriteLine($"  recovered in {overall.ElapsedMilliseconds}ms " +
                                      $"({(overall.Elapsed < TimeSpan.FromSeconds(10) ? "within" : "OVER")} the 10s budget)");

                    return session.HasFailedOver;
                }

                Console.WriteLine($"    {outcome} after {attempt.ElapsedMilliseconds}ms");
                Report(connection, session, outcome, null, "drill", cancellationToken);
            }

            Console.WriteLine("  every candidate failed");
            return false;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Waits for a frame, a hard error, or the 4s first-frame deadline.</summary>
    private static PlaybackOutcome Watch(
        MpvEventLoop events,
        MpvOpenGlRenderer renderer,
        SharedVideoTarget target,
        Stopwatch attempt,
        out int? firstFrameMs)
    {
        firstFrameMs = null;

        while (attempt.Elapsed < TimeSpan.FromSeconds(4))
        {
            while (events.Events.TryRead(out var evt))
            {
                if (evt is MpvEndFile { Reason: 4 } end)
                {
                    Console.WriteLine($"    end-file error {end.Error}");
                    return PlaybackOutcome.HttpError;
                }
            }

            if (renderer.HasFrameReady())
            {
                target.RenderFrame(renderer);
                firstFrameMs = (int)attempt.ElapsedMilliseconds;
                return PlaybackOutcome.Ok;
            }

            Thread.Sleep(2);
        }

        return PlaybackOutcome.Timeout;
    }

    private static void Report(
        SqliteConnection connection,
        FailoverSession session,
        PlaybackOutcome outcome,
        int? firstFrameMs,
        string detail,
        CancellationToken cancellationToken)
        => session.ReportAsync(connection, outcome, DateTimeOffset.UtcNow, cancellationToken, firstFrameMs, detail)
            .GetAwaiter().GetResult();

    private static async Task PrintHealthAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pr.name, h.outcome, h.ttfb_ms
            FROM stream_health h
            JOIN streams s ON s.id = h.stream_id
            JOIN providers pr ON pr.id = s.provider_id
            ORDER BY h.rowid;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ttfb = reader.IsDBNull(2) ? "-" : $"{reader.GetInt32(2)}ms";
            Console.WriteLine($"  {reader.GetString(0),-10} {reader.GetString(1),-12} {ttfb}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A scratch file in the temp directory. Leaving it is not worth failing over.
        }
    }
}
