using Iptv.Core.Data;

namespace Iptv.Core.Tests.Data;

/// <summary>
/// The PRD requires FTS5 to be confirmed available at runtime, with the app failing
/// loudly at startup if it is not. Search across channels, VOD, series and programme
/// titles is a headline feature; degrading silently to LIKE queries would turn a hard
/// startup failure into a mystery performance complaint months later.
/// </summary>
public sealed class SqliteFeaturesTests
{
    [Fact]
    public async Task Fts5_is_available_in_the_bundled_sqlite_build()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        Assert.True(await SqliteFeatures.IsFts5AvailableAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task Ensure_does_not_throw_when_fts5_is_available()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        await SqliteFeatures.EnsureFts5AvailableAsync(connection, CancellationToken.None);
    }

    [Fact]
    public async Task Probing_leaves_no_table_behind()
    {
        await using var db = new TempDatabase();
        await using var connection = await db.OpenAsync();

        await SqliteFeatures.IsFts5AvailableAsync(connection, CancellationToken.None);

        // The probe creates a virtual table to test the real capability rather than
        // trusting a compile-options string. It must not leave that table in the schema,
        // or the migrator would later see a database it does not recognise.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_schema WHERE name LIKE '%fts5_probe%';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public void Missing_feature_exception_names_the_feature_and_the_remedy()
    {
        var exception = new MissingSqliteFeatureException("FTS5");

        // This message is what a user sees when the app refuses to start, so it has to
        // say what is missing and what to do, not just that something went wrong.
        Assert.Contains("FTS5", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SQLitePCLRaw.bundle_e_sqlite3", exception.Message, StringComparison.Ordinal);
    }
}
