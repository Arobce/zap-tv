using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// How many concurrent streams it is safe to open.
/// </summary>
/// <remarks>
/// The app hardcoded one, so an account permitting four was throttled to a quarter of what
/// it allows — while the number it actually permits was sitting recorded and unread.
/// </remarks>
public sealed class ConnectionLimitTests
{
    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task ProviderAsync(
        SqliteConnection connection,
        int id,
        int? maxConnections,
        bool enabled = true)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url, max_connections, enabled)
            VALUES (@id, 'p' || @id, 'xtream', 'http://h' || @id, @max, @enabled);
            """;

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@max", (object?)maxConnections ?? DBNull.Value);
        command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task<int> LimitAsync(SqliteConnection connection)
        => ProviderRepository.GetSafeConnectionLimitAsync(connection, CancellationToken.None);

    [Fact]
    public async Task Nothing_known_means_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // A panel that has never been asked must not be read as unlimited.
        Assert.Equal(1, await LimitAsync(connection));
    }

    [Fact]
    public async Task A_provider_that_reports_zero_is_still_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await ProviderAsync(connection, 1, 0);

        // Panels do report 0. Taking it literally would refuse to play anything.
        Assert.Equal(1, await LimitAsync(connection));
    }

    [Fact]
    public async Task A_single_provider_gets_its_own_limit()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await ProviderAsync(connection, 1, 4);

        Assert.Equal(4, await LimitAsync(connection));
    }

    [Fact]
    public async Task Two_providers_get_the_smaller_limit()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await ProviderAsync(connection, 1, 4);
        await ProviderAsync(connection, 2, 1);

        // Failover can open either provider's copy of a channel, so the generous account's
        // four would exceed the other account's one — and exceeding it is what gets an
        // account blocked.
        Assert.Equal(1, await LimitAsync(connection));
    }

    [Fact]
    public async Task A_disabled_provider_does_not_constrain_the_others()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await ProviderAsync(connection, 1, 4);
        await ProviderAsync(connection, 2, 1, enabled: false);

        // Nothing will be opened on it, so its limit is not a limit on anything.
        Assert.Equal(4, await LimitAsync(connection));
    }

    [Fact]
    public async Task A_provider_with_no_recorded_limit_is_ignored_rather_than_treated_as_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await ProviderAsync(connection, 1, 4);
        await ProviderAsync(connection, 2, null);

        // Unknown is not the same as one. Treating it as one would throttle a known-good
        // account because a second provider has never been synced.
        Assert.Equal(4, await LimitAsync(connection));
    }
}
