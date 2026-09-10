using System.Runtime.InteropServices;

namespace Iptv.Mpv.Native;

/// <summary>
/// The minimum Win32, GDI and WGL surface needed to create an OpenGL context.
/// </summary>
/// <remarks>
/// WGL requires a real window with a device context before a pixel format can be set, and
/// a pixel format before a GL context can exist. There is no headless path, so a hidden
/// window is created purely to own the context.
/// </remarks>
internal static partial class Win32
{
    internal const uint PfdDrawToWindow = 0x00000004;
    internal const uint PfdSupportOpenGl = 0x00000020;
    internal const uint PfdDoubleBuffer = 0x00000001;
    internal const byte PfdTypeRgba = 0;
    internal const byte PfdMainPlane = 0;

    internal const uint WsOverlapped = 0x00000000;
    internal const int CwUseDefault = unchecked((int)0x80000000);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelFormatDescriptor
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WndClassEx wndClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetModuleHandleW(IntPtr moduleName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr LoadLibraryW(string fileName);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GetProcAddress(IntPtr module, string procName);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor pfd);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor pfd);

    [LibraryImport("opengl32.dll", SetLastError = true)]
    internal static partial IntPtr wglCreateContext(IntPtr dc);

    [LibraryImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool wglMakeCurrent(IntPtr dc, IntPtr context);

    [LibraryImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool wglDeleteContext(IntPtr context);

    /// <summary>
    /// Resolves an extension function pointer from the current GL context.
    /// </summary>
    /// <remarks>
    /// Returns null for core GL 1.1 entry points, which live in <c>opengl32.dll</c>
    /// itself. Anything resolving GL functions must try both, which is precisely the
    /// caveat mpv's own header calls out for <c>get_proc_address</c>.
    /// </remarks>
    [LibraryImport("opengl32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr wglGetProcAddress(string name);

    [LibraryImport("opengl32.dll", EntryPoint = "glGetString")]
    internal static partial IntPtr GlGetString(uint name);

    internal const uint GlVendor = 0x1F00;
    internal const uint GlRenderer = 0x1F01;
    internal const uint GlVersion = 0x1F02;
}
