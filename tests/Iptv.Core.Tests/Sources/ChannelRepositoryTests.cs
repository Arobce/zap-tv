using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// The read model the channel list binds to.
/// </summary>
/// <remarks>
/// One query returns the row and its now/next programme. Fetching the guide per row would
/// issue 20,478 queries to fill a list that shows twenty of them, which is the shape of
/// mistake that makes a virtualized list feel slower than an unvirtualized one.
/// </remarks>
public sealed class ChannelRepositoryTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_788_546_600); // 2026-09-04 18:30 UTC

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        await ExecuteAsync(connection,
            """
            INSERT INTO providers (id, name, kind, base_url, priority)
            VALUES (1, 'A', 'xtream', 'http://a.invalid', 0);
            INSERT INTO providers (id, name, kind, base_url, priority)
            VALUES (2, 'B', 'xtream', 'http://b.invalid', 5);
            """);
        return connection;
    }

    private static async Task AddAsync(
        SqliteConnection connection,
        string channelKey,
        string title,
        int providerId = 1,
        string? quality = null,
        bool separator = false,
        bool hidden = false,
        bool favorite = false)
    {
        await ExecuteAsync(connection,
            $"""
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, quality, is_separator, is_active, last_seen_utc)
            VALUES ({providerId}, '{channelKey}-{providerId}-{quality ?? "x"}', 'live', '{title}',
                    '{title.ToLowerInvariant()}',
                    'http://host.invalid/live/u/p/{providerId}-{quality ?? "x"}.ts', '{channelKey}',
                    {(quality is null ? "NULL" : $"'{quality}'")}, {(separator ? 1 : 0)}, 1, 0);

            INSERT OR IGNORE INTO channels (channel_key, display_name, is_hidden, is_favorite)
            VALUES ('{channelKey}', '{title}', {(hidden ? 1 : 0)}, {(favorite ? 1 : 0)});
            """);
    }

    private static async Task AddGuideAsync(
        SqliteConnection connection,
        string channelKey,
        string epgId,
        params (string Title, long Start, long Stop)[] programmes)
    {
        await ExecuteAsync(connection,
            $"""
            INSERT OR IGNORE INTO epg_channels (epg_channel_id, display_names, normalized_names)
            VALUES ('{epgId}', 'x', 'x');
            INSERT OR REPLACE INTO epg_map
              (channel_key, epg_channel_id, confidence, method, locked, updated_utc)
            VALUES ('{channelKey}', '{epgId}', 1.0, 'tvg_id', 0, 0);
            """);

        foreach (var (title, start, stop) in programmes)
        {
            await ExecuteAsync(connection,
                $"""
                INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
                VALUES ('{epgId}', {start}, {stop}, '{title}');
                """);
        }
    }

    [Fact]
    public async Task Lists_channels()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");
        await AddAsync(connection, "tvg:b", "Beta");

        var channels = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None);

        Assert.Equal(2, channels.Count);
    }

    [Fact]
    public async Task Excludes_separators_and_hidden_channels()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");
        await AddAsync(connection, "name:sep", "##### SPORTS #####", separator: true);
        await AddAsync(connection, "tvg:h", "Hidden One", hidden: true);

        var channels = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None);

        // Separators are shown as headings elsewhere, not as playable channels, and a
        // hidden channel is a user decision that the list must respect.
        Assert.Equal("Alpha", Assert.Single(channels).DisplayName);
    }

    [Fact]
    public async Task Returns_the_programme_on_now_and_the_one_after()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");

        var start = Now.ToUnixTimeSeconds();
        await AddGuideAsync(connection, "tvg:a", "a.epg",
            ("Earlier", start - 7200, start - 3600),
            ("On Now", start - 600, start + 600),
            ("Up Next", start + 600, start + 3600));

        var channel = Assert.Single(await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None));

        Assert.Equal("On Now", channel.NowTitle);
        Assert.Equal("Up Next", channel.NextTitle);
    }

    [Fact]
    public async Task Reports_progress_through_the_current_programme()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");

        var start = Now.ToUnixTimeSeconds();
        await AddGuideAsync(connection, "tvg:a", "a.epg", ("Half Done", start - 1800, start + 1800));

        var channel = Assert.Single(await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None));

        // Drives the per-row progress bar the PRD asks for.
        Assert.NotNull(channel.NowProgress);
        Assert.InRange(channel.NowProgress!.Value, 0.45, 0.55);
    }

    [Fact]
    public async Task A_channel_with_no_guide_reports_no_programme()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");

        var channel = Assert.Single(await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None));

        // 82% of the reference library is in this state, so it is the common case rather
        // than an edge one, and the UI must render it without looking broken.
        Assert.Null(channel.NowTitle);
        Assert.Null(channel.NowProgress);
    }

    [Fact]
    public async Task Searches_by_title()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "BBC One");
        await AddAsync(connection, "tvg:b", "ESPN");

        var channels = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery { Search = "bbc" }, Now, CancellationToken.None);

        Assert.Equal("BBC One", Assert.Single(channels).DisplayName);
    }

    [Fact]
    public async Task Favourites_sort_first()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");
        await AddAsync(connection, "tvg:z", "Zulu", favorite: true);

        var channels = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None);

        Assert.Equal("Zulu", channels[0].DisplayName);
        Assert.True(channels[0].IsFavorite);
    }

    [Fact]
    public async Task Paging_returns_distinct_windows()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        for (var i = 0; i < 10; i++)
        {
            await AddAsync(connection, $"tvg:{i}", $"Channel {i:D2}");
        }

        var first = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery { Limit = 4 }, Now, CancellationToken.None);
        var second = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery { Limit = 4, Offset = 4 }, Now, CancellationToken.None);

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.Empty(first.Select(c => c.ChannelKey).Intersect(second.Select(c => c.ChannelKey)));
    }

    [Fact]
    public async Task Picks_the_stream_from_the_highest_priority_provider()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // Same channel from both providers. Provider 1 has priority 0, which wins.
        await AddAsync(connection, "tvg:a", "Alpha", providerId: 2);
        await AddAsync(connection, "tvg:a", "Alpha", providerId: 1);

        var url = await ChannelRepository.GetPlaybackUrlAsync(
            connection, "tvg:a", StreamKind.Live, CancellationToken.None);

        Assert.Contains("/1-x.ts", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prefers_the_higher_quality_stream_within_a_provider()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha SD", quality: "Sd");
        await AddAsync(connection, "tvg:a", "Alpha UHD", quality: "Uhd");

        var url = await ChannelRepository.GetPlaybackUrlAsync(
            connection, "tvg:a", StreamKind.Live, CancellationToken.None);

        Assert.NotNull(url);

        // Quality is retained precisely so this choice can be made; discarding it during
        // normalization would make every variant indistinguishable.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT quality FROM streams WHERE url = @url;";
        command.Parameters.AddWithValue("@url", url);
        Assert.Equal("Uhd", (await command.ExecuteScalarAsync(CancellationToken.None))?.ToString());
    }

    [Fact]
    public async Task Ignores_inactive_streams_when_choosing_a_url()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddAsync(connection, "tvg:a", "Alpha");
        await ExecuteAsync(connection, "UPDATE streams SET is_active = 0;");

        Assert.Null(await ChannelRepository.GetPlaybackUrlAsync(
            connection, "tvg:a", StreamKind.Live, CancellationToken.None));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
