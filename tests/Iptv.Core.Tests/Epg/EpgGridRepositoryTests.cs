using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// One visible rectangle of the guide.
/// </summary>
/// <remarks>
/// The grid is virtualized on both axes, so this reads a page of channels over a time
/// window rather than a channel's whole schedule. On the reference library the alternative
/// is 181,328 programmes against 20,479 channels.
/// </remarks>
public sealed class EpgGridRepositoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task MapAsync(SqliteConnection connection, string channelKey, string epgId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO channels (channel_key, display_name) VALUES (@key, @key)
              ON CONFLICT(channel_key) DO NOTHING;
            INSERT INTO epg_map (channel_key, epg_channel_id, confidence, method, updated_utc)
            VALUES (@key, @epg, 1.0, 'tvg_id', 0);
            """;

        command.Parameters.AddWithValue("@key", channelKey);
        command.Parameters.AddWithValue("@epg", epgId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ProgrammeAsync(
        SqliteConnection connection,
        string epgId,
        string title,
        DateTimeOffset start,
        DateTimeOffset stop)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
            VALUES (@epg, @start, @stop, @title);
            """;

        command.Parameters.AddWithValue("@epg", epgId);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@start", start.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@stop", stop.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task<IReadOnlyList<EpgGridRow>> WindowAsync(
        SqliteConnection connection,
        IReadOnlyList<string> keys,
        DateTimeOffset from,
        DateTimeOffset to)
        => EpgGridRepository.GetWindowAsync(connection, keys, from, to, Noon, CancellationToken.None);

    [Fact]
    public async Task Every_requested_channel_gets_a_row_even_with_no_guide()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "News", Noon, Noon.AddHours(1));

        // 82% of this library has no guide. Dropping those rows would misalign the grid
        // with the channel list beside it.
        var rows = await WindowAsync(connection, ["tvg:a", "tvg:none"], Noon, Noon.AddHours(2));

        Assert.Equal(["tvg:a", "tvg:none"], rows.Select(r => r.ChannelKey));
        Assert.Single(rows[0].Programmes);
        Assert.Empty(rows[1].Programmes);
    }

    [Fact]
    public async Task Rows_come_back_in_the_order_asked_for()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await MapAsync(connection, "tvg:b", "b.epg");

        // The caller's order is the grid's vertical order. Sorting here would scramble it
        // against the channel list.
        var rows = await WindowAsync(connection, ["tvg:b", "tvg:a"], Noon, Noon.AddHours(2));

        Assert.Equal(["tvg:b", "tvg:a"], rows.Select(r => r.ChannelKey));
    }

    [Fact]
    public async Task A_programme_straddling_the_left_edge_is_included()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "Started earlier", Noon.AddHours(-1), Noon.AddMinutes(30));

        // The programme already running when the window opens is the one the viewer most
        // wants to see. A closed-interval query would drop it.
        var rows = await WindowAsync(connection, ["tvg:a"], Noon, Noon.AddHours(2));

        Assert.Equal("Started earlier", Assert.Single(rows[0].Programmes).Title);
    }

    [Fact]
    public async Task A_programme_straddling_the_right_edge_is_included()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "Runs over", Noon.AddHours(1), Noon.AddHours(4));

        Assert.Single((await WindowAsync(connection, ["tvg:a"], Noon, Noon.AddHours(2)))[0].Programmes);
    }

    [Fact]
    public async Task Programmes_wholly_outside_the_window_are_not_read()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "Before", Noon.AddHours(-4), Noon.AddHours(-3));
        await ProgrammeAsync(connection, "a.epg", "Inside", Noon, Noon.AddHours(1));
        await ProgrammeAsync(connection, "a.epg", "After", Noon.AddHours(5), Noon.AddHours(6));

        // The whole point of the horizontal window: not reading a fortnight to draw an hour.
        var rows = await WindowAsync(connection, ["tvg:a"], Noon, Noon.AddHours(2));

        Assert.Equal("Inside", Assert.Single(rows[0].Programmes).Title);
    }

    [Fact]
    public async Task A_programme_ending_exactly_at_the_window_start_is_excluded()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "Just finished", Noon.AddHours(-1), Noon);

        // Half-open on both edges, so a programme that ends the instant the window opens
        // does not draw a zero-width block.
        Assert.Empty((await WindowAsync(connection, ["tvg:a"], Noon, Noon.AddHours(2)))[0].Programmes);
    }

    [Fact]
    public async Task Programmes_are_ordered_by_start_within_a_row()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "Second", Noon.AddHours(1), Noon.AddHours(2));
        await ProgrammeAsync(connection, "a.epg", "First", Noon, Noon.AddHours(1));

        var rows = await WindowAsync(connection, ["tvg:a"], Noon, Noon.AddHours(3));

        Assert.Equal(["First", "Second"], rows[0].Programmes.Select(p => p.Title));
    }

    [Fact]
    public async Task The_programme_on_now_is_marked()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "On now", Noon.AddMinutes(-10), Noon.AddMinutes(20));
        await ProgrammeAsync(connection, "a.epg", "Next", Noon.AddMinutes(20), Noon.AddMinutes(50));

        var programmes = (await WindowAsync(connection, ["tvg:a"], Noon.AddHours(-1), Noon.AddHours(1)))[0]
            .Programmes;

        Assert.True(programmes.Single(p => p.Title == "On now").IsNow);
        Assert.False(programmes.Single(p => p.Title == "Next").IsNow);
    }

    [Fact]
    public async Task An_empty_channel_page_reads_nothing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        Assert.Empty(await WindowAsync(connection, [], Noon, Noon.AddHours(2)));
    }

    [Fact]
    public async Task Asking_for_more_rows_than_a_window_holds_is_refused()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var tooMany = Enumerable.Range(0, EpgGridRepository.MaxRows + 1)
            .Select(i => $"tvg:{i}")
            .ToList();

        // A caller passing every channel gets a clear failure rather than a query with
        // twenty thousand parameters.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => WindowAsync(connection, tooMany, Noon, Noon.AddHours(2)));
    }

    [Fact]
    public async Task Coverage_reports_the_span_the_guide_actually_has()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await MapAsync(connection, "tvg:a", "a.epg");
        await ProgrammeAsync(connection, "a.epg", "First", Noon, Noon.AddHours(1));
        await ProgrammeAsync(connection, "a.epg", "Last", Noon.AddDays(2), Noon.AddDays(2).AddHours(1));

        var (from, to) = await EpgGridRepository.GetCoverageAsync(connection, CancellationToken.None);

        // Real bounds, not a fixed fortnight: scrolling into a week of empty columns
        // because the provider publishes two days is a worse answer than stopping.
        Assert.Equal(Noon, from);
        Assert.Equal(Noon.AddDays(2).AddHours(1), to);
    }

    [Fact]
    public async Task Coverage_of_an_empty_guide_is_nothing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var (from, to) = await EpgGridRepository.GetCoverageAsync(connection, CancellationToken.None);

        Assert.Null(from);
        Assert.Null(to);
    }
}
