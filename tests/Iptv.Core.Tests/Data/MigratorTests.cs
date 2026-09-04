using Iptv.Core.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Data;

/// <summary>
/// Phase 1 exit criterion: migrations run from empty to current on a fresh machine.
/// </summary>
public sealed class MigratorTests
{
    [Fact]
    public async Task Migrating_an_empty_database_creates_every_table_the_schema_defines()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        await Migrator.MigrateAsync(connection, CancellationToken.None);

        var tables = await TableNamesAsync(connection);
        Assert.Equal(
            [
                "channels",
                "epg_channels",
                "epg_map",
                "meta",
                "playback_state",
                "programmes",
                "providers",
                "series",
                "stream_health",
                "streams",
            ],
            tables.Where(t => !t.Contains("fts", StringComparison.Ordinal)).Order().ToArray());
    }

    [Fact]
    public async Task Migrating_creates_the_full_text_search_tables()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        await Migrator.MigrateAsync(connection, CancellationToken.None);

        var tables = await TableNamesAsync(connection);
        Assert.Contains("streams_fts", tables);
        Assert.Contains("programmes_fts", tables);
    }

    [Fact]
    public async Task Migrating_records_the_schema_version()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        await Migrator.MigrateAsync(connection, CancellationToken.None);

        Assert.Equal(Migrator.LatestVersion, await SchemaVersionAsync(connection));
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        var first = await Migrator.MigrateAsync(connection, CancellationToken.None);
        var second = await Migrator.MigrateAsync(connection, CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.Equal(Migrator.LatestVersion, await SchemaVersionAsync(connection));
    }

    [Fact]
    public async Task Migrating_seeds_the_normalization_version()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        await Migrator.MigrateAsync(connection, CancellationToken.None);

        // channel_key is derived from the normalization function and carries all user
        // state, so the version it was built with has to be recorded from the start.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'normalization_version';";
        Assert.Equal("1", (await command.ExecuteScalarAsync(CancellationToken.None))?.ToString());
    }

    [Fact]
    public async Task A_failing_migration_leaves_the_schema_version_unchanged()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        var broken = new SqlMigration(1, "broken", "CREATE TABLE ok (id INTEGER); CREATE TABLE ;");

        await Assert.ThrowsAsync<SqliteException>(
            () => Migrator.MigrateAsync(connection, [broken], CancellationToken.None));

        // The whole migration must roll back, not leave the first statement applied.
        Assert.Equal(0, await SchemaVersionAsync(connection));
        Assert.DoesNotContain("ok", await TableNamesAsync(connection));
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_after_migration()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams
              (provider_id, provider_stream_id, kind, title, normalized_title, url, channel_key)
            VALUES (999, 'x', 'live', 'X', 'x', 'http://example/x', 'name:x');
            """;

        // ON DELETE CASCADE in the schema is meaningless unless foreign keys are on;
        // SQLite defaults them off per connection.
        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync(CancellationToken.None));
    }

    private static async Task<string[]> TableNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task<int> SchemaVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
