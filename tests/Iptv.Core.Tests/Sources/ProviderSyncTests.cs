using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Provider sync is a merge, never a replace.
/// </summary>
/// <remarks>
/// Everything the user owns - favourites, hidden channels, sort order, EPG mappings,
/// resume positions - hangs off keys that a delete-and-reinsert would destroy. Providers
/// also drop channels for a few hours and bring them back, so absence in one payload is
/// not evidence a channel is gone.
/// </remarks>
public sealed class ProviderSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static StreamRecord Record(
        string providerStreamId,
        string title,
        string channelKey,
        int providerId = 1)
        => new()
        {
            ProviderId = providerId,
            ProviderStreamId = providerStreamId,
            Kind = StreamKind.Live,
            Title = title,
            NormalizedTitle = ChannelNormalizer.Normalize(title),
            ChannelKey = channelKey,
            Url = $"http://host.invalid/live/u/p/{providerStreamId}.ts",
            Container = "ts",
        };

    private static async Task<SqliteConnection> OpenMigratedAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');
            INSERT INTO providers (id, name, kind, base_url) VALUES (2, 'B', 'xtream', 'http://b.invalid');
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return connection;
    }

    [Fact]
    public async Task First_sync_inserts_every_stream()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        var summary = await ProviderSync.SyncAsync(
            connection,
            providerId: 1,
            [Record("1", "BBC One", "tvg:bbc1"), Record("2", "BBC Two", "tvg:bbc2")],
            Now,
            CancellationToken.None);

        Assert.Equal(2, summary.Added);
        Assert.Equal(0, summary.Deactivated);
        Assert.Equal(2, await CountAsync(connection, "SELECT count(*) FROM streams WHERE is_active = 1"));
    }

    [Fact]
    public async Task Re_syncing_identical_data_changes_nothing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);
        var streams = new[] { Record("1", "BBC One", "tvg:bbc1") };

        await ProviderSync.SyncAsync(connection, 1, streams, Now, CancellationToken.None);
        var second = await ProviderSync.SyncAsync(connection, 1, streams, Now, CancellationToken.None);

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Deactivated);
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM streams"));
    }

    [Fact]
    public async Task A_stream_missing_from_the_payload_is_deactivated_not_deleted()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await ProviderSync.SyncAsync(
            connection, 1,
            [Record("1", "BBC One", "tvg:bbc1"), Record("2", "BBC Two", "tvg:bbc2")],
            Now, CancellationToken.None);

        var summary = await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One", "tvg:bbc1")], Now, CancellationToken.None);

        Assert.Equal(1, summary.Deactivated);

        // Still present, so favourites and history survive a provider blip.
        Assert.Equal(2, await CountAsync(connection, "SELECT count(*) FROM streams"));
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM streams WHERE is_active = 0"));
    }

    [Fact]
    public async Task A_returning_stream_is_reactivated()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);
        var both = new[] { Record("1", "BBC One", "tvg:bbc1"), Record("2", "BBC Two", "tvg:bbc2") };

        await ProviderSync.SyncAsync(connection, 1, both, Now, CancellationToken.None);
        await ProviderSync.SyncAsync(connection, 1, [both[0]], Now, CancellationToken.None);
        var summary = await ProviderSync.SyncAsync(connection, 1, both, Now, CancellationToken.None);

        Assert.Equal(1, summary.Reactivated);
        Assert.Equal(0, await CountAsync(connection, "SELECT count(*) FROM streams WHERE is_active = 0"));
    }

    [Fact]
    public async Task A_changed_title_updates_in_place()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One", "tvg:bbc1")], Now, CancellationToken.None);
        var summary = await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One HD", "tvg:bbc1")], Now, CancellationToken.None);

        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM streams"));
    }

    [Fact]
    public async Task User_state_survives_a_resync()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One", "tvg:bbc1")], Now, CancellationToken.None);
        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        await ExecuteAsync(connection,
            """
            UPDATE channels SET is_favorite = 1, user_sort_order = 42 WHERE channel_key = 'tvg:bbc1';
            INSERT INTO epg_map (channel_key, epg_channel_id, confidence, method, locked, updated_utc)
              VALUES ('tvg:bbc1', 'manual.epg', 1.0, 'manual', 1, 0);
            INSERT INTO playback_state (content_key, position_secs, updated_utc)
              VALUES ('tvg:bbc1', 1234, 0);
            """);

        // A full re-sync with the channel renamed and reordered.
        await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One HD", "tvg:bbc1")], Now, CancellationToken.None);
        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        Assert.Equal(1, await CountAsync(connection,
            "SELECT is_favorite FROM channels WHERE channel_key = 'tvg:bbc1'"));
        Assert.Equal(42, await CountAsync(connection,
            "SELECT user_sort_order FROM channels WHERE channel_key = 'tvg:bbc1'"));
        Assert.Equal(1, await CountAsync(connection,
            "SELECT locked FROM epg_map WHERE channel_key = 'tvg:bbc1'"));
        Assert.Equal(1234, await CountAsync(connection,
            "SELECT position_secs FROM playback_state WHERE content_key = 'tvg:bbc1'"));
    }

    [Fact]
    public async Task Long_absent_streams_are_pruned()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await ProviderSync.SyncAsync(
            connection, 1,
            [Record("1", "BBC One", "tvg:bbc1"), Record("2", "Gone", "tvg:gone")],
            Now, CancellationToken.None);

        // Absent now, and still absent 31 days later.
        await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One", "tvg:bbc1")], Now, CancellationToken.None);
        await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One", "tvg:bbc1")],
            Now.AddDays(31), CancellationToken.None);

        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM streams"));
    }

    [Fact]
    public async Task A_sync_never_touches_another_providers_streams()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await ProviderSync.SyncAsync(
            connection, 1, [Record("1", "BBC One", "tvg:bbc1")], Now, CancellationToken.None);
        await ProviderSync.SyncAsync(
            connection, 2, [Record("9", "ESPN", "tvg:espn", providerId: 2)], Now, CancellationToken.None);

        // Provider 1 syncs again with nothing. Provider 2's channel must be untouched.
        await ProviderSync.SyncAsync(connection, 1, [], Now, CancellationToken.None);

        Assert.Equal(1, await CountAsync(connection,
            "SELECT count(*) FROM streams WHERE provider_id = 2 AND is_active = 1"));
    }

    [Fact]
    public async Task Channels_are_created_for_each_distinct_key()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        await ProviderSync.SyncAsync(
            connection, 1,
            [Record("1", "BBC One HD", "tvg:bbc1"), Record("2", "BBC Two", "tvg:bbc2")],
            Now, CancellationToken.None);
        await ProviderSync.SyncAsync(
            connection, 2,
            [Record("9", "BBC One FHD", "tvg:bbc1", providerId: 2)],
            Now, CancellationToken.None);
        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        // Two providers, three streams, two logical channels.
        Assert.Equal(2, await CountAsync(connection, "SELECT count(*) FROM channels"));
    }

    [Fact]
    public async Task Separator_rows_do_not_become_channels()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        var separator = Record("1", "##### SPORTS #####", "name:sports") with { IsSeparator = true };
        await ProviderSync.SyncAsync(
            connection, 1, [separator, Record("2", "BBC One", "tvg:bbc1")], Now, CancellationToken.None);
        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        // Stored and visible as a heading, but not a channel that can be favourited,
        // failed over to, or counted in EPG coverage.
        Assert.Equal(2, await CountAsync(connection, "SELECT count(*) FROM streams"));
        Assert.Equal(1, await CountAsync(connection, "SELECT count(*) FROM channels"));
    }

    [Fact]
    public async Task Syncing_one_kind_does_not_deactivate_another()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenMigratedAsync(db);

        var live = Record("1", "BBC One", "tvg:bbc1");
        var movie = Record("2", "Some Film", "name:somefilm") with { Kind = StreamKind.Vod };

        await ProviderSync.SyncAsync(connection, 1, [live], StreamKind.Live, Now, CancellationToken.None);
        await ProviderSync.SyncAsync(connection, 1, [movie], StreamKind.Vod, Now, CancellationToken.None);

        // Live and VOD are fetched by separate endpoints and synced separately. Scoping
        // deactivation to the provider alone would let the VOD sync deactivate every live
        // channel, and the next live sync would reactivate them - an invisible churn that
        // rewrites is_active on the whole library twice per refresh.
        Assert.Equal(2, await CountAsync(connection, "SELECT count(*) FROM streams WHERE is_active = 1"));
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
