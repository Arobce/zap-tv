using System.Text;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Managing configured providers, which until now only a gitignored .env file could do.
/// </summary>
public sealed class ProviderRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Reversible without DPAPI, so these tests run anywhere.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        public byte[]? Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }

    private static readonly FakeProtector Protector = new();

    private static XtreamCredentials Credentials(string host = "http://a.invalid")
        => new(new Uri(host), "ACCT7X2", "SECRET99");

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private static Task<int> AddAsync(
        SqliteConnection connection,
        string name = "Main",
        string host = "http://a.invalid",
        int priority = 0)
        => ProviderRepository.UpsertAsync(
            connection, name, Credentials(host), Protector, CancellationToken.None, priority);

    private static Task<IReadOnlyList<ProviderInfo>> ListAsync(SqliteConnection connection)
        => ProviderRepository.ListAsync(connection, Protector, CancellationToken.None);

    [Fact]
    public async Task A_new_database_has_no_providers()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // The first-run state. The app must say so usefully rather than showing an empty
        // channel list, which reads as a broken sync.
        Assert.Empty(await ListAsync(connection));
    }

    [Fact]
    public async Task Adding_stores_the_provider_and_its_credentials()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var id = await AddAsync(connection);

        var provider = Assert.Single(await ListAsync(connection));
        Assert.Equal(id, provider.Id);
        Assert.Equal("Main", provider.Name);
        Assert.True(provider.HasCredentials);

        var loaded = await ProviderCredentialStore.LoadAsync(
            connection, id, Protector, CancellationToken.None);

        Assert.Equal("ACCT7X2", loaded!.Username);
    }

    [Fact]
    public async Task Adding_the_same_host_twice_updates_rather_than_duplicates()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var first = await AddAsync(connection, name: "Main");
        var second = await AddAsync(connection, name: "Renamed");

        // Matched on the host, not the name: the name is the user's label and they will
        // change it. A duplicate would give failover two identical candidates and double
        // every sync.
        Assert.Equal(first, second);
        Assert.Equal("Renamed", Assert.Single(await ListAsync(connection)).Name);
    }

    [Fact]
    public async Task Two_hosts_are_two_providers()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "One", "http://a.invalid");
        await AddAsync(connection, "Two", "http://b.invalid", priority: 1);

        Assert.Equal(["One", "Two"], (await ListAsync(connection)).Select(p => p.Name));
    }

    [Fact]
    public async Task Providers_are_listed_in_failover_order()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "Backup", "http://b.invalid", priority: 5);
        await AddAsync(connection, "Primary", "http://a.invalid", priority: 0);

        Assert.Equal(["Primary", "Backup"], (await ListAsync(connection)).Select(p => p.Name));
    }

    [Fact]
    public async Task Credentials_written_by_another_machine_read_as_missing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var id = await AddAsync(connection);

        // A DPAPI blob is bound to the user and machine that wrote it. The view has to say
        // "re-enter these" rather than "no provider", so the row still reports itself.
        await using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE providers SET password = NULL WHERE id = @id;";
            corrupt.Parameters.AddWithValue("@id", id);
            await corrupt.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var provider = Assert.Single(await ListAsync(connection));
        Assert.False(provider.HasCredentials);
        Assert.Null(await ProviderCredentialStore.LoadAsync(
            connection, id, Protector, CancellationToken.None));
    }

    [Fact]
    public async Task Disabling_keeps_the_provider_and_its_library()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var id = await AddAsync(connection);

        await ProviderRepository.SetEnabledAsync(connection, id, false, CancellationToken.None);

        // Every query filters on enabled, so a disabled provider leaves the lists and
        // failover while its streams, favourites and history stay put.
        var provider = Assert.Single(await ListAsync(connection));
        Assert.False(provider.Enabled);
    }

    [Fact]
    public async Task Recording_the_account_fills_in_what_a_test_learned()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var id = await AddAsync(connection);

        await ProviderRepository.RecordAccountAsync(connection, id, 2, Now, CancellationToken.None);

        var provider = Assert.Single(await ListAsync(connection));
        Assert.Equal(2, provider.MaxConnections);
        Assert.Equal(Now, provider.LastSyncUtc);
    }


    [Fact]
    public async Task Recording_nothing_does_not_erase_what_was_known()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var id = await AddAsync(connection);

        await ProviderRepository.RecordAccountAsync(connection, id, 2, Now, CancellationToken.None);

        // A later sync that could not read the account must not wipe the connection limit,
        // which the whole rate limiter depends on.
        await ProviderRepository.RecordAccountAsync(connection, id, null, null, CancellationToken.None);

        Assert.Equal(2, Assert.Single(await ListAsync(connection)).MaxConnections);
    }

    [Fact]
    public async Task The_channel_count_says_whether_a_sync_worked()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var id = await AddAsync(connection);

        await AddStreamAsync(connection, id, "a", "live", active: true);
        await AddStreamAsync(connection, id, "b", "live", active: true);
        await AddStreamAsync(connection, id, "c", "vod", active: true);
        await AddStreamAsync(connection, id, "d", "live", active: false);

        // Live, active, non-separator. Counting films or dead rows would report a healthy
        // number for a provider whose channels have all gone.
        Assert.Equal(2, Assert.Single(await ListAsync(connection)).LiveChannels);
    }

    [Fact]
    public async Task Deleting_takes_the_streams_with_it_but_not_the_favourites()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var id = await AddAsync(connection);

        await AddStreamAsync(connection, id, "a", "live", active: true);
        await ChannelRepository.SetFavouriteAsync(connection, "key:a", true, CancellationToken.None);

        await ProviderRepository.DeleteAsync(connection, id, CancellationToken.None);

        Assert.Empty(await ListAsync(connection));

        // channels is user-owned and hangs off channel_key, not off the provider. Losing a
        // provider must not lose the favourites that outlive it.
        Assert.Equal(1, await ChannelRepository.CountFavouritesAsync(connection, CancellationToken.None));

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM streams;";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(CancellationToken.None))!);
    }

    private static async Task AddStreamAsync(
        SqliteConnection connection,
        int providerId,
        string id,
        string kind,
        bool active)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (@provider, @sid, @kind, @sid, @sid,
                    'http://host.invalid/' || @sid, 'key:' || @sid, @active, 0, 0);
            """;

        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@sid", id);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@active", active ? 1 : 0);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
