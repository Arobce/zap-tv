using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>
/// Merges a provider's series listing into the library.
/// </summary>
/// <remarks>
/// Separate from <see cref="ProviderSync"/> because series live in their own table and
/// carry no streams until a user opens one. Episodes are fetched then, not here: the
/// reference provider lists 49,748 series and each would need its own
/// <c>get_series_info</c> request.
/// </remarks>
public static class SeriesSync
{
    /// <summary>Upserts a provider's series, preserving anything already stored.</summary>
    public static async Task<SyncSummary> SyncAsync(
        SqliteConnection connection,
        int providerId,
        IEnumerable<SeriesRecord> series,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(series);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var added = 0;
        var updated = 0;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;

            // One prepared command with parameters reset per row, per the conventions.
            // tmdb_id is deliberately not written: it is populated by the optional TMDB
            // enrichment and a provider sync must not clear it.
            command.CommandText =
                """
                INSERT INTO series
                    (provider_id, provider_series_id, title, normalized_title, series_key,
                     plot, cover_url, year, category_id)
                VALUES (@provider_id, @provider_series_id, @title, @normalized_title, @series_key,
                        @plot, @cover_url, @year, @category_id)
                ON CONFLICT(provider_id, provider_series_id) DO UPDATE SET
                    title            = excluded.title,
                    normalized_title = excluded.normalized_title,
                    series_key       = excluded.series_key,
                    plot             = excluded.plot,
                    cover_url        = excluded.cover_url,
                    year             = excluded.year,
                    category_id      = excluded.category_id;
                """;

            foreach (var name in new[]
                     {
                         "@provider_id", "@provider_series_id", "@title", "@normalized_title",
                         "@series_key", "@plot", "@cover_url", "@year", "@category_id",
                     })
            {
                command.Parameters.Add(name, SqliteType.Text);
            }

            foreach (var record in series)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var existed = await ExistsAsync(
                    connection, (SqliteTransaction)transaction, providerId, record.ProviderSeriesId)
                    .ConfigureAwait(false);

                command.Parameters["@provider_id"].Value = providerId;
                command.Parameters["@provider_series_id"].Value = record.ProviderSeriesId;
                command.Parameters["@title"].Value = record.Title;
                command.Parameters["@normalized_title"].Value = record.NormalizedTitle;
                command.Parameters["@series_key"].Value = record.SeriesKey;
                command.Parameters["@plot"].Value = (object?)record.Plot ?? DBNull.Value;
                command.Parameters["@cover_url"].Value = (object?)record.CoverUrl ?? DBNull.Value;
                command.Parameters["@year"].Value = (object?)record.Year ?? DBNull.Value;
                command.Parameters["@category_id"].Value = (object?)record.CategoryId ?? DBNull.Value;

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (existed)
                {
                    updated++;
                }
                else
                {
                    added++;
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SyncSummary(added, updated, 0, 0, 0);
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int providerId,
        string providerSeriesId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM series WHERE provider_id = @p AND provider_series_id = @s;";
        command.Parameters.AddWithValue("@p", providerId);
        command.Parameters.AddWithValue("@s", providerSeriesId);

        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }
}
