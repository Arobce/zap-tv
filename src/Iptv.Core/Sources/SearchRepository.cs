using System.Text;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>What kind of thing a search result is.</summary>
public enum SearchHitKind
{
    Channel,
    Film,
    Series,

    /// <summary>A programme in the guide, which plays the channel showing it.</summary>
    Programme,
}

/// <summary>One search result.</summary>
public sealed record SearchHit
{
    public required SearchHitKind Kind { get; init; }

    /// <summary><c>channel_key</c> for a channel, film or programme; <c>series_key</c> for a series.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>Where it is, or when. Enough to tell two identical titles apart.</summary>
    public required string Subtitle { get; init; }

    /// <summary>The <c>series.id</c> to open. Zero unless this is a series.</summary>
    public long SeriesRowId { get; init; }
}

/// <summary>
/// One search across everything.
/// </summary>
/// <remarks>
/// <para>
/// FTS5, not <c>LIKE</c>. A leading-wildcard LIKE cannot use an index and scans every row;
/// the PRD budgets 80ms per keystroke against a library of this size, and the per-tab LIKE
/// search this replaces was already costing 39ms on films alone.
/// </para>
/// <para>
/// Both indexes are external-content, which means they hold no data of their own and are
/// rebuilt rather than maintained by triggers. Triggers would put an index write on the
/// bulk ingest path, which the PRD's throughput budget cannot afford.
/// </para>
/// </remarks>
public static class SearchRepository
{
    /// <summary>
    /// How many FTS matches to consider per requested result.
    /// </summary>
    /// <remarks>
    /// The match is limited before anything joins or groups it, which is the difference
    /// between 2ms and 2002ms. This factor is the slack that makes that safe: results are
    /// deduplicated by key afterwards, so the top 20 matches could collapse to one row if
    /// a title is carried by twenty providers. Twenty times the page is enough that it
    /// does not, and small enough that the limit still does its job.
    /// </remarks>
    private const int ScanFactor = 20;

    /// <summary>
    /// The same slack for programmes, which need far less of it.
    /// </summary>
    /// <remarks>
    /// Programmes are not deduplicated — each is its own row — so the only thing that
    /// discards inner results is the filter dropping ones that have already finished, and
    /// a guide holds roughly as much past as future. Five times the page covers that;
    /// twenty cost 31ms against 11ms for no extra results.
    /// </remarks>
    private const int ProgrammeScanFactor = 5;

    /// <summary>Turns what the user typed into an FTS5 query.</summary>
    /// <remarks>
    /// FTS5 has its own operators — <c>OR</c>, <c>NEAR</c>, <c>*</c>, <c>"</c>, <c>-</c> —
    /// and a search box is free text, not a query language. Every token is quoted so those
    /// characters are literal, and given a trailing <c>*</c> so results appear while the
    /// word is still being typed.
    /// </remarks>
    public static string? BuildMatch(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var query = new StringBuilder();

        foreach (var token in term.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Quotes are the escape: a double quote inside a quoted token is doubled, and
            // everything else loses its meaning to FTS5.
            var cleaned = token.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();

            if (cleaned.Length == 0)
            {
                continue;
            }

            if (query.Length > 0)
            {
                query.Append(' ');
            }

            query.Append('"').Append(cleaned).Append("\"*");
        }

        return query.Length == 0 ? null : query.ToString();
    }

    /// <summary>Searches channels, films, series and the guide at once.</summary>
    /// <param name="limitPerKind">
    /// How many of each kind to return.
    /// </param>
    /// <remarks>
    /// Per kind rather than overall, so a term matching ten thousand films still leaves
    /// room for the one channel that matches it. A single ranked list would bury the
    /// channel under the catalogue.
    /// </remarks>
    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SqliteConnection connection,
        string? term,
        int limitPerKind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (BuildMatch(term) is not { } match)
        {
            return [];
        }

        var results = new List<SearchHit>();

        await AddStreamsAsync(connection, match, "live", SearchHitKind.Channel, limitPerKind,
            results, cancellationToken).ConfigureAwait(false);

        await AddStreamsAsync(connection, match, "vod", SearchHitKind.Film, limitPerKind,
            results, cancellationToken).ConfigureAwait(false);

        await AddSeriesAsync(connection, match, limitPerKind, results, cancellationToken)
            .ConfigureAwait(false);

        await AddProgrammesAsync(connection, match, limitPerKind, now, results, cancellationToken)
            .ConfigureAwait(false);

        return results;
    }

    private static async Task AddStreamsAsync(
        SqliteConnection connection,
        string match,
        string kind,
        SearchHitKind hitKind,
        int limit,
        List<SearchHit> results,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // The FTS match is limited before anything joins it. Grouping first and limiting
        // afterwards costs 2002ms against 2ms for this shape: the group and the ordering
        // both have to materialise every match before the limit can apply, and a common
        // word matches thousands of a quarter-million streams.
        //
        // The inner limit is generous so that deduplicating by channel_key afterwards
        // still leaves a full page. A title carried by twenty providers would otherwise
        // collapse into one result and take the page with it.
        command.CommandText =
            """
            SELECT s.channel_key, min(s.title), count(*)
            FROM (
                SELECT rowid FROM streams_fts
                 WHERE streams_fts MATCH @match
                 ORDER BY rank
                 LIMIT @scan
            ) f
            JOIN streams s ON s.id = f.rowid
            JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
            WHERE s.kind = @kind
              AND s.is_active = 1
              AND s.is_separator = 0
            GROUP BY s.channel_key
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@match", match);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@scan", limit * ScanFactor);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var copies = reader.GetInt32(2);

            results.Add(new SearchHit
            {
                Kind = hitKind,
                Key = reader.GetString(0),
                Title = reader.GetString(1),
                Subtitle = hitKind == SearchHitKind.Channel
                    ? copies > 1 ? $"channel · {copies} sources" : "channel"
                    : "film",
            });
        }
    }

    private static async Task AddSeriesAsync(
        SqliteConnection connection,
        string match,
        int limit,
        List<SearchHit> results,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // FTS, like the other three. This was a LIKE, and it was the slowest part of a
        // search at 23ms scanning 49,783 rows: a leading-wildcard LIKE cannot use a
        // B-tree, so the only way to make it fast was to stop using one.
        command.CommandText =
            """
            SELECT s.series_key, min(s.title), min(s.id), max(s.year)
            FROM (
                SELECT rowid FROM series_fts
                 WHERE series_fts MATCH @match
                 ORDER BY rank
                 LIMIT @scan
            ) f
            JOIN series s ON s.id = f.rowid
            JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
            GROUP BY s.series_key
            ORDER BY (max(s.year) IS NULL), max(s.year) DESC, min(s.title)
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@match", match);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@scan", limit * ScanFactor);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var year = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3);

            results.Add(new SearchHit
            {
                Kind = SearchHitKind.Series,
                Key = reader.GetString(0),
                Title = reader.GetString(1),
                Subtitle = year is { } y ? $"series · {y}" : "series",
                SeriesRowId = reader.GetInt64(2),
            });
        }
    }

    private static async Task AddProgrammesAsync(
        SqliteConnection connection,
        string match,
        int limit,
        DateTimeOffset now,
        List<SearchHit> results,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Only what has not finished. A guide holds a day of the past as well as the
        // future, and offering to watch something that ended this morning is worse than
        // offering nothing.
        command.CommandText =
            """
            SELECT p.title, p.start_utc, m.channel_key, c.display_name
            FROM (
                SELECT rowid FROM programmes_fts
                 WHERE programmes_fts MATCH @match
                 ORDER BY rank
                 LIMIT @scan
            ) f
            JOIN programmes p ON p.id = f.rowid
            JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
            JOIN channels c ON c.channel_key = m.channel_key
            WHERE p.stop_utc > @now
            ORDER BY p.start_utc
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@match", match);
        command.Parameters.AddWithValue("@now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@scan", limit * ProgrammeScanFactor);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToLocalTime();
            var onNow = start <= now.ToLocalTime();

            results.Add(new SearchHit
            {
                Kind = SearchHitKind.Programme,
                Key = reader.GetString(2),
                Title = reader.GetString(0),

                // The channel and the time, because a programme title alone does not say
                // where to watch it or whether it has started.
                Subtitle = onNow
                    ? $"on now · {reader.GetString(3)}"
                    : $"{start:ddd HH:mm} · {reader.GetString(3)}",
            });
        }
    }

    /// <summary>
    /// Rebuilds the stream search index.
    /// </summary>
    /// <remarks>
    /// External-content FTS5 holds no data of its own; without this the index is empty and
    /// every search returns nothing, which is what it did until now. Rebuilt after a sync
    /// rather than maintained by triggers, because a trigger would put an index write on
    /// the bulk ingest path the PRD budgets by the hundred thousand rows.
    /// </remarks>
    public static async Task RebuildStreamIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams_fts(streams_fts) VALUES('rebuild');
            INSERT INTO series_fts(series_fts) VALUES('rebuild');
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
