using System.Diagnostics;
using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Property observation and event marshalling.
/// </summary>
/// <remarks>
/// Phase 5 requires observing pause, time-pos, duration, demuxer-cache-time,
/// cache-buffering-state, video-params, hwdec-current, eof-reached and core-idle. Phase 6
/// failover detection reads three of those, and Phase 7 measures channel change from
/// them, so the marshalling has to be right before either can be built.
/// </remarks>
public sealed class MpvEventLoopTests
{
    private readonly ITestOutputHelper _output;

    public MpvEventLoopTests(ITestOutputHelper output) => _output = output;

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
        ["audio"] = "no",
    });

    /// <summary>Drains events until a predicate matches or the timeout elapses.</summary>
    private static async Task<T?> WaitForAsync<T>(
        MpvEventLoop loop,
        Func<T, bool> predicate,
        TimeSpan timeout)
        where T : MpvEvent
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var evt in loop.Events.ReadAllAsync(cts.Token))
            {
                if (evt is T typed && predicate(typed))
                {
                    return typed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out; the caller asserts on null.
        }

        return null;
    }

    [Fact]
    public async Task Observes_a_property_change()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var loop = new MpvEventLoop(handle);

        loop.ObserveProperty("pause", MpvFormat.Flag);
        loop.Start();

        handle.SetProperty("pause", "yes");

        var change = await WaitForAsync<MpvPropertyChanged>(
            loop, p => p.Name == "pause" && p.AsFlag, TimeSpan.FromSeconds(10));

        Assert.NotNull(change);
        Assert.True(change.AsFlag);
    }

    [Fact]
    public async Task Reports_playback_lifecycle_events()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();

        // A render context is required, not optional. With vo=libmpv and nothing consuming
        // frames, mpv never finishes loading the file and no FILE_LOADED ever arrives -
        // it waits for a renderer that does not exist. Production always has one, so a
        // test without one is testing a configuration that never ships.
        using var renderer = MpvSoftwareRenderer.Create(handle);
        using var loop = new MpvEventLoop(handle);
        loop.Start();

        handle.Command("loadfile", "av://lavfi:testsrc=size=160x120:rate=10");

        var loaded = await WaitForAsync<MpvSimpleEvent>(
            loop, e => e.Kind == MpvEventKind.FileLoaded, TimeSpan.FromSeconds(20));

        // Phase 7 measures channel change from loadfile to first frame, so these events
        // are the timing signal rather than a convenience.
        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task Reports_hwdec_current_for_a_real_decode()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var loop = new MpvEventLoop(handle);

        loop.ObserveProperty("hwdec-current", MpvFormat.String);
        loop.Start();

        handle.Command("loadfile", "av://lavfi:testsrc=size=640x480:rate=30");

        var change = await WaitForAsync<MpvPropertyChanged>(
            loop, p => p.Name == "hwdec-current", TimeSpan.FromSeconds(20));

        Assert.NotNull(change);
        _output.WriteLine($"hwdec-current: {change.AsString ?? "(null)"}");

        // Not asserted to be a hardware decoder. lavfi's test pattern is generated, not
        // decoded, so "no" is the correct answer here; the Phase 5 exit criterion covers
        // real MPEG-TS. What is asserted is that the property marshals as a string at all,
        // because reading it with the wrong format yields a silent null.
        Assert.True(
            change.AsString is not null || change.Value is null,
            "hwdec-current arrived in an unreadable shape.");
    }

    [Fact]
    public async Task Routes_mpv_log_messages()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var loop = new MpvEventLoop(handle);

        handle.RequestLogMessages("info");
        loop.Start();

        handle.Command("loadfile", "av://lavfi:testsrc=size=160x120:rate=10");

        var log = await WaitForAsync<MpvLogMessage>(
            loop, _ => true, TimeSpan.FromSeconds(15));

        Assert.NotNull(log);
        _output.WriteLine($"[{log.Level}] {log.Prefix}: {log.Text}");

        // Phase 5 routes these into Serilog at warn. A message with no text would mean the
        // three char* fields were read at the wrong offsets.
        Assert.NotEmpty(log.Level);
    }

    [Fact]
    public async Task Reports_why_playback_ended()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        using var loop = new MpvEventLoop(handle);
        loop.Start();

        // A path that cannot resolve, so mpv ends the file with an error rather than EOF.
        handle.Command("loadfile", "file:///definitely/not/a/real/file.mkv");

        var ended = await WaitForAsync<MpvEndFile>(loop, _ => true, TimeSpan.FromSeconds(20));

        Assert.NotNull(ended);
        _output.WriteLine($"end-file reason={ended.Reason} error={ended.Error}");

        // Failover depends on this distinction: reason 4 is an error worth switching
        // provider for, a clean EOF is not.
        Assert.Equal(4, ended.Reason);
    }

    [Fact]
    public void Disposing_stops_the_pump_promptly()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        var loop = new MpvEventLoop(handle);
        loop.Start();

        var stopwatch = Stopwatch.StartNew();
        loop.Dispose();
        stopwatch.Stop();

        _output.WriteLine($"event loop stopped in {stopwatch.ElapsedMilliseconds}ms");

        // The pump blocks in mpv_wait_event. If shutdown were not observed, closing a
        // channel would hang the app for as long as the wait timeout, on every change.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Event loop took {stopwatch.ElapsedMilliseconds}ms to stop.");
    }

    [Fact]
    public void Disposing_without_starting_is_safe()
    {
        if (!Available())
        {
            return;
        }

        using var handle = CreateHandle();
        var loop = new MpvEventLoop(handle);
        loop.Dispose();
    }
}
