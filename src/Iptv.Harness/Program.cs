using System.Diagnostics;
using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;

namespace Iptv.Harness;

/// <summary>
/// Console harness for timing ingest and running playback smoke tests.
/// </summary>
/// <remarks>
/// Credentials are read from <c>.local/provider.env</c>, which is gitignored. They are
/// never printed: every URL and error message goes through <see cref="CredentialScrubber"/>
/// on the way out, because harness output ends up pasted into issues and commit messages.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "help";

        try
        {
            return command switch
            {
                "sync" => await SyncAsync(CancellationToken.None).ConfigureAwait(false),
                "epg" => await EpgAsync(args, CancellationToken.None).ConfigureAwait(false),
                "play" => await PlayAsync(args, CancellationToken.None).ConfigureAwait(false),
                "latency" => await LatencyAsync(args, CancellationToken.None).ConfigureAwait(false),
                _ => Help(),
            };
        }
        catch (XtreamAuthenticationException exception)
        {
            Console.Error.WriteLine($"Authentication failed: {exception.Message}");
            return 2;
        }
        catch (XtreamProtocolException exception)
        {
            Console.Error.WriteLine($"Provider error: {exception.Message}");
            return 3;
        }
    }

    private static int Help()
    {
        Console.WriteLine("Iptv.Harness commands:");
        Console.WriteLine("  sync            Full live-channel sync against the configured provider");
        Console.WriteLine("  epg [file]      Ingest an XMLTV guide; downloads from the provider if no file");
        Console.WriteLine("  play [search]   Play a real stream and report decode diagnostics");
        Console.WriteLine("  latency [n]     Compare mpv option profiles for time-to-first-frame");
        return 1;
    }

    /// <summary>
    /// Compares mpv option profiles for time-to-first-frame against a real stream.
    /// </summary>
    /// <remarks>
    /// Phase 7 Step 1. On a single-connection account the dual-handle prebuffering the
    /// phase was designed around is unavailable, so the cold path is the entire feature
    /// and is measured rather than tuned by intuition.
    /// </remarks>
    private static async Task<int> LatencyAsync(string[] args, CancellationToken cancellationToken)
    {
        var trials = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 3;

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "harness.db");

        var factory = new SqliteConnectionFactory(databasePath);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var (title, url) = await FindStreamAsync(connection, null, cancellationToken)
            .ConfigureAwait(false);

        if (url is null)
        {
            Console.Error.WriteLine("No streams in the library. Run 'sync' first.");
            return 1;
        }

        if (!MpvLibrary.IsAvailable())
        {
            Console.Error.WriteLine("libmpv-2.dll not found.");
            return 1;
        }

        Console.WriteLine($"channel: {title}");
        Console.WriteLine($"trials per profile: {trials}   target: p50 under 800ms");
        Console.WriteLine();

        return await RunLatencyOnGlThreadAsync(url, trials).ConfigureAwait(false);
    }

    private static Task<int> RunLatencyOnGlThreadAsync(string url, int trials)
    {
        var completion = new TaskCompletionSource<int>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(RunLatency(url, trials));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(CredentialScrubber.Scrub(exception.ToString()));
                completion.SetResult(4);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static int RunLatency(string url, int trials)
    {
        using var glContext = WglContext.Create();
        glContext.MakeCurrent();

        if (!glContext.Query().SupportsHardwarePath)
        {
            Console.Error.WriteLine("This machine cannot use the hardware path; results would not transfer.");
            return 1;
        }

        var results = new List<(string Name, List<long> Samples, List<long> Loaded, string Rationale)>();

        foreach (var profile in LatencyBench.Profiles)
        {
            Console.Write($"{profile.Name,-22}");
            var samples = new List<long>();
            var loaded = new List<long>();

            for (var i = 0; i < trials; i++)
            {
                var measured = LatencyBench.Measure(profile, url, TimeSpan.FromSeconds(25));
                if (measured is { } timing)
                {
                    samples.Add(timing.FirstFrameMs);
                    if (timing.FileLoadedMs is { } l)
                    {
                        loaded.Add(l);
                    }

                    Console.Write($" {timing.FirstFrameMs,5}");
                }
                else
                {
                    Console.Write("   ---");
                }

                // Pacing is enforced by ProviderConnectionLimiter inside Measure rather than
                // by a sleep here, so it cannot be forgotten or tuned away.
            }

            Console.WriteLine();
            results.Add((profile.Name, samples, loaded, profile.Rationale));
        }

        WglContext.ClearCurrent();

        Console.WriteLine();
        Console.WriteLine("== results, sorted by median time to first frame ==");
        Console.WriteLine(
            $"{"profile",-22} {"median",8} {"best",8} {"worst",8} {"open",8} {"decode",8}   {"vs base",8}");

        var baseline = Median(results.First(r => r.Name == "baseline").Samples);

        foreach (var (name, samples, loaded, rationale) in results.OrderBy(r => Median(r.Samples)))
        {
            if (samples.Count == 0)
            {
                Console.WriteLine($"{name,-22}   no successful trials");
                continue;
            }

            var median = Median(samples);
            var openMs = loaded.Count > 0 ? Median(loaded) : -1;
            var decodeMs = openMs >= 0 ? median - openMs : -1;
            var delta = baseline > 0 ? $"{(median - baseline) / (double)baseline * 100:+0;-0}%" : "n/a";
            var target = median < 800 ? "  <- under 800ms" : string.Empty;

            Console.WriteLine(
                $"{name,-22} {median,8} {samples.Min(),8} {samples.Max(),8} " +
                $"{(openMs >= 0 ? openMs.ToString() : "?"),8} " +
                $"{(decodeMs >= 0 ? decodeMs.ToString() : "?"),8}   {delta,8}{target}");
            Console.WriteLine($"{string.Empty,22} {rationale}");
        }

        Console.WriteLine();
        Console.WriteLine("  open   = load command to FILE_LOADED: connect, probe, demux (provider + FFmpeg)");
        Console.WriteLine("  decode = FILE_LOADED to first presentable frame (ours)");
        return 0;
    }

    private static long Median(List<long> samples)
    {
        if (samples.Count == 0)
        {
            return long.MaxValue;
        }

        var ordered = samples.Order().ToList();
        return ordered[ordered.Count / 2];
    }

    /// <summary>
    /// Plays a real stream from the library and reports the Phase 5 decode diagnostics.
    /// </summary>
    /// <remarks>
    /// The exit criterion this exists for is <c>hwdec-current</c> against real MPEG-TS.
    /// The lavfi test pattern used elsewhere is generated rather than decoded, so it
    /// cannot answer whether hardware decoding actually engages.
    /// <para>
    /// Opens exactly one stream. The reference account permits a single connection, and
    /// exceeding it gets the account temporarily blocked - which the user would blame on
    /// this application.
    /// </para>
    /// </remarks>
    private static async Task<int> PlayAsync(string[] args, CancellationToken cancellationToken)
    {
        var search = args.Length > 1 ? args[1] : null;

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "harness.db");

        var factory = new SqliteConnectionFactory(databasePath);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var (title, url) = await FindStreamAsync(connection, search, cancellationToken)
            .ConfigureAwait(false);

        if (url is null)
        {
            Console.Error.WriteLine(
                search is null
                    ? "No streams in the library. Run 'sync' first."
                    : $"No active stream matching '{search}'.");
            return 1;
        }

        Console.WriteLine("== stream ==");
        Console.WriteLine($"  {title}");
        Console.WriteLine($"  {CredentialScrubber.Scrub(url)}");
        Console.WriteLine();

        if (!MpvLibrary.IsAvailable())
        {
            Console.Error.WriteLine("libmpv-2.dll not found; cannot play.");
            return 1;
        }

        return await PlayOnGlThreadAsync(title!, url).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs playback on a dedicated thread holding the GL context.
    /// </summary>
    /// <remarks>
    /// A WGL context belongs to one thread, and mpv's render calls must run on whichever
    /// thread holds it.
    /// </remarks>
    private static Task<int> PlayOnGlThreadAsync(string title, string url)
    {
        var completion = new TaskCompletionSource<int>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Play(title, url));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Playback failed: {CredentialScrubber.Scrub(exception.Message)}");
                completion.SetResult(4);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static int Play(string title, string url)
    {
        using var glContext = WglContext.Create();
        glContext.MakeCurrent();

        var capabilities = glContext.Query();
        Console.WriteLine("== gpu ==");
        Console.WriteLine($"  renderer         {capabilities.Renderer}");
        Console.WriteLine($"  opengl           {capabilities.Version}");
        Console.WriteLine($"  hardware path    {capabilities.SupportsHardwarePath}");
        Console.WriteLine();

        // The PRD's live profile. Applied before initialise because vo cannot change after.
        using var handle = MpvHandle.Create(new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["idle"] = "yes",
            ["keep-open"] = "yes",
            ["audio"] = "no",
            ["hwdec"] = "auto-safe",
            ["profile"] = "low-latency",
            ["cache"] = "yes",
            // Measured 11% faster to first frame; see docs/decisions/0008.
            ["cache-pause-initial"] = "no",
            ["demuxer-lavf-o"] = "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2",
            ["demuxer-max-bytes"] = "32MiB",
            ["demuxer-readahead-secs"] = "2",
            ["deinterlace"] = "auto",
        });

        using var renderer = MpvOpenGlRenderer.Create(handle);
        using var target = SharedVideoTarget.Create(1280, 720);
        using var events = new MpvEventLoop(handle);

        handle.RequestLogMessages("warn");
        foreach (var property in new[] { "hwdec-current", "video-params/w", "video-params/h", "core-idle" })
        {
            events.ObserveProperty(property, MpvFormat.String);
        }

        events.Start();

        Console.WriteLine("== playback ==");
        var stopwatch = Stopwatch.StartNew();
        handle.Command("loadfile", url);

        long? firstFrameMs = null;
        var frames = 0;
        var warnings = 0;

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            while (events.Events.TryRead(out var evt))
            {
                switch (evt)
                {
                    case MpvLogMessage log when warnings < 8:
                        Console.WriteLine($"  [{log.Level}] {CredentialScrubber.Scrub(log.Text)}");
                        warnings++;
                        break;

                    case MpvEndFile end:
                        Console.WriteLine($"  end-file reason={end.Reason} error={end.Error}");
                        if (end.Reason == 4)
                        {
                            Console.Error.WriteLine("  stream failed to open");
                            return 3;
                        }

                        break;

                    default:
                        break;
                }
            }

            if (renderer.HasFrameReady())
            {
                target.RenderFrame(renderer);
                frames++;
                firstFrameMs ??= stopwatch.ElapsedMilliseconds;

                // Ten seconds of real playback is enough to show sustained decode without
                // holding the account's only connection any longer than necessary.
                if (frames > 300)
                {
                    break;
                }
            }
            else
            {
                Thread.Sleep(2);
            }
        }

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine("== result ==");
        Console.WriteLine($"  time to first frame  {firstFrameMs?.ToString() ?? "never"} ms");
        Console.WriteLine($"  frames rendered      {frames:N0} in {stopwatch.ElapsedMilliseconds:N0}ms");

        var hwdec = handle.GetProperty("hwdec-current");
        var width = handle.GetProperty("video-params/w");
        var height = handle.GetProperty("video-params/h");
        var codec = handle.GetProperty("video-codec");

        Console.WriteLine($"  resolution           {width ?? "?"}x{height ?? "?"}");
        Console.WriteLine($"  codec                {codec ?? "?"}");
        Console.WriteLine($"  hwdec-current        {hwdec ?? "(none)"}");

        // Anything other than absent or "no" is a hardware decoder. Matching on "d3d11"
        // alone was wrong: with hwdec=auto-safe, mpv picks the best backend for the
        // adapter, and on NVIDIA that is nvdec rather than d3d11va. Reporting working
        // hardware decode as software would have sent someone chasing a non-problem.
        var hardware = !string.IsNullOrWhiteSpace(hwdec) &&
                       !hwdec.Equals("no", StringComparison.OrdinalIgnoreCase);

        var copyBack = hardware && hwdec!.EndsWith("-copy", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine(
            hardware
                ? $"  DECODE               hardware via {hwdec}" +
                  (copyBack ? " (copy-back: frames cross system memory)" : " (zero-copy)")
                : "  DECODE               SOFTWARE - this presents to users as 'the app is slow'");

        WglContext.ClearCurrent();
        return frames > 0 ? 0 : 3;
    }

    /// <summary>Finds an active, non-separator live stream to play.</summary>
    private static async Task<(string? Title, string? Url)> FindStreamAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string? search,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT title, url FROM streams
            WHERE kind = 'live' AND is_active = 1 AND is_separator = 0
              AND (@search IS NULL OR title LIKE '%' || @search || '%')
            ORDER BY
              -- Prefer a channel with guide data: it is more likely to be a real,
              -- working channel than an unmapped filler entry.
              (SELECT count(*) FROM epg_map m WHERE m.channel_key = streams.channel_key) DESC,
              length(title)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@search", (object?)search ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Ingests a guide and reports the Phase 3 timings.
    /// </summary>
    /// <remarks>
    /// Download and parse are timed separately and deliberately. The reference provider
    /// takes ~50s to serve its 70MB guide and varies by several-fold between runs, so a
    /// combined figure would swamp the parse budget in provider-side noise.
    /// </remarks>
    private static async Task<int> EpgAsync(string[] args, CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "harness.db");

        var factory = new SqliteConnectionFactory(databasePath);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        Stream guide;
        long bytes;

        if (args.Length > 1 && File.Exists(args[1]))
        {
            bytes = new FileInfo(args[1]).Length;
            Console.WriteLine($"== source ==\n  file, {bytes / (1024 * 1024.0):F1}MB");
            guide = File.OpenRead(args[1]);
        }
        else
        {
            if (LoadCredentials() is not { } credentials)
            {
                Console.Error.WriteLine("No .local/provider.env and no file argument.");
                return 1;
            }

            using var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("IptvPlayer/0.1");

            var url = new Uri($"{credentials.BaseUrl.ToString().TrimEnd('/')}/xmltv.php" +
                              $"?username={Uri.EscapeDataString(credentials.Username)}" +
                              $"&password={Uri.EscapeDataString(credentials.Password)}");

            Console.WriteLine("== download ==");
            var download = Stopwatch.StartNew();
            var payload = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            download.Stop();
            bytes = payload.Length;
            Console.WriteLine($"  {bytes / (1024 * 1024.0):F1}MB in {download.Elapsed.TotalSeconds:F1}s " +
                              $"({bytes / (1024 * 1024.0) / download.Elapsed.TotalSeconds:F1}MB/s)");
            guide = new MemoryStream(payload);
        }

        await using (guide)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);

            Console.WriteLine();
            Console.WriteLine("== ingest ==");
            var result = await EpgIngest.IngestAsync(
                connection, guide, new EpgIngestOptions(), DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            var allocated = (GC.GetTotalAllocatedBytes(precise: true) - before) / (1024 * 1024.0);
            var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024.0);

            Console.WriteLine($"  channels         {result.Channels:N0}");
            Console.WriteLine($"  programmes       {result.Programmes:N0}");
            Console.WriteLine($"  parse + insert   {result.ParseAndInsert.TotalSeconds:F2}s   (target 8s)");
            Console.WriteLine($"  total incl. FTS  {result.Total.TotalSeconds:F2}s   (target 15s)");
            Console.WriteLine($"  allocated        {allocated:F0}MB");
            Console.WriteLine($"  peak working set {peakWorkingSet:F0}MB   (target under 400MB)");
        }

        Console.WriteLine();
        Console.WriteLine("== guide quality ==");
        Console.WriteLine($"  epg channels stored   {await ScalarAsync(connection, "SELECT count(*) FROM epg_channels"):N0}");
        Console.WriteLine($"  with a usable id      {await ScalarAsync(connection, "SELECT count(*) FROM epg_channels WHERE epg_channel_id <> ''"):N0}");
        Console.WriteLine($"  with a display name   {await ScalarAsync(connection, "SELECT count(*) FROM epg_channels WHERE display_names <> ''"):N0}");
        Console.WriteLine($"  channels with a guide {await ScalarAsync(connection, "SELECT count(DISTINCT epg_channel_id) FROM programmes"):N0}");
        Console.WriteLine($"  programmes with no id {await ScalarAsync(connection, "SELECT count(*) FROM programmes WHERE epg_channel_id = ''"):N0}");

        var first = await ScalarAsync(connection, "SELECT min(start_utc) FROM programmes");
        var last = await ScalarAsync(connection, "SELECT max(stop_utc) FROM programmes");
        if (first > 0)
        {
            Console.WriteLine($"  guide spans           " +
                              $"{DateTimeOffset.FromUnixTimeSeconds(first):yyyy-MM-dd HH:mm} to " +
                              $"{DateTimeOffset.FromUnixTimeSeconds(last):yyyy-MM-dd HH:mm} " +
                              $"({(last - first) / 86400.0:F1} days)");
        }

        Console.WriteLine();
        Console.WriteLine("== projected EPG coverage ==");

        // The number Phase 4 lives or dies on: how many of the user's channels can be
        // matched to a guide entry by exact tvg_id, before any name matching.
        var byId = await ScalarAsync(
            connection,
            """
            SELECT count(DISTINCT s.channel_key)
            FROM streams s
            JOIN epg_channels e ON lower(e.epg_channel_id) = lower(s.tvg_id)
            WHERE s.is_separator = 0 AND s.tvg_id IS NOT NULL AND s.tvg_id <> '';
            """);

        // Matching an id is not the same as having a guide. A declared channel with no
        // programmes matches perfectly and still shows the user an empty row.
        var withProgrammes = await ScalarAsync(
            connection,
            """
            SELECT count(DISTINCT s.channel_key)
            FROM streams s
            JOIN epg_channels e ON lower(e.epg_channel_id) = lower(s.tvg_id)
            WHERE s.is_separator = 0 AND s.tvg_id IS NOT NULL AND s.tvg_id <> ''
              AND EXISTS (SELECT 1 FROM programmes p WHERE p.epg_channel_id = e.epg_channel_id);
            """);

        // The ceiling no amount of matching can exceed: the guide only carries programmes
        // for so many channels.
        var guideChannels = await ScalarAsync(
            connection, "SELECT count(DISTINCT epg_channel_id) FROM programmes");

        var channels = await ScalarAsync(connection, "SELECT count(*) FROM channels");
        if (channels > 0)
        {
            Console.WriteLine($"  user channels         {channels:N0}");
            Console.WriteLine($"  id match              {byId:N0} ({byId / (double)channels:P1})");
            Console.WriteLine($"  id match with a guide {withProgrammes:N0} ({withProgrammes / (double)channels:P1})");
            Console.WriteLine($"  CEILING, any matcher  {guideChannels:N0} ({guideChannels / (double)channels:P1})");
            Console.WriteLine();
            Console.WriteLine("  The ceiling is set by guide completeness, not match quality:");
            Console.WriteLine("  name matching cannot invent programmes the guide does not carry.");
        }

        Console.WriteLine();
        Console.WriteLine("== matching ==");
        var match = Stopwatch.StartNew();
        var report = await EpgMatcher.MatchAsync(connection, cancellationToken).ConfigureAwait(false);
        match.Stop();

        Console.WriteLine($"  matched            {report.Matched:N0} in {match.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"  library coverage   {report.LibraryCoverage:P1}   ({report.Matched:N0} of {report.TotalChannels:N0})");
        Console.WriteLine($"  ceiling recovery   {report.CeilingRecovery:P1}   ({report.Matched:N0} of {report.Ceiling:N0})   <- the number that judges the matcher");

        var locked = await ScalarAsync(connection, "SELECT count(*) FROM epg_map WHERE locked = 1");
        Console.WriteLine($"  manual mappings    {locked:N0} (preserved)");
        return 0;
    }

    private static async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        if (LoadCredentials() is not { } credentials)
        {
            Console.Error.WriteLine(
                "No .local/provider.env found. Expected XTREAM_HOST, XTREAM_USER, XTREAM_PASS.");
            return 1;
        }

        // One handler for the process lifetime, per the PRD. The default .NET User-Agent
        // is rejected by some panels.
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IptvPlayer/0.1");

        var client = new XtreamClient(http, credentials);

        var total = Stopwatch.StartNew();

        Console.WriteLine("== account ==");
        var account = await client.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  status           {account.Status}");
        Console.WriteLine($"  max_connections  {account.MaxConnections}");
        Console.WriteLine($"  active_cons      {account.ActiveConnections}");
        if (account.ExpiresAtUnix is { } expiry)
        {
            Console.WriteLine($"  expires          {DateTimeOffset.FromUnixTimeSeconds(expiry):yyyy-MM-dd}");
        }

        if (account.MaxConnections == 1)
        {
            // Phase 7 depends on this: two mpv handles consume two connections.
            Console.WriteLine("  NOTE: single connection - channel-change prebuffering must stay disabled.");
        }

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "harness.db");

        var factory = new SqliteConnectionFactory(databasePath);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        await SqliteFeatures.EnsureFts5AvailableAsync(connection, cancellationToken).ConfigureAwait(false);
        var providerId = await EnsureProviderAsync(connection, credentials, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== fetch ==");
        var fetch = Stopwatch.StartNew();
        var records = new List<StreamRecord>(capacity: 32_000);
        await foreach (var stream in client.GetLiveStreamsAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(StreamMapper.FromXtreamLive(stream, providerId, credentials));
        }

        fetch.Stop();
        Console.WriteLine($"  {records.Count:N0} live streams in {fetch.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"  separators       {records.Count(r => r.IsSeparator):N0}");
        Console.WriteLine($"  no tvg_id        {records.Count(r => r.TvgId is null):N0}");
        Console.WriteLine($"  with catchup     {records.Count(r => r.CatchupKind is not null):N0}");

        Console.WriteLine();
        Console.WriteLine("== merge ==");
        var write = Stopwatch.StartNew();
        var summary = await ProviderSync
            .SyncAsync(connection, providerId, records, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await ProviderSync.RefreshChannelsAsync(connection, cancellationToken).ConfigureAwait(false);
        write.Stop();

        Console.WriteLine($"  live: added {summary.Added:N0}  updated {summary.Updated:N0}  " +
                          $"reactivated {summary.Reactivated:N0}  deactivated {summary.Deactivated:N0}  " +
                          $"pruned {summary.Pruned:N0}");

        // VOD and series are separate endpoints and separate catalogues. Deactivation is
        // scoped per kind, so syncing one cannot deactivate another.
        Console.WriteLine();
        Console.WriteLine("== vod ==");
        var vodFetch = Stopwatch.StartNew();
        var movies = new List<StreamRecord>(capacity: 16_000);
        await foreach (var movie in client.GetVodStreamsAsync(cancellationToken).ConfigureAwait(false))
        {
            movies.Add(StreamMapper.FromXtreamVod(movie, providerId, credentials));
        }

        vodFetch.Stop();
        Console.WriteLine($"  {movies.Count:N0} films in {vodFetch.ElapsedMilliseconds:N0}ms");

        var vodSummary = await ProviderSync
            .SyncAsync(connection, providerId, movies, StreamKind.Vod, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"  added {vodSummary.Added:N0}  updated {vodSummary.Updated:N0}  " +
                          $"deactivated {vodSummary.Deactivated:N0}");

        Console.WriteLine();
        Console.WriteLine("== series ==");
        var seriesFetch = Stopwatch.StartNew();
        var shows = new List<SeriesRecord>(capacity: 64_000);
        await foreach (var show in client.GetSeriesAsync(cancellationToken).ConfigureAwait(false))
        {
            shows.Add(SeriesMapper.FromXtream(show, providerId));
        }

        seriesFetch.Stop();
        Console.WriteLine($"  {shows.Count:N0} series in {seriesFetch.ElapsedMilliseconds:N0}ms " +
                          $"({shows.Sum(s => s.SeasonCount):N0} seasons declared)");
        Console.WriteLine("  episodes are NOT fetched: one request per series would be " +
                          $"{shows.Count:N0} requests on a {account.MaxConnections}-connection account");

        var seriesSummary = await SeriesSync
            .SyncAsync(connection, providerId, shows, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"  added {seriesSummary.Added:N0}  updated {seriesSummary.Updated:N0}");
        Console.WriteLine($"  wrote in {write.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"  channels         {await ScalarAsync(connection, "SELECT count(*) FROM channels"):N0}");
        Console.WriteLine($"  distinct keys    {await ScalarAsync(connection, "SELECT count(DISTINCT channel_key) FROM streams"):N0}");

        total.Stop();
        Console.WriteLine();
        Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"database {CredentialScrubber.Scrub(databasePath)}");
        return 0;
    }

    private static async Task<int> EnsureProviderAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        XtreamCredentials credentials,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Credentials are not written here. Persisting them is Phase 9's job and requires
        // DPAPI encryption; the harness holds them in memory for the run only.
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url)
            VALUES (1, 'harness', 'xtream', @base_url)
            ON CONFLICT(id) DO UPDATE SET base_url = excluded.base_url;
            SELECT 1;
            """;
        command.Parameters.AddWithValue("@base_url", credentials.BaseUrl.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private static async Task<long> ScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>Reads credentials from the gitignored local env file.</summary>
    private static XtreamCredentials? LoadCredentials()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".local", "provider.env");
            if (File.Exists(candidate))
            {
                var values = File.ReadAllLines(candidate)
                    .Where(line => line.Contains('=', StringComparison.Ordinal) &&
                                   !line.StartsWith('#'))
                    .Select(line => line.Split('=', 2))
                    .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

                if (values.TryGetValue("XTREAM_HOST", out var host) &&
                    values.TryGetValue("XTREAM_USER", out var user) &&
                    values.TryGetValue("XTREAM_PASS", out var pass))
                {
                    return new XtreamCredentials(new Uri(host), user, pass);
                }
            }

            directory = directory.Parent;
        }

        return null;
    }
}
