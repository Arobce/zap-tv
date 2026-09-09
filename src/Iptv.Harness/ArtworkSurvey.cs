using Iptv.Core.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Reports how much of the catalogue actually has artwork.
/// </summary>
/// <remarks>
/// A poster grid is worth building only if there are posters. The same mistake as the
/// guide grid is available here: the field exists on every row, so the schema says
/// nothing about whether providers fill it, and a grid over a catalogue with no covers is
/// worse than the list it replaced.
/// </remarks>
internal static class ArtworkSurvey
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        await using var connection = await new SqliteConnectionFactory(databasePath)
            .OpenAsync(cancellationToken).ConfigureAwait(false);

        await ReportAsync(
            connection,
            "films",
            """
            SELECT count(DISTINCT channel_key),
                   count(DISTINCT CASE WHEN logo_url LIKE 'http%' THEN channel_key END)
            FROM streams
            WHERE kind = 'vod' AND is_active = 1 AND is_separator = 0;
            """,
            cancellationToken).ConfigureAwait(false);

        await ReportAsync(
            connection,
            "series",
            """
            SELECT count(DISTINCT series_key),
                   count(DISTINCT CASE WHEN cover_url LIKE 'http%' THEN series_key END)
            FROM series;
            """,
            cancellationToken).ConfigureAwait(false);

        // Gated the same way the live list gates itself. Counting the channels table raw
        // gives 118,763 against a library of about 20,000 live channels: the table is a
        // merge target that is never deleted from, so it still holds rows created before
        // the sync was scoped to live, and reporting those would answer a question nobody
        // asked.
        await ReportAsync(
            connection,
            "live",
            """
            SELECT count(*), count(CASE WHEN c.logo_url LIKE 'http%' THEN 1 END)
            FROM channels c
            WHERE c.is_hidden = 0
              AND EXISTS (SELECT 1 FROM streams s
                           WHERE s.channel_key = c.channel_key
                             AND s.kind = 'live'
                             AND s.is_active = 1
                             AND s.is_separator = 0);
            """,
            cancellationToken).ConfigureAwait(false);

        // Anything present but not an http URL is what the row filter throws away. Worth
        // counting separately: a large number here would mean the filter is wrong rather
        // than the catalogue being thin.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT count(*) FROM streams
                 WHERE kind = 'vod' AND logo_url IS NOT NULL
                   AND logo_url NOT LIKE 'http%' AND trim(logo_url) <> '';
                """;

            var odd = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"non-http film artwork values rejected: {Convert.ToInt64(odd):N0}");
        }

        return 0;
    }

    private static async Task ReportAsync(
        SqliteConnection connection,
        string label,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var total = reader.GetInt64(0);
        var withArt = reader.GetInt64(1);
        var share = total == 0 ? 0 : (double)withArt / total * 100;

        Console.WriteLine($"{label,-8} {withArt,8:N0} of {total,8:N0} have artwork  ({share:F1}%)");
    }
}
