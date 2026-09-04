using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>What a sync changed, for the Providers view and the logs.</summary>
public sealed record SyncSummary(int Added, int Updated, int Reactivated, int Deactivated, int Pruned)
{
    public static readonly SyncSummary Empty = new(0, 0, 0, 0, 0);

    public int TotalSeen => Added + Updated + Reactivated;
}

/// <summary>
/// Merges a provider's catalogue into the library.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sync is a merge, never a replace.</b> Favourites, hidden channels, sort order, EPG
/// mappings and resume positions all hang off keys that a delete-and-reinsert would
/// destroy, and they are the only part of the library the user actually created.
/// </para>
/// <para>
/// Absence from one payload is not evidence a channel is gone. Providers drop channels for
/// hours at a time, so a missing stream is deactivated and only removed once it has been
/// absent for <see cref="PruneAfterDays"/> days.
/// </para>
/// </remarks>
public static class ProviderSync
{
    /// <summary>How long a stream must stay absent before it is deleted outright.</summary>
    private const int PruneAfterDays = 30;

    /// <summary>Merges <paramref name="streams"/> into the library for one provider.</summary>
    public static async Task<SyncSummary> SyncAsync(
        SqliteConnection connection,
        int providerId,
        IEnumerable<StreamRecord> streams,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(streams);

        var timestamp = now.ToUnixTimeSeconds();

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var added = 0;
        var updated = 0;
        var reactivated = 0;

        await PrepareSeenTableAsync(
            connection, (SqliteTransaction)transaction, cancellationToken).ConfigureAwait(false);

        await using (var upsert = CreateUpsertCommand(connection, (SqliteTransaction)transaction))
        {
            foreach (var record in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outcome = await UpsertAsync(upsert, record, providerId, timestamp)
                    .ConfigureAwait(false);

                switch (outcome)
                {
                    case UpsertOutcome.Inserted:
                        added++;
                        break;
                    case UpsertOutcome.Reactivated:
                        reactivated++;
                        break;
                    case UpsertOutcome.Updated:
                        updated++;
                        break;
                    default:
                        break;
                }
            }
        }

        var deactivated = await DeactivateAbsentAsync(
            connection, (SqliteTransaction)transaction, providerId, cancellationToken)
            .ConfigureAwait(false);

        var pruned = await PruneLongAbsentAsync(
            connection, (SqliteTransaction)transaction, providerId, timestamp, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SyncSummary(added, updated, reactivated, deactivated, pruned);
    }

    /// <summary>
    /// Ensures a <c>channels</c> row exists for every distinct key, without disturbing
    /// user-owned columns.
    /// </summary>
    /// <remarks>
    /// The <c>ON CONFLICT</c> clause updates only provider-derived columns. Listing them
    /// explicitly rather than replacing the row is what keeps <c>is_favorite</c>,
    /// <c>is_hidden</c> and <c>user_sort_order</c> intact across a re-sync.
    /// <para>
    /// Separator rows are excluded: they are shown as section headings but are not
    /// channels, so they must not be favouritable, failed over to, or counted in the EPG
    /// coverage denominator.
    /// </para>
    /// </remarks>
    public static async Task<int> RefreshChannelsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO channels (channel_key, display_name, logo_url, country)
            SELECT
                s.channel_key,
                -- Prefer the longest title: providers abbreviate inconsistently and the
                -- fuller name is the more useful label.
                (SELECT t.title FROM streams t
                  WHERE t.channel_key = s.channel_key AND t.is_separator = 0
                  ORDER BY length(t.title) DESC, t.title ASC LIMIT 1),
                (SELECT t.logo_url FROM streams t
                  WHERE t.channel_key = s.channel_key AND t.logo_url IS NOT NULL
                  LIMIT 1),
                (SELECT t.country FROM streams t
                  WHERE t.channel_key = s.channel_key AND t.country IS NOT NULL
                  LIMIT 1)
            FROM streams s
            WHERE s.is_separator = 0
            GROUP BY s.channel_key
            ON CONFLICT(channel_key) DO UPDATE SET
                display_name = excluded.display_name,
                logo_url     = COALESCE(excluded.logo_url, channels.logo_url),
                country      = COALESCE(excluded.country, channels.country);
            """;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private enum UpsertOutcome
    {
        Unchanged,
        Inserted,
        Updated,
        Reactivated,
    }

    private static SqliteCommand CreateUpsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;

        // One prepared command reused across every row, per the conventions, with
        // parameter values reset per row rather than the command being rebuilt.
        //
        // The ON CONFLICT clause lists provider-derived columns only. That is what makes
        // this a merge: a row's identity, and anything the user owns, is left alone.
        //
        // Recording the row in temp.sync_seen is part of the same statement batch so a
        // stream can never be written without being marked as seen, which would then
        // deactivate it moments later in the same transaction.
        command.CommandText =
            """
            INSERT INTO streams (
                provider_id, provider_stream_id, kind, title, normalized_title, tvg_id,
                logo_url, category_id, url, container, quality, country, channel_key,
                catchup_kind, catchup_source, catchup_days, is_separator,
                is_active, last_seen_utc)
            VALUES (
                @provider_id, @provider_stream_id, @kind, @title, @normalized_title, @tvg_id,
                @logo_url, @category_id, @url, @container, @quality, @country, @channel_key,
                @catchup_kind, @catchup_source, @catchup_days, @is_separator,
                1, @seen)
            ON CONFLICT(provider_id, provider_stream_id, kind) DO UPDATE SET
                title            = excluded.title,
                normalized_title = excluded.normalized_title,
                tvg_id           = excluded.tvg_id,
                logo_url         = excluded.logo_url,
                category_id      = excluded.category_id,
                url              = excluded.url,
                container        = excluded.container,
                quality          = excluded.quality,
                country          = excluded.country,
                channel_key      = excluded.channel_key,
                catchup_kind     = excluded.catchup_kind,
                catchup_source   = excluded.catchup_source,
                catchup_days     = excluded.catchup_days,
                is_separator     = excluded.is_separator,
                is_active        = 1,
                last_seen_utc    = excluded.last_seen_utc;

            INSERT OR IGNORE INTO temp.sync_seen (provider_stream_id, kind)
            VALUES (@provider_stream_id, @kind);
            """;

        foreach (var name in new[]
                 {
                     "@provider_id", "@provider_stream_id", "@kind", "@title", "@normalized_title",
                     "@tvg_id", "@logo_url", "@category_id", "@url", "@container", "@quality",
                     "@country", "@channel_key", "@catchup_kind", "@catchup_source",
                     "@catchup_days", "@is_separator", "@seen",
                 })
        {
            command.Parameters.Add(name, SqliteType.Text);
        }

        return command;
    }

    private static async Task<UpsertOutcome> UpsertAsync(
        SqliteCommand command,
        StreamRecord record,
        int providerId,
        long timestamp)
    {
        // Determining insert-vs-update before writing, because ON CONFLICT makes the two
        // indistinguishable afterwards and the summary has to tell them apart.
        var existing = await ReadExistingAsync(command.Connection!, command.Transaction!,
            providerId, record.ProviderStreamId, record.Kind).ConfigureAwait(false);

        command.Parameters["@provider_id"].Value = providerId;
        command.Parameters["@provider_stream_id"].Value = record.ProviderStreamId;
        command.Parameters["@kind"].Value = ToDbKind(record.Kind);
        command.Parameters["@title"].Value = record.Title;
        command.Parameters["@normalized_title"].Value = record.NormalizedTitle;
        command.Parameters["@tvg_id"].Value = (object?)record.TvgId ?? DBNull.Value;
        command.Parameters["@logo_url"].Value = (object?)record.LogoUrl ?? DBNull.Value;
        command.Parameters["@category_id"].Value = (object?)record.CategoryId ?? DBNull.Value;
        command.Parameters["@url"].Value = record.Url;
        command.Parameters["@container"].Value = (object?)record.Container ?? DBNull.Value;
        command.Parameters["@quality"].Value = (object?)record.Quality?.ToString() ?? DBNull.Value;
        command.Parameters["@country"].Value = (object?)record.Country ?? DBNull.Value;
        command.Parameters["@channel_key"].Value = record.ChannelKey;
        command.Parameters["@catchup_kind"].Value = (object?)record.CatchupKind ?? DBNull.Value;
        command.Parameters["@catchup_source"].Value = (object?)record.CatchupSource ?? DBNull.Value;
        command.Parameters["@catchup_days"].Value = (object?)record.CatchupDays ?? DBNull.Value;
        command.Parameters["@is_separator"].Value = record.IsSeparator ? 1 : 0;
        command.Parameters["@seen"].Value = timestamp;

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        return existing switch
        {
            null => UpsertOutcome.Inserted,
            false => UpsertOutcome.Reactivated,
            _ => UpsertOutcome.Updated,
        };
    }

    /// <summary>Returns the row's active flag, or null when it does not exist.</summary>
    private static async Task<bool?> ReadExistingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int providerId,
        string providerStreamId,
        StreamKind kind)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT is_active FROM streams
            WHERE provider_id = @provider_id
              AND provider_stream_id = @provider_stream_id
              AND kind = @kind;
            """;
        command.Parameters.AddWithValue("@provider_id", providerId);
        command.Parameters.AddWithValue("@provider_stream_id", providerStreamId);
        command.Parameters.AddWithValue("@kind", ToDbKind(kind));

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Creates the per-run table recording which streams this payload contained.
    /// </summary>
    /// <remarks>
    /// Membership is tracked explicitly rather than inferred from <c>last_seen_utc</c>.
    /// A timestamp comparison silently fails whenever two syncs share a clock value -
    /// a manual refresh straight after an automatic one, a coarse clock, or a test using a
    /// fixed <c>now</c> - and the symptom is a removed channel that never deactivates.
    /// </remarks>
    private static async Task PrepareSeenTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DROP TABLE IF EXISTS temp.sync_seen;
            CREATE TEMP TABLE sync_seen (
                provider_stream_id TEXT NOT NULL,
                kind               TEXT NOT NULL,
                PRIMARY KEY (provider_stream_id, kind)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks streams the provider no longer lists as inactive.</summary>
    private static async Task<int> DeactivateAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int providerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // Scoped to one provider: another provider's catalogue is none of this sync's
        // business, and deactivating it would silently halve a multi-provider library.
        command.CommandText =
            """
            UPDATE streams
               SET is_active = 0
             WHERE provider_id = @provider_id
               AND is_active = 1
               AND NOT EXISTS (
                   SELECT 1 FROM temp.sync_seen s
                    WHERE s.provider_stream_id = streams.provider_stream_id
                      AND s.kind = streams.kind);
            """;
        command.Parameters.AddWithValue("@provider_id", providerId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes streams that have been absent long enough to be considered gone.</summary>
    private static async Task<int> PruneLongAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int providerId,
        long timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM streams
             WHERE provider_id = @provider_id
               AND is_active = 0
               AND last_seen_utc IS NOT NULL
               AND last_seen_utc < @cutoff;
            """;
        command.Parameters.AddWithValue("@provider_id", providerId);
        command.Parameters.AddWithValue(
            "@cutoff", timestamp - (PruneAfterDays * 24L * 60 * 60));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToDbKind(StreamKind kind) => kind switch
    {
        StreamKind.Live => "live",
        StreamKind.Vod => "vod",
        StreamKind.SeriesEpisode => "series_episode",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown stream kind."),
    };
}
