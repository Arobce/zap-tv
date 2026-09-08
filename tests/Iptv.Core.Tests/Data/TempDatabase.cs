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

        // Unpooled, so disposing a connection releases the file handle and this database
        // can be deleted without ClearAllPools. See the remark on the factory: that call
        // is global, and xunit runs test classes in parallel.
        Factory = new SqliteConnectionFactory(Path_, pooled: false);
    }

    public string Path_ { get; }

    public SqliteConnectionFactory Factory { get; }

    public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        => Factory.OpenAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // No ClearAllPools here. It is global, and clearing pools while another test class
        // is mid-query against its own database made unrelated tests fail about one run in
        // six. Pooling is off for this factory instead, so there is nothing to clear.
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
