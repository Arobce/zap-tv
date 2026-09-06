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
    private readonly List<ChannelRow> _rows = [];

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
        _searchDebounce.Tick += async (_, _) => await LoadChannelsAsync(SearchBox.Text);

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

            await LoadChannelsAsync(null);
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

            // Start on the first channel rather than an empty screen. A TV app that opens
            // showing nothing is asking the user to do work before it has done any, and
            // it also means the playback path is exercised on every launch rather than
            // only when someone clicks.
            // Prefer a channel the guide covers. It is a proxy for "real channel the
            // provider actually carries": a large share of this catalogue is filler that
            // never streams, and starting on one of those makes a working player look
            // broken.
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

    private async Task LoadChannelsAsync(string? search)
    {
        await using var connection = await OpenAsync();

        var stopwatch = Stopwatch.StartNew();
        var channels = await ChannelRepository.GetChannelsAsync(
            connection,
            new ChannelQuery { Search = string.IsNullOrWhiteSpace(search) ? null : search, Limit = 500 },
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        stopwatch.Stop();

        _rows.Clear();
        _rows.AddRange(channels.Select(c => new ChannelRow(c)));

        // Reassigning rather than mutating: ItemsRepeater does not observe a plain List,
        // and the slice does not yet need incremental loading.
        ChannelList.ItemsSource = null;
        ChannelList.ItemsSource = _rows;

        var withGuide = _rows.Count(r => r.NowTitle is not null);
        CountText.Text =
            $"{_rows.Count:N0} shown · {withGuide:N0} with guide · query {stopwatch.ElapsedMilliseconds}ms";

        if (string.IsNullOrWhiteSpace(search))
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

    private async void OnChannelClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string channelKey })
        {
            return;
        }

        var row = _rows.Find(r => r.ChannelKey == channelKey);
        if (row is not null)
        {
            await PlayChannelAsync(row);
        }
    }

    private async Task PlayChannelAsync(ChannelRow row)
    {
        if (_handle is null)
        {
            return;
        }

        await using var connection = await OpenAsync();
        var url = await ChannelRepository.GetPlaybackUrlAsync(
            connection, row.ChannelKey, CancellationToken.None);

        if (url is null)
        {
            StatusText.Text = "no playable stream for this channel";
            return;
        }

        ChannelTitle.Text = row.DisplayName;
        ProgrammeTitle.Text = row.NowLine;

        if (!_swapChainAttached)
        {
            StatusText.Text = "no video surface; cannot play";
            return;
        }

        _switchTimer = Stopwatch.StartNew();
        StatusText.Text = "opening...";

        Log($"loadfile {url}");
        _handle.Command("loadfile", url);
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
