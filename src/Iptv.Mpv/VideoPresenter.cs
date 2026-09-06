using System.Diagnostics;
using Vortice.DXGI;

namespace Iptv.Mpv;

/// <summary>How video is reaching the screen.</summary>
public enum VideoBackend
{
    /// <summary>Not yet started, or startup failed.</summary>
    None,

    /// <summary>OpenGL rendering into a D3D11 texture shared via WGL_NV_DX_interop2.</summary>
    Hardware,

    /// <summary>CPU rendering. Diagnostic fallback; will not hold 1080i.</summary>
    Software,
}

/// <summary>
/// Drives mpv rendering on a dedicated thread and presents through a DXGI swap chain.
/// </summary>
/// <remarks>
/// <para>
/// The swap chain is created for composition, so the caller can attach it to a
/// <c>SwapChainPanel</c> and let XAML composite above and below it. That is the whole
/// reason for this path rather than <c>--wid</c>, which puts video in its own HWND above
/// everything.
/// </para>
/// <para>
/// Everything GL runs on one owned thread. A WGL context belongs to a single thread, and
/// mpv's render calls must happen on whichever thread holds it. Nothing here touches XAML;
/// the swap chain pointer is the only thing that crosses to the UI thread.
/// </para>
/// </remarks>
public sealed class VideoPresenter : IDisposable
{
    private readonly int _initialWidth;
    private readonly int _initialHeight;
    private readonly MpvHandle _handle;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<IntPtr> _swapChainReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Thread? _renderThread;
    private IDXGISwapChain1? _swapChain;
    private long _framesPresented;
    private long _loopIterations;

    public VideoPresenter(MpvHandle handle, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _handle = handle;
        _initialWidth = width;
        _initialHeight = height;
    }

    /// <summary>Which path startup actually selected.</summary>
    public VideoBackend Backend { get; private set; } = VideoBackend.None;

    /// <summary>Why the hardware path was rejected, when it was.</summary>
    public string? BackendDetail { get; private set; }

    /// <summary>Frames presented since start, for diagnostics.</summary>
    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>
    /// Whatever killed the render thread, or null while it is healthy.
    /// </summary>
    /// <remarks>
    /// A dead render thread and an idle one look identical from outside: no frames, no
    /// error. This is the difference.
    /// </remarks>
    public Exception? Fault { get; private set; }

    /// <summary>Raised on the render thread when it dies.</summary>
    public event EventHandler<Exception>? RenderThreadFaulted;

    /// <summary>Iterations of the present loop, whether or not a frame was ready.</summary>
    /// <remarks>
    /// Distinguishes "the loop is running and mpv has nothing" from "the loop is not
    /// running", which <see cref="FramesPresented"/> alone cannot.
    /// </remarks>
    public long LoopIterations => Interlocked.Read(ref _loopIterations);

    /// <summary>
    /// Starts the render thread and completes once the swap chain exists.
    /// </summary>
    /// <returns>
    /// The native <c>IDXGISwapChain1</c> pointer, to hand to
    /// <c>ISwapChainPanelNative.SetSwapChain</c> on the UI thread.
    /// </returns>
    public Task<IntPtr> StartAsync()
    {
        _renderThread = new Thread(RenderLoop)
        {
            Name = "mpv-render",
            IsBackground = true,
        };

        _renderThread.Start();
        return _swapChainReady.Task;
    }

    private void RenderLoop()
    {
        WglContext? glContext = null;
        MpvOpenGlRenderer? renderer = null;
        SharedVideoTarget? target = null;

        try
        {
            glContext = WglContext.Create();
            glContext.MakeCurrent();

            var capabilities = glContext.Query();
            if (!capabilities.SupportsHardwarePath)
            {
                // Not a failure. Decision 0002 chose software rendering as the automatic
                // fallback precisely for this machine, and the caller needs to know which
                // path it got rather than discovering it from the frame rate.
                Backend = VideoBackend.Software;
                BackendDetail =
                    $"{capabilities.Renderer}, GL {capabilities.Version}, " +
                    $"NV_DX_interop2={capabilities.HasDxInterop}";

                _swapChainReady.TrySetException(new NotSupportedException(
                    $"This driver cannot present through a shared texture ({BackendDetail}). " +
                    $"Software rendering is required."));
                return;
            }

            renderer = MpvOpenGlRenderer.Create(_handle);
            target = SharedVideoTarget.Create(_initialWidth, _initialHeight);

            _swapChain = CreateSwapChain(target, _initialWidth, _initialHeight);

            Backend = VideoBackend.Hardware;
            BackendDetail = $"{capabilities.Renderer}, GL {capabilities.Version}";

            _swapChainReady.TrySetResult(_swapChain.NativePointer);

            Present(renderer, target, _swapChain);
        }
        catch (Exception exception)
        {
            // TrySetException is a no-op once the task has completed, so a failure inside
            // Present would otherwise vanish entirely: the render thread dies, frames stop,
            // and nothing anywhere reports why. Recording it separately is what makes a
            // silent black screen diagnosable.
            Fault = exception;
            RenderThreadFaulted?.Invoke(this, exception);
            _swapChainReady.TrySetException(exception);
        }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            _swapChain?.Dispose();
            _swapChain = null;

            WglContext.ClearCurrent();
            glContext?.Dispose();
        }
    }

    private void Present(
        MpvOpenGlRenderer renderer,
        SharedVideoTarget target,
        IDXGISwapChain1 swapChain)
    {
        var token = _shutdown.Token;

        while (!token.IsCancellationRequested)
        {
            Interlocked.Increment(ref _loopIterations);

            if (!renderer.HasFrameReady())
            {
                // mpv has nothing new. Sleeping briefly rather than spinning keeps a core
                // free; the render callback will have work again within a frame interval.
                Thread.Sleep(1);
                continue;
            }

            target.RenderFrame(renderer);

            using var backBuffer = swapChain.GetBuffer<Vortice.Direct3D11.ID3D11Texture2D>(0);
            target.DeviceContext.CopyResource(backBuffer, target.Texture);

            // Present with vsync. The frame is already on the GPU, so this is a flip
            // rather than a copy.
            swapChain.Present(1, PresentFlags.None);
            Interlocked.Increment(ref _framesPresented);
        }
    }

    private static IDXGISwapChain1 CreateSwapChain(SharedVideoTarget target, int width, int height)
    {
        using var dxgiDevice = target.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,

            // FlipSequential is required for composition; the older bit-block models
            // cannot be attached to a SwapChainPanel at all.
            SwapEffect = SwapEffect.FlipSequential,

            // The video is opaque and XAML draws over it. Premultiplied would let the
            // panel blend the video itself, which is not wanted and costs fill rate.
            AlphaMode = AlphaMode.Ignore,
        };

        return factory.CreateSwapChainForComposition(target.Device, description);
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        // The render thread owns the GL context, the mpv render context and the swap
        // chain, and all three must be released on it. Joining rather than abandoning is
        // what makes that ordering hold.
        if (_renderThread is not null && _renderThread.IsAlive)
        {
            _renderThread.Join(TimeSpan.FromSeconds(5));
        }

        _shutdown.Dispose();
    }
}
