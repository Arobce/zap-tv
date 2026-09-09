using Iptv.Presentation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Data;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Iptv.Mpv;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace Iptv.App;

/// <summary>
/// Phase 6 vertical slice: the channel list and playback, over the real library.
/// </summary>
/// <remarks>
/// Deliberately not MVVM yet. The point of the slice is to find out what actually hurts
/// when using this as a TV, and a view model layer added before that is a guess about
/// which abstractions will earn their keep.
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary><c>ISwapChainPanelNative</c>. Classic COM, so declared rather than projected.</summary>
    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }

    private readonly string _databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer",
        "harness.db");

    /// <summary>Diagnostic log for the slice. See <see cref="AppLog"/>.</summary>
    private static readonly string LogPath = AppLog.Path;

    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _heartbeat;
    private readonly List<LibraryRow> _rows = [];

    /// <summary>Which catalogue the list is showing.</summary>
    /// <remarks>
    /// Its own type rather than reusing <see cref="LibraryKind"/>. A view and a row kind
    /// looked like the same thing while there were exactly three of each; favourites and
    /// continue-watching are views over kinds that already exist, and conflating them
    /// would mean inventing row kinds that no row ever has.
    /// </remarks>
    private readonly LibraryBrowser _browser;

    private static void Log(string message) => AppLog.Write(message);

    private MpvHandle? _handle;
    private VideoPresenter? _presenter;
    private MpvEventLoop? _events;
    private Stopwatch? _switchTimer;
    private bool _swapChainAttached;

    /// <summary>
    /// Guards the provider against this application.
    /// </summary>
    /// <remarks>
    /// One connection and a two-second floor between channel changes, until the account
    /// is read and the real limit is known. Two seconds is short enough not to be felt
    /// while surfing and long enough that holding a key down cannot open dozens of
    /// streams, which is what gets an account blocked.
    /// </remarks>
    private ProviderConnectionLimiter _connections =
        new(maxConnections: 1, minimumInterval: TimeSpan.FromSeconds(2));

    private ConnectionLease? _currentStream;

    /// <summary>The failover plan for whatever is playing. Null when nothing is.</summary>
    private FailoverSession? _session;

    /// <summary>Whether the current attempt has produced a picture.</summary>
    /// <remarks>
    /// Frames presented, not mpv's own state. A stream that opens, negotiates and then
    /// delivers nothing looks healthy to every property mpv exposes, and the whole point of
    /// the check is to catch that.
    /// </remarks>
    private bool _firstFrameSeen;

    private long _framesAtLastCheck;
    private long _framesAtOpen;
    /// <summary>Decides when the stream has died. See decision 0012.</summary>
    private readonly StallDetector _stalls = new();
    private readonly DispatcherQueueTimer _firstFrameDeadline;
    private readonly DispatcherQueueTimer _stallWatch;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _positionSave;

    public MainWindow()
    {
        InitializeComponent();
        Title = "ZapTV";

        // Sized through AppWindow rather than left at the WinUI default, which opens
        // larger than a 1080p screen. Resizing later with Win32 MoveWindow does not drive
        // a XAML relayout, so elements stay positioned for the original size.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        // 80ms is the PRD's search budget. Debouncing longer makes typing feel laggy;
        // shorter reissues the query mid-keystroke for no benefit.
        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(80);
        _browser = new LibraryBrowser(
            new SqliteConnectionFactory(_databasePath),
            (seriesRowId, _) => FetchEpisodesAsync(seriesRowId));

        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += async (_, _) => await LoadLibraryAsync(SearchBox.Text);

        // A rising frame count is the only thing that distinguishes "presenting" from
        // "set up correctly and showing nothing", which look identical on screen.
        _heartbeat = DispatcherQueue.CreateTimer();
        _heartbeat.Interval = TimeSpan.FromSeconds(2);
        _heartbeat.Tick += (_, _) =>
        {
            if (_presenter is null)
            {
                return;
            }

            var frames = _presenter.FramesPresented;
            Log($"heartbeat: frames={frames} loops={_presenter.LoopIterations} " +
                $"backend={_presenter.Backend} scale={_presenter.CompositionScaleApplied:F2} " +
                $"fault={_presenter.Fault?.Message ?? "none"} " +
                $"resizeFailed={_presenter.ResizeFailed?.Message ?? "none"} " +
                $"scaleFailed={_presenter.ScaleFailed?.Message ?? "none"}");

            var elapsed = _switchTimer is { } t ? $" · {t.ElapsedMilliseconds}ms" : string.Empty;
            var provider = _session is { HasFailedOver: true, Current: { } current }
                ? $" · via {current.ProviderName}"
                : string.Empty;

            StatusText.Text = frames > 0
                ? $"presenting · {frames:N0} frames{elapsed}{provider}"
                : $"no frames yet{elapsed}";
        };
        _heartbeat.Start();

        // The PRD's two failure detectors. The first-frame one counts presented frames
        // rather than asking mpv how it is doing, because a stream that opens and then
        // delivers nothing reports itself as fine. The stall one needs a second signal as
        // well - see StallDetector, and decision 0012 for what frames alone measured.
        _firstFrameDeadline = DispatcherQueue.CreateTimer();
        _firstFrameDeadline.Interval = TimeSpan.FromSeconds(4);
        _firstFrameDeadline.IsRepeating = false;
        _firstFrameDeadline.Tick += async (_, _) =>
        {
            if (_firstFrameSeen)
            {
                return;
            }

            await FailOverAsync(PlaybackOutcome.Timeout, "no first frame within 4s");
        };

        // Sampled at 1s, which is the interval StallDetector's window is calibrated
        // against. Sampling slower would stretch its four-second window; faster would only
        // ask mpv the same question more often.
        _stallWatch = DispatcherQueue.CreateTimer();
        _stallWatch.Interval = TimeSpan.FromSeconds(1);
        _stallWatch.Tick += async (_, _) => await CheckForStallAsync();
        _stallWatch.Start();

        // 10s. Short enough that a crash or a power cut loses a few seconds rather than a
        // few minutes, long enough that it is not a write per second for hours.
        _positionSave = DispatcherQueue.CreateTimer();
        _positionSave.Interval = TimeSpan.FromSeconds(10);
        _positionSave.Tick += async (_, _) => await SavePositionAsync();
        _positionSave.Start();

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => ToastPanel.Visibility = Visibility.Collapsed;

        StartTransport();

        // Key presses need somewhere to land before anything has been clicked.
        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);

        Closed += OnClosed;

        // Wait for the panel rather than starting from the constructor. SetSwapChain on a
        // panel that has not been loaded silently does nothing - no error, no exception,
        // just a black rectangle - and ActualWidth is zero until layout has run, so the
        // swap chain would be created at the wrong size as well. The compositing spike
        // worked precisely because it used this event.
        VideoPanel.Loaded += OnVideoPanelLoaded;

        _ = LoadChannelsSafelyAsync();
    }

    private async Task LoadChannelsSafelyAsync()
    {
        try
        {
            // First run, or a database with nothing configured. Opening straight to
            // Providers rather than showing an empty channel list, which reads as a broken
            // sync rather than as nothing set up yet.
            if (!File.Exists(_databasePath) ||
                (await Providers.ListAsync(CancellationToken.None)).Count == 0)
            {
                LibraryText.Text = "no providers yet";
                await ShowProvidersAsync();
                return;
            }

            await LoadSettingsAsync();
            UpdateModeButtons();
            await LoadCategoriesAsync();
            await ApplyAsync(await _browser.LoadAsync(CancellationToken.None));

            // Deliberately not awaited. The guide download is 64MB and the ingest moves
            // 164,661 rows; blocking the window on a task the user did not ask for would
            // mean the app takes a minute to appear.
            _ = RefreshGuideInBackgroundAsync();
        }
        catch (Exception exception)
        {
            Log($"library load failed: {exception}");
            LibraryText.Text = $"library failed: {exception.Message}";
        }
    }

    private async void OnVideoPanelLoaded(object sender, RoutedEventArgs e)
    {
        VideoPanel.Loaded -= OnVideoPanelLoaded;

        try
        {
            await StartPlayerAsync();

            // --test-pattern plays mpv's built-in generator instead of a provider stream.
            // It takes the network, the provider and dead channels out of the picture, so
            // a black panel can only be the rendering path. It also costs the account
            // nothing, which matters on a single-connection subscription.
            if (Environment.GetCommandLineArgs().Contains("--test-pattern", StringComparer.Ordinal))
            {
                Log("test pattern requested; not contacting the provider");
                ChannelTitle.Text = "Test pattern";
                ProgrammeTitle.Text = "av://lavfi:testsrc — no provider involved";
                _switchTimer = Stopwatch.StartNew();
                _handle!.Command("loadfile", "av://lavfi:testsrc=size=1280x720:rate=30");
                return;
            }

            // No auto-play against a real provider. Opening a stream the user did not ask
            // for spends one of a single-connection account's only slot, and repeated
            // launches look to the provider like connection abuse.
            StatusText.Text = "ready — pick a channel";
        }
        catch (Exception exception)
        {
            Log($"player start failed: {exception}");
            StatusText.Text = $"player failed: {exception.Message}";
        }
    }

    /// <summary>Watches for a picture that has frozen while playback claims to continue.</summary>
    /// <remarks>
    /// Counts presented frames rather than reading <c>core-idle</c> and
    /// <c>cache-buffering-state</c>. The demuxer is configured to reconnect, so a provider
    /// that stops sending shows up as an endless healthy-looking reconnect loop while the
    /// picture sits still. Frames are the thing the viewer actually sees stop.
    /// </remarks>
    private async Task CheckForStallAsync()
    {
        if (_presenter is null || _session?.Current is null)
        {
            _stalls.Reset();
            return;
        }

        var frames = _presenter.FramesPresented;

        if (!_firstFrameSeen)
        {
            // Compared against the count at open, not against zero. The presenter runs
            // continuously across channel changes, so its total never resets and a new
            // stream would otherwise inherit the previous one's success.
            if (frames <= _framesAtOpen)
            {
                return;
            }

            _firstFrameSeen = true;
            _firstFrameDeadline.Stop();
            _framesAtLastCheck = frames;

            var ttfb = _switchTimer is { } opened ? (int)opened.ElapsedMilliseconds : (int?)null;
            Log($"first frame after {ttfb}ms on stream {_session.Current.StreamId}");

            // The demuxer has read the headers by now, which is when mpv publishes the
            // track list. Asked earlier it reports none on a file that has several.
            RefreshTracks();

            await using var connection = await OpenAsync();
            await _session.ReportAsync(
                connection, PlaybackOutcome.Ok, DateTimeOffset.UtcNow, CancellationToken.None, ttfb);
            return;
        }

        // The detector rather than a frame counter here. Waiting for frames to stop meant
        // waiting for mpv's 32MiB buffer to play out first, which measured 8.3s, 12.1s and
        // 19.0s on three cuts of one channel against a 10s budget - see decision 0012. The
        // buffer draining at real time says the same thing in about five, whatever it holds.
        if (!_stalls.Observe(new StallSample
        {
            FramesPresented = frames,
            CacheSeconds = ReadCacheSeconds(),
            At = DateTimeOffset.UtcNow,
        }))
        {
            return;
        }

        var reason = _stalls.Reason ?? "playback stopped";
        _stalls.Reset();

        await FailOverAsync(PlaybackOutcome.Stall, reason);
    }

    /// <summary>Seconds of demuxed data buffered ahead, or null when mpv does not say.</summary>
    /// <remarks>
    /// Null is a normal answer, not a failure: some containers report nothing here, and the
    /// detector falls back to counting frames for those.
    /// </remarks>
    private double? ReadCacheSeconds()
    {
        var raw = _handle?.GetProperty("demuxer-cache-duration");

        return double.TryParse(
            raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.IsNaN(value)
            ? value
            : null;
    }

    /// <summary>Opens a connection, applying the required pragmas.</summary>
    private async Task<SqliteConnection> OpenAsync()
        => await new SqliteConnectionFactory(_databasePath).OpenAsync(CancellationToken.None);

    /// <summary>Fills the list from whatever the browser currently points at.</summary>
    /// <remarks>
    /// The queries, the summaries and the navigation rules moved to
    /// <see cref="LibraryBrowser"/>, where they are tested. What is left here is the part
    /// that genuinely needs the window: pushing rows at the repeater and writing the
    /// status lines.
    /// </remarks>
    private async Task ApplyAsync(BrowseResult result)
    {
        _rows.Clear();
        _rows.AddRange(result.Rows);
        _playingIndex = -1;

        SetPosterMode(result.UsePosters);

        // Reassigning rather than mutating: ItemsRepeater does not observe a plain List,
        // and the slice does not yet need incremental loading.
        ChannelList.ItemsSource = null;
        ChannelList.ItemsSource = _rows;

        CountText.Text = result.Summary;

        if (result.Level == BrowseLevel.Catalogue && _browser.Search is null)
        {
            await using var connection = await OpenAsync();
            LibraryText.Text = await DescribeLibraryAsync(connection);
        }
    }

    /// <summary>Loads the current catalogue and shows it.</summary>
    private async Task LoadLibraryAsync(string? search)
    {
        var result = await _browser.SetSearchAsync(search, CancellationToken.None);
        await ApplyAsync(result);
    }

    private static async Task<string> DescribeLibraryAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        // Channels backed by a live stream, not every row in the table. VOD keys have
        // channels rows too - the same key derivation serves both - and counting those
        // reported 118,763 channels above a list that has 20,479 in it.
        command.CommandText =
            """
            SELECT
              (SELECT count(*) FROM channels c
                WHERE EXISTS (SELECT 1 FROM streams s
                               WHERE s.channel_key = c.channel_key
                                 AND s.kind = 'live'
                                 AND s.is_active = 1
                                 AND s.is_separator = 0)),
              (SELECT count(*) FROM epg_map),
              (SELECT count(*) FROM programmes);
            """;

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            return "empty library";
        }

        return $"{reader.GetInt64(0):N0} channels · {reader.GetInt64(1):N0} mapped · " +
               $"{reader.GetInt64(2):N0} programmes";
    }

    /// <summary>Starts the render thread and binds its swap chain to the panel.</summary>
    private async Task StartPlayerAsync()
    {
        // The live profile from the PRD, including the one option measurement justified.
        _handle = MpvHandle.Create(new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["idle"] = "yes",
            ["keep-open"] = "yes",
            // Audio on. It was disabled during the render debugging so a silent picture
            // could not be mistaken for a broken one, and a television that cannot make
            // a sound is not a television.
            ["volume"] = "70",
            ["volume-max"] = "130",
            ["hwdec"] = "auto-safe",
            ["profile"] = "low-latency",
            ["cache"] = "yes",
            ["cache-pause-initial"] = "no",
            ["demuxer-lavf-o"] = "reconnect=1,reconnect_streamed=1,reconnect_delay_max=2",
            ["demuxer-max-bytes"] = "32MiB",
            ["demuxer-readahead-secs"] = "2",
            ["deinterlace"] = "auto",
        });

        _events = new MpvEventLoop(_handle);
        _events.ObserveProperty("hwdec-current", MpvFormat.String);
        _handle.RequestLogMessages("warn");
        _events.Start();
        _ = PumpEventsAsync(_events);

        var width = Math.Max(640, (int)VideoPanel.ActualWidth);
        var height = Math.Max(360, (int)VideoPanel.ActualHeight);

        _presenter = new VideoPresenter(_handle, width, height);

        try
        {
            Log("presenter starting");
            var swapChain = await _presenter.StartAsync();

            var native = WinRT.CastExtensions.As<ISwapChainPanelNative>(VideoPanel);
            Marshal.ThrowExceptionForHR(native.SetSwapChain(swapChain));
            _swapChainAttached = true;

            // The swap chain must follow the panel. Without this it stays at whatever size
            // the panel had at startup and the picture is stretched on every resize, which
            // the PRD lists as a Phase 0.2 requirement.
            VideoPanel.SizeChanged += OnVideoPanelSizeChanged;
            VideoPanel.CompositionScaleChanged += OnCompositionScaleChanged;

            // Applied once up front. Without this the very first frames are cropped until
            // something happens to resize the panel.
            _presenter.SetCompositionScale(
                VideoPanel.CompositionScaleX <= 0 ? 1.0 : VideoPanel.CompositionScaleX,
                VideoPanel.CompositionScaleY <= 0 ? 1.0 : VideoPanel.CompositionScaleY);

            Log($"swap chain attached; backend={_presenter.Backend} detail={_presenter.BackendDetail}; panel {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight}");

            StatusText.Text = $"ready · {_presenter.Backend} · {_presenter.BackendDetail}";
        }
        catch (Exception exception)
        {
            // Decision 0002: software rendering is the fallback for machines that fail the
            // capability check. It is not wired into the panel yet, so say so plainly
            // rather than showing a black rectangle.
            Log($"presenter FAILED: {exception}");
            StatusText.Text = $"no hardware video path: {exception.Message}";
        }
    }

    /// <summary>Surfaces mpv's own view of decoding, live.</summary>
    /// <remarks>
    /// Read continuously rather than sampled once: measurement showed hardware decode is
    /// not deterministic, with one run in five falling back to software on the same
    /// stream. A value captured at startup would misreport the current state.
    /// </remarks>
    private async Task PumpEventsAsync(MpvEventLoop loop)
    {
        await foreach (var evt in loop.Events.ReadAllAsync())
        {
            switch (evt)
            {
                // Reason 4 is an error. It is now a failover trigger rather than a message:
                // saying "channel may be dead" while three working providers carry the same
                // channel is exactly what Phase 8 replaces.
                case MpvEndFile end:
                    Log($"end-file reason={end.Reason} error={end.Error}");
                    if (end.Reason == 4)
                    {
                        // Marshalled: this is the mpv event-loop thread, and the handler
                        // touches XAML.
                        DispatcherQueue.TryEnqueue(async () => await FailOverAsync(
                            PlaybackOutcome.HttpError, $"mpv end-file error {end.Error}"));
                    }

                    break;

                case MpvLogMessage log:
                    Log($"mpv [{log.Level}] {log.Text}");
                    break;

                default:
                    break;
            }

            if (evt is not MpvPropertyChanged { Name: "hwdec-current" } change)
            {
                continue;
            }

            Log($"hwdec-current = {change.AsString ?? "(null)"}; " +
                $"frames presented {_presenter?.FramesPresented ?? 0}");

            var decoder = change.AsString;
            var hardware = !string.IsNullOrWhiteSpace(decoder) &&
                           !decoder.Equals("no", StringComparison.OrdinalIgnoreCase);

            DispatcherQueue.TryEnqueue(() =>
            {
                var elapsed = _switchTimer is { } t ? $" · first frame {t.ElapsedMilliseconds}ms" : string.Empty;
                StatusText.Text = hardware
                    ? $"decode: {decoder} (hardware){elapsed}"
                    : $"decode: SOFTWARE{elapsed}";
            });
        }
    }

    private async void OnModeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<LibraryView>(tag, out var mode))
        {
            return;
        }

        if (mode == _browser.View)
        {
            return;
        }

        // Cleared before the load, not after: the browser clears its own search, and
        // leaving text in the box would show a term that is not being applied.
        _searchDebounce.Stop();
        _suppressSearch = true;
        SearchBox.Text = string.Empty;
        _suppressSearch = false;

        try
        {
            var result = await _browser.SwitchViewAsync(mode, CancellationToken.None);

            UpdateModeButtons();
            await LoadCategoriesAsync();
            await ApplyAsync(result);
        }
        catch (Exception exception)
        {
            Log($"catalogue load failed: {exception}");
            CountText.Text = $"load failed: {exception.Message}";
        }
    }

    /// <summary>Marks the selected tab by disabling it.</summary>
    /// <remarks>
    /// A disabled button cannot be clicked again and reads as current without needing a
    /// selection visual the slice has no style system for yet.
    /// </remarks>
    private void UpdateModeButtons()
    {
        AllTab.IsEnabled = _browser.View != LibraryView.All;
        LiveTab.IsEnabled = _browser.View != LibraryView.Live;
        FilmsTab.IsEnabled = _browser.View != LibraryView.Films;
        SeriesTab.IsEnabled = _browser.View != LibraryView.Series;
        FavouritesTab.IsEnabled = _browser.View != LibraryView.Favourites;
        ContinueTab.IsEnabled = _browser.View != LibraryView.Continue;
    }

    private async void OnRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
        {
            return;
        }

        var row = _rows.Find(r => r.Key == key);
        if (row is null)
        {
            return;
        }

        // Caught, because this is an async void handler: an exception escaping it is
        // unhandled at the top of the stack, and the whole reason a broken query here
        // presented as "clicking does nothing" is that it had nowhere to be reported.
        try
        {
            if (row.Kind == LibraryKind.Series)
            {
                await OpenSeriesAsync(row);
                return;
            }

            if (row.Kind == LibraryKind.Season)
            {
                await OpenSeasonAsync(row);
                return;
            }

            if (row.Playable)
            {
                await PlayAsync(row);
            }
        }
        catch (Exception exception)
        {
            Log($"row click failed: {exception}");
            CountText.Text = $"failed: {exception.Message}";
            StatusText.Text = "see the log";
        }
    }

    /// <summary>Opens a series, showing seasons or episodes as the browser decides.</summary>
    private async Task OpenSeriesAsync(LibraryRow row)
    {
        Log($"opening series {row.Title} (row {row.SeriesRowId})");

        ChannelTitle.Text = row.Title;
        ProgrammeTitle.Text = "series";
        CountText.Text = "fetching episodes...";

        await ApplyAsync(await _browser.OpenSeriesAsync(row, CancellationToken.None));
    }

    /// <summary>Shows one season's episodes.</summary>
    private async Task OpenSeasonAsync(LibraryRow row)
    {
        ProgrammeTitle.Text = row.Title;
        await ApplyAsync(_browser.OpenSeason(row));
    }

    /// <summary>Asks the provider for a series' episodes and stores them.</summary>
    private async Task<IReadOnlyList<EpisodeRecord>> FetchEpisodesAsync(long seriesRowId)
    {
        await using var connection = await OpenAsync();

        var info = await EpisodeSync.GetFetchInfoAsync(
            connection, seriesRowId, CancellationToken.None);

        if (info is null)
        {
            Log($"no fetch info for series row {seriesRowId}");
            CountText.Text = "this series is not on an enabled provider";
            return [];
        }

        // Read from the database, DPAPI-encrypted by whichever sync wrote them. The app has
        // no other route to the provider, which is why episodes were unreachable before the
        // credential store existed.
        var credentials = await ProviderCredentialStore.LoadAsync(
            connection, info.ProviderId, new DpapiSecretProtector(), CancellationToken.None);

        if (credentials is null)
        {
            Log($"no stored credentials for provider {info.ProviderId}");
            CountText.Text = "no stored provider credentials — run: harness sync";
            return [];
        }

        try
        {
            using var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZapTV/0.1");

            var stopwatch = Stopwatch.StartNew();
            var response = await new XtreamClient(http, credentials)
                .GetSeriesInfoAsync(info.ProviderSeriesId, CancellationToken.None);

            var episodes = EpisodeSync.Map(response, credentials);
            stopwatch.Stop();

            Log($"fetched {episodes.Count} episodes for series {info.ProviderSeriesId} " +
                $"in {stopwatch.ElapsedMilliseconds}ms");

            await EpisodeSync.ReplaceAsync(
                connection, info.ProviderId, seriesRowId, episodes,
                DateTimeOffset.UtcNow, CancellationToken.None);

            return episodes;
        }
        catch (Exception exception)
        {
            // A metadata request, not a stream, so a failure here costs nothing and must
            // not take the app down. Said plainly rather than left as an empty list.
            Log($"series fetch failed: {exception}");
            CountText.Text = $"could not fetch episodes: {exception.Message}";
            return [];
        }
    }

    /// <summary>What is playing, when it is resumable. Null for live television.</summary>
    /// <remarks>
    /// Live is excluded deliberately: a position in a broadcast means nothing an hour
    /// later, and recording one fills the table with rows that can never be used.
    /// </remarks>
    private string? _resumeKey;

    /// <summary>Writes down where the resumable thing currently is.</summary>
    /// <remarks>
    /// Called on a timer and again when playback is replaced, because the timer alone
    /// loses up to its own interval, and closing the window is exactly when the last few
    /// seconds matter.
    /// </remarks>
    private async Task SavePositionAsync()
    {
        if (_resumeKey is not { } key || _handle is null || !_firstFrameSeen)
        {
            return;
        }

        if (!TryReadSeconds("time-pos", out var position))
        {
            return;
        }

        var duration = TryReadSeconds("duration", out var length) ? length : (int?)null;

        try
        {
            await using var connection = await OpenAsync();
            await PlaybackStateRepository.SaveAsync(
                connection, key, position, duration, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Losing a resume position must never take playback down with it.
            Log($"saving position failed: {exception.Message}");
        }
    }

    /// <summary>Reads an mpv property that holds a number of seconds.</summary>
    private bool TryReadSeconds(string property, out int seconds)
    {
        seconds = 0;

        var raw = _handle?.GetProperty(property);
        if (raw is null ||
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.IsNaN(value) || value <= 0)
        {
            return false;
        }

        seconds = (int)value;
        return true;
    }

    /// <summary>Sets where mpv should start the next file, or clears it.</summary>
    /// <remarks>
    /// The <c>start</c> option is sticky: set once, it applies to every subsequent file
    /// until changed. Clearing it explicitly is what stops the next channel opening a
    /// minute in.
    /// </remarks>
    private void SetStartPosition(int? seconds)
    {
        _handle?.SetProperty(
            "start",
            seconds is { } value ? value.ToString(CultureInfo.InvariantCulture) : "none");
    }

    /// <summary>Opens one episode's own URL.</summary>
    private async Task PlayEpisodeAsync(LibraryRow row, string url)
    {
        if (!_swapChainAttached || _handle is null)
        {
            StatusText.Text = "no video surface; cannot play";
            return;
        }

        // No failover session: an episode has one source. _session stays null, which the
        // stall watcher reads as "nothing playing" and so leaves alone - correct here,
        // since there would be nothing to fail over to anyway.
        _session = null;
        _playingIndex = _rows.IndexOf(row);

        ChannelTitle.Text = _browser.OpenSeries is { } series ? $"{series.Title} — {row.Title}" : row.Title;
        ProgrammeTitle.Text = row.Subtitle;

        // The position of whatever was playing before this, saved before it is replaced.
        await SavePositionAsync();

        _resumeKey = row.Key;

        await using (var connection = await OpenAsync())
        {
            var resume = await PlaybackStateRepository.GetResumeSecondsAsync(
                connection, row.Key, CancellationToken.None);

            SetStartPosition(resume);

            if (resume is { } seconds)
            {
                Toast($"Resuming at {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}");
            }
        }

        _currentStream?.Dispose();
        _currentStream = null;

        try
        {
            _currentStream = await _connections.AcquireAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _switchTimer = Stopwatch.StartNew();
        _firstFrameSeen = false;
        ClearTracks();
        _stalls.Reset();
        _framesAtOpen = _presenter?.FramesPresented ?? 0;
        StatusText.Text = "opening...";

        RootGrid.Focus(FocusState.Programmatic);
        Log($"loadfile episode {row.Key}");
        _handle.Command("loadfile", url);
    }


    private async Task PlayAsync(LibraryRow row)
    {
        if (_handle is null)
        {
            return;
        }

        // An episode carries its own URL, so it skips the channel_key lookup and the
        // failover plan: a series episode has exactly one source, and there is nothing to
        // fail over to.
        if (row.Kind == LibraryKind.Episode && row.EpisodeUrl is { } episodeUrl)
        {
            await PlayEpisodeAsync(row, episodeUrl);
            return;
        }

        await using var connection = await OpenAsync();

        // Same plan for a film as for a channel: a film's key is a channel_key, so provider
        // priority, reliability and the safety guard all apply without a second path.
        var session = await FailoverSession.StartAsync(
            connection,
            row.Key,
            // Episode reaches here from continue-watching, where the row was built from a
            // stored position and carries no URL. Its channel_key is its ep: key, so the
            // normal lookup works — but only if the kind is right, since that key exists
            // solely on series_episode rows.
            row.Kind switch
            {
                LibraryKind.Film => StreamKind.Vod,
                LibraryKind.Episode => StreamKind.SeriesEpisode,
                _ => StreamKind.Live,
            },
            DateTimeOffset.UtcNow,
            QualityPreference.Highest,
            CancellationToken.None);

        foreach (var refused in session.Excluded)
        {
            Log($"failover excluded stream {refused.Candidate.StreamId}: {refused.Reason}");
        }

        if (session.Current is null)
        {
            StatusText.Text = session.Excluded.Count > 0
                ? "no playable stream — every alternative was a different channel"
                : "no playable stream for this entry";
            return;
        }

        if (!_swapChainAttached)
        {
            StatusText.Text = "no video surface; cannot play";
            return;
        }

        _session = session;

        // Where the arrow keys step from. Looked up rather than passed in, because a
        // channel can also be reached by clicking, and both have to leave the same trail.
        _playingIndex = _rows.IndexOf(row);

        ChannelTitle.Text = row.Title;
        ProgrammeTitle.Text = row.Subtitle;

        await SavePositionAsync();

        // Films resume; live television does not. Clearing the key as well as the start
        // option matters, because both are sticky and a channel opened after a film would
        // otherwise inherit the film's position.
        // Episodes reach here only from continue-watching; opened from a season they take
        // the URL path above. Either way they resume.
        if (row.Kind is LibraryKind.Film or LibraryKind.Episode)
        {
            _resumeKey = row.Key;

            var resume = await PlaybackStateRepository.GetResumeSecondsAsync(
                connection, row.Key, CancellationToken.None);

            SetStartPosition(resume);

            if (resume is { } seconds)
            {
                Toast($"Resuming at {TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}");
            }
        }
        else
        {
            _resumeKey = null;
            SetStartPosition(null);
        }

        // Back to the root, so the next arrow press changes channel instead of scrolling
        // the list button that was just clicked.
        RootGrid.Focus(FocusState.Programmatic);

        await OpenCurrentAsync("opening...");
    }

    /// <summary>Opens whatever stream the session currently points at.</summary>
    /// <remarks>
    /// The single place a stream is opened, so the connection lease, the first-frame
    /// deadline and the attempt timer cannot drift apart between the first attempt and a
    /// failover.
    /// </remarks>
    private async Task OpenCurrentAsync(string status)
    {
        if (_handle is null || _session?.Current is not { } candidate)
        {
            return;
        }

        // Release the previous slot before taking one for the new stream. loadfile replaces
        // the stream anyway, but the accounting has to match reality or the limiter refuses
        // every change after the first.
        _currentStream?.Dispose();
        _currentStream = null;

        // Waits rather than refuses. A stream that dies 200ms in is still inside the
        // minimum interval, and refusing there would show an error while a working
        // alternative sat unused.
        try
        {
            _currentStream = await _connections.AcquireAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _switchTimer = Stopwatch.StartNew();
        _firstFrameSeen = false;
        ClearTracks();
        _stalls.Reset();
        _framesAtOpen = _presenter?.FramesPresented ?? 0;
        StatusText.Text = status;

        // 4s, per the PRD. Restarted on every open so a failover gets the same budget as
        // the first attempt rather than inheriting what is left of it.
        _firstFrameDeadline.Stop();
        _firstFrameDeadline.Start();

        Log($"loadfile stream={candidate.StreamId} provider={candidate.ProviderName} attempt={_session.AttemptNumber}");
        _handle.Command("loadfile", candidate.Url);
    }

    /// <summary>
    /// Records how the current attempt ended and opens the next candidate if there is one.
    /// </summary>
    /// <remarks>
    /// Always on the UI thread. mpv events arrive on the event-loop thread and this touches
    /// XAML, which the conventions forbid from a callback thread.
    /// </remarks>
    private async Task FailOverAsync(PlaybackOutcome outcome, string detail)
    {
        if (_session is not { Current: { } failed })
        {
            return;
        }

        _firstFrameDeadline.Stop();

        var elapsed = _switchTimer is { } timer ? (int)timer.ElapsedMilliseconds : (int?)null;
        Log($"attempt failed: stream={failed.StreamId} outcome={outcome} after {elapsed}ms — {detail}");

        await using var connection = await OpenAsync();
        var next = await _session.ReportAsync(
            connection,
            outcome,
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            _firstFrameSeen ? elapsed : null,
            detail);

        if (next is null)
        {
            // Only now is it a hard error. Saying "dead channel" on the first failure when
            // three providers remain is the behaviour the whole phase exists to replace.
            _currentStream?.Dispose();
            _currentStream = null;
            StatusText.Text = $"all {_session.AttemptNumber} streams failed for this channel";
            return;
        }

        await OpenCurrentAsync($"switching to {next.ProviderName}...");
    }

    /// <summary>Keeps the video surface matched to the panel.</summary>
    /// <remarks>
    /// ActualWidth is in logical units; the swap chain wants physical pixels, so the
    /// rasterization scale is applied. Skipping it leaves the video rendering at a
    /// fraction of the panel size on any display above 100%, which looks like a soft or
    /// blurry picture rather than a sizing bug.
    /// </remarks>
    private void OnVideoPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = VideoPanel.CompositionScaleX <= 0 ? 1.0 : VideoPanel.CompositionScaleX;
        var scaleY = VideoPanel.CompositionScaleY <= 0 ? 1.0 : VideoPanel.CompositionScaleY;

        var width = (int)Math.Round(e.NewSize.Width * scale);
        var height = (int)Math.Round(e.NewSize.Height * scaleY);

        // Scale before size. A composition swap chain is drawn into the panel's logical
        // space one chain pixel per logical unit, so a chain sized in physical pixels needs
        // the inverse transform or the panel shows its top-left corner and crops the rest.
        _presenter?.SetCompositionScale(scale, scaleY);
        _presenter?.Resize(width, height);

        Log($"panel resized to {e.NewSize.Width:F0}x{e.NewSize.Height:F0} logical, " +
            $"{width}x{height} physical, scale {scale:F2}");
    }

    /// <summary>Keeps the transform right when the window moves to a different display.</summary>
    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        var scale = sender.CompositionScaleX <= 0 ? 1.0 : sender.CompositionScaleX;
        var scaleY = sender.CompositionScaleY <= 0 ? 1.0 : sender.CompositionScaleY;

        _presenter?.SetCompositionScale(scale, scaleY);
        _presenter?.Resize(
            (int)Math.Round(sender.ActualWidth * scale),
            (int)Math.Round(sender.ActualHeight * scaleY));

        Log($"composition scale changed to {scale:F2}x{scaleY:F2}");
    }

    // --- watching, rather than demonstrating ---

    private int _volume = 70;
    private bool _muted;
    private bool _fullScreen;

    /// <summary>Index into <see cref="_rows"/> of what is playing, for channel up/down.</summary>
    private int _playingIndex = -1;

    /// <summary>
    /// The remote control.
    /// </summary>
    /// <remarks>
    /// Handled on the root grid so a press lands wherever focus happens to be. Typing in
    /// the search box must not change channel, so text input is excluded explicitly rather
    /// than relying on a KeyboardAccelerator's own idea of when a text control is active.
    /// </remarks>
    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Sliders alongside text boxes: a focused slider takes the arrow keys for itself,
        // and letting both act would move the thumb and change channel on one press.
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or Slider)
        {
            // Escape still gets out, because a search box you cannot leave without the
            // mouse is worse than no shortcut at all.
            if (e.Key == VirtualKey.Escape)
            {
                RootGrid.Focus(FocusState.Programmatic);
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case VirtualKey.F:
                SetFullScreen(!_fullScreen);
                break;

            case VirtualKey.Escape:
                // Fullscreen first. Escape means "back out of the innermost thing", and
                // leaving the series list while still fullscreen would be the wrong one.
                if (_numbers.IsActive)
                {
                    // A half-typed number is more inner than anything else on screen.
                    _numberTimer?.Stop();
                    _numbers.Reset();
                    NumberEntryPanel.Visibility = Visibility.Collapsed;
                }
                else if (_fullScreen)
                {
                    SetFullScreen(false);
                }
                else if (_browser.Search is not null)
                {
                    // A search is the innermost thing when one is active, so Escape leaves
                    // it before it leaves anything else.
                    _suppressSearch = true;
                    SearchBox.Text = string.Empty;
                    _suppressSearch = false;

                    await ApplyAsync(await _browser.SetSearchAsync(null, CancellationToken.None));
                }
                else if (await _browser.BackAsync(CancellationToken.None) is { } back)
                {
                    // The browser knows how many levels there are; a series with one
                    // season has no season list to return to. Null means it was already at
                    // the catalogue and Escape has nothing left to unwind.
                    ProgrammeTitle.Text = back.Level == BrowseLevel.Seasons ? "series" : ProgrammeTitle.Text;
                    await ApplyAsync(back);
                }

                break;

            // Space and K both pause. K is mpv's own binding and the one anyone who has
            // used a player expects; space is the one everyone else does.
            case VirtualKey.Space:
            case VirtualKey.K:
            case (VirtualKey)0xB3:
                TogglePause();
                break;

            case VirtualKey.M:
            case (VirtualKey)0xAD:
                ToggleMute();
                break;

            // Slash focuses the search box, as it does in a browser. Oem2 rather than a
            // named key: there is no VirtualKey.Slash, and the code is layout dependent.
            case (VirtualKey)0xBF:
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
                break;

            case VirtualKey.I:
                // The overlay covers the top of the picture. Hiding it is what a viewer
                // wants for most of the time they are watching.
                TitleOverlay.Visibility = TitleOverlay.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                break;

            // B for bookmark. F is fullscreen and S is free but reads as "stop"; B is what
            // browsers use for the same idea.
            case VirtualKey.B:
                await ToggleFavouriteAsync();
                break;

            case VirtualKey.Left:
                AdjustVolume(-5);
                break;

            case VirtualKey.Right:
                AdjustVolume(+5);
                break;

            // J and L are mpv's seek keys. Ten seconds back and thirty forward is not
            // symmetry for its own sake: back is for catching a line of dialogue, forward
            // is for skipping something, and those are different sizes of mistake.
            case VirtualKey.J:
                SeekRelative(-10);
                break;

            case VirtualKey.L:
                SeekRelative(+30);
                break;

            case VirtualKey.Up:
            case VirtualKey.PageUp:
            case (VirtualKey)0xB1:
                await StepChannelAsync(-1);
                break;

            case VirtualKey.Down:
            case VirtualKey.PageDown:
            case (VirtualKey)0xB0:
                await StepChannelAsync(+1);
                break;

            case VirtualKey.Enter:
                // Ends a part-typed number early rather than waiting out the timeout.
                await CommitNumberAsync(_numbers.Commit());
                break;

            default:
                if (DigitOf(e.Key) is { } digit)
                {
                    await PressDigitAsync(digit);
                    break;
                }

                return;
        }

        // Only for keys actually consumed. Marking everything handled would swallow Tab
        // and the arrow keys the list itself needs.
        e.Handled = true;
    }

    /// <summary>Moves to the next or previous entry in the list that is playable.</summary>
    /// <remarks>
    /// Separator rows are in the list on purpose — they are the provider's own grouping and
    /// read as headings — but stepping onto one would open nothing. Skipped rather than
    /// filtered out, so the visible order still matches what stepping does.
    /// </remarks>
    private async Task StepChannelAsync(int direction)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var index = _playingIndex < 0 ? (direction > 0 ? -1 : 0) : _playingIndex;

        for (var step = 0; step < _rows.Count; step++)
        {
            index += direction;

            if (index < 0)
            {
                index = _rows.Count - 1;
            }
            else if (index >= _rows.Count)
            {
                index = 0;
            }

            if (_rows[index].Playable)
            {
                await PlayAsync(_rows[index]);
                return;
            }
        }
    }

    /// <summary>Marks the playing channel as a favourite, or unmarks it.</summary>
    /// <remarks>
    /// Acts on what is playing rather than on a selected row: the list has no selection
    /// model yet, and "the channel I am watching" is the one worth keeping anyway.
    /// </remarks>
    private async Task ToggleFavouriteAsync()
    {
        if (_playingIndex < 0 || _playingIndex >= _rows.Count)
        {
            Toast("Nothing playing to favourite");
            return;
        }

        await ToggleFavouriteAsync(_rows[_playingIndex], star: null);
    }

    /// <summary>The star on a row.</summary>
    /// <remarks>
    /// The row is found by key rather than by index. The list virtualises, so the button
    /// that raised this belongs to whichever row is bound to that container right now, and
    /// an index captured when the template was instantiated would have moved on.
    /// </remarks>
    private async void OnFavouriteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } button)
        {
            return;
        }

        try
        {
            if (_rows.Find(r => r.Key == key) is { } row)
            {
                await ToggleFavouriteAsync(row, button);
            }
        }
        catch (Exception exception)
        {
            // async void: nothing above this catches, so an exception here would vanish
            // and the star would simply not respond.
            Log($"favouriting {key} failed: {exception}");
            Toast("Could not save that favourite");
        }
    }

    /// <summary>Flips one row's favourite state and redraws its star.</summary>
    private async Task ToggleFavouriteAsync(LibraryRow row, Button? star)
    {
        if (!row.CanFavourite)
        {
            // Favourites are a live-TV idea: they sort a 20,479-row list. Films and
            // episodes have continue-watching instead.
            Toast("Favourites are for live channels");
            return;
        }

        await using var connection = await OpenAsync();

        // Toggled against the stored value, not against the row's. Two rows can carry the
        // same channel — a search hit and the catalogue entry behind it — and the database
        // is the only thing that knows which way it is now.
        var favourite = await ChannelRepository.ToggleFavouriteAsync(
            connection, row.Key, CancellationToken.None);

        row.IsFavourite = favourite;

        // Set directly as well as on the model. The binding is one-way and evaluated when
        // the container is bound, so without this the star only catches up when the row
        // scrolls out of view and back.
        if (star is not null)
        {
            star.Content = row.FavouriteGlyph;
        }

        Toast(favourite ? $"★  {row.Title}" : $"Removed  {row.Title}");
        Log($"favourite {(favourite ? "set" : "cleared")} for {row.Key}");
    }

    private void TogglePause()
    {
        if (_handle is null || _session?.Current is null)
        {
            return;
        }

        var paused = !string.Equals(_handle.GetProperty("pause"), "yes", StringComparison.Ordinal);
        _handle.SetProperty("pause", paused ? "yes" : "no");

        // A paused live stream keeps its connection open and falls behind. Saying so is
        // more honest than letting the user discover it as latency when they resume.
        Toast(paused ? "Paused" : "Playing");
    }

    private void AdjustVolume(int delta)
    {
        if (_handle is null)
        {
            return;
        }

        _volume = Math.Clamp(_volume + delta, 0, 130);
        _muted = false;

        _handle.SetProperty("mute", "no");
        _handle.SetProperty("volume", _volume.ToString(CultureInfo.InvariantCulture));

        Toast($"Volume {_volume}%");
    }

    private void ToggleMute()
    {
        if (_handle is null)
        {
            return;
        }

        _muted = !_muted;
        _handle.SetProperty("mute", _muted ? "yes" : "no");
        Toast(_muted ? "Muted" : $"Volume {_volume}%");
    }

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetFullScreen(!_fullScreen);
        e.Handled = true;
    }

    /// <summary>Fills the screen with video, hiding the list and the overlay.</summary>
    /// <remarks>
    /// The presenter change and the sidebar collapse have to happen together. Going
    /// fullscreen with the 380px list still there gives a fullscreen window showing a
    /// channel list, which is not what anybody means by fullscreen.
    /// </remarks>
    private void SetFullScreen(bool on)
    {
        _fullScreen = on;

        AppWindow.SetPresenter(on
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Overlapped);

        // Restored to whatever the current view wants, not to the list width: coming out
        // of fullscreen on the film grid must not hand it back a sidebar sized for rows.
        SidebarColumn.Width = on ? new GridLength(0) : new GridLength(SidebarWidth);
        Sidebar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TitleOverlay.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // Focus follows, or the next key press goes to whatever the list left focused and
        // Escape cannot get back out.
        RootGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows a message over the video for a moment.</summary>
    private void Toast(string message)
    {
        ToastText.Text = message;
        ToastPanel.Visibility = Visibility.Visible;

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Guards the combo's own repopulation from being read as a user choice.</summary>
    /// <remarks>
    /// Assigning ItemsSource raises SelectionChanged. Without this, switching from Live to
    /// Films would reload the list twice and the second reload would carry a category from
    /// the catalogue that was just left.
    /// </remarks>
    private bool _loadingCategories;

    /// <summary>Suppresses the search handler while the box is cleared programmatically.</summary>
    private bool _suppressSearch;

    /// <summary>Fills the category picker for whichever catalogue is showing.</summary>
    private async Task LoadCategoriesAsync()
    {
        // The browser knows which views have categories at all. Emptied rather than left
        // showing the previous catalogue's, which would be a filter the list is not using.
        if (_browser.CategoryKindForView is not { } kind)
        {
            _loadingCategories = true;
            CategoryBox.ItemsSource = null;
            CategoryBox.IsEnabled = false;
            _loadingCategories = false;
            return;
        }

        await using var connection = await OpenAsync();
        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, kind, CancellationToken.None);

        // "All categories" is a row rather than a cleared selection, because a ComboBox
        // with no way back to unfiltered is a trap.
        var names = new List<string> { AllCategories };
        names.AddRange(categories.Select(c => $"{c.Name}  ({c.Count:N0})"));

        _loadingCategories = true;
        CategoryBox.ItemsSource = names;
        CategoryBox.SelectedIndex = 0;
        CategoryBox.IsEnabled = categories.Count > 0;
        _loadingCategories = false;
    }

    private const string AllCategories = "All categories";

    private async void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCategories || CategoryBox.SelectedItem is not string selected)
        {
            return;
        }

        // The count is display only; the query matches on the name the provider gave.
        // Trimming it back off here keeps the list rows and the filter in one place rather
        // than storing a parallel list of names beside the one being shown.
        var category = selected == AllCategories
            ? null
            : selected[..selected.LastIndexOf("  (", StringComparison.Ordinal)];

        try
        {
            await ApplyAsync(await _browser.SetCategoryAsync(category, CancellationToken.None));
        }
        catch (Exception exception)
        {
            Log($"category filter failed: {exception}");
            CountText.Text = $"filter failed: {exception.Message}";
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        // Restarting the timer on each keystroke is the debounce; the query only runs once
        // typing pauses.
        if (_suppressSearch)
        {
            return;
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _searchDebounce.Stop();
        _heartbeat.Stop();
        _firstFrameDeadline.Stop();
        _stallWatch.Stop();
        _toastTimer.Stop();
        _positionSave.Stop();
        _session = null;

        // Release the provider slot before anything else: the stream must be given back
        // even if teardown below throws.
        _currentStream?.Dispose();
        _currentStream = null;

        // Presenter first: its render thread owns the GL and mpv render contexts, and both
        // must be released before the handle they were created from.
        _presenter?.Dispose();
        _presenter = null;

        _events?.Dispose();
        _events = null;

        _handle?.Dispose();
        _handle = null;
    }
}
