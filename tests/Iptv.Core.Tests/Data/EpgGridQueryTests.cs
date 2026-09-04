using System.Diagnostics;
using System.Globalization;
using Iptv.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Iptv.Core.Tests.Data;

/// <summary>
/// Phase 1 exit criterion: the query the EPG grid actually issues - a set of channels
/// against a time window - stays fast with a realistic guide loaded.
/// </summary>
/// <remarks>
/// The single-channel point lookup that the PRD originally specified is not a meaningful
/// benchmark; it is fast with or without an index. The grid window is what the UI issues
/// on every scroll frame.
/// <para>
/// Timing assertions are deliberately loose. A shared CI runner is an order of magnitude
/// noisier than a dev machine, and a test that fails on runner contention gets muted,
/// which is worse than no test. The precise figure is reported to test output for humans;
/// the tight assertion is on the query plan, which is deterministic and catches the
/// regression that actually matters - a dropped or unusable index.
/// </para>
/// <para>
/// Measured on the dev machine: 0.66ms mean with the index, 20.85ms with it dropped.
/// Note that the 100ms ceiling below does <em>not</em> by itself catch a missing index -
/// verified by removing <c>ix_programmes_lookup</c>, which fails the plan test while the
/// timing test still passes. The two tests are not redundant; the plan test is load-bearing.
/// </para>
/// </remarks>
public sealed class EpgGridQueryTests
{
    private const int ChannelCount = 2_000;
    private const int ProgrammesPerChannel = 100;
    private const int VisibleChannels = 200;

    private readonly ITestOutputHelper _output;

    public EpgGridQueryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Grid_window_query_uses_the_lookup_index()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + GridWindowSql(VisibleChannels);

        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                plan.Add(reader.GetString(reader.GetOrdinal("detail")));
            }
        }

        var text = string.Join(" | ", plan);
        _output.WriteLine(text);

        // A full scan here would still return correct rows, just slowly, so correctness
        // tests would not notice. This is the assertion that would.
        Assert.Contains("ix_programmes_lookup", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN programmes", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grid_window_query_stays_responsive_with_a_full_guide_loaded()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        var seedElapsed = await SeedProgrammesAsync(connection);
        _output.WriteLine(
            $"seeded {ChannelCount * ProgrammesPerChannel:N0} rows in {seedElapsed.TotalSeconds:F2}s " +
            $"({(ChannelCount * ProgrammesPerChannel) / seedElapsed.TotalSeconds:N0} rows/s)");

        await using var command = connection.CreateCommand();
        command.CommandText = GridWindowSql(VisibleChannels);

        // Warm up: the first execution pays for statement preparation and page cache
        // population, neither of which the UI pays on a steady-state scroll.
        var warmup = await CountRowsAsync(command);
        Assert.True(warmup > 0, "The seeded data should intersect the queried window.");

        var stopwatch = Stopwatch.StartNew();
        const int iterations = 20;
        for (var i = 0; i < iterations; i++)
        {
            await CountRowsAsync(command);
        }

        stopwatch.Stop();
        var perQuery = stopwatch.Elapsed.TotalMilliseconds / iterations;
        _output.WriteLine($"grid window query: {perQuery:F2}ms mean over {iterations} runs");

        Assert.True(
            perQuery < 100,
            $"Grid window query averaged {perQuery:F2}ms, which is far enough above the 15ms " +
            $"target to indicate a missing index rather than runner noise.");
    }

    /// <summary>
    /// The query shape from the PRD: the visible channels against the visible time window.
    /// </summary>
    private static string GridWindowSql(int channelCount)
    {
        // Channel ids are generated, not user input. Parameterised in the seeded test
        // below via literals of the same shape so the plan matches production.
        var ids = string.Join(
            ", ",
            Enumerable.Range(0, channelCount).Select(i => $"'ch{i.ToString(CultureInfo.InvariantCulture)}'"));

        return $"""
            SELECT id, epg_channel_id, start_utc, stop_utc, title
            FROM programmes
            WHERE epg_channel_id IN ({ids})
              AND stop_utc  > @from
              AND start_utc < @to;
            """;
    }

    private static async Task<int> CountRowsAsync(SqliteCommand command)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@from", WindowStart);
        command.Parameters.AddWithValue("@to", WindowStart + (3 * 3600));

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows++;
        }

        return rows;
    }

    private const long WindowStart = 1_780_000_000;

    /// <summary>
    /// Seeds a realistic guide: every channel has back-to-back half-hour programmes.
    /// </summary>
    private static async Task<TimeSpan> SeedProgrammesAsync(SqliteConnection connection)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
            VALUES (@channel, @start, @stop, @title);
            """;

        // One prepared command with parameters reset per row, per the PRD conventions.
        var channel = command.Parameters.Add("@channel", SqliteType.Text);
        var start = command.Parameters.Add("@start", SqliteType.Integer);
        var stop = command.Parameters.Add("@stop", SqliteType.Integer);
        var title = command.Parameters.Add("@title", SqliteType.Text);

        // Start the guide well before the queried window so the window lands mid-schedule.
        var origin = WindowStart - (ProgrammesPerChannel / 2 * 1800L);

        for (var c = 0; c < ChannelCount; c++)
        {
            channel.Value = $"ch{c.ToString(CultureInfo.InvariantCulture)}";
            for (var p = 0; p < ProgrammesPerChannel; p++)
            {
                var begin = origin + (p * 1800L);
                start.Value = begin;
                stop.Value = begin + 1800L;
                title.Value = $"Programme {p}";
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        await transaction.CommitAsync(CancellationToken.None);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
}
