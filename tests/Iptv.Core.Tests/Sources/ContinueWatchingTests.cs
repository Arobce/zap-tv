using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Resolving unfinished positions into things that can be shown and played.
/// </summary>
/// <remarks>
/// A content key is not a title. Without this join, resuming a film meant finding it
/// again among 97,269, which is the same as not having resume at all.
/// </remarks>
public sealed class ContinueWatchingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url) VALUES
              (1, 'A', 'xtream', 'http://a.invalid'),
              (2, 'Disabled', 'xtream', 'http://b.invalid');
            UPDATE providers SET enabled = 0 WHERE id = 2;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    private static async Task AddStreamAsync(
        SqliteConnection connection,
        string key,
        string title,
        string kind = "vod",
        long providerId = 1,
        bool active = true)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (@provider, @sid, @kind, @title, @title,
                    'http://host.invalid/' || @sid, @key, @active, 0, 0);
            """;

        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@active", active ? 1 : 0);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task SaveAsync(
        SqliteConnection connection,
        string key,
        int position = 600,
        int? duration = 7200,
        TimeSpan ago = default)
        => PlaybackStateRepository.SaveAsync(
            connection, key, position, duration, Now - ago, CancellationToken.None);

    private static Task<IReadOnlyList<ContinueWatchingItem>> ListAsync(SqliteConnection connection)
        => PlaybackStateRepository.GetContinueWatchingItemsAsync(connection, 20, CancellationToken.None);

    [Fact]
    public async Task A_started_film_is_listed_with_its_title()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "name:thefilm", "The Film");
        await SaveAsync(connection, "name:thefilm");

        var item = Assert.Single(await ListAsync(connection));
        Assert.Equal("The Film", item.Title);
        Assert.Equal(600, item.PositionSeconds);
        Assert.False(item.IsEpisode);
    }

    [Fact]
    public async Task An_episode_is_listed_and_marked_as_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // An episode's channel_key is its ep: key, so one join covers both kinds.
        await AddStreamAsync(connection, "ep:1:4242", "Pilot", kind: "series_episode");
        await SaveAsync(connection, "ep:1:4242");

        Assert.True(Assert.Single(await ListAsync(connection)).IsEpisode);
    }

    [Fact]
    public async Task A_finished_film_is_not_offered()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "name:thefilm", "The Film");
        await SaveAsync(connection, "name:thefilm", position: 7150, duration: 7200);

        Assert.Empty(await ListAsync(connection));
    }

    [Fact]
    public async Task A_position_whose_stream_has_gone_is_not_offered()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // The provider dropped the film. Offering to resume something that cannot open is
        // worse than not offering it.
        await AddStreamAsync(connection, "name:gone", "Gone", active: false);
        await SaveAsync(connection, "name:gone");

        Assert.Empty(await ListAsync(connection));
    }

    [Fact]
    public async Task A_position_with_no_stream_at_all_is_not_offered()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, "name:never-existed");

        Assert.Empty(await ListAsync(connection));
    }

    [Fact]
    public async Task A_disabled_provider_does_not_contribute()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "name:film", "Film", providerId: 2);
        await SaveAsync(connection, "name:film");

        Assert.Empty(await ListAsync(connection));
    }

    [Fact]
    public async Task Live_channels_never_appear()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // Nothing writes live positions, but a stale row from an earlier build must not
        // resurface as a resumable item.
        await AddStreamAsync(connection, "tvg:bbc", "BBC One", kind: "live");
        await SaveAsync(connection, "tvg:bbc");

        Assert.Empty(await ListAsync(connection));
    }

    [Fact]
    public async Task The_same_film_from_two_providers_is_one_entry()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "name:film", "The Film");
        await AddStreamAsync(connection, "name:film", "The Film");
        await SaveAsync(connection, "name:film");

        Assert.Single(await ListAsync(connection));
    }

    [Fact]
    public async Task Newest_first()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "name:a", "Older");
        await AddStreamAsync(connection, "name:b", "Newer");

        await SaveAsync(connection, "name:a", ago: TimeSpan.FromDays(2));
        await SaveAsync(connection, "name:b", ago: TimeSpan.FromHours(1));

        Assert.Equal(["Newer", "Older"], (await ListAsync(connection)).Select(i => i.Title));
    }

    [Fact]
    public async Task Progress_is_reported_for_the_row()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "name:film", "The Film");
        await SaveAsync(connection, "name:film", position: 1800, duration: 7200);

        Assert.Equal(0.25, Assert.Single(await ListAsync(connection)).Progress);
    }
}
