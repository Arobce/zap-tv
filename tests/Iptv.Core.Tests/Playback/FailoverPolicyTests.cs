using Iptv.Core.Playback;
using Iptv.Core.Sources;

namespace Iptv.Core.Tests.Playback;

/// <summary>
/// Failover ordering and the guard that stops it playing the wrong channel.
/// </summary>
/// <remarks>
/// The guard carries most of the weight here. Ordering badly costs a few seconds; failing
/// over across a title collision shows a viewer a different channel while the UI insists
/// it is the one they picked.
/// </remarks>
public sealed class FailoverPolicyTests
{
    private static StreamCandidate Candidate(
        long id,
        string normalizedTitle = "espn",
        string? country = null,
        int priority = 0,
        Quality? quality = null,
        int attempts = 0,
        int successes = 0,
        long providerId = 1) => new()
        {
            StreamId = id,
            Url = $"http://host/{id}",
            ProviderId = providerId,
            ProviderName = $"provider {providerId}",
            ProviderPriority = priority,
            NormalizedTitle = normalizedTitle,
            Country = country,
            Quality = quality,
            Attempts = attempts,
            Successes = successes,
        };

    [Fact]
    public void No_candidates_yields_an_empty_plan()
    {
        var plan = FailoverPolicy.Plan("tvg:espn.us", []);

        Assert.Null(plan.Primary);
        Assert.Empty(plan.Candidates);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void Provider_priority_outranks_every_measurement()
    {
        // The second provider has a perfect record and the better copy. The user still put
        // the first one first, and that is not a preference the app gets to overrule.
        var plan = FailoverPolicy.Plan("tvg:espn.us",
        [
            Candidate(1, priority: 0, quality: Quality.Sd, attempts: 20, successes: 10),
            Candidate(2, priority: 1, quality: Quality.Uhd, attempts: 20, successes: 20),
        ]);

        Assert.Equal(1, plan.Primary!.StreamId);
        Assert.Equal([1L, 2L], plan.Candidates.Select(c => c.StreamId));
    }

    [Fact]
    public void Reliability_outranks_quality_within_one_provider()
    {
        var plan = FailoverPolicy.Plan("tvg:espn.us",
        [
            Candidate(1, quality: Quality.Uhd, attempts: 40, successes: 4),
            Candidate(2, quality: Quality.Sd, attempts: 40, successes: 38),
        ]);

        Assert.Equal(2, plan.Primary!.StreamId);
    }

    [Fact]
    public void An_untried_stream_outranks_a_consistently_failing_one()
    {
        // Smoothing has to leave room for a stream with no history to be tried at all.
        // A raw rate would score the unknown stream 0 and bury it below the broken one.
        var plan = FailoverPolicy.Plan("tvg:espn.us",
        [
            Candidate(1, attempts: 30, successes: 1),
            Candidate(2, attempts: 0, successes: 0),
        ]);

        Assert.Equal(2, plan.Primary!.StreamId);
    }

    [Fact]
    public void A_long_record_outranks_a_single_lucky_attempt()
    {
        // Ids chosen so the deterministic tiebreak favours the lucky stream. Without that
        // the assertion holds even on a raw success rate, where 198/200 and 1/1 land in
        // the same band and the id decides - passing for a reason the test is not about.
        var lucky = Candidate(1, attempts: 1, successes: 1);
        var proven = Candidate(2, attempts: 200, successes: 198);

        var plan = FailoverPolicy.Plan("tvg:espn.us", [lucky, proven]);

        Assert.Equal(2, plan.Primary!.StreamId);
    }

    [Fact]
    public void Quality_preference_decides_when_history_is_equal()
    {
        StreamCandidate[] candidates =
        [
            Candidate(1, quality: Quality.Sd),
            Candidate(2, quality: Quality.Uhd),
            Candidate(3, quality: Quality.Hd),
        ];

        Assert.Equal(
            [2L, 3L, 1L],
            FailoverPolicy.Plan("tvg:espn.us", candidates, QualityPreference.Highest)
                .Candidates.Select(c => c.StreamId));

        Assert.Equal(
            [1L, 3L, 2L],
            FailoverPolicy.Plan("tvg:espn.us", candidates, QualityPreference.Stable)
                .Candidates.Select(c => c.StreamId));
    }

    [Fact]
    public void Unknown_quality_sorts_last_under_either_preference()
    {
        StreamCandidate[] candidates = [Candidate(1), Candidate(2, quality: Quality.Sd)];

        Assert.Equal(2, FailoverPolicy.Plan("tvg:e", candidates, QualityPreference.Highest).Primary!.StreamId);
        Assert.Equal(2, FailoverPolicy.Plan("tvg:e", candidates, QualityPreference.Stable).Primary!.StreamId);
    }

    [Fact]
    public void The_plan_is_deterministic_when_everything_else_ties()
    {
        var plan = FailoverPolicy.Plan("tvg:espn.us", [Candidate(9), Candidate(3), Candidate(7)]);

        Assert.Equal([3L, 7L, 9L], plan.Candidates.Select(c => c.StreamId));
    }

    // --- the safety guard ---

    [Fact]
    public void Two_countries_sharing_a_name_never_fail_over_to_each_other()
    {
        // The PRD's exit criterion. "US: ESPN" and "UK| ESPN" normalize onto one key by
        // design; substituting one for the other is the failure this guard exists for.
        var plan = FailoverPolicy.Plan("name:espn",
        [
            Candidate(1, country: "US"),
            Candidate(2, country: "UK"),
        ]);

        Assert.Single(plan.Candidates);
        Assert.Equal(1, plan.Primary!.StreamId);

        var refused = Assert.Single(plan.Excluded);
        Assert.Equal(2, refused.Candidate.StreamId);
        Assert.Contains("country", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_country_on_one_side_only_is_refused()
    {
        // Untagged means unknown, not international. Reading it as agreement would let
        // through exactly the substitution the guard is for.
        var plan = FailoverPolicy.Plan("tvg:espn.us",
        [
            Candidate(1, country: "US"),
            Candidate(2, country: null),
        ]);

        Assert.Single(plan.Candidates);
        Assert.Single(plan.Excluded);
    }

    [Fact]
    public void Countries_agreeing_in_different_case_are_accepted()
    {
        var plan = FailoverPolicy.Plan("tvg:espn.us",
        [
            Candidate(1, country: "US"),
            Candidate(2, country: "us"),
        ]);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void An_inferred_key_requires_the_titles_to_match_exactly()
    {
        // "espn 2" and "espn2" produce the same name: key, which is right for showing one
        // row and not enough to swap the stream underneath it.
        var plan = FailoverPolicy.Plan("name:espn2",
        [
            Candidate(1, normalizedTitle: "espn 2"),
            Candidate(2, normalizedTitle: "espn2"),
        ]);

        Assert.Single(plan.Candidates);
        var refused = Assert.Single(plan.Excluded);
        Assert.Contains("differs", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tvg_id_key_allows_titles_to_differ()
    {
        // The provider asserted these are the same channel. Two providers spelling it
        // differently is normal and is not evidence against them.
        var plan = FailoverPolicy.Plan("tvg:espn.us",
        [
            Candidate(1, normalizedTitle: "espn"),
            Candidate(2, normalizedTitle: "espn east"),
        ]);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void The_guard_measures_against_the_stream_actually_chosen()
    {
        // Ordering runs first, so the anchor is whichever stream will really be opened.
        // Guarding against the list's arbitrary first element instead would accept or
        // refuse different candidates depending on query order.
        var plan = FailoverPolicy.Plan("name:espn",
        [
            Candidate(1, country: "UK", priority: 5),
            Candidate(2, country: "US", priority: 0),
            Candidate(3, country: "US", priority: 9),
        ]);

        Assert.Equal(2, plan.Primary!.StreamId);
        Assert.Equal([2L, 3L], plan.Candidates.Select(c => c.StreamId));
        Assert.Equal(1, Assert.Single(plan.Excluded).Candidate.StreamId);
    }

    [Fact]
    public void The_primary_is_never_excluded_by_the_guard()
    {
        // There is nothing to substitute it for. A plan that refuses its own first choice
        // would leave a playable channel unplayable.
        var plan = FailoverPolicy.Plan("name:espn", [Candidate(1, country: "US")]);

        Assert.Equal(1, plan.Primary!.StreamId);
        Assert.Empty(plan.Excluded);
    }
}
