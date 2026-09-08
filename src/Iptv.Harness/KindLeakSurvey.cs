using Iptv.Core.Data;
using Iptv.Core.Sources;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Measures how far live channels and films have run into each other.
/// </summary>
/// <remarks>
/// <c>channel_key</c> is derived from the normalized title and nothing else, so a film
/// called "The Terminal" and a live channel called "The Terminal" are the same key. That
/// is correct for deduplicating one catalogue across providers and wrong across catalogues,
/// where it merges two unrelated things into one row.
/// </remarks>
internal static class KindLeakSurvey
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var term = args.Length > 1 ? args[1] : null;

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        await using var connection = await new SqliteConnectionFactory(databasePath)
            .OpenAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine("== keys carried by more than one kind ==");
        await ReportCollisionsAsync(connection, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== channels rows whose name comes from a film ==");
        await ReportMislabelledAsync(connection, cancellationToken).ConfigureAwait(false);

        if (term is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"== searching \"{term}\" ==");

            var live = await ChannelRepository.GetChannelsAsync(
                connection,
                new ChannelQuery { Search = term, Limit = 10 },
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"  live  {live.Count} rows");
            foreach (var row in live)
            {
                Console.WriteLine($"    {row.DisplayName}");
            }

            var films = await LibraryRepository.GetFilmsAsync(
                connection,
                new CatalogueQuery { Search = term, Limit = 10 },
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"  films {films.Count} rows");
            foreach (var row in films)
            {
                Console.WriteLine($"    {row.Title}");
            }
        }

        return 0;
    }

    private static async Task ReportCollisionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*), sum(n)
            FROM (
                SELECT s.channel_key, count(DISTINCT s.kind) AS kinds, count(*) AS n
                FROM streams s
                WHERE s.is_active = 1 AND s.is_separator = 0
                GROUP BY s.channel_key
                HAVING count(DISTINCT s.kind) > 1
            );
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var keys = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var streams = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            Console.WriteLine($"  {keys:N0} keys spanning {streams:N0} streams");
        }
    }

    /// <summary>
    /// Channels whose display name was taken from a VOD row.
    /// </summary>
    /// <remarks>
    /// RefreshChannelsAsync picks the longest title across every stream sharing the key,
    /// regardless of kind, so a film title routinely wins and the live channel is listed
    /// under a movie's name.
    /// </remarks>
    private static async Task ReportMislabelledAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.channel_key, c.display_name
            FROM channels c
            WHERE EXISTS (SELECT 1 FROM streams s
                           WHERE s.channel_key = c.channel_key
                             AND s.kind = 'live' AND s.is_active = 1 AND s.is_separator = 0)
              AND EXISTS (SELECT 1 FROM streams v
                           WHERE v.channel_key = c.channel_key
                             AND v.kind = 'vod' AND v.is_active = 1 AND v.is_separator = 0
                             AND v.title = c.display_name)
              -- And no live stream carries that same title. Without this the report counts
              -- the case where a channel and a film are simply named identically, which is
              -- not a mislabelling: the name shown is the channel's own.
              AND NOT EXISTS (SELECT 1 FROM streams l
                               WHERE l.channel_key = c.channel_key
                                 AND l.kind = 'live' AND l.is_active = 1 AND l.is_separator = 0
                                 AND l.title = c.display_name)
            LIMIT 12;
            """;

        var any = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            any = true;
            Console.WriteLine($"  {reader.GetString(0),-34} shown as \"{reader.GetString(1)}\"");
        }

        if (!any)
        {
            Console.WriteLine("  none");
        }
    }
}
