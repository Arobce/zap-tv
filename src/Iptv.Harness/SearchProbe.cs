using System.Diagnostics;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Times each part of a search separately.
/// </summary>
/// <remarks>
/// Written because the combined search came in at 8.7 to 19.8 seconds against an 80ms
/// budget, and "the search is slow" does not say which of four queries is slow or why. The
/// last time a query was guessed at rather than measured, two fixes in a row made it worse.
/// </remarks>
internal static class SearchProbe
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var term = args.Length > 1 ? args[1] : "news";

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        await using var connection = await new SqliteConnectionFactory(databasePath)
            .OpenAsync(cancellationToken).ConfigureAwait(false);

        var match = SearchRepository.BuildMatch(term)!;
        Console.WriteLine($"== '{term}' as FTS5: {match} ==");
        Console.WriteLine();

        await TimeAsync("bare fts match, live", connection,
            """
            SELECT count(*) FROM streams_fts f
            JOIN streams s ON s.id = f.rowid
            WHERE streams_fts MATCH @match AND s.kind = 'live';
            """, match, cancellationToken).ConfigureAwait(false);

        await TimeAsync("fts match only", connection,
            "SELECT count(*) FROM streams_fts WHERE streams_fts MATCH @match;",
            match, cancellationToken).ConfigureAwait(false);

        await TimeAsync("with rank ordering", connection,
            """
            SELECT f.rowid FROM streams_fts f
            WHERE streams_fts MATCH @match
            ORDER BY rank
            LIMIT 200;
            """, match, cancellationToken).ConfigureAwait(false);

        await TimeAsync("grouped, as shipped", connection,
            """
            SELECT s.channel_key, min(s.title), count(*), min(f.rank)
            FROM streams_fts f
            JOIN streams s ON s.id = f.rowid
            JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
            WHERE streams_fts MATCH @match
              AND s.kind = 'live' AND s.is_active = 1 AND s.is_separator = 0
            GROUP BY s.channel_key
            ORDER BY min(f.rank)
            LIMIT 20;
            """, match, cancellationToken).ConfigureAwait(false);

        await TimeAsync("limited inner, then grouped", connection,
            """
            SELECT s.channel_key, min(s.title), count(*)
            FROM (SELECT rowid FROM streams_fts WHERE streams_fts MATCH @match LIMIT 400) f
            JOIN streams s ON s.id = f.rowid
            JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
            WHERE s.kind = 'live' AND s.is_active = 1 AND s.is_separator = 0
            GROUP BY s.channel_key
            LIMIT 20;
            """, match, cancellationToken).ConfigureAwait(false);

        await TimeAsync("programmes, limited inner", connection,
            """
            SELECT p.title
            FROM (SELECT rowid FROM programmes_fts WHERE programmes_fts MATCH @match
                   ORDER BY rank LIMIT 400) f
            JOIN programmes p ON p.id = f.rowid
            JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
            JOIN channels c ON c.channel_key = m.channel_key
            WHERE p.stop_utc > 0
            ORDER BY p.start_utc
            LIMIT 20;
            """, match, cancellationToken).ConfigureAwait(false);

        await TimeAsync("series LIKE", connection,
            """
            SELECT s.series_key, min(s.title), min(s.id), max(s.year)
            FROM series s
            JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
            WHERE s.title LIKE Q
            GROUP BY s.series_key
            ORDER BY (max(s.year) IS NULL), max(s.year) DESC, min(s.title)
            LIMIT 20;
            """.Replace("Q", "'%sport%'"), match, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== full SearchAsync, repeated on one connection ==");
        for (var i = 0; i < 4; i++)
        {
            var sw = Stopwatch.StartNew();
            var hits = await SearchRepository.SearchAsync(
                connection, term, 20, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            Console.WriteLine($"  run {i + 1,-27} {sw.ElapsedMilliseconds,7}ms   {hits.Count} hits");
        }

        Console.WriteLine();
        Console.WriteLine("== plans ==");
        await PlanAsync(connection,
            """
            SELECT s.channel_key, min(f.rank)
            FROM streams_fts f
            JOIN streams s ON s.id = f.rowid
            WHERE streams_fts MATCH @match AND s.kind = 'live'
            GROUP BY s.channel_key ORDER BY min(f.rank) LIMIT 20;
            """, match, cancellationToken).ConfigureAwait(false);

        return 0;
    }

    private static async Task TimeAsync(
        string label,
        SqliteConnection connection,
        string sql,
        string match,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = 0;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@match", match);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows++;
            }
        }

        stopwatch.Stop();
        Console.WriteLine($"  {label,-30} {stopwatch.ElapsedMilliseconds,7}ms   {rows} rows");
    }

    private static async Task PlanAsync(
        SqliteConnection connection,
        string sql,
        string match,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@match", match);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine("     " + reader.GetString(3));
        }
    }
}
