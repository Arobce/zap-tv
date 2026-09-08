using Microsoft.Data.Sqlite;

namespace Iptv.Core.Data;

/// <summary>
/// Opens connections to the library database with the pragma set the PRD requires.
/// </summary>
/// <remarks>
/// <para>
/// WAL is load-bearing rather than a tuning preference: the EPG refresh writes while the
/// UI reads, and without it the guide grid stutters during background sync.
/// </para>
/// <para>
/// <c>cache_size</c> is a 64MB budget <em>per connection</em>, and Microsoft.Data.Sqlite
/// pools connections per connection string. Keep the number of distinct live connections
/// small and deliberate - one long-lived reader for the UI and one writer - rather than
/// opening ad-hoc connections from repositories and multiplying the budget by the pool size.
/// </para>
/// </remarks>
public sealed class SqliteConnectionFactory
{
    /// <summary>
    /// Applied in this order on every connection. Order matters: <c>journal_mode</c> must
    /// be set before the others so that later pragmas apply to a WAL database.
    /// </summary>
    private static readonly string[] Pragmas =
    [
        "PRAGMA journal_mode=WAL;",
        "PRAGMA synchronous=NORMAL;",
        "PRAGMA temp_store=MEMORY;",
        "PRAGMA mmap_size=268435456;",
        "PRAGMA cache_size=-64000;",
        // Raw SQLite defaults foreign keys OFF per connection; Microsoft.Data.Sqlite
        // turns them on for us. Stated explicitly anyway so the guarantee survives a
        // provider swap or a connection-string change: the schema is full of
        // ON DELETE CASCADE, and without enforcement, deleting a provider would
        // silently orphan its streams and their health history instead of cascading.
        "PRAGMA foreign_keys=ON;",
    ];

    private readonly string _connectionString;

    /// <param name="pooled">
    /// Whether connections may be pooled.
    /// </param>
    /// <remarks>
    /// False exists for tests. Releasing a pooled connection leaves the file handle open,
    /// so a temporary database cannot be deleted, and the documented remedy —
    /// <c>SqliteConnection.ClearAllPools</c> — is global: called while another test is
    /// running in parallel, it reaches into that test's database too. That produced
    /// intermittent failures in unrelated tests roughly one run in six. Turning pooling off
    /// removes the need for the global call rather than trying to time it.
    /// </remarks>
    public SqliteConnectionFactory(string databasePath, bool pooled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooled,
        }.ToString();
    }

    /// <summary>Absolute path to the library database file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Opens a connection and applies the required pragmas. The caller owns the returned
    /// connection and must dispose it.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // A half-configured connection is worse than none: the caller would get WAL
            // without the cache budget, and the failure would surface later as a slow query.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ApplyPragmasAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        foreach (var pragma in Pragmas)
        {
            command.CommandText = pragma;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
