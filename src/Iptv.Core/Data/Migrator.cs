using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Data;

/// <summary>A single versioned schema change.</summary>
/// <param name="Version">Monotonic, starting at 1.</param>
/// <param name="Name">Human-readable name, used in logs and failure messages.</param>
/// <param name="Sql">The statements to apply, run as one transaction.</param>
public sealed record SqlMigration(int Version, string Name, string Sql);

/// <summary>
/// Applies schema migrations to the library database.
/// </summary>
/// <remarks>
/// Schema version is tracked in <c>PRAGMA user_version</c> rather than a table. The
/// <c>meta</c> table is itself created by migration 001, so using it for schema version
/// would need bootstrapping; <c>user_version</c> exists in an empty database and costs
/// nothing to read. <c>meta</c> still holds the normalization version, which is a
/// different concern with a different lifetime.
/// </remarks>
public static class Migrator
{
    private static readonly ImmutableArray<SqlMigration> All = LoadEmbeddedMigrations();

    /// <summary>The version an up-to-date database reports.</summary>
    public static int LatestVersion => All.IsEmpty ? 0 : All[^1].Version;

    /// <summary>Applies every migration newer than the database's current version.</summary>
    /// <returns>The migrations that were applied, in order. Empty when already current.</returns>
    public static Task<ImmutableArray<SqlMigration>> MigrateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
        => MigrateAsync(connection, All, cancellationToken);

    /// <summary>Applies a specific migration set. Exposed for tests.</summary>
    public static async Task<ImmutableArray<SqlMigration>> MigrateAsync(
        SqliteConnection connection,
        IReadOnlyList<SqlMigration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(migrations);

        // SqliteConnectionFactory sets this too, but MigrateAsync accepts any connection
        // and ON DELETE CASCADE is inert without it. Cheap to repeat.
        // Must be outside a transaction: SQLite silently ignores it inside one.
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken)
            .ConfigureAwait(false);

        var current = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var applied = ImmutableArray.CreateBuilder<SqlMigration>();

        foreach (var migration in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await ApplyAsync(connection, migration, cancellationToken).ConfigureAwait(false);
            applied.Add(migration);
        }

        return applied.ToImmutable();
    }

    private static async Task ApplyAsync(
        SqliteConnection connection,
        SqlMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // user_version takes a literal, not a parameter. The value is an int from a
            // migration this assembly embeds, so there is no injection surface here.
            command.CommandText = $"PRAGMA user_version = {migration.Version};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads migrations from embedded resources named <c>NNN_Name.sql</c>.
    /// </summary>
    private static ImmutableArray<SqlMigration> LoadEmbeddedMigrations()
    {
        var assembly = typeof(Migrator).Assembly;
        const string prefix = "Iptv.Core.Data.Migrations.";

        var migrations = new List<SqlMigration>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = name[prefix.Length..^".sql".Length];
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 ||
                !int.TryParse(
                    fileName[..separator],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var version))
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{name}' does not follow the NNN_Name.sql convention.");
            }

            migrations.Add(new SqlMigration(version, fileName[(separator + 1)..], Read(assembly, name)));
        }

        var ordered = migrations.OrderBy(m => m.Version).ToImmutableArray();

        var duplicate = ordered.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Two embedded migrations share version {duplicate.Key}. Versions must be unique.");
        }

        return ordered;
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
