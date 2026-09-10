using System.Diagnostics;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// The join between mpv's OpenGL output and Direct3D presentation.
/// </summary>
/// <remarks>
/// This is the assertion the whole presentation design rests on: mpv renders through
/// OpenGL, and the resulting pixels are readable from a D3D11 texture. Everything after
/// this - the swap chain, the SwapChainPanel, the XAML overlay - is presentation plumbing
/// over a texture that is already correct.
/// <para>
/// Verified by reading the D3D texture back and inspecting pixels. Asserting on return
/// codes would pass just as happily if the two APIs were writing to different memory,
/// which is the failure this design exists to avoid.
/// </para>
/// </remarks>
public sealed class SharedVideoTargetTests
{
    private const int Width = 320;
    private const int Height = 240;

    private readonly ITestOutputHelper _output;

    public SharedVideoTargetTests(ITestOutputHelper output) => _output = output;

    /// <summary>Runs work on a dedicated thread holding a current GL context.</summary>
    private static void OnGlThread(Action body) => GlThread.Run(body);

    private static MpvHandle CreateHandle() => MpvHandle.Create(new Dictionary<string, string>
    {
        ["vo"] = "libmpv",
        ["idle"] = "yes",
        ["keep-open"] = "yes",
        ["audio"] = "no",
    });

    [SkippableFact]
    public void Creates_a_texture_shared_between_d3d11_and_opengl()
    {
        OnGlThread(() =>
        {
            using var target = SharedVideoTarget.Create(Width, Height);

            Assert.NotEqual(0, target.Framebuffer);
            Assert.NotEqual(IntPtr.Zero, target.Texture.NativePointer);
            _output.WriteLine($"shared target: fbo {target.Framebuffer}, {target.Width}x{target.Height}");
        });
    }

    [SkippableFact]
    public void Mpv_pixels_reach_the_d3d11_texture()
    {
        OnGlThread(() =>
        {
            using var handle = CreateHandle();
            using var renderer = MpvOpenGlRenderer.Create(handle);
            using var target = SharedVideoTarget.Create(Width, Height);

            handle.Command("loadfile", "av://lavfi:testsrc=size=320x240:rate=30");

            var arrived = false;
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(25))
            {
                if (renderer.HasFrameReady())
                {
                    target.RenderFrame(renderer);

                    // Read out of D3D, not out of GL. That is the difference between
                    // "OpenGL rendered something" and "Direct3D can see it".
                    if (HasNonBlackPixels(target.ReadBack()))
                    {
                        arrived = true;
                        break;
                    }
                }

                Thread.Sleep(30);
            }

            _output.WriteLine($"mpv pixels visible in the D3D11 texture after {stopwatch.ElapsedMilliseconds}ms");

            Assert.True(
                arrived,
                "No mpv pixels appeared in the D3D11 texture. GL and D3D may be writing to " +
                "different memory, which every return code would report as success.");
        });
    }

    [SkippableFact]
    public void Sustains_rendering_through_the_shared_texture()
    {
        OnGlThread(() =>
        {
            using var handle = CreateHandle();
            using var renderer = MpvOpenGlRenderer.Create(handle);
            using var target = SharedVideoTarget.Create(Width, Height);

            handle.Command("loadfile", "av://lavfi:testsrc=size=320x240:rate=60");

            var frames = 0;
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10) && frames < 100)
            {
                if (renderer.HasFrameReady())
                {
                    target.RenderFrame(renderer);
                    frames++;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            _output.WriteLine(
                $"{frames} shared-texture frames in {stopwatch.ElapsedMilliseconds}ms " +
                $"({frames * 1000.0 / stopwatch.ElapsedMilliseconds:F0} fps)");

            // A missing lock/unlock pair often survives a handful of frames and then
            // corrupts or stalls, so sustained rendering is the meaningful check.
            Assert.True(frames >= 30, $"Only {frames} frames rendered through the shared texture.");
        });
    }

    [SkippableFact]
    public void Repeated_create_and_dispose_cycles_are_clean()
    {
        OnGlThread(() =>
        {
            // Each target holds a D3D11 device, a texture, a GL renderbuffer, an FBO and a
            // driver-side interop registration. Releasing them out of order leaks inside
            // the driver, which a short run will not reveal.
            for (var i = 0; i < 20; i++)
            {
                using var target = SharedVideoTarget.Create(Width, Height);
                Assert.NotEqual(0, target.Framebuffer);
            }
        });
    }

    [SkippableFact]
    public void Disposing_twice_is_safe()
    {
        OnGlThread(() =>
        {
            var target = SharedVideoTarget.Create(Width, Height);
            target.Dispose();
            target.Dispose();
        });
    }

    private static bool HasNonBlackPixels(ReadOnlySpan<byte> pixels)
    {
        foreach (var value in pixels)
        {
            if (value > 16)
            {
                return true;
            }
        }

        return false;
    }
}
