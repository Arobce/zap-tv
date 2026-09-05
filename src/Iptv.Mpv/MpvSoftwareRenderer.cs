using System.Runtime.InteropServices;
using Iptv.Mpv.Native;

namespace Iptv.Mpv;

/// <summary>
/// Renders mpv output into a CPU buffer via <c>MPV_RENDER_API_TYPE_SW</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diagnostic use only. Never ship this as the playback path.</b> It copies every frame
/// through system memory and will not hold 1080i. It exists because spike 0.1 established
/// that libmpv offers only <c>opengl</c> and <c>sw</c>, and this is the one that can be
/// exercised without a GPU context, a window, or ANGLE.
/// </para>
/// <para>
/// It earns its place by validating the render API marshalling - the NULL-terminated
/// parameter array, the enum values, the struct layout - independently of the compositing
/// work. When the ANGLE path misbehaves, this answers "is the interop wrong, or is the GL
/// context wrong?", which is otherwise an expensive question.
/// </para>
/// </remarks>
public sealed class MpvSoftwareRenderer : IDisposable
{
    /// <summary>Four bytes per pixel, BGRA order with an ignored alpha byte.</summary>
    private const string PixelFormat = "bgr0";

    private const int BytesPerPixel = 4;

    private IntPtr _context;

    private MpvSoftwareRenderer(IntPtr context) => _context = context;

    public bool IsDisposed => _context == IntPtr.Zero;

    /// <summary>Creates a software render context for an initialised handle.</summary>
    public static MpvSoftwareRenderer Create(MpvHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // The API type is a C string mpv reads during the call; it must stay alive for the
        // duration, which a marshalled parameter would not guarantee once the array is
        // built by hand.
        var apiType = Marshal.StringToCoTaskMemUTF8("sw");
        try
        {
            var parameters = new[]
            {
                new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = apiType },
                MpvRenderParam.Terminator,
            };

            var result = CreateContext(handle, parameters, out var context);
            if (result < 0)
            {
                throw new MpvException(
                    $"mpv_render_context_create failed: {MpvInterop.DescribeError(result)}.",
                    (MpvError)result);
            }

            return new MpvSoftwareRenderer(context);
        }
        finally
        {
            Marshal.FreeCoTaskMem(apiType);
        }
    }

    /// <summary>
    /// Renders the current frame into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">At least <c>width * height * 4</c> bytes.</param>
    /// <returns>The number of bytes written per row.</returns>
    public int Render(Span<byte> destination, int width, int height)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var stride = width * BytesPerPixel;
        if (destination.Length < stride * height)
        {
            throw new ArgumentException(
                $"Destination needs {stride * height} bytes for {width}x{height}, got {destination.Length}.",
                nameof(destination));
        }

        var format = Marshal.StringToCoTaskMemUTF8(PixelFormat);
        try
        {
            unsafe
            {
                // mpv writes through these pointers during the call, so every one has to
                // reference memory that outlives it. size[] and strideValue are locals
                // held on this stack frame; destination is pinned for the duration.
                var size = stackalloc int[2];
                size[0] = width;
                size[1] = height;

                nint strideValue = stride;

                fixed (byte* pixels = destination)
                {
                    var parameters = new[]
                    {
                        new MpvRenderParam { Type = MpvRenderParamType.SwSize, Data = (IntPtr)size },
                        new MpvRenderParam { Type = MpvRenderParamType.SwFormat, Data = format },
                        new MpvRenderParam { Type = MpvRenderParamType.SwStride, Data = (IntPtr)(&strideValue) },
                        new MpvRenderParam { Type = MpvRenderParamType.SwPointer, Data = (IntPtr)pixels },
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

            return stride;
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }
    }

    /// <summary>Whether mpv has a new frame to present.</summary>
    public bool HasFrameReady()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Bit 0 is MPV_RENDER_UPDATE_FRAME.
        return (MpvInterop.mpv_render_context_update(_context) & 1) != 0;
    }

    public void Dispose()
    {
        var context = Interlocked.Exchange(ref _context, IntPtr.Zero);
        if (context != IntPtr.Zero)
        {
            // Must be freed before the handle it was created from; the Phase 5 leak check
            // exists because getting this order wrong survives a short run and fails a
            // long one.
            MpvInterop.mpv_render_context_free(context);
        }
    }

    private static unsafe int CreateContext(
        MpvHandle handle,
        MpvRenderParam[] parameters,
        out IntPtr context)
    {
        fixed (MpvRenderParam* array = parameters)
        {
            return MpvInterop.mpv_render_context_create(out context, handle.RawHandle, (IntPtr)array);
        }
    }
}
