using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Iptv.Mpv;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Iptv.App;

/// <summary>
/// Phase 0.2 compositing spike.
/// </summary>
/// <remarks>
/// Answers one question: does XAML composite above video presented through a
/// <c>SwapChainPanel</c>? If it does, overlays, the EPG panel and context menus all work.
/// If it does not, the presentation path is wrong, and no z-order change fixes it — that
/// is the airspace violation <c>--wid</c> causes and this whole design exists to avoid.
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// <c>ISwapChainPanelNative</c>. Classic COM, not a WinRT interface, so it is declared
    /// here rather than projected.
    /// </summary>
    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }

    private readonly DispatcherQueueTimer _statusTimer;
    private MpvHandle? _handle;
    private VideoPresenter? _presenter;

    public MainWindow()
    {
        InitializeComponent();

        Title = "IPTV Player — compositing spike";

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statusTimer.Tick += (_, _) => UpdateFrameCount();

        VideoPanel.Loaded += OnPanelLoaded;
        Closed += OnClosed;
    }

    private async void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _handle = MpvHandle.Create(new Dictionary<string, string>
            {
                ["vo"] = "libmpv",
                ["idle"] = "yes",
                ["keep-open"] = "yes",
                ["audio"] = "no",
                ["hwdec"] = "auto-safe",
            });

            var width = Math.Max(1, (int)VideoPanel.ActualWidth);
            var height = Math.Max(1, (int)VideoPanel.ActualHeight);

            _presenter = new VideoPresenter(_handle, width, height);

            // The render thread creates the swap chain; this awaits it rather than
            // blocking, because blocking the UI thread here would deadlock against the
            // panel's own layout.
            var swapChain = await _presenter.StartAsync();

            AttachSwapChain(swapChain);

            // mpv's built-in test pattern: no network, no fixture file. The point is the
            // compositing, not the source.
            _handle.Command("loadfile", "av://lavfi:testsrc=size=1280x720:rate=30");

            StatusText.Text = $"backend: {_presenter.Backend}  |  {_presenter.BackendDetail}";
            _statusTimer.Start();
        }
        catch (Exception exception)
        {
            // A failure here is the spike's actual result, so it goes on screen rather
            // than into a debugger.
            StatusText.Text = "FAILED";
            FrameText.Text = exception.Message;
        }
    }

    /// <summary>Binds the DXGI swap chain to the panel. Must run on the UI thread.</summary>
    private void AttachSwapChain(IntPtr swapChain)
    {
        var native = WinRT.CastExtensions.As<ISwapChainPanelNative>(VideoPanel);
        var result = native.SetSwapChain(swapChain);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private void UpdateFrameCount()
    {
        if (_presenter is null)
        {
            return;
        }

        // A rising count is what distinguishes "presenting" from "showed one frame and
        // stalled", which look identical in a screenshot.
        FrameText.Text = $"frames presented: {_presenter.FramesPresented:N0}";
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer.Stop();

        // Presenter first: its render thread owns the GL context and the mpv render
        // context, and both must be released before the handle they were created from.
        _presenter?.Dispose();
        _presenter = null;

        _handle?.Dispose();
        _handle = null;
    }
}
