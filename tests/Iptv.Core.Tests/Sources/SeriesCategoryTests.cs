using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Categories for the series catalogue.
/// </summary>
/// <remarks>
/// Series are the one catalogue that does not live in <c>streams</c> — a series carries no
/// streams until someone opens it and the episodes are fetched — so counting and filtering
/// them is a separate path from live and VOD rather than the same one with a parameter.
/// </remarks>
public sealed class SeriesCategoryTests
{
    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await ExecuteAsync(connection,
            """
            INSERT INTO providers (id, name, kind, base_url, priority) VALUES
              (1, 'A', 'xtream', 'http://a.invalid', 0),
              (2, 'B', 'xtream', 'http://b.invalid', 1);
            """);

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task<SyncSummary> SyncAsync(
        SqliteConnection connection,
        int providerId,
        params SeriesRecord[] series)
        => SeriesSync.SyncAsync(connection, providerId, series, CancellationToken.None);

    private static SeriesRecord Series(
        int providerId,
        string id,
        string title,
        string? categoryId)
        => new()
        {
            ProviderId = providerId,
            ProviderSeriesId = id,
            Title = title,
            NormalizedTitle = title.ToLowerInvariant(),
            SeriesKey = $"key:{title.ToLowerInvariant()}",
            CategoryId = categoryId,
        };

    [Fact]
    public async Task A_series_category_is_stored_and_counted()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Series,
            [("10", "Drama", 0), ("11", "Comedy", 0)],
            CancellationToken.None);

        await SyncAsync(
            connection, 1,
            Series(1, "s1", "The Wire", "10"),
            Series(1, "s2", "The Sopranos", "10"),
            Series(1, "s3", "Peep Show", "11"));

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Series, CancellationToken.None);

        Assert.Equal(2, categories.Count);
        Assert.Equal("Comedy", categories[0].Name);
        Assert.Equal(1, categories[0].Count);
        Assert.Equal("Drama", categories[1].Name);
        Assert.Equal(2, categories[1].Count);
    }

    [Fact]
    public async Task An_empty_category_is_not_listed()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Series,
            [("10", "Drama", 0), ("99", "Nothing here", 0)],
            CancellationToken.None);

        await SyncAsync(connection, 1, Series(1, "s1", "The Wire", "10"));

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Series, CancellationToken.None);

        // Providers ship categories holding nothing. A name that leads to an empty screen
        // is worse than no name.
        Assert.Equal(["Drama"], categories.Select(c => c.Name));
    }

    [Fact]
    public async Task The_same_name_from_two_providers_is_one_entry()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // Deliberately different ids for the same name: providers do not agree on numbering,
        // and the user picks a name once rather than picking between two identical rows.
        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Series, [("10", "Drama", 0)], CancellationToken.None);
        await CategoryRepository.ReplaceAsync(
            connection, 2, CategoryKind.Series, [("77", "Drama", 0)], CancellationToken.None);

        await SyncAsync(connection, 1, Series(1, "s1", "The Wire", "10"));
        await SyncAsync(connection, 2, Series(2, "s2", "The Sopranos", "77"));

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Series, CancellationToken.None);

        Assert.Single(categories);
        Assert.Equal(2, categories[0].Count);
    }

    [Fact]
    public async Task A_disabled_provider_contributes_nothing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 2, CategoryKind.Series, [("77", "Drama", 0)], CancellationToken.None);
        await SyncAsync(connection, 2, Series(2, "s2", "The Sopranos", "77"));

        await ExecuteAsync(connection, "UPDATE providers SET enabled = 0 WHERE id = 2;");

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Series, CancellationToken.None);

        Assert.Empty(categories);
    }

    [Fact]
    public async Task A_live_category_of_the_same_name_is_a_different_thing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // One provider's live "10" has nothing to do with its series "10". Asking for
        // series categories must not count channels.
        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Live, [("10", "Drama", 0)], CancellationToken.None);

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Series, CancellationToken.None);

        Assert.Empty(categories);
    }

    [Fact]
    public async Task Browsing_a_category_returns_only_its_series()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Series,
            [("10", "Drama", 0), ("11", "Comedy", 0)],
            CancellationToken.None);

        await SyncAsync(
            connection, 1,
            Series(1, "s1", "The Wire", "10"),
            Series(1, "s3", "Peep Show", "11"));

        var drama = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery { Category = "Drama", Limit = 50 }, CancellationToken.None);

        Assert.Equal(["The Wire"], drama.Select(s => s.Title));
    }

    [Fact]
    public async Task No_category_returns_everything()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SyncAsync(
            connection, 1,
            Series(1, "s1", "The Wire", "10"),
            Series(1, "s2", "Uncategorised", null));

        var all = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery { Limit = 50 }, CancellationToken.None);

        // Including the one the provider filed nowhere. A series with no category is still
        // in the catalogue; it is only absent from the category lists.
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task A_series_with_no_category_is_in_no_category()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Series, [("10", "Drama", 0)], CancellationToken.None);

        await SyncAsync(connection, 1, Series(1, "s2", "Uncategorised", null));

        var drama = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery { Category = "Drama", Limit = 50 }, CancellationToken.None);

        Assert.Empty(drama);
    }

    [Fact]
    public async Task Re_syncing_updates_the_category()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SyncAsync(connection, 1, Series(1, "s1", "The Wire", "10"));
        await SyncAsync(connection, 1, Series(1, "s1", "The Wire", "11"));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT category_id FROM series WHERE provider_series_id = 's1';";

        // A provider that refiles a series must not leave it in both places, and the upsert
        // is the only thing that decides that.
        Assert.Equal("11", (string?)await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
