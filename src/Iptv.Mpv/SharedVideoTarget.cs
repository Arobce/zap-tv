using Iptv.Mpv.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Iptv.Mpv;

/// <summary>
/// A D3D11 texture that OpenGL renders into, shared through <c>WGL_NV_DX_interop2</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the join between the two halves of the presentation path. mpv renders through
/// OpenGL; XAML composites through DXGI. The extension registers a D3D11 texture as a GL
/// renderbuffer so both refer to the same memory, with no copy and no readback per frame.
/// </para>
/// <para>
/// Everything here must run on the thread holding the GL context. The lock/unlock pair
/// around rendering is not optional bookkeeping: rendering into an unlocked object usually
/// appears to work and produces intermittent tearing or a stale frame, which reads as a
/// timing bug rather than a missing lock.
/// </para>
/// </remarks>
public sealed class SharedVideoTarget : IDisposable
{
    private readonly GlFunctions _gl;
    private readonly WglDxInterop _interop;
    private readonly IntPtr _interopDevice;
    private readonly IntPtr[] _registered;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private ID3D11Texture2D? _texture;
    private uint _renderbuffer;
    private uint _framebuffer;
    private bool _disposed;

    private SharedVideoTarget(
        ID3D11Device device,
        ID3D11DeviceContext deviceContext,
        ID3D11Texture2D texture,
        GlFunctions gl,
        WglDxInterop interop,
        IntPtr interopDevice,
        IntPtr registeredObject,
        uint renderbuffer,
        uint framebuffer,
        int width,
        int height)
    {
        _device = device;
        _deviceContext = deviceContext;
        _texture = texture;
        _gl = gl;
        _interop = interop;
        _interopDevice = interopDevice;
        _registered = [registeredObject];
        _renderbuffer = renderbuffer;
        _framebuffer = framebuffer;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The GL framebuffer to hand to <see cref="MpvOpenGlRenderer.Render"/>.</summary>
    public int Framebuffer => (int)_framebuffer;

    /// <summary>The shared texture, for presentation through DXGI.</summary>
    public ID3D11Texture2D Texture =>
        _texture ?? throw new ObjectDisposedException(nameof(SharedVideoTarget));

    /// <summary>
    /// The D3D11 device backing the shared texture.
    /// </summary>
    /// <remarks>
    /// The swap chain must be created on this same device. A swap chain on a different
    /// device cannot receive a copy from this texture, and the failure is a cryptic
    /// E_INVALIDARG from CopyResource rather than anything naming the mismatch.
    /// </remarks>
    public ID3D11Device Device =>
        _device ?? throw new ObjectDisposedException(nameof(SharedVideoTarget));

    /// <summary>The immediate context for this device.</summary>
    public ID3D11DeviceContext DeviceContext =>
        _deviceContext ?? throw new ObjectDisposedException(nameof(SharedVideoTarget));

    /// <summary>
    /// Creates a shared target of the given size.
    /// </summary>
    /// <remarks>
    /// Requires a current GL context on the calling thread. Throws
    /// <see cref="NotSupportedException"/> when the driver lacks the extension, which is
    /// the caller's signal to fall back to software rendering rather than fail.
    /// </remarks>
    public static SharedVideoTarget Create(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var gl = GlFunctions.TryResolve()
            ?? throw new NotSupportedException(
                "This driver does not expose OpenGL framebuffer objects, so mpv cannot render " +
                "to a shared texture. Use software rendering.");

        var interop = WglDxInterop.TryResolve()
            ?? throw new NotSupportedException(
                "This driver does not expose WGL_NV_DX_interop2, so an OpenGL frame cannot be " +
                "shared with Direct3D without a full readback. Use software rendering.");

        // BGRA is what both the swap chain and mpv's output expect; a mismatch here shows
        // up as swapped colour channels rather than an error.
        // Explicitly typed locals: the overload taking (out device, out context) is
        // ambiguous with the one taking (out device, out featureLevel) when both are var.
        FeatureLevel[] featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        ID3D11Device device;
        ID3D11DeviceContext deviceContext;

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out device,
            out deviceContext).CheckError();

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };

        var texture = device.CreateTexture2D(description);

        var interopDevice = interop.OpenDevice(device.NativePointer);
        if (interopDevice == IntPtr.Zero)
        {
            texture.Dispose();
            deviceContext.Dispose();
            device.Dispose();
            throw new NotSupportedException(
                "wglDXOpenDeviceNV failed for this D3D11 device. The GL and D3D devices must be " +
                "on the same adapter.");
        }

        var renderbuffer = gl.GenRenderbuffer();
        var registered = interop.RegisterObject(
            interopDevice,
            texture.NativePointer,
            renderbuffer,
            GlFunctions.Renderbuffer,
            WglDxInterop.AccessWriteDiscard);

        if (registered == IntPtr.Zero)
        {
            gl.DeleteRenderbuffer(renderbuffer);
            interop.CloseDevice(interopDevice);
            texture.Dispose();
            deviceContext.Dispose();
            device.Dispose();
            throw new NotSupportedException("wglDXRegisterObjectNV failed for the shared texture.");
        }

        var framebuffer = gl.GenFramebuffer();

        // The renderbuffer has no storage until GL owns the shared object. Attaching and
        // validating outside a lock reports GL_FRAMEBUFFER_INCOMPLETE_ATTACHMENT for a
        // registration that is in fact perfectly good - the first thing this code did
        // wrong, and a failure that looks like an unsupported driver rather than a
        // sequencing mistake.
        IntPtr[] objects = [registered];
        var locked = interop.LockObjects(interopDevice, objects);

        var complete = false;
        if (locked)
        {
            gl.BindFramebuffer(framebuffer);
            gl.AttachRenderbuffer(renderbuffer);
            complete = gl.IsFramebufferComplete();
            gl.BindFramebuffer(0);
            interop.UnlockObjects(interopDevice, objects);
        }

        if (!complete)
        {
            gl.DeleteFramebuffer(framebuffer);
            interop.UnregisterObject(interopDevice, registered);
            gl.DeleteRenderbuffer(renderbuffer);
            interop.CloseDevice(interopDevice);
            texture.Dispose();
            deviceContext.Dispose();
            device.Dispose();
            throw new NotSupportedException(
                locked
                    ? "The shared framebuffer is incomplete even with the object locked, so this " +
                      "driver cannot back a GL renderbuffer with a D3D11 texture."
                    : "wglDXLockObjectsNV failed during setup, so the shared object could not be " +
                      "validated.");
        }

        return new SharedVideoTarget(
            device, deviceContext, texture, gl, interop, interopDevice,
            registered, renderbuffer, framebuffer, width, height);
    }

    /// <summary>
    /// Renders one mpv frame into the shared texture.
    /// </summary>
    /// <remarks>
    /// The lock is held for the shortest possible span. While GL owns the object, D3D
    /// cannot present it, so holding the lock across anything slow stalls the compositor.
    /// </remarks>
    public void RenderFrame(MpvOpenGlRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_interop.LockObjects(_interopDevice, _registered))
        {
            throw new InvalidOperationException(
                "wglDXLockObjectsNV failed; the shared texture could not be acquired for GL.");
        }

        try
        {
            // flipY: GL renders bottom-up, D3D and XAML expect top-down. Without this the
            // picture is perfect and upside down, which is easy to misdiagnose as a driver
            // fault rather than an orientation convention.
            renderer.Render(Framebuffer, Width, Height, flipY: true);
        }
        finally
        {
            _interop.UnlockObjects(_interopDevice, _registered);
        }
    }

    /// <summary>
    /// Copies the shared texture back to system memory.
    /// </summary>
    /// <remarks>
    /// Diagnostics and tests only. A per-frame readback defeats the entire point of
    /// sharing the texture; it exists so a test can assert that real pixels arrived in the
    /// D3D resource rather than trusting that the calls returned success.
    /// </remarks>
    public byte[] ReadBack()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        try
        {
            _deviceContext!.CopyResource(staging, _texture!);
            var mapped = _deviceContext.Map(staging, 0, MapMode.Read);

            try
            {
                var pixels = new byte[Width * Height * 4];
                for (var row = 0; row < Height; row++)
                {
                    unsafe
                    {
                        var source = (byte*)mapped.DataPointer + (row * (int)mapped.RowPitch);
                        new ReadOnlySpan<byte>(source, Width * 4)
                            .CopyTo(pixels.AsSpan(row * Width * 4));
                    }
                }

                return pixels;
            }
            finally
            {
                _deviceContext.Unmap(staging, 0);
            }
        }
        finally
        {
            staging.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Reverse order of acquisition. Closing the interop device before unregistering
        // the object leaks the registration inside the driver, which survives a short run.
        if (_framebuffer != 0)
        {
            _gl.DeleteFramebuffer(_framebuffer);
            _framebuffer = 0;
        }

        if (_registered[0] != IntPtr.Zero)
        {
            _interop.UnregisterObject(_interopDevice, _registered[0]);
            _registered[0] = IntPtr.Zero;
        }

        if (_renderbuffer != 0)
        {
            _gl.DeleteRenderbuffer(_renderbuffer);
            _renderbuffer = 0;
        }

        if (_interopDevice != IntPtr.Zero)
        {
            _interop.CloseDevice(_interopDevice);
        }

        _texture?.Dispose();
        _texture = null;
        _deviceContext?.Dispose();
        _deviceContext = null;
        _device?.Dispose();
        _device = null;
    }
}
