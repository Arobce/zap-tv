using System.Text;
using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// The EPG ingest pipeline: parse, batch, stage, swap.
/// </summary>
/// <remarks>
/// Staged and swapped rather than written in place, because a failed or partial download
/// must never leave the user with a half-empty guide. The guide is the thing they look at
/// first, and a silently truncated one is worse than an obviously stale one.
/// </remarks>
public sealed class EpgIngestTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_788_546_600); // 2026-09-04 18:30 UTC

    private static Stream Xmltv(params string[] programmes)
    {
        var builder = new StringBuilder();
        builder.Append("<tv><channel id=\"c1\"><display-name>Channel One</display-name>")
               .Append("<display-name>C1</display-name></channel>");
        foreach (var programme in programmes)
        {
            builder.Append(programme);
        }

        builder.Append("</tv>");
        return new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Programme(string start, string stop, string title, string channel = "c1")
        => $"<programme start=\"{start}\" stop=\"{stop}\" channel=\"{channel}\">" +
           $"<title>{title}</title></programme>";

    private static async Task<SqliteConnection> OpenMigratedAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    [Fact]
    public async Task Ingests_channels_and_programmes()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        var result = await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Show A")),
            new EpgIngestOptions(),
            Now,
            CancellationToken.None);

        Assert.Equal(1, result.Channels);
        Assert.Equal(1, result.Programmes);
        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM programmes"));
        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM epg_channels"));
    }

    [Fact]
    public async Task Stores_every_display_name_and_its_normalized_form()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Show A")),
            new EpgIngestOptions(),
            Now,
            CancellationToken.None);

        var names = await TextAsync(connection, "SELECT display_names FROM epg_channels");
        var normalized = await TextAsync(connection, "SELECT normalized_names FROM epg_channels");

        Assert.Equal("Channel One\nC1", names);
        // Normalized at ingest so Phase 4 matching does not renormalize thousands of names
        // on every run.
        Assert.Equal("channel one\nc1", normalized);
    }

    [Fact]
    public async Task A_second_ingest_replaces_rather_than_appends()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);
        var options = new EpgIngestOptions();

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Old Show")),
            options, Now, CancellationToken.None);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "New Show")),
            options, Now, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM programmes"));
        Assert.Equal("New Show", await TextAsync(connection, "SELECT title FROM programmes"));
    }

    [Fact]
    public async Task Cancellation_leaves_the_previous_guide_intact()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);
        var options = new EpgIngestOptions();

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Good Show")),
            options, Now, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Replacement")),
            options, Now, cts.Token));

        // The whole point of staging: an interrupted refresh must not empty the guide.
        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM programmes"));
        Assert.Equal("Good Show", await TextAsync(connection, "SELECT title FROM programmes"));
    }

    [Fact]
    public async Task A_failed_ingest_leaves_no_staging_table_behind()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "X")),
            new EpgIngestOptions(), Now, cts.Token));

        Assert.Equal(0, await ScalarAsync(
            connection,
            "SELECT count(*) FROM sqlite_schema WHERE name LIKE '%staging%'"));
    }

    [Fact]
    public async Task Drops_programmes_that_ended_more_than_a_day_ago()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(
                // Ended three days before "now".
                Programme("20260901100000 +0000", "20260901110000 +0000", "Ancient"),
                Programme("20260904190000 +0000", "20260904200000 +0000", "Current")),
            new EpgIngestOptions(), Now, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM programmes"));
        Assert.Equal("Current", await TextAsync(connection, "SELECT title FROM programmes"));
    }

    [Fact]
    public async Task Drops_programmes_beyond_the_configured_horizon()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(
                Programme("20260904190000 +0000", "20260904200000 +0000", "Current"),
                // 20 days out, past the 14-day default.
                Programme("20260924190000 +0000", "20260924200000 +0000", "Far Future")),
            new EpgIngestOptions(), Now, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM programmes"));
        Assert.Equal("Current", await TextAsync(connection, "SELECT title FROM programmes"));
    }

    [Fact]
    public async Task Applies_a_per_provider_offset_correction()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Shifted")),
            new EpgIngestOptions { OffsetMinutes = 60 },
            Now,
            CancellationToken.None);

        // XMLTV offsets are frequently wrong, so the user can nudge a whole provider.
        // 2026-09-04 00:00 UTC is 1788480000; the programme starts at 19:00 (+68400),
        // giving 1788548400, and the +60 minute correction adds 3600.
        var start = await ScalarAsync(connection, "SELECT start_utc FROM programmes");
        Assert.Equal(1_788_548_400 + 3600, start);
    }

    [Fact]
    public async Task Programmes_are_searchable_after_ingest()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Wildlife Documentary")),
            new EpgIngestOptions(), Now, CancellationToken.None);

        // The FTS index is external-content and bound to the table by name and rowid, so
        // the staging swap invalidates it. It has to be rebuilt or search silently returns
        // nothing for the entire guide.
        Assert.Equal(1, await ScalarAsync(
            connection,
            "SELECT count(*) FROM programmes_fts WHERE programmes_fts MATCH 'wildlife'"));
    }

    [Fact]
    public async Task The_lookup_index_survives_the_swap()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Show")),
            new EpgIngestOptions(), Now, CancellationToken.None);

        // Dropping the old table takes its indexes with it. Losing this one would leave
        // the grid doing full scans - correct results, unusable scrolling.
        var plan = await TextAsync(
            connection,
            """
            EXPLAIN QUERY PLAN
            SELECT id FROM programmes
            WHERE epg_channel_id IN ('c1') AND stop_utc > 0 AND start_utc < 99999999999;
            """,
            column: 3);

        Assert.Contains("ix_programmes_lookup", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_parse_and_total_timings_separately()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        var result = await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Show")),
            new EpgIngestOptions(), Now, CancellationToken.None);

        // Reported separately because a regression in insert throughput is a different
        // problem from a regression in index or FTS rebuild cost.
        Assert.True(result.ParseAndInsert > TimeSpan.Zero);
        Assert.True(result.Total >= result.ParseAndInsert);
    }

    [Fact]
    public async Task An_empty_guide_does_not_destroy_the_existing_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);
        var options = new EpgIngestOptions();

        await EpgIngest.IngestAsync(
            connection,
            Xmltv(Programme("20260904190000 +0000", "20260904200000 +0000", "Good Show")),
            options, Now, CancellationToken.None);

        // A provider serving an empty but well-formed document is a provider-side failure,
        // not an instruction to wipe the guide.
        await Assert.ThrowsAsync<EpgIngestException>(() => EpgIngest.IngestAsync(
            connection,
            new MemoryStream(Encoding.UTF8.GetBytes("<tv></tv>")),
            options, Now, CancellationToken.None));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM programmes"));
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static async Task<string> TextAsync(
        SqliteConnection connection,
        string sql,
        int column = 0)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        var builder = new StringBuilder();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            builder.Append(reader.GetValue(column));
        }

        return builder.ToString();
    }
}
