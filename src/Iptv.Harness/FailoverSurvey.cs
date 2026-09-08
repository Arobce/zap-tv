using Iptv.Core.Sources;
using Iptv.Core.Data;
using Iptv.Core.Playback;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Measures what the failover safety guard actually does to a real library.
/// </summary>
/// <remarks>
/// The guard's rules are conservative on purpose, and conservative rules have a cost that
/// only shows up against real data: if they refuse nearly every alternative, the channel
/// with two providers behaves like the channel with one and Phase 8 has bought nothing.
/// This command says which it is, so the PRD can be amended on evidence rather than on the
/// rule sounding sensible.
/// </remarks>
internal static class FailoverSurvey
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        var factory = new SqliteConnectionFactory(databasePath);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var keys = await MultiStreamKeysAsync(connection, cancellationToken).ConfigureAwait(false);

        var singles = await SingleStreamCountAsync(connection, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"live channels with one stream only : {singles:N0}");
        Console.WriteLine($"live channels with alternatives    : {keys.Count:N0}");

        if (keys.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing to survey. One provider with no duplicate titles gives failover");
            Console.WriteLine("nothing to work with; the guard is untested until a second is configured.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var protectedKeys = 0;
        var totalAlternatives = 0;
        var acceptedAlternatives = 0;
        var byCountry = 0;
        var byTitle = 0;

        // Kept for the report: an aggregate that says "38% refused" is not actionable,
        // and the examples are what show whether the refusals are correct.
        var examples = new List<string>();

        foreach (var key in keys)
        {
            var plan = await StreamHealthRepository.PlanAsync(
                connection, key, StreamKind.Live, now, QualityPreference.Highest, cancellationToken).ConfigureAwait(false);

            totalAlternatives += plan.Candidates.Count - 1 + plan.Excluded.Count;
            acceptedAlternatives += plan.Candidates.Count - 1;

            if (plan.Excluded.Count == 0)
            {
                continue;
            }

            protectedKeys++;

            foreach (var excluded in plan.Excluded)
            {
                if (excluded.Reason.StartsWith("country", StringComparison.Ordinal))
                {
                    byCountry++;
                }
                else
                {
                    byTitle++;
                }
            }

            if (examples.Count < 12)
            {
                examples.Add($"  {key,-38} {plan.Excluded[0].Reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"alternative streams                : {totalAlternatives:N0}");
        Console.WriteLine($"  accepted for failover            : {acceptedAlternatives:N0} ({Percent(acceptedAlternatives, totalAlternatives)})");
        Console.WriteLine($"  refused, country disagreed       : {byCountry:N0} ({Percent(byCountry, totalAlternatives)})");
        Console.WriteLine($"  refused, inferred key + title    : {byTitle:N0} ({Percent(byTitle, totalAlternatives)})");
        Console.WriteLine();
        Console.WriteLine($"channels the guard touched         : {protectedKeys:N0} of {keys.Count:N0}");
        Console.WriteLine($"channels left with a real fallback : {CountWithFallback(acceptedAlternatives, keys.Count)}");

        if (examples.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Refusals, first few:");
            foreach (var example in examples)
            {
                Console.WriteLine(example);
            }
        }

        return 0;
    }

    private static string CountWithFallback(int accepted, int keys)
        => accepted == 0 ? "0 — the guard refused every alternative" : $"at most {Math.Min(accepted, keys):N0}";

    private static string Percent(int part, int whole)
        => whole == 0 ? "n/a" : $"{part / (double)whole:P1}";

    /// <summary>Live channel keys carried by more than one active stream.</summary>
    private static async Task<IReadOnlyList<string>> MultiStreamKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.channel_key
            FROM streams s
            JOIN providers pr ON pr.id = s.provider_id
            WHERE s.kind = 'live' AND s.is_active = 1 AND s.is_separator = 0 AND pr.enabled = 1
            GROUP BY s.channel_key
            HAVING count(*) > 1;
            """;

        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private static async Task<long> SingleStreamCountAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*) FROM (
                SELECT s.channel_key
                FROM streams s
                JOIN providers pr ON pr.id = s.provider_id
                WHERE s.kind = 'live' AND s.is_active = 1 AND s.is_separator = 0 AND pr.enabled = 1
                GROUP BY s.channel_key
                HAVING count(*) = 1
            );
            """;

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
