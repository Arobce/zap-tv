using Iptv.Core.Sources;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Playback;

/// <summary>
/// Walks a channel's candidates, recording how each attempt ended.
/// </summary>
/// <remarks>
/// <para>
/// Sequencing only. It never opens a stream and never touches mpv: the caller reports what
/// happened and is told what to try next. That keeps the retry logic testable without a
/// player and stops a failure path from depending on the thing that is failing.
/// </para>
/// <para>
/// It does not own the provider connection lease either. The app already holds exactly one
/// and releases it before each open; a second owner would make the accounting disagree
/// with reality, which is how a single-connection account ends up refusing every change.
/// </para>
/// </remarks>
public sealed class FailoverSession
{
    private readonly IReadOnlyList<StreamCandidate> _candidates;
    private int _index;

    private FailoverSession(string channelKey, FailoverPlan plan)
    {
        ChannelKey = channelKey;
        _candidates = plan.Candidates;
        Excluded = plan.Excluded;
    }

    public string ChannelKey { get; }

    /// <summary>Candidates the safety guard refused. Logged, not retried.</summary>
    public IReadOnlyList<ExcludedCandidate> Excluded { get; }

    /// <summary>The stream to open now, or null once every candidate has failed.</summary>
    public StreamCandidate? Current => _index < _candidates.Count ? _candidates[_index] : null;

    /// <summary>1 for the first attempt. Used to decide whether to say "switched to".</summary>
    public int AttemptNumber => _index + 1;

    /// <summary>How many streams could still be tried after the current one.</summary>
    public int Remaining => Math.Max(0, _candidates.Count - _index - 1);

    /// <summary>True once the first candidate has failed, so the UI can say so.</summary>
    public bool HasFailedOver => _index > 0;

    /// <summary>Builds a session for a channel.</summary>
    public static async Task<FailoverSession> StartAsync(
        SqliteConnection connection,
        string channelKey,
        StreamKind kind,
        DateTimeOffset now,
        QualityPreference preference,
        CancellationToken cancellationToken)
    {
        var plan = await StreamHealthRepository
            .PlanAsync(connection, channelKey, kind, now, preference, cancellationToken)
            .ConfigureAwait(false);

        return new FailoverSession(channelKey, plan);
    }

    /// <summary>
    /// Records how the current attempt ended and advances if it failed.
    /// </summary>
    /// <returns>
    /// The next stream to open, or null when the attempt succeeded or nothing is left.
    /// Check <see cref="Current"/> to tell those apart.
    /// </returns>
    public async Task<StreamCandidate?> ReportAsync(
        SqliteConnection connection,
        PlaybackOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        int? timeToFirstFrameMs = null,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Current is not { } candidate)
        {
            // Reporting after exhaustion. Not an error - a late mpv event can arrive after
            // the session has given up - but there is nothing left to attribute it to.
            return null;
        }

        await StreamHealthRepository.RecordAsync(
            connection,
            new HealthAttempt
            {
                StreamId = candidate.StreamId,
                Outcome = outcome,
                TimeToFirstFrameMs = timeToFirstFrameMs,
                Detail = detail,
            },
            now,
            cancellationToken).ConfigureAwait(false);

        if (outcome == PlaybackOutcome.Ok)
        {
            // Deliberately does not advance. A stream that started can still stall later,
            // and that stall must be attributed to this candidate and fail over from here.
            return null;
        }

        _index++;
        return Current;
    }

    /// <summary>Marks a playing stream as failed and moves on, without re-recording success.</summary>
    /// <remarks>
    /// The stall case: the stream opened, was recorded <see cref="PlaybackOutcome.Ok"/>,
    /// and then stopped delivering. It gets a second row because a stream that opens and
    /// dies is not as good as one that opens and keeps working, and the ranking should
    /// know the difference.
    /// </remarks>
    public Task<StreamCandidate?> ReportStallAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        int? timeToFirstFrameMs = null,
        string? detail = null)
        => ReportAsync(connection, PlaybackOutcome.Stall, now, cancellationToken, timeToFirstFrameMs, detail);
}
