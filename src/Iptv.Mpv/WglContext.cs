using System.Runtime.InteropServices;
using Iptv.Mpv.Native;

namespace Iptv.Mpv;

/// <summary>
/// An OpenGL context created through WGL on a hidden window.
/// </summary>
/// <remarks>
/// <para>
/// WGL has no headless path: a pixel format needs a device context, a device context needs
/// a window, and a GL context needs a pixel format. The window exists only to own the
/// context and is never shown.
/// </para>
/// <para>
/// The context comes from <c>opengl32.dll</c>, which is part of Windows, so this adds no
/// redistributable. What it does depend on is a vendor driver: with no GPU driver
/// installed, Windows provides a software OpenGL 1.1 implementation that mpv cannot use.
/// <see cref="Capabilities"/> reports that so the caller can fall back to software
/// rendering rather than failing.
/// </para>
/// </remarks>
public sealed class WglContext : IDisposable
{
    private const string WindowClassName = "IptvPlayerGlHost";

    private static readonly object ClassGate = new();
    private static bool _classRegistered;

    private IntPtr _window;
    private IntPtr _deviceContext;
    private IntPtr _glContext;

    private WglContext(IntPtr window, IntPtr deviceContext, IntPtr glContext)
    {
        _window = window;
        _deviceContext = deviceContext;
        _glContext = glContext;
    }

    public bool IsDisposed => _glContext == IntPtr.Zero;

    /// <summary>What the driver on this machine actually supports.</summary>
    /// <param name="Vendor">GL_VENDOR.</param>
    /// <param name="Renderer">GL_RENDERER. "GDI Generic" means no vendor driver.</param>
    /// <param name="Version">GL_VERSION. 1.1 means the software fallback.</param>
    /// <param name="HasDxInterop">Whether WGL_NV_DX_interop2 is available.</param>
    public readonly record struct Capabilities(
        string Vendor,
        string Renderer,
        string Version,
        bool HasDxInterop)
    {
        /// <summary>
        /// Whether this context can drive mpv's OpenGL renderer.
        /// </summary>
        /// <remarks>
        /// Requires both a real driver and the D3D11 sharing extension. Without the
        /// extension the frame cannot reach a SwapChainPanel without a full readback,
        /// which is the software path with extra steps.
        /// </remarks>
        public bool SupportsHardwarePath =>
            HasDxInterop &&
            !Version.StartsWith("1.1", StringComparison.Ordinal) &&
            !Renderer.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates a context, or throws if WGL rejects the request.</summary>
    public static WglContext Create()
    {
        EnsureWindowClass();

        var window = Win32.CreateWindowExW(
            0, WindowClassName, "IptvPlayer GL host", Win32.WsOverlapped,
            Win32.CwUseDefault, Win32.CwUseDefault, 1, 1,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);

        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed for the GL host window (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var deviceContext = IntPtr.Zero;
        var glContext = IntPtr.Zero;

        try
        {
            deviceContext = Win32.GetDC(window);
            if (deviceContext == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetDC failed for the GL host window.");
            }

            var descriptor = new Win32.PixelFormatDescriptor
            {
                nSize = (ushort)Marshal.SizeOf<Win32.PixelFormatDescriptor>(),
                nVersion = 1,
                dwFlags = Win32.PfdDrawToWindow | Win32.PfdSupportOpenGl | Win32.PfdDoubleBuffer,
                iPixelType = Win32.PfdTypeRgba,
                cColorBits = 32,
                cAlphaBits = 8,
                cDepthBits = 24,
                cStencilBits = 8,
                iLayerType = Win32.PfdMainPlane,
            };

            var format = Win32.ChoosePixelFormat(deviceContext, ref descriptor);
            if (format == 0 || !Win32.SetPixelFormat(deviceContext, format, ref descriptor))
            {
                throw new InvalidOperationException(
                    $"No suitable OpenGL pixel format (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            glContext = Win32.wglCreateContext(deviceContext);
            if (glContext == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"wglCreateContext failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            return new WglContext(window, deviceContext, glContext);
        }
        catch
        {
            if (glContext != IntPtr.Zero)
            {
                Win32.wglDeleteContext(glContext);
            }

            if (deviceContext != IntPtr.Zero)
            {
                Win32.ReleaseDC(window, deviceContext);
            }

            Win32.DestroyWindow(window);
            throw;
        }
    }

    /// <summary>Makes this context current on the calling thread.</summary>
    /// <remarks>
    /// A GL context belongs to one thread at a time. mpv's render calls must happen on
    /// whichever thread holds it, which is why the render loop owns a dedicated thread.
    /// </remarks>
    public void MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if (!Win32.wglMakeCurrent(_deviceContext, _glContext))
        {
            throw new InvalidOperationException(
                $"wglMakeCurrent failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>Releases the context from the calling thread.</summary>
    public static void ClearCurrent() => Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

    /// <summary>Queries what this machine's driver supports. Requires the context to be current.</summary>
    public Capabilities Query()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        return new Capabilities(
            GetString(Win32.GlVendor),
            GetString(Win32.GlRenderer),
            GetString(Win32.GlVersion),
            HasExtension("wglDXOpenDeviceNV") && HasExtension("wglDXRegisterObjectNV"));
    }

    /// <summary>
    /// Resolves a GL or WGL entry point, for mpv's <c>get_proc_address</c>.
    /// </summary>
    /// <remarks>
    /// Both sources are required. <c>wglGetProcAddress</c> returns null for core GL 1.1
    /// functions, which live as ordinary exports in <c>opengl32.dll</c>; mpv's own header
    /// calls out exactly this and expects the caller to compensate.
    /// </remarks>
    public static IntPtr GetProcAddress(string name)
    {
        var address = Win32.wglGetProcAddress(name);
        if (address != IntPtr.Zero && !IsSentinel(address))
        {
            return address;
        }

        var module = Win32.LoadLibraryW("opengl32.dll");
        return module == IntPtr.Zero ? IntPtr.Zero : Win32.GetProcAddress(module, name);
    }

    /// <summary>
    /// Some drivers return 1, 2, 3 or -1 to mean "not supported" rather than null.
    /// </summary>
    private static bool IsSentinel(IntPtr address)
    {
        var value = address.ToInt64();
        return value is 1 or 2 or 3 or -1;
    }

    private static bool HasExtension(string entryPoint) => GetProcAddress(entryPoint) != IntPtr.Zero;

    private static string GetString(uint name)
    {
        var pointer = Win32.GlGetString(name);
        return pointer == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(pointer) ?? "unknown";
    }

    private static void EnsureWindowClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered)
            {
                return;
            }

            var wndClass = new Win32.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                hInstance = Win32.GetModuleHandleW(IntPtr.Zero),
                lpszClassName = Marshal.StringToHGlobalUni(WindowClassName),
            };

            if (Win32.RegisterClassExW(ref wndClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();

                // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is fine on a second call.
                if (error != 1410)
                {
                    throw new InvalidOperationException(
                        $"RegisterClassEx failed for the GL host window (Win32 error {error}).");
                }
            }

            _classRegistered = true;
        }
    }

    /// <summary>
    /// Kept alive for the process lifetime.
    /// </summary>
    /// <remarks>
    /// The window class holds a raw function pointer to this delegate. If it were
    /// collected, any message to the hidden window would jump into freed memory.
    /// </remarks>
    private static readonly WindowProc WindowProcedure = Win32.DefWindowProcW;

    private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void Dispose()
    {
        var glContext = Interlocked.Exchange(ref _glContext, IntPtr.Zero);
        if (glContext == IntPtr.Zero)
        {
            return;
        }

        Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        Win32.wglDeleteContext(glContext);

        if (_deviceContext != IntPtr.Zero)
        {
            Win32.ReleaseDC(_window, _deviceContext);
            _deviceContext = IntPtr.Zero;
        }

        if (_window != IntPtr.Zero)
        {
            Win32.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }
}
