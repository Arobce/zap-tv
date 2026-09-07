using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Mpv;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    /// <summary>
    /// Diagnostic log for the slice.
    /// </summary>
    /// <remarks>
    /// A GUI has nowhere to print, and reading state off screenshots is guesswork. Serilog
    /// replaces this once there is a settings surface to configure it from; for now the
    /// point is simply to be able to see what happened.
    /// </remarks>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer",
        "logs",
        "app.log");

    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _heartbeat;
    private readonly List<LibraryRow> _rows = [];

    /// <summary>Which catalogue the list is showing.</summary>
    private LibraryKind _mode = LibraryKind.Live;

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            // Scrubbed: stream URLs carry the account's credentials, and this file is
            // exactly the sort of thing that ends up pasted into a bug report.
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:HH:mm:ss.fff}  {Iptv.Core.Xtream.CredentialScrubber.Scrub(message)}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never take the app down.
        }
    }

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
    private readonly ProviderConnectionLimiter _connections =
        new(maxConnections: 1, minimumInterval: TimeSpan.FromSeconds(2));

    private ConnectionLease? _currentStream;

    public MainWindow()
    {
        InitializeComponent();
        Title = "IPTV Player";

        // Sized through AppWindow rather than left at the WinUI default, which opens
        // larger than a 1080p screen. Resizing later with Win32 MoveWindow does not drive
        // a XAML relayout, so elements stay positioned for the original size.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        // 80ms is the PRD's search budget. Debouncing longer makes typing feel laggy;
        // shorter reissues the query mid-keystroke for no benefit.
        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(80);
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
            Log($"heartbeat: frames={frames} loops={_presenter.LoopIterations} backend={_presenter.Backend} fault={_presenter.Fault?.Message ?? "none"}");

            var elapsed = _switchTimer is { } t ? $" · {t.ElapsedMilliseconds}ms" : string.Empty;
            StatusText.Text = frames > 0
                ? $"presenting · {frames:N0} frames{elapsed}"
                : $"no frames yet{elapsed}";
        };
        _heartbeat.Start();

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
            if (!File.Exists(_databasePath))
            {
                LibraryText.Text = "No library. Run: dotnet run --project src/Iptv.Harness -- sync";
                return;
            }

            UpdateModeButtons();
            await LoadLibraryAsync(null);
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

    /// <summary>Opens a connection, applying the required pragmas.</summary>
    private async Task<SqliteConnection> OpenAsync()
        => await new SqliteConnectionFactory(_databasePath).OpenAsync(CancellationToken.None);

    /// <summary>Fills the list from whichever catalogue is selected.</summary>
    /// <remarks>
    /// Three queries rather than one union: live rows carry now/next and a progress bar,
    /// films are grouped streams, and series are containers with no stream at all. They
    /// share a row type for display but nothing at the storage layer.
    /// </remarks>
    private async Task LoadLibraryAsync(string? search)
    {
        await using var connection = await OpenAsync();
        var term = string.IsNullOrWhiteSpace(search) ? null : search;

        var stopwatch = Stopwatch.StartNew();
        _rows.Clear();
        string summary;

        switch (_mode)
        {
            case LibraryKind.Film:
            {
                var films = await LibraryRepository.GetFilmsAsync(
                    connection,
                    new CatalogueQuery { Search = term, Limit = 500 },
                    CancellationToken.None);

                foreach (var film in films)
                {
                    _rows.Add(LibraryRow.FromFilm(film));
                }

                // Counted only for the unfiltered view. The count scans the whole VOD
                // table - 138ms over 158,255 rows even indexed - and repeating it per
                // keystroke would double the cost of every search for a number nobody
                // reads while typing.
                summary = term is null
                    ? $"{_rows.Count:N0} of {await LibraryRepository.CountFilmsAsync(connection, new CatalogueQuery(), CancellationToken.None):N0} films"
                    : $"{_rows.Count:N0} films matching";
                break;
            }

            case LibraryKind.Series:
            {
                var series = await LibraryRepository.GetSeriesAsync(
                    connection,
                    new CatalogueQuery { Search = term, Limit = 500 },
                    CancellationToken.None);

                foreach (var show in series)
                {
                    _rows.Add(LibraryRow.FromSeries(show));
                }

                summary = $"{_rows.Count:N0} series · newest first";
                break;
            }

            default:
            {
                var channels = await ChannelRepository.GetChannelsAsync(
                    connection,
                    new ChannelQuery { Search = term, Limit = 500 },
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);

                var withGuide = 0;
                foreach (var channel in channels)
                {
                    if (channel.NowTitle is not null)
                    {
                        withGuide++;
                    }

                    _rows.Add(LibraryRow.FromChannel(channel));
                }

                summary = $"{_rows.Count:N0} shown · {withGuide:N0} with guide";
                break;
            }
        }

        stopwatch.Stop();

        // Reassigning rather than mutating: ItemsRepeater does not observe a plain List,
        // and the slice does not yet need incremental loading.
        ChannelList.ItemsSource = null;
        ChannelList.ItemsSource = _rows;

        CountText.Text = $"{summary} · query {stopwatch.ElapsedMilliseconds}ms";

        if (term is null)
        {
            LibraryText.Text = await DescribeLibraryAsync(connection);
        }
    }

    private static async Task<string> DescribeLibraryAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              (SELECT count(*) FROM channels),
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
            ["audio"] = "no",
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
                // Reason 4 is an error, and it is the difference between "this channel is
                // dead" and "the player is broken". Without it, both look like a black
                // rectangle.
                case MpvEndFile end:
                    Log($"end-file reason={end.Reason} error={end.Error}");
                    if (end.Reason == 4)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                            StatusText.Text = "stream failed to open (channel may be dead)");
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
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<LibraryKind>(tag, out var mode))
        {
            return;
        }

        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        UpdateModeButtons();

        // The search term does not carry across catalogues: a term that matched channels
        // usually matches nothing in a film catalogue, and an empty list on switching
        // reads as a broken tab rather than an empty search.
        _searchDebounce.Stop();
        SearchBox.Text = string.Empty;

        try
        {
            await LoadLibraryAsync(null);
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
        LiveTab.IsEnabled = _mode != LibraryKind.Live;
        FilmsTab.IsEnabled = _mode != LibraryKind.Film;
        SeriesTab.IsEnabled = _mode != LibraryKind.Series;
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

        if (!row.Playable)
        {
            // Series have no stream until their episodes are fetched, which sync skips
            // deliberately: one request per series against 49,783 of them.
            ChannelTitle.Text = row.Title;
            ProgrammeTitle.Text = row.Subtitle;
            StatusText.Text = "no episodes yet — series fetch is not wired up";
            return;
        }

        await PlayAsync(row);
    }

    private async Task PlayAsync(LibraryRow row)
    {
        if (_handle is null)
        {
            return;
        }

        await using var connection = await OpenAsync();

        // Same lookup for a film as for a channel: a film's key is a channel_key, so
        // provider priority and quality ordering apply to both without a second path.
        var url = await ChannelRepository.GetPlaybackUrlAsync(
            connection, row.Key, CancellationToken.None);

        if (url is null)
        {
            StatusText.Text = "no playable stream for this entry";
            return;
        }

        ChannelTitle.Text = row.Title;
        ProgrammeTitle.Text = row.Subtitle;

        if (!_swapChainAttached)
        {
            StatusText.Text = "no video surface; cannot play";
            return;
        }

        // Release the previous channel's slot before taking one for the new channel.
        // loadfile replaces the stream anyway, but the accounting has to match reality or
        // the limiter refuses every change after the first.
        _currentStream?.Dispose();
        _currentStream = null;

        if (!_connections.TryAcquire(out var lease))
        {
            // Says why rather than doing nothing. A button that silently ignores a click
            // is indistinguishable from a broken one, and the wait is short.
            var wait = _connections.TimeUntilNextOpen;
            StatusText.Text = $"easing off the provider — retry in {wait.TotalSeconds:F0}s";
            Log($"channel change refused by limiter; {wait.TotalMilliseconds:F0}ms remaining");
            return;
        }

        _currentStream = lease;
        _switchTimer = Stopwatch.StartNew();
        StatusText.Text = "opening...";

        Log($"loadfile {url}");
        _handle.Command("loadfile", url);
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

        _presenter?.Resize(width, height);
        Log($"panel resized to {e.NewSize.Width:F0}x{e.NewSize.Height:F0} logical, {width}x{height} physical");
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        // Restarting the timer on each keystroke is the debounce; the query only runs once
        // typing pauses.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _searchDebounce.Stop();
        _heartbeat.Stop();

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
