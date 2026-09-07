using Iptv.Core.Sources;

namespace Iptv.Core.Playback;

/// <summary>How a playback attempt ended.</summary>
/// <remarks>
/// Mirrors the <c>outcome</c> check constraint on <c>stream_health</c>. The names are
/// stored, so renaming one is a migration.
/// </remarks>
public enum PlaybackOutcome
{
    /// <summary>A video frame was presented.</summary>
    Ok,

    /// <summary>No first frame inside the budget.</summary>
    Timeout,

    /// <summary>The provider refused the connection or returned an error status.</summary>
    HttpError,

    /// <summary>Playback started and then stopped delivering data.</summary>
    Stall,

    /// <summary>The stream opened but could not be decoded.</summary>
    DecodeError,
}

/// <summary>Which copy of a channel to reach for first when several exist.</summary>
public enum QualityPreference
{
    /// <summary>Best picture available. The default.</summary>
    Highest,

    /// <summary>Least bandwidth, for connections where the UHD copy stutters.</summary>
    Stable,
}

/// <summary>One stream that could serve a given <c>channel_key</c>.</summary>
public sealed record StreamCandidate
{
    public required long StreamId { get; init; }

    public required string Url { get; init; }

    public required long ProviderId { get; init; }

    public required string ProviderName { get; init; }

    /// <summary>Lower sorts first, matching <c>providers.priority</c>.</summary>
    public required int ProviderPriority { get; init; }

    public Quality? Quality { get; init; }

    /// <summary>Country prefix from the stream's own title, or null if it carried none.</summary>
    public string? Country { get; init; }

    /// <summary>The stream's normalized title, used by the failover safety guard.</summary>
    public required string NormalizedTitle { get; init; }

    /// <summary>Attempts recorded in <c>stream_health</c> within the rolling window.</summary>
    public int Attempts { get; init; }

    /// <summary>Attempts in the window that ended <see cref="PlaybackOutcome.Ok"/>.</summary>
    public int Successes { get; init; }

    /// <summary>
    /// Success rate, smoothed toward even odds.
    /// </summary>
    /// <remarks>
    /// Laplace rather than raw <c>Successes / Attempts</c>: a stream tried once and
    /// working scores 1.0 raw, which would rank it above one that has worked 200 times out
    /// of 202. Adding a notional success and failure makes confidence grow with evidence,
    /// so an untried stream sits mid-table rather than at either extreme.
    /// </remarks>
    public double Reliability => (Successes + 1.0) / (Attempts + 2.0);
}

/// <summary>A candidate the safety guard refused, and why.</summary>
/// <remarks>
/// Returned rather than merely dropped: the PRD requires exclusions to be logged, and a
/// channel whose every alternative is refused looks identical to one with no alternatives
/// unless the reason survives.
/// </remarks>
public sealed record ExcludedCandidate(StreamCandidate Candidate, string Reason);
