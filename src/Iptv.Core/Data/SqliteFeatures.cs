using Microsoft.Data.Sqlite;

namespace Iptv.Core.Data;

/// <summary>
/// Raised when the SQLite build in use lacks a feature the application depends on.
/// </summary>
public sealed class MissingSqliteFeatureException : Exception
{
    public MissingSqliteFeatureException(string feature)
        : base(
            $"The SQLite build in use does not support {feature}, which this application requires. " +
            $"Ensure the SQLitePCLRaw.bundle_e_sqlite3 package is referenced and that no other " +
            $"SQLitePCLRaw bundle (bundle_green in particular) is winning the reference. " +
            $"Continuing without {feature} would silently degrade search rather than fail.")
    {
        Feature = feature;
    }

    public string Feature { get; }
}

/// <summary>
/// Runtime capability checks for the SQLite build actually loaded in this process.
/// </summary>
public static class SqliteFeatures
{
    /// <summary>
    /// Determines whether FTS5 is usable by creating a temporary virtual table.
    /// </summary>
    /// <remarks>
    /// Probing the real capability rather than reading <c>pragma_compile_options</c>: the
    /// compile-options string can be present in a build where the extension still fails to
    /// register, and the thing worth knowing is whether <c>CREATE VIRTUAL TABLE</c> works.
    /// The probe targets the <c>temp</c> schema so nothing is written to the library file.
    /// </remarks>
    public static async Task<bool> IsFts5AvailableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        try
        {
            command.CommandText =
                "CREATE VIRTUAL TABLE temp.iptv_fts5_probe USING fts5(probe);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return false;
        }

        command.CommandText = "DROP TABLE temp.iptv_fts5_probe;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Throws <see cref="MissingSqliteFeatureException"/> when FTS5 is unavailable.
    /// </summary>
    /// <remarks>
    /// Call this at startup. The PRD requires failing loudly rather than degrading search.
    /// <para>
    /// The unavailable branch has no automated test: it needs a SQLite build compiled
    /// without FTS5, which the pinned bundle never produces. The message itself is tested
    /// by constructing the exception directly.
    /// </para>
    /// </remarks>
    public static async Task EnsureFts5AvailableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await IsFts5AvailableAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new MissingSqliteFeatureException("FTS5");
        }
    }
}
