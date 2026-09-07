using System.Diagnostics;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Resizing the shared surface.
/// </summary>
/// <remarks>
/// The PRD requires surviving resize and DPI change without corruption. Until now the
/// swap chain was built at the panel's initial size and never changed, so maximising the
/// window stretched the picture rather than re-rendering it.
/// </remarks>
public sealed class SharedVideoTargetResizeTests
{
    private readonly ITestOutputHelper _output;

    public SharedVideoTargetResizeTests(ITestOutputHelper output) => _output = output;

    private void OnGlThread(Action body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var context = WglContext.Create();
                context.MakeCurrent();

                if (!context.Query().SupportsHardwarePath || !MpvLibrary.IsAvailable())
                {
                    _output.WriteLine("Hardware path or libmpv unavailable; skipping.");
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

    [Fact]
    public void Resizing_changes_the_reported_size()
    {
        OnGlThread(() =>
        {
            using var target = SharedVideoTarget.Create(320, 240);
            target.Resize(640, 480);

            Assert.Equal(640, target.Width);
            Assert.Equal(480, target.Height);
        });
    }

    [Fact]
    public void Resizing_keeps_the_same_d3d_device()
    {
        OnGlThread(() =>
        {
            using var target = SharedVideoTarget.Create(320, 240);
            var before = target.Device.NativePointer;

            target.Resize(800, 600);

            // The swap chain is bound to this device. Recreating it on resize would leave
            // the swap chain unable to receive a copy, and the failure is a cryptic
            // E_INVALIDARG from CopyResource rather than anything naming the mismatch.
            Assert.Equal(before, target.Device.NativePointer);
        });
    }

    [Fact]
    public void Resizing_produces_a_working_surface()
    {
        OnGlThread(() =>
        {
            using var handle = MpvHandle.Create(new Dictionary<string, string>
            {
                ["vo"] = "libmpv",
                ["idle"] = "yes",
                ["keep-open"] = "yes",
                ["audio"] = "no",
            });

            using var renderer = MpvOpenGlRenderer.Create(handle);
            using var target = SharedVideoTarget.Create(320, 240);

            handle.Command("loadfile", "av://lavfi:testsrc=size=640x480:rate=30");

            // Render at the original size first, then resize mid-playback, which is what
            // dragging a window edge does.
            Assert.True(RenderUntilPixels(renderer, target), "No pixels before resize.");

            target.Resize(800, 600);

            // The assertion that matters: pixels arrive in the *new* texture. A resize that
            // silently leaves the old texture registered would keep rendering to memory the
            // swap chain no longer presents, which shows as a frozen picture rather than an
            // error.
            Assert.True(RenderUntilPixels(renderer, target), "No pixels after resize.");
            Assert.Equal(800 * 600 * 4, target.ReadBack().Length);
        });
    }

    [Fact]
    public void Resizing_to_the_same_size_is_a_no_op()
    {
        OnGlThread(() =>
        {
            using var target = SharedVideoTarget.Create(320, 240);
            var texture = target.Texture.NativePointer;

            target.Resize(320, 240);

            // Dragging a window edge produces a resize per mouse move. Rebuilding a
            // driver-side registration on each would stall the drag.
            Assert.Equal(texture, target.Texture.NativePointer);
        });
    }

    [Fact]
    public void Repeated_resizes_do_not_leak()
    {
        OnGlThread(() =>
        {
            using var target = SharedVideoTarget.Create(320, 240);

            // Each resize registers and unregisters a driver-side object. Getting the
            // release order wrong survives a handful and fails a long drag.
            for (var i = 0; i < 40; i++)
            {
                var size = 320 + (i * 8);
                target.Resize(size, size);
                Assert.Equal(size, target.Width);
            }
        });
    }

    private static bool RenderUntilPixels(MpvOpenGlRenderer renderer, SharedVideoTarget target)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (renderer.HasFrameReady())
            {
                target.RenderFrame(renderer);
                foreach (var value in target.ReadBack())
                {
                    if (value > 16)
                    {
                        return true;
                    }
                }
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
