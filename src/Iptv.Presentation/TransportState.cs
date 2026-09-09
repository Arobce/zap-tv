using System.Globalization;

namespace Iptv.Presentation;

/// <summary>
/// The transport bar: where playback is, how long the thing is, and whether seeking means
/// anything.
/// </summary>
/// <remarks>
/// <para>
/// Hidden for live television. A live stream has no end to seek towards and no position
/// worth reporting — the provider serves the edge of the broadcast and nothing else — so a
/// bar there would be a scrubber that does nothing, which is worse than no bar.
/// </para>
/// <para>
/// Shown without seeking when the duration is unknown, which happens for a moment after
/// opening a film and permanently for the ones the provider serves without one. The
/// elapsed time is still worth showing; a slider whose end is a guess is not.
/// </para>
/// </remarks>
public sealed record TransportState
{
    /// <summary>Live, or nothing playing.</summary>
    public static readonly TransportState Hidden = new();

    public bool IsVisible { get; init; }

    /// <summary>Whether the duration is known well enough to scrub against.</summary>
    public bool CanSeek { get; init; }

    public int PositionSeconds { get; init; }

    /// <summary>Zero when unknown.</summary>
    public int DurationSeconds { get; init; }

    public string PositionText => Format(PositionSeconds);

    /// <summary>The duration, or a dash when the provider did not give one.</summary>
    public string DurationText => DurationSeconds > 0 ? Format(DurationSeconds) : "--:--";

    /// <summary>How far through, 0 to 1. Zero when the duration is unknown.</summary>
    public double Fraction => DurationSeconds > 0
        ? Math.Clamp((double)PositionSeconds / DurationSeconds, 0, 1)
        : 0;

    /// <summary>Builds the state for what is playing.</summary>
    /// <param name="live">Whether this is live television.</param>
    /// <param name="positionSeconds">mpv's time-pos, or null before the first frame.</param>
    /// <param name="durationSeconds">mpv's duration, or null when it does not know one.</param>
    public static TransportState For(bool live, int? positionSeconds, int? durationSeconds)
    {
        if (live || positionSeconds is null)
        {
            return Hidden;
        }

        var duration = durationSeconds is > 0 ? durationSeconds.Value : 0;

        return new TransportState
        {
            IsVisible = true,
            // A position past the end means the duration is wrong, which mpv reports for
            // some transcoded files. Seeking against a wrong end lands nowhere near where
            // the slider says, so it is refused rather than offered.
            CanSeek = duration > 0 && positionSeconds.Value <= duration,
            PositionSeconds = Math.Max(0, positionSeconds.Value),
            DurationSeconds = duration,
        };
    }

    /// <summary>Where a scrub to <paramref name="fraction"/> of the bar lands.</summary>
    public int SeekTarget(double fraction)
        => DurationSeconds <= 0
            ? 0
            : (int)Math.Round(Math.Clamp(fraction, 0, 1) * DurationSeconds);

    /// <summary>h:mm:ss for anything an hour or longer, m:ss below.</summary>
    /// <remarks>
    /// The leading "0:" is dropped for short content because a 42 minute episode reading
    /// "0:42:15" is harder to take in at a glance than "42:15", and glancing is the only
    /// way anybody reads this.
    /// </remarks>
    public static string Format(int seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalHours >= 1
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:00}:{2:00}",
                (int)span.TotalHours,
                span.Minutes,
                span.Seconds)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1:00}",
                span.Minutes,
                span.Seconds);
    }
}
