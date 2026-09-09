using Iptv.Core.Playback;

namespace Iptv.Core.Tests.Playback;

public sealed class StallDetectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Feeds roughly one sample per second and returns when the detector fires.
    /// </summary>
    /// <remarks>
    /// Deliberately not exactly one per second. A timer fires late, never early, so every
    /// sample lands a few milliseconds after its nominal time and no two are exactly a
    /// window apart. An earlier version of these tests used exact seconds, which let a
    /// window comparison that could never be satisfied in practice pass every test here —
    /// the real drill caught it and this jitter is what would have.
    /// </remarks>
    private static int? SecondsUntilStall(
        StallDetector detector,
        int seconds,
        Func<int, long> frames,
        Func<int, double?> cache)
    {
        var drift = TimeSpan.Zero;

        for (var second = 1; second <= seconds; second++)
        {
            drift += TimeSpan.FromMilliseconds(3 + (second * 7 % 11));

            var stalled = detector.Observe(new StallSample
            {
                FramesPresented = frames(second),
                CacheSeconds = cache(second),
                At = Start.AddSeconds(second) + drift,
            });

            if (stalled)
            {
                return second;
            }
        }

        return null;
    }

    [Fact]
    public void A_healthy_stream_never_stalls()
    {
        var detector = new StallDetector();

        // Frames climbing, cache holding around the readahead target and jittering as a
        // real one does.
        var result = SecondsUntilStall(
            detector,
            seconds: 120,
            frames: second => second * 25L,
            cache: second => 2.0 + (second % 3 * 0.2));

        Assert.Null(result);
    }

    [Fact]
    public void A_cut_is_caught_from_the_draining_buffer_while_frames_still_flow()
    {
        var detector = new StallDetector();

        // The measured failure. The provider goes at t=1 with 30s buffered; frames keep
        // coming out of that buffer for another 30 seconds, so the frame rule would not
        // fire until t=35 at the earliest.
        var result = SecondsUntilStall(
            detector,
            seconds: 40,
            frames: second => second * 25L,
            cache: second => Math.Max(0, 30.0 - second));

        Assert.NotNull(result);
        Assert.True(result <= 6, $"detected at {result}s; the 10s budget needs it well under that");
    }

    [Fact]
    public void A_slow_stream_is_not_a_cut()
    {
        var detector = new StallDetector();

        // Losing half a second of buffer per second: behind, but still receiving. A
        // channel change here would be the app inventing a failure out of a hiccup.
        var result = SecondsUntilStall(
            detector,
            seconds: 30,
            frames: second => second * 25L,
            cache: second => Math.Max(0.5, 20.0 - (second * 0.5)));

        Assert.Null(result);
    }

    [Fact]
    public void A_dip_that_recovers_is_not_a_cut()
    {
        var detector = new StallDetector();

        // Three seconds of real drain, then refill. Shorter than the window on purpose:
        // this is the shape of a provider blip, and it must survive one.
        var result = SecondsUntilStall(
            detector,
            seconds: 40,
            frames: second => second * 25L,
            cache: second => second is >= 5 and <= 7 ? 8.0 - (second - 4) : 8.0);

        Assert.Null(result);
    }

    [Fact]
    public void Frames_stopping_still_stalls_when_no_cache_is_reported()
    {
        var detector = new StallDetector();

        // Some containers report no cache duration at all. The frame rule is the whole
        // detector for those, which is why it is kept.
        var result = SecondsUntilStall(
            detector,
            seconds: 20,
            frames: _ => 500L,
            cache: _ => null);

        Assert.Equal(6, result);
    }

    [Fact]
    public void Frames_stopping_stalls_at_the_frame_timeout()
    {
        var detector = new StallDetector();

        // Cache held constant so only the frame rule can fire. First sample establishes
        // the frame count, so the timeout elapses one sample later.
        var result = SecondsUntilStall(
            detector,
            seconds: 20,
            frames: _ => 500L,
            cache: _ => 4.0);

        Assert.Equal(6, result);
    }

    [Fact]
    public void Reset_forgets_a_drain_in_progress()
    {
        var detector = new StallDetector();

        for (var second = 1; second <= 3; second++)
        {
            detector.Observe(new StallSample
            {
                FramesPresented = second * 25L,
                CacheSeconds = 30.0 - second,
                At = Start.AddSeconds(second),
            });
        }

        detector.Reset();

        // The drain that was three quarters of the way to firing must not carry into the
        // stream that replaced it.
        var stalled = detector.Observe(new StallSample
        {
            FramesPresented = 100,
            CacheSeconds = 26.0,
            At = Start.AddSeconds(4),
        });

        Assert.False(stalled);
        Assert.Null(detector.Reason);
    }

    [Fact]
    public void The_reason_says_which_rule_fired()
    {
        var detector = new StallDetector();

        SecondsUntilStall(
            detector,
            seconds: 40,
            frames: second => second * 25L,
            cache: second => Math.Max(0, 30.0 - second));

        Assert.Contains("drained", detector.Reason);
    }

    [Fact]
    public void A_buffer_that_empties_faster_than_real_time_still_counts()
    {
        var detector = new StallDetector();

        // Seeking or a discontinuity can drop the cache faster than playback consumes it.
        // That is still nothing arriving.
        var result = SecondsUntilStall(
            detector,
            seconds: 20,
            frames: second => second * 25L,
            cache: second => Math.Max(0, 40.0 - (second * 4)));

        Assert.NotNull(result);
    }
}
