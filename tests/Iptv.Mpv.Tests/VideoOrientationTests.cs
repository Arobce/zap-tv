using System.Diagnostics;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Which way up the picture comes out of the shared texture.
/// </summary>
/// <remarks>
/// <para>
/// OpenGL's framebuffer origin is bottom-left and Direct3D's is top-left, so one of the
/// two has to flip and the choice is not observable from any call succeeding. Every
/// existing test here proves frames arrive; none of them proves the frame is the right way
/// up, which is how the app shipped upside down while the render loop reported perfect
/// health.
/// </para>
/// <para>
/// The source is a 64x64 image whose top half is red and bottom half is blue, so the
/// answer is one pixel read rather than an eyeball on a screenshot.
/// </para>
/// </remarks>
public sealed class VideoOrientationTests
{
    private readonly ITestOutputHelper _output;

    public VideoOrientationTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    /// <summary>Top half red, bottom half blue. pad places the input at (0,0), the top-left.</summary>
    private const string SplitSource =
        "av://lavfi:color=c=red:size=64x32:rate=30,pad=64:64:0:0:blue,format=rgb24";

    private void OnGlThread(Action body)
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
                        $"Driver cannot support the hardware path ({capabilities.Renderer}); skipping.");
                    return;
                }

                if (!MpvLibrary.IsAvailable())
                {
                    _output.WriteLine("libmpv-2.dll not present; skipping.");
                    return;
                }

                body();
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

    /// <summary>Averages a row, so one stray pixel of scaler ringing cannot decide the test.</summary>
    private static (int R, int G, int B) SampleRow(byte[] bgra, int row)
    {
        long r = 0, g = 0, b = 0;

        for (var column = 0; column < Size; column++)
        {
            var offset = ((row * Size) + column) * 4;
            b += bgra[offset];
            g += bgra[offset + 1];
            r += bgra[offset + 2];
        }

        return ((int)(r / Size), (int)(g / Size), (int)(b / Size));
    }

    [Fact]
    public void The_top_of_the_source_is_the_top_of_the_shared_texture()
    {
        OnGlThread(() =>
        {
            using var handle = MpvHandle.Create(new Dictionary<string, string>
            {
                ["vo"] = "libmpv",
                ["idle"] = "yes",
                ["keep-open"] = "yes",
                ["audio"] = "no",

                // No scaling of the 64x64 source into the 64x64 target, so the halves stay
                // exactly halves and the sampled rows are unambiguous.
                ["video-unscaled"] = "yes",
            });

            using var renderer = MpvOpenGlRenderer.Create(handle);
            using var target = SharedVideoTarget.Create(Size, Size);

            handle.Command("loadfile", SplitSource);

            var stopwatch = Stopwatch.StartNew();
            var rendered = false;

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (renderer.HasFrameReady())
                {
                    target.RenderFrame(renderer);
                    rendered = true;
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.True(rendered, "mpv never reported a frame ready within 20s.");

            var pixels = target.ReadBack();
            var top = SampleRow(pixels, 4);
            var bottom = SampleRow(pixels, Size - 5);

            _output.WriteLine($"row 4    = R{top.R} G{top.G} B{top.B}");
            _output.WriteLine($"row {Size - 5} = R{bottom.R} G{bottom.G} B{bottom.B}");

            // Red at the top means the D3D texture's first row holds the source's first
            // row, which is what SwapChainPanel composites as the top of the picture.
            Assert.True(
                top.R > top.B,
                $"the top row is not red (R{top.R} B{top.B}); the picture is upside down.");

            Assert.True(
                bottom.B > bottom.R,
                $"the bottom row is not blue (R{bottom.R} B{bottom.B}); the picture is upside down.");
        });
    }
}
