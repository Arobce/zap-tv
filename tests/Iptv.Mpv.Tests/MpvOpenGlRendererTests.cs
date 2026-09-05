using System.Diagnostics;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// The shipping presentation path: mpv rendering through a real OpenGL context.
/// </summary>
/// <remarks>
/// Every test here runs on a dedicated thread. A WGL context belongs to one thread at a
/// time, and xunit runs tests on pool threads that it is free to reuse or move between
/// awaits, so making a context current on a pool thread makes the outcome depend on
/// scheduling.
/// <para>
/// Skips where the driver cannot support the path - a CI runner or driverless VM gets
/// OpenGL 1.1 from Windows - because that machine's answer is the software fallback, not a
/// failure.
/// </para>
/// </remarks>
public sealed class MpvOpenGlRendererTests
{
    private readonly ITestOutputHelper _output;

    public MpvOpenGlRendererTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Runs work on a dedicated thread holding a current GL context.
    /// </summary>
    private void OnGlThread(Action<WglContext> body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var context = WglContext.Create();
                context.MakeCurrent();

                var capabilities = context.Query();
                if (!capabilities.SupportsHardwarePath)
                {
                    _output.WriteLine(
                        $"Driver cannot support the hardware path ({capabilities.Renderer}, " +
                        $"GL {capabilities.Version}, NV_DX_interop2={capabilities.HasDxInterop}); skipping.");
                    return;
                }

                if (!MpvLibrary.IsAvailable())
                {
                    _output.WriteLine("libmpv-2.dll not present; skipping.");
                    return;
                }

                body(context);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                WglContext.ClearCurrent();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromMinutes(2));

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static MpvHandle CreateHandle() => MpvHandle.Create(new Dictionary<string, string>
    {
        ["vo"] = "libmpv",
        ["idle"] = "yes",
        ["keep-open"] = "yes",
        ["audio"] = "no",
    });

    [Fact]
    public void Creates_an_opengl_render_context()
    {
        OnGlThread(_ =>
        {
            using var handle = CreateHandle();
            using var renderer = MpvOpenGlRenderer.Create(handle);

            // This is the assertion the whole presentation design rests on: mpv accepted a
            // WGL-created context and resolved its GL entry points through our resolver.
            Assert.False(renderer.IsDisposed);
            _output.WriteLine("mpv accepted the WGL context and built an OpenGL render context");
        });
    }

    [Fact]
    public void Renders_a_frame_into_the_default_framebuffer()
    {
        OnGlThread(_ =>
        {
            using var handle = CreateHandle();
            using var renderer = MpvOpenGlRenderer.Create(handle);

            handle.Command("loadfile", "av://lavfi:testsrc=size=320x240:rate=30");

            var rendered = false;
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (renderer.HasFrameReady())
                {
                    // FBO 0 is the hidden window's own framebuffer. Nothing is presented;
                    // what is being proven is that mpv drives a real GL pipeline without
                    // erroring, which is the step ANGLE was supposed to enable.
                    renderer.Render(framebuffer: 0, width: 320, height: 240);
                    rendered = true;
                    break;
                }

                Thread.Sleep(50);
            }

            _output.WriteLine($"first GL frame rendered after {stopwatch.ElapsedMilliseconds}ms");
            Assert.True(rendered, "mpv never reported a frame ready within 20s.");
        });
    }

    [Fact]
    public void Renders_many_frames_without_failing()
    {
        OnGlThread(_ =>
        {
            using var handle = CreateHandle();
            using var renderer = MpvOpenGlRenderer.Create(handle);

            handle.Command("loadfile", "av://lavfi:testsrc=size=320x240:rate=60");

            var frames = 0;
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10) && frames < 100)
            {
                if (renderer.HasFrameReady())
                {
                    renderer.Render(0, 320, 240);
                    frames++;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            _output.WriteLine($"{frames} frames in {stopwatch.ElapsedMilliseconds}ms");

            // A single frame can succeed while the resolver delegate is collected moments
            // later. Sustained rendering is what shows the callback lifetime is right.
            Assert.True(frames >= 30, $"Only {frames} frames rendered; expected sustained output.");
        });
    }

    [Fact]
    public void Repeated_create_and_free_cycles_are_clean()
    {
        OnGlThread(_ =>
        {
            for (var i = 0; i < 25; i++)
            {
                using var handle = CreateHandle();
                using var renderer = MpvOpenGlRenderer.Create(handle);
                Assert.False(renderer.IsDisposed);
            }
        });
    }
}
