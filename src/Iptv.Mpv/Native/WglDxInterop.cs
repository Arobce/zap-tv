using System.Runtime.InteropServices;

namespace Iptv.Mpv.Native;

/// <summary>
/// <c>WGL_NV_DX_interop2</c> entry points, resolved at runtime.
/// </summary>
/// <remarks>
/// <para>
/// These are driver extensions, not exports of <c>opengl32.dll</c>, so they must be
/// resolved through <c>wglGetProcAddress</c> on a thread holding a current GL context.
/// There is nothing to link against and no import to fail at load time; absence shows up
/// as a null pointer, which is why <see cref="IsSupported"/> exists.
/// </para>
/// <para>
/// The extension lets a D3D11 texture be registered as a GL object. mpv then renders into
/// it as an ordinary framebuffer, and DXGI presents the same memory - no copy, no readback.
/// </para>
/// </remarks>
internal sealed class WglDxInterop
{
    /// <summary>Access flag: GL writes, D3D reads.</summary>
    internal const uint AccessWriteDiscard = 0x0002;

    private readonly OpenDeviceDelegate _openDevice;
    private readonly CloseDeviceDelegate _closeDevice;
    private readonly RegisterObjectDelegate _registerObject;
    private readonly UnregisterObjectDelegate _unregisterObject;
    private readonly LockObjectsDelegate _lockObjects;
    private readonly UnlockObjectsDelegate _unlockObjects;

    private WglDxInterop(
        OpenDeviceDelegate openDevice,
        CloseDeviceDelegate closeDevice,
        RegisterObjectDelegate registerObject,
        UnregisterObjectDelegate unregisterObject,
        LockObjectsDelegate lockObjects,
        UnlockObjectsDelegate unlockObjects)
    {
        _openDevice = openDevice;
        _closeDevice = closeDevice;
        _registerObject = registerObject;
        _unregisterObject = unregisterObject;
        _lockObjects = lockObjects;
        _unlockObjects = unlockObjects;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr OpenDeviceDelegate(IntPtr dxDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool CloseDeviceDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr RegisterObjectDelegate(
        IntPtr device, IntPtr dxObject, uint name, uint type, uint access);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UnregisterObjectDelegate(IntPtr device, IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool LockObjectsDelegate(IntPtr device, int count, IntPtr[] objects);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UnlockObjectsDelegate(IntPtr device, int count, IntPtr[] objects);

    /// <summary>Whether the current context's driver exposes the extension.</summary>
    internal static bool IsSupported =>
        WglContext.GetProcAddress("wglDXOpenDeviceNV") != IntPtr.Zero &&
        WglContext.GetProcAddress("wglDXRegisterObjectNV") != IntPtr.Zero &&
        WglContext.GetProcAddress("wglDXLockObjectsNV") != IntPtr.Zero;

    /// <summary>Resolves the entry points, or returns null when unsupported.</summary>
    /// <remarks>Must be called on a thread with a current GL context.</remarks>
    internal static WglDxInterop? TryResolve()
    {
        var open = Resolve<OpenDeviceDelegate>("wglDXOpenDeviceNV");
        var close = Resolve<CloseDeviceDelegate>("wglDXCloseDeviceNV");
        var register = Resolve<RegisterObjectDelegate>("wglDXRegisterObjectNV");
        var unregister = Resolve<UnregisterObjectDelegate>("wglDXUnregisterObjectNV");
        var lockObjects = Resolve<LockObjectsDelegate>("wglDXLockObjectsNV");
        var unlockObjects = Resolve<UnlockObjectsDelegate>("wglDXUnlockObjectsNV");

        if (open is null || close is null || register is null ||
            unregister is null || lockObjects is null || unlockObjects is null)
        {
            return null;
        }

        return new WglDxInterop(open, close, register, unregister, lockObjects, unlockObjects);
    }

    internal IntPtr OpenDevice(IntPtr d3dDevice) => _openDevice(d3dDevice);

    internal bool CloseDevice(IntPtr device) => _closeDevice(device);

    /// <summary>Registers a D3D11 texture as the backing store of a GL object.</summary>
    /// <param name="type">GL target, e.g. GL_TEXTURE_2D (0x0DE1) or GL_RENDERBUFFER (0x8D41).</param>
    internal IntPtr RegisterObject(IntPtr device, IntPtr dxObject, uint glName, uint type, uint access)
        => _registerObject(device, dxObject, glName, type, access);

    internal bool UnregisterObject(IntPtr device, IntPtr obj) => _unregisterObject(device, obj);

    /// <summary>
    /// Takes ownership of the shared object for GL.
    /// </summary>
    /// <remarks>
    /// Rendering into an unlocked object is undefined: it usually appears to work and
    /// produces intermittent tearing or a stale frame, which reads as a timing bug rather
    /// than a missing lock.
    /// </remarks>
    internal bool LockObjects(IntPtr device, IntPtr[] objects)
        => _lockObjects(device, objects.Length, objects);

    /// <summary>Returns ownership to D3D so the texture can be presented.</summary>
    internal bool UnlockObjects(IntPtr device, IntPtr[] objects)
        => _unlockObjects(device, objects.Length, objects);

    private static TDelegate? Resolve<TDelegate>(string name)
        where TDelegate : Delegate
    {
        var address = WglContext.GetProcAddress(name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }
}
