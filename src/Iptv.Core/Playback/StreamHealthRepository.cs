using Iptv.Core.Sources;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Playback;

/// <summary>One recorded playback attempt.</summary>
public sealed record HealthAttempt
{
    public required long StreamId { get; init; }

    public required PlaybackOutcome Outcome { get; init; }

    /// <summary>Time to first video frame. Null when no frame arrived.</summary>
    public int? TimeToFirstFrameMs { get; init; }

    /// <summary>
    /// Free text for the diagnostics view.
    /// </summary>
    /// <remarks>
    /// Scrubbed on the way in: the natural thing to put here is the URL that failed, and
    /// that carries the account's username and password in its path.
    /// </remarks>
    public string? Detail { get; init; }
}

/// <summary>Per-provider reliability, for the diagnostics view.</summary>
public sealed record ProviderHealth
{
    public required long ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public required int Attempts { get; init; }

    public required int Successes { get; init; }

    /// <summary>Mean time to first frame over successful attempts. Null when there are none.</summary>
    public double? AverageTimeToFirstFrameMs { get; init; }

    public double SuccessRate => Attempts == 0 ? 0 : Successes / (double)Attempts;
}

/// <summary>
/// Reads failover candidates and writes the attempt history they are ranked by.
/// </summary>
/// <remarks>
/// The rolling window is seven days, per the PRD. A provider that was broken last month
/// and is fine now should not be held against forever, and a window shorter than a week
/// misses the weekly pattern of providers that fall over at peak time.
/// </remarks>
public static class StreamHealthRepository
{
    /// <summary>The ranking window from the PRD.</summary>
    public static readonly TimeSpan RollingWindow = TimeSpan.FromDays(7);

    /// <summary>Rows older than this are pruned on start.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Rows kept per stream, however recent.</summary>
    public const int MaxRowsPerStream = 500;

    /// <summary>Fetches every stream that could serve a channel, with its recent record.</summary>
    public static async Task<IReadOnlyList<StreamCandidate>> GetCandidatesAsync(
        SqliteConnection connection,
        string channelKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);

        await using var command = connection.CreateCommand();

        // Aggregated in one pass rather than a per-stream history query. A channel rarely
        // has more than a handful of candidates, but this runs while a stream is stalling
        // and the user is watching a frozen picture.
        command.CommandText =
            """
            SELECT
                s.id,
                s.url,
                s.provider_id,
                pr.name,
                pr.priority,
                s.quality,
                s.title,
                s.normalized_title,
                count(h.stream_id),
                coalesce(sum(h.outcome = 'ok'), 0)
            FROM streams s
            JOIN providers pr ON pr.id = s.provider_id
            LEFT JOIN stream_health h
                   ON h.stream_id = s.id
                  AND h.attempted_utc >= @since
            WHERE s.channel_key = @key
              AND s.is_active = 1
              AND s.is_separator = 0
              AND pr.enabled = 1
            GROUP BY s.id;
            """;

        command.Parameters.AddWithValue("@key", channelKey);
        command.Parameters.AddWithValue("@since", (now - RollingWindow).ToUnixTimeSeconds());

        var results = new List<StreamCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var title = reader.GetString(6);

            results.Add(new StreamCandidate
            {
                StreamId = reader.GetInt64(0),
                Url = reader.GetString(1),
                ProviderId = reader.GetInt64(2),
                ProviderName = reader.GetString(3),
                ProviderPriority = reader.GetInt32(4),
                Quality = reader.IsDBNull(5) ? null : ParseQuality(reader.GetString(5)),

                // Derived from the title here rather than read from a column. The country
                // prefix is per stream and the stored channels.country is per key, so the
                // one value cannot answer a question about several differently-titled
                // streams sharing that key - which is exactly the case the guard is for.
                Country = ChannelNormalizer.ExtractCountry(title),
                NormalizedTitle = reader.GetString(7),
                Attempts = reader.GetInt32(8),
                Successes = reader.GetInt32(9),
            });
        }

        return results;
    }

    /// <summary>Builds the guarded, ordered plan for a channel.</summary>
    public static async Task<FailoverPlan> PlanAsync(
        SqliteConnection connection,
        string channelKey,
        DateTimeOffset now,
        QualityPreference preference,
        CancellationToken cancellationToken)
    {
        var candidates = await GetCandidatesAsync(connection, channelKey, now, cancellationToken)
            .ConfigureAwait(false);

        return FailoverPolicy.Plan(channelKey, candidates, preference);
    }

    /// <summary>Appends one attempt.</summary>
    public static async Task RecordAsync(
        SqliteConnection connection,
        HealthAttempt attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(attempt);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO stream_health (stream_id, attempted_utc, outcome, ttfb_ms, detail)
            VALUES (@stream, @at, @outcome, @ttfb, @detail);
            """;

        command.Parameters.AddWithValue("@stream", attempt.StreamId);
        command.Parameters.AddWithValue("@at", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@outcome", ToStorage(attempt.Outcome));
        command.Parameters.AddWithValue("@ttfb", (object?)attempt.TimeToFirstFrameMs ?? DBNull.Value);

        // Scrubbed unconditionally. Callers pass mpv's own error text, which quotes the
        // URL it failed on, and this table is read straight into a diagnostics view.
        command.Parameters.AddWithValue(
            "@detail",
            attempt.Detail is null ? DBNull.Value : Xtream.CredentialScrubber.Scrub(attempt.Detail));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Per-provider reliability over the rolling window.</summary>
    public static async Task<IReadOnlyList<ProviderHealth>> GetProviderHealthAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();

        // Time-to-first-frame averaged over successes only. Including failures would mix
        // in the rows where no frame ever arrived and make a provider that fails fast look
        // quicker than one that works.
        command.CommandText =
            """
            SELECT
                pr.id,
                pr.name,
                count(*),
                coalesce(sum(h.outcome = 'ok'), 0),
                avg(CASE WHEN h.outcome = 'ok' THEN h.ttfb_ms END)
            FROM stream_health h
            JOIN streams s   ON s.id = h.stream_id
            JOIN providers pr ON pr.id = s.provider_id
            WHERE h.attempted_utc >= @since
            GROUP BY pr.id
            ORDER BY pr.priority, pr.name;
            """;

        command.Parameters.AddWithValue("@since", (now - RollingWindow).ToUnixTimeSeconds());

        var results = new List<ProviderHealth>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ProviderHealth
            {
                ProviderId = reader.GetInt64(0),
                ProviderName = reader.GetString(1),
                Attempts = reader.GetInt32(2),
                Successes = reader.GetInt32(3),
                AverageTimeToFirstFrameMs = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            });
        }

        return results;
    }

    /// <summary>Applies the retention policy. Call on start.</summary>
    /// <returns>Rows deleted.</returns>
    /// <remarks>
    /// Two rules, both needed. Age alone lets one channel that is retried in a loop grow
    /// without bound inside the window; the per-stream cap alone keeps ancient rows for a
    /// stream nobody watches any more.
    /// </remarks>
    public static async Task<int> PruneAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        int deleted;

        await using (var byAge = connection.CreateCommand())
        {
            byAge.Transaction = (SqliteTransaction)transaction;
            byAge.CommandText = "DELETE FROM stream_health WHERE attempted_utc < @cutoff;";
            byAge.Parameters.AddWithValue("@cutoff", (now - Retention).ToUnixTimeSeconds());
            deleted = await byAge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var byCount = connection.CreateCommand())
        {
            byCount.Transaction = (SqliteTransaction)transaction;

            // rowid, because stream_health has no primary key and rows are otherwise
            // indistinguishable when two attempts land in the same second.
            byCount.CommandText =
                """
                DELETE FROM stream_health
                WHERE rowid IN (
                    SELECT rowid FROM (
                        SELECT
                            rowid,
                            row_number() OVER (
                                PARTITION BY stream_id
                                ORDER BY attempted_utc DESC, rowid DESC
                            ) AS rank
                        FROM stream_health
                    )
                    WHERE rank > @keep
                );
                """;

            byCount.Parameters.AddWithValue("@keep", MaxRowsPerStream);
            deleted += await byCount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private static string ToStorage(PlaybackOutcome outcome) => outcome switch
    {
        PlaybackOutcome.Ok => "ok",
        PlaybackOutcome.Timeout => "timeout",
        PlaybackOutcome.HttpError => "http_error",
        PlaybackOutcome.Stall => "stall",
        PlaybackOutcome.DecodeError => "decode_error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unmapped outcome"),
    };

    private static Quality? ParseQuality(string stored)
        => Enum.TryParse<Quality>(stored, ignoreCase: true, out var quality) ? quality : null;
}
