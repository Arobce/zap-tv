namespace Iptv.Core.Playback;

/// <summary>One observation of a playing stream.</summary>
public readonly record struct StallSample
{
    /// <summary>Frames the presenter has drawn since it started. Never resets.</summary>
    public required long FramesPresented { get; init; }

    /// <summary>
    /// Seconds of demuxed data buffered ahead of the play position.
    /// </summary>
    /// <remarks>
    /// mpv's <c>demuxer-cache-duration</c>. Null when it does not report one, which happens
    /// for some containers and for the moment before the demuxer has read anything.
    /// </remarks>
    public required double? CacheSeconds { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// Decides when a playing stream has actually died.
/// </summary>
/// <remarks>
/// <para>
/// Waiting for frames to stop is not enough, and the measurement that showed it is in
/// decision 0012. A stream cut mid-transfer keeps playing out of the demuxer's buffer, and
/// with <c>demuxer-max-bytes</c> at 32MiB that buffer holds far more than the two seconds
/// <c>demuxer-readahead-secs</c> asks for — tens of seconds of a typical channel. Frames
/// therefore keep arriving long after the provider has gone, and the frame-based watcher
/// only starts counting once they stop. Measured recovery was 8.3s, 12.1s and 19.0s across
/// three cuts of the same channel, scaling with how much happened to be buffered, against
/// a 10s budget. The recovery itself took 3ms every time.
/// </para>
/// <para>
/// The signal that fires immediately is the buffer draining at real time. If the cache
/// holds four fewer seconds of video than it did four seconds ago, nothing is arriving at
/// all. That is a cut. A stream that is merely slow still delivers something, so its cache
/// drains more slowly than it plays, and this deliberately does not fire on it — a brief
/// hiccup must not become a channel change.
/// </para>
/// <para>
/// The frame rule is kept as a backstop, for streams that report no cache duration.
/// </para>
/// </remarks>
public sealed class StallDetector
{
    /// <summary>No new frames for this long is a stall, whatever the cache says.</summary>
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the buffer must be observed draining before believing it.</summary>
    /// <remarks>
    /// Four seconds. Long enough that a single slow sample cannot trigger it, short enough
    /// to leave most of the 10s budget for the reconnect that follows.
    /// </remarks>
    public static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The fraction of real time the buffer must lose to count as receiving nothing.
    /// </summary>
    /// <remarks>
    /// Not 1.0. Playback and the sampling clock are not the same clock, and a stream
    /// playing slightly fast or a sample arriving slightly late would each put a genuine
    /// cut just under the threshold and delay detection by another window.
    /// </remarks>
    public const double DrainRatio = 0.85;

    private readonly List<StallSample> _samples = [];

    private long _lastFrames = -1;
    private DateTimeOffset _lastFrameAt;

    /// <summary>Why the stream was judged dead. Null while it is healthy.</summary>
    public string? Reason { get; private set; }

    /// <summary>Forgets everything, for a newly opened stream.</summary>
    public void Reset()
    {
        _samples.Clear();
        _lastFrames = -1;
        Reason = null;
    }

    /// <summary>Takes one observation and says whether the stream has died.</summary>
    public bool Observe(StallSample sample)
    {
        if (sample.FramesPresented != _lastFrames)
        {
            _lastFrames = sample.FramesPresented;
            _lastFrameAt = sample.At;
        }
        else if (sample.At - _lastFrameAt >= FrameTimeout)
        {
            Reason = $"no new frames for {(sample.At - _lastFrameAt).TotalSeconds:F0}s";
            return true;
        }

        return ObserveCache(sample);
    }

    private bool ObserveCache(StallSample sample)
    {
        if (sample.CacheSeconds is null)
        {
            // Nothing to measure. The frame rule above is the whole detector for this
            // stream, which is why it is kept rather than replaced.
            _samples.Clear();
            return false;
        }

        _samples.Add(sample);

        // Keep the newest sample that is at least a window old, and everything after it.
        //
        // Not "everything inside the window". That leaves the oldest sample younger than
        // the window, so the span measured below is always shorter than the window and the
        // rule can never fire. It passed its tests anyway, because synthetic samples land
        // exactly on the boundary and equality saved it; a real timer is always a few
        // milliseconds late and never is.
        var cutoff = sample.At - DrainWindow;
        var keep = 0;

        for (var index = 0; index < _samples.Count; index++)
        {
            if (_samples[index].At > cutoff)
            {
                break;
            }

            keep = index;
        }

        if (keep > 0)
        {
            _samples.RemoveRange(0, keep);
        }

        var oldest = _samples[0];
        var elapsed = (sample.At - oldest.At).TotalSeconds;

        if (elapsed < DrainWindow.TotalSeconds)
        {
            return false;
        }

        var lost = (oldest.CacheSeconds ?? 0) - sample.CacheSeconds.Value;

        if (lost < DrainRatio * elapsed)
        {
            return false;
        }

        Reason =
            $"buffer drained {lost:F1}s in {elapsed:F1}s, so nothing is arriving";

        return true;
    }
}
