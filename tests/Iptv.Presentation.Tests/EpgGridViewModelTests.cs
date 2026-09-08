using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Presentation;
using Microsoft.Data.Sqlite;

namespace Iptv.Presentation.Tests;

/// <summary>
/// Which rectangle of the guide is visible, and where its blocks sit.
/// </summary>
/// <remarks>
/// The arithmetic is here rather than in the panel because this is where off-by-ones live:
/// deciding which of 3,450 rows and which of thirty hours are on screen, and turning times
/// into pixels. On screen a wrong answer looks like a rendering glitch.
/// </remarks>
public sealed class EpgGridViewModelTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"zap-epg-{Guid.NewGuid():N}");

    private readonly SqliteConnectionFactory _factory;

    private static readonly DateTimeOffset Noon = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    public EpgGridViewModelTests()
        => _factory = new SqliteConnectionFactory(Path.Combine(_directory, "guide.db"), pooled: false);

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = await _factory.OpenAsync(CancellationToken.None);
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    /// <summary>A channel with a live stream, a guide mapping and optional favourite.</summary>
    private static async Task ChannelAsync(
        SqliteConnection connection,
        string key,
        string name,
        bool favourite = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO channels (channel_key, display_name, is_favorite) VALUES (@key, @name, @fav);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (1, @key, 'live', @name, @name, 'http://h/' || @key, @key, 1, 0, 0);
            INSERT INTO epg_map (channel_key, epg_channel_id, confidence, method, updated_utc)
            VALUES (@key, @key || '.epg', 1.0, 'tvg_id', 0);
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@fav", favourite ? 1 : 0);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ProgrammeAsync(
        SqliteConnection connection,
        string key,
        string title,
        DateTimeOffset start,
        DateTimeOffset stop)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
            VALUES (@key || '.epg', @start, @stop, @title);
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@start", start.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@stop", stop.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private EpgGridViewModel Model() => new(_factory) { PixelsPerMinute = 6, RowHeight = 40, RowBuffer = 1 };

    [Fact]
    public async Task An_empty_guide_has_nothing_to_draw()
    {
        await using var connection = await OpenAsync();

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        Assert.False(model.HasGuide);
        Assert.Equal(0, model.TotalWidth);

        var viewport = await model.RealizeAsync(0, 0, 800, 600, Noon, CancellationToken.None);
        Assert.Empty(viewport.Blocks);
    }

    [Fact]
    public async Task Only_channels_with_a_guide_get_rows()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "Has guide");
        await ProgrammeAsync(connection, "tvg:a", "News", Noon, Noon.AddHours(1));

        await using (var bare = connection.CreateCommand())
        {
            bare.CommandText =
                """
                INSERT INTO channels (channel_key, display_name) VALUES ('tvg:b', 'No guide');
                INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                     url, channel_key, is_active, is_separator, last_seen_utc)
                VALUES (1, 'tvg:b', 'live', 'No guide', 'no guide', 'http://h/b', 'tvg:b', 1, 0, 0);
                """;
            await bare.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // 82% of the real library has no guide. A grid where 17,000 rows are permanently
        // blank is not a guide, it is a way to lose the ones that work.
        Assert.Equal("Has guide", Assert.Single(model.Channels).DisplayName);
    }

    [Fact]
    public async Task Favourites_come_first()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "AAA Ordinary");
        await ChannelAsync(connection, "tvg:z", "ZZZ Favourite", favourite: true);
        await ProgrammeAsync(connection, "tvg:a", "x", Noon, Noon.AddHours(1));
        await ProgrammeAsync(connection, "tvg:z", "y", Noon, Noon.AddHours(1));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(["ZZZ Favourite", "AAA Ordinary"], model.Channels.Select(c => c.DisplayName));
    }

    [Fact]
    public async Task The_surface_is_sized_to_the_guide_not_to_a_fixed_horizon()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "Only", Noon, Noon.AddHours(2));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // Two hours at 6px a minute. Offering a fortnight would be offering thirteen days
        // of blank; see docs/decisions/0010.
        Assert.Equal(2 * 60 * 6, model.TotalWidth);
        Assert.Equal(40, model.TotalHeight);
    }

    [Fact]
    public async Task A_block_is_positioned_by_its_start_and_sized_by_its_length()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "First", Noon, Noon.AddMinutes(30));
        await ProgrammeAsync(connection, "tvg:a", "Second", Noon.AddMinutes(30), Noon.AddMinutes(90));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        var viewport = await model.RealizeAsync(0, 0, 1200, 400, Noon, CancellationToken.None);

        var first = viewport.Blocks.Single(b => b.Block.Title == "First");
        var second = viewport.Blocks.Single(b => b.Block.Title == "Second");

        Assert.Equal(0, first.X);
        Assert.Equal(180, first.Width);
        Assert.Equal(180, second.X);
        Assert.Equal(360, second.Width);
        Assert.Equal(0, first.Y);
    }

    [Fact]
    public async Task A_very_short_programme_stays_wide_enough_to_read()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "Ident", Noon, Noon.AddMinutes(1));
        await ProgrammeAsync(connection, "tvg:a", "Film", Noon.AddMinutes(1), Noon.AddHours(2));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        var viewport = await model.RealizeAsync(0, 0, 1200, 400, Noon, CancellationToken.None);

        // Six pixels of unreadable sliver otherwise. Overlapping its neighbour slightly is
        // the better trade.
        Assert.Equal(24, viewport.Blocks.Single(b => b.Block.Title == "Ident").Width);
    }

    [Fact]
    public async Task Rows_outside_the_vertical_window_are_not_realized()
    {
        await using var connection = await OpenAsync();

        for (var i = 0; i < 60; i++)
        {
            var key = $"tvg:{i:00}";
            await ChannelAsync(connection, key, $"Channel {i:00}");
            await ProgrammeAsync(connection, key, $"P{i}", Noon, Noon.AddHours(1));
        }

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // A 200px viewport at 40px a row is five rows, plus one of buffer either side.
        var viewport = await model.RealizeAsync(0, 0, 800, 200, Noon, CancellationToken.None);

        Assert.Equal(60, model.Channels.Count);
        Assert.InRange(viewport.Rows.Count, 6, 9);
        Assert.Equal(0, viewport.Rows[0].Y);
    }

    [Fact]
    public async Task Scrolling_down_realizes_a_later_page_of_rows()
    {
        await using var connection = await OpenAsync();

        for (var i = 0; i < 60; i++)
        {
            var key = $"tvg:{i:00}";
            await ChannelAsync(connection, key, $"Channel {i:00}");
            await ProgrammeAsync(connection, key, $"P{i}", Noon, Noon.AddHours(1));
        }

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // 800px down is row 20 at 40px a row.
        var viewport = await model.RealizeAsync(0, 800, 800, 200, Noon, CancellationToken.None);

        Assert.Equal(19 * 40, viewport.Rows[0].Y);
        Assert.Equal("Channel 19", viewport.Rows[0].Channel.DisplayName);

        // Y is absolute on the scroll surface, not relative to the viewport: the panel
        // positions inside a canvas the ScrollViewer moves.
        Assert.All(viewport.Blocks, b => Assert.True(b.Y >= 19 * 40));
    }

    [Fact]
    public async Task Programmes_outside_the_horizontal_window_are_not_read()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "Now", Noon, Noon.AddHours(1));
        await ProgrammeAsync(connection, "tvg:a", "Much later", Noon.AddHours(20), Noon.AddHours(21));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // A 600px viewport is 100 minutes, plus 30 of buffer each side.
        var viewport = await model.RealizeAsync(0, 0, 600, 400, Noon, CancellationToken.None);

        Assert.Equal("Now", Assert.Single(viewport.Blocks).Block.Title);
    }

    [Fact]
    public async Task Scrolling_right_reads_the_later_window()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "Early", Noon, Noon.AddHours(1));
        await ProgrammeAsync(connection, "tvg:a", "Late", Noon.AddHours(10), Noon.AddHours(11));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // Ten hours in: 600 minutes at 6px is 3600px.
        var viewport = await model.RealizeAsync(3600, 0, 600, 400, Noon, CancellationToken.None);

        Assert.Equal("Late", Assert.Single(viewport.Blocks).Block.Title);
    }

    [Fact]
    public async Task Hour_marks_land_on_the_hour()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "x", Noon, Noon.AddHours(4));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        var viewport = await model.RealizeAsync(0, 0, 800, 400, Noon, CancellationToken.None);

        // On o'clock, not on wherever the scroll stopped.
        Assert.All(viewport.HourMarks, mark => Assert.Equal(0, mark.Time.Minute));
        Assert.Equal(Noon, viewport.HourMarks[0].Time);
    }

    [Fact]
    public async Task Times_and_pixels_round_trip()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "x", Noon, Noon.AddHours(6));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(0, model.XOf(Noon));
        Assert.Equal(360, model.XOf(Noon.AddHours(1)));
        Assert.Equal(Noon.AddHours(1), model.TimeAt(360));
    }

    [Fact]
    public async Task A_viewport_with_no_size_draws_nothing()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "x", Noon, Noon.AddHours(1));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        // The first layout pass reports zero before anything has been measured. Querying
        // there would be a wasted read on every startup.
        Assert.Empty((await model.RealizeAsync(0, 0, 0, 0, Noon, CancellationToken.None)).Blocks);
    }

    [Fact]
    public async Task The_programme_on_now_is_marked_for_the_view()
    {
        await using var connection = await OpenAsync();

        await ChannelAsync(connection, "tvg:a", "A");
        await ProgrammeAsync(connection, "tvg:a", "On now", Noon.AddMinutes(-10), Noon.AddMinutes(20));

        var model = Model();
        await model.LoadAsync(CancellationToken.None);

        var viewport = await model.RealizeAsync(0, 0, 800, 400, Noon, CancellationToken.None);

        Assert.True(Assert.Single(viewport.Blocks).Block.IsNow);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory is disposable either way.
        }
    }
}
