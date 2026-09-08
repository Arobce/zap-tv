using Microsoft.Data.Sqlite;

namespace Iptv.Core.Epg;

/// <summary>Whether the guide should be refetched, and why.</summary>
public sealed record EpgRefreshDecision
{
    public required bool ShouldRefresh { get; init; }

    /// <summary>One line, safe to log or show. Always says why, including when refusing.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Decides when to refetch the guide.
/// </summary>
/// <remarks>
/// <para>
/// Automatic refresh is a requirement rather than a nicety: the reference provider
/// publishes about 24 hours ahead, so a guide is empty for anyone who opens the app two
/// days after a sync. Measured on a two-day-old guide, 10 of 3,450 channels had anything
/// on air. See docs/decisions/0010.
/// </para>
/// <para>
/// The download is 64MB, so the throttle matters as much as the trigger. A provider that
/// publishes nothing useful must not be refetched on every launch.
/// </para>
/// </remarks>
public static class EpgRefreshPolicy
{
    /// <summary>The <c>meta</c> key holding when a refresh was last attempted.</summary>
    public const string LastAttemptKey = "epg_last_attempt_utc";

    /// <summary>
    /// No second attempt inside this window, however stale the guide looks.
    /// </summary>
    /// <remarks>
    /// Attempts, not successes. A provider whose guide is permanently short would
    /// otherwise be refetched every launch, downloading 64MB to learn the same thing.
    /// </remarks>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(4);

    /// <summary>Refresh once the guide has less than this left to run.</summary>
    /// <remarks>
    /// Six hours is an evening. Waiting until the guide has actually expired means the
    /// grid is empty at the moment somebody opens it, which is the failure this prevents.
    /// </remarks>
    public static readonly TimeSpan HorizonFloor = TimeSpan.FromHours(6);

    /// <summary>Decides from the stored state.</summary>
    public static EpgRefreshDecision Decide(
        DateTimeOffset? lastAttempt,
        DateTimeOffset? guideEnd,
        DateTimeOffset now)
    {
        // Checked first, and deliberately: it is the guard against hammering a provider,
        // and every reason to refresh below would otherwise override it.
        if (lastAttempt is { } attempted && now - attempted < MinimumInterval)
        {
            var wait = MinimumInterval - (now - attempted);
            return new EpgRefreshDecision
            {
                ShouldRefresh = false,
                Reason = $"tried {Describe(now - attempted)} ago; next attempt in {Describe(wait)}",
            };
        }

        if (guideEnd is not { } end)
        {
            return new EpgRefreshDecision { ShouldRefresh = true, Reason = "no guide stored" };
        }

        var remaining = end - now;

        if (remaining <= TimeSpan.Zero)
        {
            return new EpgRefreshDecision { ShouldRefresh = true, Reason = "the guide has expired" };
        }

        return remaining < HorizonFloor
            ? new EpgRefreshDecision
            {
                ShouldRefresh = true,
                Reason = $"the guide runs out in {Describe(remaining)}",
            }
            : new EpgRefreshDecision
            {
                ShouldRefresh = false,
                Reason = $"the guide covers another {Describe(remaining)}",
            };
    }

    /// <summary>Reads the state and decides.</summary>
    public static async Task<EpgRefreshDecision> DecideAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var lastAttempt = await GetLastAttemptAsync(connection, cancellationToken).ConfigureAwait(false);
        var (_, end) = await EpgGridRepository.GetCoverageAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        return Decide(lastAttempt, end, now);
    }

    /// <summary>When a refresh was last attempted, successful or not.</summary>
    public static async Task<DateTimeOffset?> GetLastAttemptAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = @key;";
        command.Parameters.AddWithValue("@key", LastAttemptKey);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string text && long.TryParse(text, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
    }

    /// <summary>
    /// Records an attempt.
    /// </summary>
    /// <remarks>
    /// Written before the download, not after. A refresh that fails halfway or times out
    /// must still count, or a provider that is down would be retried on every launch.
    /// </remarks>
    public static async Task RecordAttemptAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO meta (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;

        command.Parameters.AddWithValue("@key", LastAttemptKey);
        command.Parameters.AddWithValue("@value", now.ToUnixTimeSeconds().ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "less than a minute";
        }

        return span < TimeSpan.FromHours(1)
            ? $"{(int)span.TotalMinutes}m"
            : $"{span.TotalHours:F1}h";
    }
}
