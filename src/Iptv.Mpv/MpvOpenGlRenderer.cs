using System.Runtime.InteropServices;
using Iptv.Mpv.Native;

namespace Iptv.Mpv;

/// <summary>
/// Renders mpv output through <c>MPV_RENDER_API_TYPE_OPENGL</c> into a GL framebuffer.
/// </summary>
/// <remarks>
/// <para>
/// This is the shipping path. Spike 0.1 established that OpenGL and software are the only
/// backends libmpv offers, and decision 0002 chose a native WGL context over bundling
/// ANGLE: <c>opengl32.dll</c> is part of Windows, so this costs no redistributable and no
/// supply-chain obligation.
/// </para>
/// <para>
/// The GL context belongs to one thread at a time, and every call here must happen on the
/// thread holding it. That is why the render loop owns a dedicated thread rather than
/// running on the thread pool.
/// </para>
/// </remarks>
public sealed class MpvOpenGlRenderer : IDisposable
{
    /// <summary>GL_RGBA8, the framebuffer format mpv is told to expect.</summary>
    private const int GlRgba8 = 0x8058;

    /// <summary>
    /// Held for the lifetime of the render context.
    /// </summary>
    /// <remarks>
    /// mpv stores the raw function pointer and calls it during initialisation and
    /// rendering. If the delegate were collected, mpv would call into freed memory - a
    /// crash that appears at a random later frame rather than at the point of the mistake.
    /// </remarks>
    private readonly GetProcAddressDelegate _resolver;

    private IntPtr _context;

    private MpvOpenGlRenderer(IntPtr context, GetProcAddressDelegate resolver)
    {
        _context = context;
        _resolver = resolver;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetProcAddressDelegate(IntPtr ctx, IntPtr name);

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvOpenGlInitParams
    {
        public IntPtr GetProcAddress;
        public IntPtr GetProcAddressContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvOpenGlFbo
    {
        public int Fbo;
        public int Width;
        public int Height;
        public int InternalFormat;
    }

    public bool IsDisposed => _context == IntPtr.Zero;

    /// <summary>
    /// Creates a render context bound to the GL context current on this thread.
    /// </summary>
    /// <remarks>
    /// The caller must have made a <see cref="WglContext"/> current first; mpv resolves GL
    /// entry points during this call and they are only available on a thread holding a
    /// context.
    /// </remarks>
    public static MpvOpenGlRenderer Create(MpvHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        GetProcAddressDelegate resolver = static (_, name) =>
        {
            var managed = Marshal.PtrToStringUTF8(name);
            return managed is null ? IntPtr.Zero : WglContext.GetProcAddress(managed);
        };

        var initParams = new MpvOpenGlInitParams
        {
            GetProcAddress = Marshal.GetFunctionPointerForDelegate(resolver),
            GetProcAddressContext = IntPtr.Zero,
        };

        var apiType = Marshal.StringToCoTaskMemUTF8("opengl");
        var initHandle = GCHandle.Alloc(initParams, GCHandleType.Pinned);

        try
        {
            var parameters = new[]
            {
                new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = apiType },
                new MpvRenderParam
                {
                    Type = MpvRenderParamType.OpenGlInitParams,
                    Data = initHandle.AddrOfPinnedObject(),
                },
                MpvRenderParam.Terminator,
            };

            int result;
            IntPtr context;
            unsafe
            {
                fixed (MpvRenderParam* array = parameters)
                {
                    result = MpvInterop.mpv_render_context_create(out context, handle.RawHandle, (IntPtr)array);
                }
            }

            if (result < 0)
            {
                throw new MpvException(
                    $"mpv_render_context_create(opengl) failed: {MpvInterop.DescribeError(result)}. " +
                    $"This usually means no GL context is current on this thread, or the driver " +
                    $"is the Windows software rasteriser.",
                    (MpvError)result);
            }

            // The resolver is captured into the returned instance so it outlives this
            // method for as long as mpv can call it.
            return new MpvOpenGlRenderer(context, resolver);
        }
        finally
        {
            initHandle.Free();
            Marshal.FreeCoTaskMem(apiType);
        }
    }

    /// <summary>
    /// Renders the current frame into a framebuffer object.
    /// </summary>
    /// <param name="framebuffer">An FBO name, or 0 for the default framebuffer.</param>
    /// <param name="flipY">
    /// True when the target has OpenGL's bottom-up orientation and the consumer expects
    /// top-down. Getting this wrong produces a picture that is upside down but otherwise
    /// perfect, which is easy to misread as a driver problem.
    /// </param>
    public void Render(int framebuffer, int width, int height, bool flipY = false)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var fbo = new MpvOpenGlFbo
        {
            Fbo = framebuffer,
            Width = width,
            Height = height,
            InternalFormat = GlRgba8,
        };

        var flip = flipY ? 1 : 0;

        unsafe
        {
            var parameters = new[]
            {
                new MpvRenderParam { Type = MpvRenderParamType.OpenGlFbo, Data = (IntPtr)(&fbo) },
                new MpvRenderParam { Type = MpvRenderParamType.FlipY, Data = (IntPtr)(&flip) },
                MpvRenderParam.Terminator,
            };

            fixed (MpvRenderParam* array = parameters)
            {
                var result = MpvInterop.mpv_render_context_render(_context, (IntPtr)array);
                if (result < 0)
                {
                    throw new MpvException(
                        $"mpv_render_context_render failed: {MpvInterop.DescribeError(result)}.",
                        (MpvError)result);
                }
            }
        }
    }

    /// <summary>Whether mpv has a new frame to present.</summary>
    public bool HasFrameReady()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return (MpvInterop.mpv_render_context_update(_context) & 1) != 0;
    }

    public void Dispose()
    {
        var context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (context == IntPtr.Zero)
        {
            return;
        }

        // Must be freed on the thread holding the GL context, and before the mpv handle
        // it was created from. Both orderings survive a short run and fail a long one,
        // which is what the Phase 5 leak criterion is about.
        MpvInterop.mpv_render_context_free(context);

        GC.KeepAlive(_resolver);
    }
}
