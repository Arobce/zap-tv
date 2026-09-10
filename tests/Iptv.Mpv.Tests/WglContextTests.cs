using Iptv.Mpv;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Establishes whether this machine can drive the hardware presentation path.
/// </summary>
/// <remarks>
/// Decision 0002 chose native WGL plus WGL_NV_DX_interop2, with software rendering as an
/// automatic fallback. The whole design rests on the extension being present on real
/// hardware, so it is reported explicitly rather than assumed: a machine with no vendor
/// driver gets OpenGL 1.1 from Windows, which mpv cannot use.
/// </remarks>
public sealed class WglContextTests
{
    private readonly ITestOutputHelper _output;

    public WglContextTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void Creates_a_context_and_reports_what_the_driver_supports()
    {
        Requires.OpenGl();

        using var context = WglContext.Create();
        context.MakeCurrent();

        var capabilities = context.Query();

        _output.WriteLine($"vendor           {capabilities.Vendor}");
        _output.WriteLine($"renderer         {capabilities.Renderer}");
        _output.WriteLine($"version          {capabilities.Version}");
        _output.WriteLine($"NV_DX_interop2   {capabilities.HasDxInterop}");
        _output.WriteLine($"hardware path    {capabilities.SupportsHardwarePath}");

        WglContext.ClearCurrent();

        // Not asserted as true. A CI runner or a driverless VM legitimately fails this,
        // and the app's answer there is the software fallback, not a crash. What is
        // asserted is that the query works at all and returns something meaningful.
        Assert.NotEqual("unknown", capabilities.Version);
    }

    [SkippableFact]
    public void Resolves_core_gl_functions_that_wglGetProcAddress_does_not_return()
    {
        Requires.OpenGl();

        using var context = WglContext.Create();
        context.MakeCurrent();

        try
        {
            // glGetString is a GL 1.1 export. wglGetProcAddress returns null for these on
            // most drivers, so a resolver that only asks WGL hands mpv a null pointer for
            // a function that plainly exists. mpv's own header warns about this.
            Assert.NotEqual(IntPtr.Zero, WglContext.GetProcAddress("glGetString"));
        }
        finally
        {
            WglContext.ClearCurrent();
        }
    }

    [SkippableFact]
    public void Resolves_nothing_for_a_function_that_does_not_exist()
    {
        Requires.OpenGl();

        using var context = WglContext.Create();
        context.MakeCurrent();

        try
        {
            // Guards the sentinel handling: some drivers return 1, 2, 3 or -1 rather than
            // null for an unsupported entry point, and treating those as valid pointers
            // means calling into address 0x1.
            Assert.Equal(IntPtr.Zero, WglContext.GetProcAddress("glDefinitelyNotARealFunction"));
        }
        finally
        {
            WglContext.ClearCurrent();
        }
    }

    [SkippableFact]
    public void Contexts_can_be_created_and_destroyed_repeatedly()
    {
        // Each context owns a hidden window and a device context. Leaking either exhausts
        // a desktop heap that is not large, and the failure appears far from the cause.
        for (var i = 0; i < 25; i++)
        {
            using var context = WglContext.Create();
            context.MakeCurrent();
            WglContext.ClearCurrent();
        }
    }

    [SkippableFact]
    public void Disposing_twice_is_safe()
    {
        var context = WglContext.Create();
        context.Dispose();
        context.Dispose();

        Assert.True(context.IsDisposed);
    }
}
