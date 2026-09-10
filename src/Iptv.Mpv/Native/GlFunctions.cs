using System.Runtime.InteropServices;

namespace Iptv.Mpv.Native;

/// <summary>
/// The framebuffer-object subset of OpenGL, resolved at runtime.
/// </summary>
/// <remarks>
/// FBO entry points are GL 3.0, so they are not exports of <c>opengl32.dll</c> - Windows
/// ships GL 1.1 headers and imports only. They must be resolved through
/// <c>wglGetProcAddress</c> on a thread holding a current context, which is also why this
/// is an instance resolved per context rather than a static P/Invoke class.
/// </remarks>
internal sealed class GlFunctions
{
    internal const uint Framebuffer = 0x8D40;
    internal const uint Renderbuffer = 0x8D41;
    internal const uint ColorAttachment0 = 0x8CE0;
    internal const uint FramebufferComplete = 0x8CD5;
    internal const uint Texture2D = 0x0DE1;
    internal const uint Bgra = 0x80E1;
    internal const uint UnsignedByte = 0x1401;

    private readonly GenDelegate _genFramebuffers;
    private readonly GenDelegate _genRenderbuffers;
    private readonly DeleteDelegate _deleteFramebuffers;
    private readonly DeleteDelegate _deleteRenderbuffers;
    private readonly BindDelegate _bindFramebuffer;
    private readonly FramebufferRenderbufferDelegate _framebufferRenderbuffer;
    private readonly CheckFramebufferStatusDelegate _checkFramebufferStatus;
    private readonly ReadPixelsDelegate _readPixels;
    private readonly FinishDelegate _finish;

    private GlFunctions(
        GenDelegate genFramebuffers,
        GenDelegate genRenderbuffers,
        DeleteDelegate deleteFramebuffers,
        DeleteDelegate deleteRenderbuffers,
        BindDelegate bindFramebuffer,
        FramebufferRenderbufferDelegate framebufferRenderbuffer,
        CheckFramebufferStatusDelegate checkFramebufferStatus,
        ReadPixelsDelegate readPixels,
        FinishDelegate finish)
    {
        _genFramebuffers = genFramebuffers;
        _genRenderbuffers = genRenderbuffers;
        _deleteFramebuffers = deleteFramebuffers;
        _deleteRenderbuffers = deleteRenderbuffers;
        _bindFramebuffer = bindFramebuffer;
        _framebufferRenderbuffer = framebufferRenderbuffer;
        _checkFramebufferStatus = checkFramebufferStatus;
        _readPixels = readPixels;
        _finish = finish;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GenDelegate(int count, out uint names);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DeleteDelegate(int count, ref uint names);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BindDelegate(uint target, uint name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FramebufferRenderbufferDelegate(
        uint target, uint attachment, uint renderbufferTarget, uint renderbuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint CheckFramebufferStatusDelegate(uint target);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReadPixelsDelegate(
        int x, int y, int width, int height, uint format, uint type, IntPtr pixels);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FinishDelegate();

    /// <summary>Resolves the entry points, or returns null when the driver lacks FBO support.</summary>
    internal static GlFunctions? TryResolve()
    {
        var genFramebuffers = Resolve<GenDelegate>("glGenFramebuffers");
        var genRenderbuffers = Resolve<GenDelegate>("glGenRenderbuffers");
        var deleteFramebuffers = Resolve<DeleteDelegate>("glDeleteFramebuffers");
        var deleteRenderbuffers = Resolve<DeleteDelegate>("glDeleteRenderbuffers");
        var bindFramebuffer = Resolve<BindDelegate>("glBindFramebuffer");
        var framebufferRenderbuffer = Resolve<FramebufferRenderbufferDelegate>("glFramebufferRenderbuffer");
        var checkStatus = Resolve<CheckFramebufferStatusDelegate>("glCheckFramebufferStatus");

        // glReadPixels and glFinish are GL 1.1, so they come from opengl32.dll itself;
        // WglContext.GetProcAddress already tries both sources.
        var readPixels = Resolve<ReadPixelsDelegate>("glReadPixels");
        var finish = Resolve<FinishDelegate>("glFinish");

        if (genFramebuffers is null || genRenderbuffers is null || deleteFramebuffers is null ||
            deleteRenderbuffers is null || bindFramebuffer is null ||
            framebufferRenderbuffer is null || checkStatus is null ||
            readPixels is null || finish is null)
        {
            return null;
        }

        return new GlFunctions(
            genFramebuffers, genRenderbuffers, deleteFramebuffers, deleteRenderbuffers,
            bindFramebuffer, framebufferRenderbuffer, checkStatus, readPixels, finish);
    }

    internal uint GenFramebuffer()
    {
        _genFramebuffers(1, out var name);
        return name;
    }

    internal uint GenRenderbuffer()
    {
        _genRenderbuffers(1, out var name);
        return name;
    }

    internal void DeleteFramebuffer(uint name) => _deleteFramebuffers(1, ref name);

    internal void DeleteRenderbuffer(uint name) => _deleteRenderbuffers(1, ref name);

    internal void BindFramebuffer(uint name) => _bindFramebuffer(Framebuffer, name);

    internal void AttachRenderbuffer(uint renderbuffer)
        => _framebufferRenderbuffer(Framebuffer, ColorAttachment0, Renderbuffer, renderbuffer);

    internal bool IsFramebufferComplete() => _checkFramebufferStatus(Framebuffer) == FramebufferComplete;

    internal void ReadPixels(int width, int height, IntPtr destination)
        => _readPixels(0, 0, width, height, Bgra, UnsignedByte, destination);

    internal void Finish() => _finish();

    private static TDelegate? Resolve<TDelegate>(string name)
        where TDelegate : Delegate
    {
        var address = WglContext.GetProcAddress(name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }
}
