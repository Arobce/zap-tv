using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>What slice of a catalogue to fetch.</summary>
public sealed record CatalogueQuery
{
    /// <summary>Substring match on the title. Null shows everything.</summary>
    public string? Search { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }

    /// <summary>Provider category name to restrict to. Null shows every category.</summary>
    public string? Category { get; init; }
}

/// <summary>One film or series, shaped for a poster grid.</summary>
public sealed record CatalogueItem
{
    /// <summary>
    /// <c>channel_key</c> for a film, <c>series_key</c> for a series.
    /// </summary>
    /// <remarks>
    /// A film's key is the same one the live path uses, so playback and resume position go
    /// through one route rather than two parallel ones.
    /// </remarks>
    public required string Key { get; init; }

    public required string Title { get; init; }

    public string? ImageUrl { get; init; }

    public int? Year { get; init; }

    /// <summary>Genre for a series, container for a film.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Seasons the provider declared. Zero for films.</summary>
    public int SeasonCount { get; init; }

    /// <summary>The <c>series.id</c> episodes hang off. Zero for films.</summary>
    /// <remarks>
    /// The row id, not the series_key. Episodes are fetched per provider listing, and a
    /// key can span several of those.
    /// </remarks>
    public long SeriesRowId { get; init; }
}

/// <summary>
/// Reads the VOD and series catalogues.
/// </summary>
/// <remarks>
/// Separate from <see cref="ChannelRepository"/> because the shapes differ: a film is one
/// playable stream, a series is a container with no stream until its episodes are fetched.
/// <para>
/// Every query pages. On the reference library this is 158,255 films and 49,783 series,
/// and loading them to show twenty is the difference between a view that opens instantly
/// and one that does not open.
/// </para>
/// </remarks>
public static class LibraryRepository
{
    /// <summary>Fetches a page of the VOD catalogue.</summary>
    public static async Task<IReadOnlyList<CatalogueItem>> GetFilmsAsync(
        SqliteConnection connection,
        CatalogueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = connection.CreateCommand();

        // Grouped by channel_key so a film carried by two providers is one entry.
        //
        // Aggregates rather than correlated subqueries. A subquery per group runs for every
        // group before the LIMIT can apply, because the sort needs the computed title:
        // 217ms over 158,255 rows against 40ms for the aggregate form. min() picks a
        // deterministic title rather than the longest one, which is a worse label only when
        // providers disagree, and not worth five times the cost of opening the view.
        command.CommandText =
            """
            SELECT
                s.channel_key,
                min(s.title),
                max(s.logo_url),
                max(s.container)
            FROM streams s
            WHERE s.kind = 'vod'
              AND s.is_active = 1
              AND s.is_separator = 0
              AND (@search IS NULL OR s.title LIKE '%' || @search || '%')
              AND (@category IS NULL OR EXISTS (
                     SELECT 1 FROM categories cat
                      WHERE cat.provider_id = s.provider_id
                        AND cat.kind        = 'vod'
                        AND cat.category_id = s.category_id
                        AND cat.name        = @category))
            GROUP BY s.channel_key
            ORDER BY min(s.title)
            LIMIT @limit OFFSET @offset;
            """;

        AddParameters(command, query);

        var results = new List<CatalogueItem>(query.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CatalogueItem
            {
                Key = reader.GetString(0),
                Title = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                ImageUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                Subtitle = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return results;
    }

    /// <summary>Fetches a page of the series catalogue.</summary>
    public static async Task<IReadOnlyList<CatalogueItem>> GetSeriesAsync(
        SqliteConnection connection,
        CatalogueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = connection.CreateCommand();

        // Grouped by series_key, which exists precisely so the same show from two
        // providers is one entry in the browse view rather than two.
        //
        // Newest first, with undated series last: an absent year must not sort as zero and
        // dominate the top of a 49,783-entry catalogue.
        command.CommandText =
            """
            SELECT
                s.series_key,
                min(s.title),
                max(s.cover_url),
                max(s.year),
                min(s.id)
            FROM series s
            WHERE (@search IS NULL OR s.title LIKE '%' || @search || '%')
            GROUP BY s.series_key
            ORDER BY (max(s.year) IS NULL), max(s.year) DESC, min(s.title)
            LIMIT @limit OFFSET @offset;
            """;

        AddParameters(command, query);

        var results = new List<CatalogueItem>(query.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CatalogueItem
            {
                Key = reader.GetString(0),
                Title = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                ImageUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                Year = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                SeriesRowId = reader.GetInt64(4),
            });
        }

        return results;
    }

    /// <summary>Counts the VOD catalogue without materialising it.</summary>
    public static async Task<int> CountFilmsAsync(
        SqliteConnection connection,
        CatalogueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(DISTINCT s.channel_key)
            FROM streams s
            WHERE s.kind = 'vod'
              AND s.is_active = 1
              AND s.is_separator = 0
              AND (@search IS NULL OR s.title LIKE '%' || @search || '%')
              AND (@category IS NULL OR EXISTS (
                     SELECT 1 FROM categories cat
                      WHERE cat.provider_id = s.provider_id
                        AND cat.kind        = 'vod'
                        AND cat.category_id = s.category_id
                        AND cat.name        = @category));
            """;

        command.Parameters.AddWithValue("@search", (object?)query.Search ?? DBNull.Value);
        command.Parameters.AddWithValue("@category", (object?)query.Category ?? DBNull.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static void AddParameters(SqliteCommand command, CatalogueQuery query)
    {
        command.Parameters.AddWithValue("@search", (object?)query.Search ?? DBNull.Value);
        command.Parameters.AddWithValue("@category", (object?)query.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", query.Limit);
        command.Parameters.AddWithValue("@offset", query.Offset);
    }
}
