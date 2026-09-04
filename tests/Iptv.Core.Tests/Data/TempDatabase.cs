using Iptv.Core.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Data;

/// <summary>
/// A throwaway on-disk database for a single test.
/// </summary>
/// <remarks>
/// Deliberately on disk rather than <c>:memory:</c>. WAL, <c>mmap_size</c> and the
/// staging-table swap all behave differently in memory, so an in-memory database would
/// let bugs through that only appear against a real file.
/// </remarks>
public sealed class TempDatabase : IAsyncDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"iptv-test-{Guid.NewGuid():N}");
        Path_ = Path.Combine(_directory, "library.db");
        Factory = new SqliteConnectionFactory(Path_);
    }

    public string Path_ { get; }

    public SqliteConnectionFactory Factory { get; }

    public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        => Factory.OpenAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Pooled connections keep the file handle open, which makes the delete fail on
        // Windows. Clearing the pool is the documented way to release them.
        SqliteConnection.ClearAllPools();

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
            // A leaked handle should not fail an otherwise passing test; the temp
            // directory is disposable either way.
        }
    }
}
