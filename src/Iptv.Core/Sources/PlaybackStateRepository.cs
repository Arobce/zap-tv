using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>Where a piece of content was left.</summary>
public sealed record PlaybackPosition
{
    public required string ContentKey { get; init; }

    public required int PositionSeconds { get; init; }

    /// <summary>Total length, when the player knew it.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>Whether it was watched to the end.</summary>
    public required bool Completed { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>How far through, 0 to 1, or null when the length is unknown.</summary>
    public double? Progress => DurationSeconds is > 0
        ? Math.Clamp(PositionSeconds / (double)DurationSeconds.Value, 0, 1)
        : null;
}

/// <summary>
/// Remembers where films and episodes were left.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on <c>content_key</c> rather than a stream id, so a position survives a provider
/// re-sync that renumbers everything, and so the same film from two providers is one
/// position rather than two.
/// </para>
/// <para>
/// Live television is deliberately not recorded. A position in a broadcast means nothing
/// an hour later, and writing one would fill the table with rows that can never be used.
/// </para>
/// </remarks>
public static class PlaybackStateRepository
{
    /// <summary>
    /// Below this, nothing is recorded.
    /// </summary>
    /// <remarks>
    /// Opening something and closing it again is not watching it. Without a floor, every
    /// glance at a film leaves a row, and the continue-watching list fills with things
    /// nobody started.
    /// </remarks>
    public const int MinimumSeconds = 30;

    /// <summary>Watched counts as past this fraction of the length...</summary>
    public const double CompletionFraction = 0.95;

    /// <summary>...and within this many seconds of the end.</summary>
    /// <remarks>
    /// Both, not either. Each measure is wrong on its own at one end of the range: 95% of
    /// a two-hour film still leaves six minutes to watch, and two minutes from the end of a
    /// twelve-minute episode is only 83% of it. Requiring both takes the later of the two
    /// thresholds, which is the one that means "actually finished" in each case.
    /// </remarks>
    public const int CompletionTailSeconds = 120;

    /// <summary>Records a position, marking it complete when it is near the end.</summary>
    /// <returns>True when something was written.</returns>
    public static async Task<bool> SaveAsync(
        SqliteConnection connection,
        string contentKey,
        int positionSeconds,
        int? durationSeconds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);

        if (positionSeconds < MinimumSeconds)
        {
            // Left alone rather than cleared. Restarting something and abandoning it after
            // ten seconds should not destroy the position from last night.
            return false;
        }

        var completed = IsComplete(positionSeconds, durationSeconds);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO playback_state (content_key, position_secs, duration_secs, completed, updated_utc)
            VALUES (@key, @position, @duration, @completed, @now)
            ON CONFLICT(content_key) DO UPDATE SET
                position_secs = excluded.position_secs,
                duration_secs = COALESCE(excluded.duration_secs, playback_state.duration_secs),
                completed     = excluded.completed,
                updated_utc   = excluded.updated_utc;
            """;

        command.Parameters.AddWithValue("@key", contentKey);
        command.Parameters.AddWithValue("@position", positionSeconds);
        command.Parameters.AddWithValue("@duration", (object?)durationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("@now", now.ToUnixTimeSeconds());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Whether a position counts as having reached the end.</summary>
    public static bool IsComplete(int positionSeconds, int? durationSeconds)
    {
        if (durationSeconds is not > 0)
        {
            // Length unknown, so there is no end to be near. Recorded as unwatched, which
            // at worst offers a resume the user declines.
            return false;
        }

        var duration = durationSeconds.Value;
        return positionSeconds >= duration - CompletionTailSeconds
               && positionSeconds >= duration * CompletionFraction;
    }

    /// <summary>The stored position, whatever its state.</summary>
    public static async Task<PlaybackPosition?> GetAsync(
        SqliteConnection connection,
        string contentKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT content_key, position_secs, duration_secs, completed, updated_utc
            FROM playback_state
            WHERE content_key = @key;
            """;

        command.Parameters.AddWithValue("@key", contentKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>
    /// Where playback should start, or null to start at the beginning.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetAsync"/> because "what is stored" and "where do I start"
    /// are different questions. Something watched to the end has a large stored position
    /// and should still start from zero, which is the case a single method gets wrong.
    /// </remarks>
    public static async Task<int?> GetResumeSecondsAsync(
        SqliteConnection connection,
        string contentKey,
        CancellationToken cancellationToken)
    {
        var stored = await GetAsync(connection, contentKey, cancellationToken).ConfigureAwait(false);

        return stored is { Completed: false } && stored.PositionSeconds >= MinimumSeconds
            ? stored.PositionSeconds
            : null;
    }

    /// <summary>Recently watched, unfinished things, newest first.</summary>
    public static async Task<IReadOnlyList<PlaybackPosition>> GetContinueWatchingAsync(
        SqliteConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT content_key, position_secs, duration_secs, completed, updated_utc
            FROM playback_state
            WHERE completed = 0
            ORDER BY updated_utc DESC
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<PlaybackPosition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Read(reader));
        }

        return results;
    }

    /// <summary>Forgets a position, so it starts from the beginning again.</summary>
    public static async Task ClearAsync(
        SqliteConnection connection,
        string contentKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_state WHERE content_key = @key;";
        command.Parameters.AddWithValue("@key", contentKey);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PlaybackPosition Read(SqliteDataReader reader) => new()
    {
        ContentKey = reader.GetString(0),
        PositionSeconds = reader.GetInt32(1),
        DurationSeconds = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        Completed = reader.GetInt64(3) == 1,
        UpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
    };
}
