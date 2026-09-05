using System.Diagnostics;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Exercises the render API end to end using the software backend.
/// </summary>
/// <remarks>
/// The software path is not the playback path and never will be. It is here because it
/// validates the render API interop - the NULL-terminated parameter array, the enum
/// values read from render.h, the struct layout, the pointer lifetimes - without a GPU
/// context, a window, or ANGLE. When the ANGLE path later misbehaves, this separates
/// "the interop is wrong" from "the GL context is wrong".
/// <para>
/// Source is mpv's built-in lavfi test pattern, so nothing is downloaded and no fixture
/// video is committed.
/// </para>
/// </remarks>
public sealed class MpvSoftwareRendererTests
{
    private const int Width = 320;
    private const int Height = 240;

    private readonly ITestOutputHelper _output;

    public MpvSoftwareRendererTests(ITestOutputHelper output) => _output = output;

    private bool Available()
    {
        if (MpvLibrary.IsAvailable())
        {
            return true;
        }

        _output.WriteLine("libmpv-2.dll not present; skipping.");
        return false;
    }

    private static MpvHandle CreateHandle() => MpvHandle.Create(new Dictionary<string, string>
    {
        ["vo"] = "libmpv",
        ["idle"] = "yes",
        ["keep-open"] = "yes",
        // No audio device in CI, and this test is about pixels.
        ["audio"] = "no",
    });

    [Fact]
    public void Creates_and_frees_a_software_render_context()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var renderer = MpvSoftwareRenderer.Create(handle);

        Assert.False(renderer.IsDisposed);
    }

    [Fact]
    public async Task Renders_an_actual_frame()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var renderer = MpvSoftwareRenderer.Create(handle);

        // mpv's built-in test pattern: no fixture file, no network.
        handle.Command("loadfile", "av://lavfi:testsrc=size=320x240:rate=30");

        var buffer = new byte[Width * Height * 4];
        var rendered = false;

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (renderer.HasFrameReady())
            {
                renderer.Render(buffer, Width, Height);
                if (HasNonBlackPixels(buffer))
                {
                    rendered = true;
                    break;
                }
            }

            await Task.Delay(50);
        }

        _output.WriteLine($"first non-black frame after {deadline.ElapsedMilliseconds}ms");

        // The assertion that matters: mpv decoded a frame and wrote pixels through our
        // parameter array into our buffer. An all-black buffer would mean the call
        // succeeded while writing nowhere, which is the classic marshalling failure.
        Assert.True(rendered, "No non-black frame was produced within 20s.");
    }

    [Fact]
    public void Render_rejects_a_buffer_that_is_too_small()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var renderer = MpvSoftwareRenderer.Create(handle);

        // mpv writes width*height*4 bytes through a raw pointer. Passing a short buffer
        // corrupts the heap rather than failing, so this is checked before the call.
        var tooSmall = new byte[16];
        Assert.Throws<ArgumentException>(() => renderer.Render(tooSmall, Width, Height));
    }

    [Fact]
    public void Render_context_can_be_freed_repeatedly()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        var renderer = MpvSoftwareRenderer.Create(handle);

        renderer.Dispose();
        renderer.Dispose();

        Assert.True(renderer.IsDisposed);
    }

    [Fact]
    public void Repeated_create_and_free_cycles_do_not_exhaust_resources()
    {
        if (!Available())
        {
            return;
        }

        // Phase 5 exit criterion covers render context lifetime specifically. Freeing the
        // context after its handle, or not at all, survives a short run and fails a long
        // one, so the cycle is what gets tested rather than a single instance.
        for (var i = 0; i < 50; i++)
        {
            using var handle = CreateHandle();
            using var renderer = MpvSoftwareRenderer.Create(handle);
            Assert.False(renderer.IsDisposed);
        }
    }

    private static bool HasNonBlackPixels(ReadOnlySpan<byte> buffer)
    {
        foreach (var value in buffer)
        {
            if (value > 16)
            {
                return true;
            }
        }

        return false;
    }
}
