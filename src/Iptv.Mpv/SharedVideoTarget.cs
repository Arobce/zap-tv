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
/// <para>
/// The D3D device outlives any individual texture. <see cref="Resize"/> replaces the
/// texture and its GL objects while keeping the device, because the swap chain is bound to
/// that device: recreating it would leave the swap chain unable to receive a copy, and the
/// failure is a cryptic <c>E_INVALIDARG</c> from <c>CopyResource</c> rather than anything
/// naming the mismatch.
/// </para>
/// </remarks>
public sealed class SharedVideoTarget : IDisposable
{
    private readonly GlFunctions _gl;
    private readonly WglDxInterop _interop;
    private readonly IntPtr _interopDevice;
    private readonly IntPtr[] _registered = [IntPtr.Zero];

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private ID3D11Texture2D? _texture;
    private uint _renderbuffer;
    private uint _framebuffer;
    private bool _disposed;

    private SharedVideoTarget(
        ID3D11Device device,
        ID3D11DeviceContext deviceContext,
        GlFunctions gl,
        WglDxInterop interop,
        IntPtr interopDevice)
    {
        _device = device;
        _deviceContext = deviceContext;
        _gl = gl;
        _interop = interop;
        _interopDevice = interopDevice;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The GL framebuffer to hand to <see cref="MpvOpenGlRenderer.Render"/>.</summary>
    public int Framebuffer => (int)_framebuffer;

    /// <summary>The shared texture, for presentation through DXGI.</summary>
    public ID3D11Texture2D Texture =>
        _texture ?? throw new ObjectDisposedException(nameof(SharedVideoTarget));

    /// <summary>
    /// The D3D11 device backing the shared texture.
    /// </summary>
    /// <remarks>
    /// The swap chain must be created on this same device, and it stays valid across a
    /// <see cref="Resize"/>.
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

        var interopDevice = interop.OpenDevice(device.NativePointer);
        if (interopDevice == IntPtr.Zero)
        {
            deviceContext.Dispose();
            device.Dispose();
            throw new NotSupportedException(
                "wglDXOpenDeviceNV failed for this D3D11 device. The GL and D3D devices must be " +
                "on the same adapter.");
        }

        var target = new SharedVideoTarget(device, deviceContext, gl, interop, interopDevice);

        try
        {
            target.AttachTexture(width, height);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Replaces the texture and its GL objects at a new size, keeping the D3D device.
    /// </summary>
    /// <remarks>
    /// Must run on the thread holding the GL context. A no-op when the size is unchanged,
    /// because a resize tears down and rebuilds a driver-side registration and window
    /// dragging would otherwise do that on every mouse move.
    /// </remarks>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width == Width && height == Height)
        {
            return;
        }

        DetachTexture();
        AttachTexture(width, height);
    }

    /// <summary>Creates the texture, registers it with GL, and builds the framebuffer.</summary>
    private void AttachTexture(int width, int height)
    {
        // BGRA is what both the swap chain and mpv's output expect; a mismatch here shows
        // up as swapped colour channels rather than an error.
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

        _texture = _device!.CreateTexture2D(description);
        _renderbuffer = _gl.GenRenderbuffer();

        _registered[0] = _interop.RegisterObject(
            _interopDevice,
            _texture.NativePointer,
            _renderbuffer,
            GlFunctions.Renderbuffer,
            WglDxInterop.AccessWriteDiscard);

        if (_registered[0] == IntPtr.Zero)
        {
            throw new NotSupportedException("wglDXRegisterObjectNV failed for the shared texture.");
        }

        _framebuffer = _gl.GenFramebuffer();

        // The renderbuffer has no storage until GL owns the shared object. Attaching and
        // validating outside a lock reports an incomplete attachment for a registration
        // that is in fact perfectly good, which looks like an unsupported driver rather
        // than a sequencing mistake.
        var locked = _interop.LockObjects(_interopDevice, _registered);

        var complete = false;
        if (locked)
        {
            _gl.BindFramebuffer(_framebuffer);
            _gl.AttachRenderbuffer(_renderbuffer);
            complete = _gl.IsFramebufferComplete();
            _gl.BindFramebuffer(0);
            _interop.UnlockObjects(_interopDevice, _registered);
        }

        if (!complete)
        {
            throw new NotSupportedException(
                locked
                    ? "The shared framebuffer is incomplete even with the object locked, so this " +
                      "driver cannot back a GL renderbuffer with a D3D11 texture."
                    : "wglDXLockObjectsNV failed during setup, so the shared object could not be " +
                      "validated.");
        }

        Width = width;
        Height = height;
    }

    /// <summary>Releases the texture and its GL objects, in reverse order of acquisition.</summary>
    /// <remarks>
    /// Unregistering before closing the interop device matters: the reverse order leaks the
    /// registration inside the driver, which a short run will not reveal.
    /// </remarks>
    private void DetachTexture()
    {
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

        _texture?.Dispose();
        _texture = null;
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
            // No flip. The reasoning that says otherwise is that GL's framebuffer origin
            // is bottom-left and D3D's is top-left, so the picture must need inverting -
            // and that reasoning is wrong here, because NV_DX_interop shares the texture's
            // memory rather than copying through either API's coordinate space. GL's first
            // row and D3D's first row are the same bytes, so mpv's unflipped output already
            // lands the right way up and FLIP_Y turns it over.
            //
            // Asserted rather than argued: VideoOrientationTests renders a red-over-blue
            // source and reads the texture back. This shipped upside down for weeks because
            // every check asked whether frames arrived, never which way up.
            renderer.Render(Framebuffer, Width, Height, flipY: false);
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

        DetachTexture();

        if (_interopDevice != IntPtr.Zero)
        {
            _interop.CloseDevice(_interopDevice);
        }

        _deviceContext?.Dispose();
        _deviceContext = null;
        _device?.Dispose();
        _device = null;
    }
}
