using Iptv.Core.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Data;

/// <summary>
/// The PRD requires a specific pragma set on every connection. WAL in particular is
/// load-bearing: EPG refresh writes while the UI reads, and without it the grid stutters
/// during background sync. These tests assert the pragmas are actually in effect on the
/// returned connection, not merely that the factory issued the statements.
/// </summary>
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"iptv-test-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Open_enables_write_ahead_logging()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Open_sets_synchronous_to_normal()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        // 1 == NORMAL. FULL (2) costs an fsync per transaction, which the batched
        // programme inserts cannot afford; WAL makes NORMAL durable enough here.
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA synchronous;"));
    }

    [Fact]
    public async Task Open_stores_temporary_tables_in_memory()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        // 2 == MEMORY.
        Assert.Equal("2", await ScalarAsync(connection, "PRAGMA temp_store;"));
    }

    [Fact]
    public async Task Open_applies_the_configured_page_cache_budget()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        // Negative values are a KiB budget rather than a page count: -64000 == 64MB.
        Assert.Equal("-64000", await ScalarAsync(connection, "PRAGMA cache_size;"));
    }

    [Fact]
    public async Task Open_enables_foreign_key_enforcement()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        // Microsoft.Data.Sqlite happens to enable this by default, so this asserts the
        // outcome rather than our pragma. That is deliberate: what matters is that
        // connections from this factory enforce cascades, however that comes about.
        // Verified to have teeth - setting ForeignKeys=false on the connection string
        // fails this test.
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task Open_enables_memory_mapped_io()
    {
        var factory = new SqliteConnectionFactory(_dbPath);

        await using var connection = await factory.OpenAsync(CancellationToken.None);

        Assert.Equal("268435456", await ScalarAsync(connection, "PRAGMA mmap_size;"));
    }

    [Fact]
    public async Task Open_creates_the_database_directory_when_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iptv-test-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested", "library.db");
        var factory = new SqliteConnectionFactory(nested);

        try
        {
            // Scoped so the connection is closed before the directory is removed.
            await using (await factory.OpenAsync(CancellationToken.None))
            {
                Assert.True(File.Exists(nested));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value?.ToString();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
