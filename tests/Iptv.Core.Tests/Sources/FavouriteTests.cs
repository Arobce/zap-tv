using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Favourites, which the schema and the list query have always supported and nothing could
/// set.
/// </summary>
/// <remarks>
/// 20,479 live channels is not something anyone navigates from scratch twice. The list
/// query has sorted favourites first since the beginning; this is what puts anything there.
/// </remarks>
public sealed class FavouriteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    private static async Task AddChannelAsync(SqliteConnection connection, string key, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO channels (channel_key, display_name) VALUES (@key, @name);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (1, @key, 'live', @name, @name, 'http://host.invalid/' || @key, @key, 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@name", name);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task<IReadOnlyList<ChannelListItem>> ListAsync(
        SqliteConnection connection,
        bool favouritesOnly = false)
        => ChannelRepository.GetChannelsAsync(
            connection,
            new ChannelQuery { FavouritesOnly = favouritesOnly },
            Now,
            CancellationToken.None);

    [Fact]
    public async Task Toggling_marks_and_unmarks()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc", "BBC One");

        Assert.True(await ChannelRepository.ToggleFavouriteAsync(
            connection, "tvg:bbc", CancellationToken.None));

        Assert.True(Assert.Single(await ListAsync(connection)).IsFavorite);

        Assert.False(await ChannelRepository.ToggleFavouriteAsync(
            connection, "tvg:bbc", CancellationToken.None));

        Assert.False(Assert.Single(await ListAsync(connection)).IsFavorite);
    }

    [Fact]
    public async Task Favourites_sort_above_everything_else()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddChannelAsync(connection, "tvg:a", "AAA First Alphabetically");
        await AddChannelAsync(connection, "tvg:z", "ZZZ Last Alphabetically");

        await ChannelRepository.SetFavouriteAsync(connection, "tvg:z", true, CancellationToken.None);

        // The whole point: the channel you actually watch is at the top of 20,479 rows
        // rather than wherever its name happens to fall.
        Assert.Equal(
            ["ZZZ Last Alphabetically", "AAA First Alphabetically"],
            (await ListAsync(connection)).Select(c => c.DisplayName));
    }

    [Fact]
    public async Task Favourites_only_hides_the_rest()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddChannelAsync(connection, "tvg:a", "Kept");
        await AddChannelAsync(connection, "tvg:b", "Hidden");

        await ChannelRepository.SetFavouriteAsync(connection, "tvg:a", true, CancellationToken.None);

        Assert.Equal("Kept", Assert.Single(await ListAsync(connection, favouritesOnly: true)).DisplayName);
    }

    [Fact]
    public async Task Setting_is_idempotent()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc", "BBC One");

        await ChannelRepository.SetFavouriteAsync(connection, "tvg:bbc", true, CancellationToken.None);
        await ChannelRepository.SetFavouriteAsync(connection, "tvg:bbc", true, CancellationToken.None);

        Assert.Equal(1, await ChannelRepository.CountFavouritesAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task Favouriting_a_channel_with_no_row_yet_creates_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // A list can be built from streams before RefreshChannelsAsync has run. Failing
        // silently because the row does not exist is worse than creating it.
        await ChannelRepository.SetFavouriteAsync(connection, "tvg:new", true, CancellationToken.None);

        Assert.Equal(1, await ChannelRepository.CountFavouritesAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task A_provider_sync_does_not_clear_favourites()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc", "BBC One");

        await ChannelRepository.SetFavouriteAsync(connection, "tvg:bbc", true, CancellationToken.None);

        // The prohibition in CLAUDE.md, asserted rather than trusted: sync merges and never
        // replaces, precisely so user-owned state survives it.
        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        Assert.Equal(1, await ChannelRepository.CountFavouritesAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task Favouriting_is_per_channel()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddChannelAsync(connection, "tvg:a", "One");
        await AddChannelAsync(connection, "tvg:b", "Two");

        await ChannelRepository.SetFavouriteAsync(connection, "tvg:a", true, CancellationToken.None);

        var channels = await ListAsync(connection);
        Assert.True(channels.Single(c => c.ChannelKey == "tvg:a").IsFavorite);
        Assert.False(channels.Single(c => c.ChannelKey == "tvg:b").IsFavorite);
    }
}
