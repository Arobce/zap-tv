using Iptv.Core.Sources;

namespace Iptv.Core.Playback;

/// <summary>An ordered failover plan for one channel.</summary>
public sealed record FailoverPlan
{
    /// <summary>Candidates to try, in order. Empty when nothing is playable.</summary>
    public required IReadOnlyList<StreamCandidate> Candidates { get; init; }

    /// <summary>Candidates the safety guard refused, with reasons.</summary>
    public required IReadOnlyList<ExcludedCandidate> Excluded { get; init; }

    /// <summary>The stream to open first, or null when there is nothing to open.</summary>
    public StreamCandidate? Primary => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>
/// Decides which streams may stand in for a failed one, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// Pure. The ordering is arithmetic over data the repository has already fetched, so it
/// is testable without a database and cannot issue a query while a stream is stalling.
/// </para>
/// <para>
/// The guard exists because <see cref="ChannelNormalizer"/> deliberately collapses
/// <c>US: ESPN</c> and <c>UK| ESPN</c> onto one <c>channel_key</c>. That is right for
/// grouping a list and wrong for silently swapping a stream: a viewer who is told they
/// are watching one channel and shown another has been misled by the app, which is worse
/// than an error message.
/// </para>
/// </remarks>
public static class FailoverPolicy
{
    /// <summary>Builds the ordered, guarded list of streams to try for a channel.</summary>
    /// <param name="channelKey">
    /// The channel being played. A <c>tvg:</c> key came from a provider-declared id and is
    /// trusted; a <c>name:</c> key was inferred from titles and is not.
    /// </param>
    /// <param name="candidates">Every active stream carrying that key.</param>
    /// <param name="preference">Which copy to prefer when reliability ties.</param>
    public static FailoverPlan Plan(
        string channelKey,
        IReadOnlyList<StreamCandidate> candidates,
        QualityPreference preference = QualityPreference.Highest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return new FailoverPlan { Candidates = [], Excluded = [] };
        }

        var ordered = new List<StreamCandidate>(candidates);
        ordered.Sort((left, right) => Compare(left, right, preference));

        // The first choice is the anchor every substitute is measured against, rather than
        // channels.country, which is one denormalized value for a key that may span
        // several differently-titled streams. Comparing against the stream actually being
        // played is the question the guard is really asking.
        var anchor = ordered[0];
        var trusted = channelKey.StartsWith("tvg:", StringComparison.Ordinal);

        var accepted = new List<StreamCandidate>(ordered.Count) { anchor };
        var excluded = new List<ExcludedCandidate>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var candidate = ordered[i];

            if (!CountriesAgree(anchor.Country, candidate.Country))
            {
                excluded.Add(new ExcludedCandidate(
                    candidate,
                    $"country {Describe(candidate.Country)} does not agree with {Describe(anchor.Country)}"));
                continue;
            }

            // A tvg_id key is the provider asserting identity; two streams sharing one are
            // the same channel by declaration. Without it the key came from squashed
            // titles, where "ESPN 2" and "ESPN2" collapse together correctly for grouping
            // but are not evidence enough to substitute one for the other.
            if (!trusted && !string.Equals(anchor.NormalizedTitle, candidate.NormalizedTitle, StringComparison.Ordinal))
            {
                excluded.Add(new ExcludedCandidate(
                    candidate,
                    $"inferred key: title \"{candidate.NormalizedTitle}\" differs from \"{anchor.NormalizedTitle}\""));
                continue;
            }

            accepted.Add(candidate);
        }

        return new FailoverPlan { Candidates = accepted, Excluded = excluded };
    }

    /// <summary>
    /// Whether two country prefixes are close enough to substitute across.
    /// </summary>
    /// <remarks>
    /// Absent on one side only is a refusal, not a pass. An untagged title is unknown
    /// rather than international, and treating unknown as agreement would readmit exactly
    /// the swap the guard exists to prevent.
    /// </remarks>
    private static bool CountriesAgree(string? anchor, string? candidate)
        => (anchor is null && candidate is null)
           || (anchor is not null && candidate is not null
               && string.Equals(anchor, candidate, StringComparison.OrdinalIgnoreCase));

    private static string Describe(string? country) => country ?? "(none)";

    private static int Compare(StreamCandidate left, StreamCandidate right, QualityPreference preference)
    {
        // Provider priority is a user ordering, so it outranks anything measured. Someone
        // who put their paid provider first does not want it demoted by two bad evenings.
        var byPriority = left.ProviderPriority.CompareTo(right.ProviderPriority);
        if (byPriority != 0)
        {
            return byPriority;
        }

        // Compared on a coarse grid. Raw reliability almost never ties, which would make
        // the quality preference below dead code and let a rounding-level difference in
        // history override an explicit user setting.
        var byReliability = Bucket(right.Reliability).CompareTo(Bucket(left.Reliability));
        if (byReliability != 0)
        {
            return byReliability;
        }

        var byQuality = CompareQuality(left.Quality, right.Quality, preference);
        if (byQuality != 0)
        {
            return byQuality;
        }

        // Stable ordering, so the same library produces the same plan twice running.
        return left.StreamId.CompareTo(right.StreamId);
    }

    /// <summary>Rounds reliability to 5% bands.</summary>
    private static int Bucket(double reliability) => (int)Math.Round(reliability * 20, MidpointRounding.AwayFromZero);

    private static int CompareQuality(Quality? left, Quality? right, QualityPreference preference)
    {
        // Unknown quality sorts last either way: it is usually an oddly-titled stream, and
        // guessing it is UHD is as wrong as guessing it is SD.
        var l = left is { } lq ? (int)lq : -1;
        var r = right is { } rq ? (int)rq : -1;

        if (l == r)
        {
            return 0;
        }

        if (l < 0)
        {
            return 1;
        }

        if (r < 0)
        {
            return -1;
        }

        return preference == QualityPreference.Highest ? r.CompareTo(l) : l.CompareTo(r);
    }
}
