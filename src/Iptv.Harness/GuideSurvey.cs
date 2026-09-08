using Iptv.Core.Data;
using Iptv.Core.Epg;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Reports the shape of the stored guide.
/// </summary>
/// <remarks>
/// Written because the grid window returned nothing against a guide that supposedly spans
/// 2.7 days: a coverage percentage says how many channels have <em>any</em> guide, and
/// says nothing about whether that guide covers the time anybody is going to look at. A
/// grid is worth building only if there is something to draw in it.
/// </remarks>
internal static class GuideSurvey
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        await using var connection = await new SqliteConnectionFactory(databasePath)
            .OpenAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        var (from, to) = await EpgGridRepository.GetCoverageAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine("== stored guide ==");
        if (from is null || to is null)
        {
            Console.WriteLine("  empty. Run: epg");
            return 0;
        }

        Console.WriteLine($"  spans      {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  now        {now:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  remaining  {(to.Value - now).TotalHours:F1}h ahead, " +
                          $"{(now - from.Value).TotalHours:F1}h behind");

        Console.WriteLine();
        Console.WriteLine("== totals ==");
        await ScalarAsync(connection, "programmes", "SELECT count(*) FROM programmes;", cancellationToken)
            .ConfigureAwait(false);
        await ScalarAsync(connection, "guide channels",
            "SELECT count(DISTINCT epg_channel_id) FROM programmes;", cancellationToken).ConfigureAwait(false);
        await ScalarAsync(connection, "mapped channels", "SELECT count(*) FROM epg_map;", cancellationToken)
            .ConfigureAwait(false);
        await ScalarAsync(connection, "mapped with any programme",
            """
            SELECT count(*) FROM epg_map m
             WHERE EXISTS (SELECT 1 FROM programmes p WHERE p.epg_channel_id = m.epg_channel_id);
            """, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== on air, by hour from now ==");

        // The number that decides whether a grid is worth drawing. Coverage counts channels
        // with any guide at all; this counts channels with guide at the moment somebody
        // opens the app, and then at intervals ahead of it.
        foreach (var hours in new[] { 0, 1, 6, 12, 24, 48 })
        {
            var at = now.AddHours(hours);
            await ScalarAsync(
                connection,
                $"+{hours,2}h",
                """
                SELECT count(DISTINCT m.channel_key)
                FROM epg_map m
                JOIN programmes p ON p.epg_channel_id = m.epg_channel_id
                WHERE p.start_utc <= @at AND p.stop_utc > @at;
                """,
                cancellationToken,
                ("@at", at.ToUnixTimeSeconds())).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task ScalarAsync(
        SqliteConnection connection,
        string label,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value2 = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  {label,-26} {Convert.ToInt64(value2),10:N0}");
    }
}
