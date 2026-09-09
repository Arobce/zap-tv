using System.Diagnostics;
using Iptv.Core.Data;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Cuts a real stream mid-playback and measures the recovery.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 8 exit criterion: "killing a stream mid-playback (block the host in the
/// firewall) results in automatic recovery on another provider within 10s". The existing
/// <c>drill</c> proves the recovery half offline, but it starts from a URL that was never
/// alive, so it never exercises the failure this criterion is actually about — a stream
/// that is playing and then stops.
/// </para>
/// <para>
/// The two failures are not the same. A dead URL fails at connect: mpv reports it quickly
/// and as an error. A stream cut while bytes are flowing produces no error at all — the
/// socket simply stops delivering, mpv keeps its demuxer open waiting for more, and the
/// only evidence is that the frame count stopped moving. That is why the stall watcher
/// counts presented frames rather than asking mpv how it is doing, and until now nothing
/// had tested it against a real provider.
/// </para>
/// <para>
/// This still cannot prove the <em>cross-provider</em> half. That needs a second
/// subscription, and one account is configured. What it does prove is detection and
/// recovery within the 10s budget against a genuine mid-stream cut.
/// </para>
/// </remarks>
internal static class KillSwitchDrill
{
    /// <summary>How long to let the stream run before cutting it.</summary>
    /// <remarks>
    /// Long enough that playback is unambiguously established rather than still buffering,
    /// short enough not to hold a single-connection account open for no reason.
    /// </remarks>
    private static readonly TimeSpan PlayBeforeKill = TimeSpan.FromSeconds(6);

    internal static async Task<int> RunAsync(
        string title,
        string url,
        CancellationToken cancellationToken)
    {
        if (!MpvLibrary.IsAvailable())
        {
            Console.Error.WriteLine("libmpv-2.dll not found; cannot run the drill.");
            return 1;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var advertised))
        {
            Console.Error.WriteLine("The resolved stream is not an absolute URL.");
            return 1;
        }

        var (upstream, hops) = await ResolveRedirectsAsync(advertised, cancellationToken)
            .ConfigureAwait(false);

        // The origin, not the advertised host. This provider answers a stream request with
        // a 302 to a different machine, and mpv follows it — so a forwarder placed on the
        // advertised host carries the redirect and nothing else, and cutting it cuts a
        // connection the player stopped using seconds ago.
        url = upstream.ToString();

        using var killSwitch = TcpKillSwitch.Start(upstream.Host, upstream.Port);

        // The same URL with its authority pointed at the forwarder. Everything else - the
        // path, and therefore the credentials in it - is untouched, so the provider sees a
        // request identical to the one it would have received directly.
        var forwarded = new UriBuilder(upstream)
        {
            Host = "127.0.0.1",
            Port = killSwitch.Port,
        }.Uri.ToString();

        Console.WriteLine("== setup ==");
        Console.WriteLine($"  channel      {title}");
        Console.WriteLine($"  advertised   {advertised.Host}:{advertised.Port}");
        Console.WriteLine($"  origin       {upstream.Host}:{upstream.Port}" +
                          (hops > 0 ? $"  (after {hops} redirect(s))" : "  (no redirect)"));
        Console.WriteLine($"  through      127.0.0.1:{killSwitch.Port}");
        Console.WriteLine($"  cut after    {PlayBeforeKill.TotalSeconds:F0}s of playback");
        Console.WriteLine();

        var path = Path.Combine(Path.GetTempPath(), $"iptv-kill-{Guid.NewGuid():N}.db");

        try
        {
            await using var connection = await new SqliteConnectionFactory(path)
                .OpenAsync(cancellationToken).ConfigureAwait(false);

            await SeedAsync(connection, forwarded, url, cancellationToken).ConfigureAwait(false);

            var session = await FailoverSession.StartAsync(
                connection, "tvg:kill", StreamKind.Live, DateTimeOffset.UtcNow,
                QualityPreference.Highest, cancellationToken).ConfigureAwait(false);

            var result = await DriveAsync(connection, session, killSwitch, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("== stream_health ==");
            await PrintHealthAsync(connection, cancellationToken).ConfigureAwait(false);

            return result ? 0 : 5;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    /// <summary>
    /// Follows the provider's redirects by hand to find where the stream really lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One short-lived request. It costs a connection on a single-connection account for
    /// the moment it takes to read the response head, which is why it asks for headers only
    /// and disposes at once rather than reading any of the stream.
    /// </para>
    /// <para>
    /// Done here rather than by rewriting the Location header inside the forwarder. Both
    /// work; this one does not depend on the response head arriving in a single TCP read,
    /// which is an assumption that holds until it does not.
    /// </para>
    /// </remarks>
    private static async Task<(Uri Origin, int Hops)> ResolveRedirectsAsync(
        Uri advertised,
        CancellationToken cancellationToken)
    {
        const int MaxHops = 5;

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ZapTV/0.1");

        var current = advertised;

        for (var hop = 0; hop < MaxHops; hop++)
        {
            try
            {
                using var response = await http
                    .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is < 300 or >= 400 ||
                    response.Headers.Location is not { } location)
                {
                    return (current, hop);
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
            }
            catch (Exception exception)
            {
                // Not fatal. The drill can still try the advertised host, and will say
                // plainly if nothing went through the forwarder.
                Console.WriteLine(
                    $"  redirect check failed: {CredentialScrubber.Scrub(exception.Message)}");

                return (current, hop);
            }
        }

        return (current, MaxHops);
    }

    /// <summary>
    /// Two candidates for one channel: the cuttable route, then the direct one.
    /// </summary>
    /// <remarks>
    /// Same title and no country prefix on either, so the failover safety guard has no
    /// reason to refuse the second. A drill the guard silently blocked would report "no
    /// recovery" for entirely the wrong reason.
    /// </remarks>
    private static async Task SeedAsync(
        SqliteConnection connection,
        string forwardedUrl,
        string directUrl,
        CancellationToken cancellationToken)
    {
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url, priority, max_connections) VALUES
              (1, 'Cuttable', 'xtream', 'http://127.0.0.1', 0, 1),
              (2, 'Direct',   'xtream', 'http://127.0.0.1', 1, 1);

            INSERT INTO channels (channel_key, display_name) VALUES ('tvg:kill', 'Kill drill');

            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, quality, is_active, is_separator, last_seen_utc)
            VALUES
              (1, 'cuttable', 'live', 'Kill drill', 'kill drill', @forwarded, 'tvg:kill', 'Hd', 1, 0, 0),
              (2, 'direct',   'live', 'Kill drill', 'kill drill', @direct,    'tvg:kill', 'Hd', 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@forwarded", forwardedUrl);
        command.Parameters.AddWithValue("@direct", directUrl);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<bool> DriveAsync(
        SqliteConnection connection,
        FailoverSession session,
        TcpKillSwitch killSwitch,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Drive(connection, session, killSwitch, cancellationToken));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"drill failed: {CredentialScrubber.Scrub(exception.Message)}");
                completion.SetResult(false);
            }
        });

        // A WGL context belongs to one thread, and mpv's render calls must run on it.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    private static bool Drive(
        SqliteConnection connection,
        FailoverSession session,
        TcpKillSwitch killSwitch,
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
            ["demuxer-max-bytes"] = "32MiB",
            ["demuxer-readahead-secs"] = "2",
            ["deinterlace"] = "auto",

            // The app's reconnect settings, kept rather than dropped. This drill is asking
            // what happens on a real cut with the real configuration, and mpv retrying for
            // two seconds first is part of that answer, not noise to be removed.
            ["demuxer-lavf-o"] = "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2",
        });

        using var renderer = MpvOpenGlRenderer.Create(handle);
        using var target = SharedVideoTarget.Create(1280, 720);
        using var events = new MpvEventLoop(handle);
        events.Start();

        var limiter = new ProviderConnectionLimiter(1, TimeSpan.FromSeconds(2));
        ConnectionLease? lease = null;

        var cut = Stopwatch.StartNew();
        var killed = false;

        try
        {
            while (session.Current is { } candidate)
            {
                lease?.Dispose();
                lease = limiter.AcquireAsync(cancellationToken).GetAwaiter().GetResult();

                Console.WriteLine($"== attempt {session.AttemptNumber}: {candidate.ProviderName} ==");

                var attempt = Stopwatch.StartNew();
                handle.Command("loadfile", candidate.Url);

                if (!killed)
                {
                    if (!PlayThenCut(events, renderer, target, attempt, killSwitch, out var frames))
                    {
                        Console.Error.WriteLine(
                            "  the stream never played, so there was nothing to cut. " +
                            "Try another channel: killswitch <search>");
                        return false;
                    }

                    var kb = killSwitch.BytesForwarded / 1024;

                    Console.WriteLine(
                        $"  upstream said: {killSwitch.FirstStatusLine ?? "nothing recorded"}");
                    Console.WriteLine($"  cut after {frames} frames and {kb:N0} KB forwarded");

                    // A stream that played without the forwarder carrying it was never
                    // going through the kill switch, so cutting it proves nothing. Said
                    // plainly rather than reported as a 40s detection.
                    if (kb < 64)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine(
                            "  Only " + kb + " KB went through the forwarder while " + frames +
                            " frames played, so the client is not using it - the provider " +
                            "redirected it straight to the origin. Nothing was cut.");
                        return false;
                    }

                    killed = true;
                    cut.Restart();

                    var detector = new StallDetector();
                    var stalled = WaitForStall(handle, renderer, target, detector, frames);

                    if (detector.Reason is null)
                    {
                        Console.Error.WriteLine(
                            $"  no stall detected in {stalled}ms. The stream outlived the cut, " +
                            "which means it was not flowing through the forwarder.");
                        return false;
                    }

                    Console.WriteLine($"  stall detected after {stalled}ms: {detector.Reason}");

                    Report(connection, session, PlaybackOutcome.Stall, null,
                           "killswitch: connection cut mid-stream", cancellationToken);

                    continue;
                }

                var outcome = Watch(events, renderer, target, attempt, out var firstFrameMs);

                if (outcome == PlaybackOutcome.Ok)
                {
                    Console.WriteLine($"  playing after {firstFrameMs}ms");
                    Report(connection, session, PlaybackOutcome.Ok, firstFrameMs,
                           "killswitch: recovered", cancellationToken);

                    var total = cut.ElapsedMilliseconds;
                    var within = cut.Elapsed < TimeSpan.FromSeconds(10);

                    Console.WriteLine();
                    Console.WriteLine(
                        $"  recovered {total}ms after the cut " +
                        $"({(within ? "within" : "OVER")} the 10s budget)");

                    return within;
                }

                Console.WriteLine($"  {outcome} after {attempt.ElapsedMilliseconds}ms");
                Report(connection, session, outcome, null, "killswitch", cancellationToken);
            }

            Console.WriteLine("  every candidate failed");
            return false;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Plays until established, then cuts the connection.</summary>
    /// <returns>False if the stream never produced a frame, so there was nothing to cut.</returns>
    private static bool PlayThenCut(
        MpvEventLoop events,
        MpvOpenGlRenderer renderer,
        SharedVideoTarget target,
        Stopwatch attempt,
        TcpKillSwitch killSwitch,
        out int frames)
    {
        frames = 0;

        var firstFrame = Stopwatch.StartNew();
        var playing = false;

        while (attempt.Elapsed < TimeSpan.FromSeconds(20))
        {
            while (events.Events.TryRead(out var evt))
            {
                if (evt is MpvEndFile { Reason: 4 } end)
                {
                    Console.WriteLine($"  end-file error {end.Error} before the cut");
                    return false;
                }
            }

            if (renderer.HasFrameReady())
            {
                target.RenderFrame(renderer);
                frames++;

                if (!playing)
                {
                    playing = true;
                    firstFrame.Restart();
                    Console.WriteLine($"  first frame after {attempt.ElapsedMilliseconds}ms");
                }
            }

            if (playing && firstFrame.Elapsed >= PlayBeforeKill)
            {
                killSwitch.Kill();
                return true;
            }

            Thread.Sleep(2);
        }

        return false;
    }

    /// <summary>
    /// Runs the app's own detector against the cut stream and reports when it fires.
    /// </summary>
    /// <remarks>
    /// The detector rather than a copy of its rules. The point of this drill is to measure
    /// what the app would do, and a drill with its own idea of what a stall is would keep
    /// passing after the app stopped agreeing with it.
    /// </remarks>
    private static long WaitForStall(
        MpvHandle handle,
        MpvOpenGlRenderer renderer,
        SharedVideoTarget target,
        StallDetector detector,
        int frames)
    {
        var sinceCut = Stopwatch.StartNew();
        var sinceSample = Stopwatch.StartNew();

        while (sinceCut.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (renderer.HasFrameReady())
            {
                target.RenderFrame(renderer);
                frames++;
            }

            // Once a second, matching the app's watch interval. Sampling faster would
            // measure something the app never sees.
            if (sinceSample.Elapsed >= TimeSpan.FromSeconds(1))
            {
                sinceSample.Restart();

                var cache = ReadCacheSeconds(handle);

                // Printed per sample. This drill exists to measure detection, and a run
                // that reports only the verdict cannot say which signal was available or
                // why one rule fired before the other.
                Console.WriteLine(
                    $"    t+{sinceCut.ElapsedMilliseconds / 1000,2}s  frames {frames,6}  " +
                    $"cache {(cache is { } c ? $"{c,6:F1}s" : "     -")}");

                var stalled = detector.Observe(new StallSample
                {
                    FramesPresented = frames,
                    CacheSeconds = cache,
                    At = DateTimeOffset.UtcNow,
                });

                if (stalled)
                {
                    return sinceCut.ElapsedMilliseconds;
                }
            }

            Thread.Sleep(2);
        }

        return sinceCut.ElapsedMilliseconds;
    }

    /// <summary>Seconds of demuxed data buffered ahead, or null if mpv does not say.</summary>
    private static double? ReadCacheSeconds(MpvHandle handle)
    {
        var raw = handle.GetProperty("demuxer-cache-duration");

        return double.TryParse(
            raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) && !double.IsNaN(value)
            ? value
            : null;
    }

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
                    Console.WriteLine($"  end-file error {end.Error}");
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
        => session.ReportAsync(
            connection, outcome, DateTimeOffset.UtcNow, cancellationToken, firstFrameMs, detail)
            .GetAwaiter().GetResult();

    private static async Task PrintHealthAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
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
            // A scratch file in the temp directory.
        }
    }
}
