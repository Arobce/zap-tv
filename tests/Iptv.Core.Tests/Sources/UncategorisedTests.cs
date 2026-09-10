using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// The bucket for what the provider filed under a category it never named.
/// </summary>
/// <remarks>
/// On the reference account this is 13 ids covering 816 films — Spanish, Dutch, Indian and
/// Malaysian titles that are perfectly real and completely unreachable, because every
/// browse path finds a row through a category name and these have none.
/// </remarks>
public sealed class UncategorisedTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await ExecuteAsync(connection,
            "INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');");

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task AddStreamAsync(
        SqliteConnection connection,
        string title,
        string? categoryId,
        string kind = "vod")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO channels (channel_key, display_name) VALUES (@key, @title);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, category_id, is_active, is_separator, last_seen_utc)
            VALUES (1, @sid, @kind, @title, @title, 'http://a.invalid/' || @sid, @key, @cat, 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@key", $"key:{title.ToLowerInvariant()}");
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@cat", (object?)categoryId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Films_the_provider_never_named_are_listed_as_a_bucket()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Vod, [("10", "Drama", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "10");
        await AddStreamAsync(connection, "Orphan", "1064");
        await AddStreamAsync(connection, "Another orphan", "1056");

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Vod, CancellationToken.None);

        var bucket = categories.Single(c => c.Name == CategoryRepository.UncategorisedName);
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public async Task The_bucket_sorts_last()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // "(uncategorised)" starting with a bracket would sort first alphabetically. It is
        // appended instead: it is not a category, it is where the ones without a category
        // ended up.
        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Vod, [("10", "Zulu", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "10");
        await AddStreamAsync(connection, "Orphan", "1064");

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Vod, CancellationToken.None);

        Assert.Equal(CategoryRepository.UncategorisedName, categories[^1].Name);
    }

    [Fact]
    public async Task No_orphans_means_no_bucket()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Vod, [("10", "Drama", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "10");

        var categories = await CategoryRepository.GetCategoriesAsync(
            connection, CategoryKind.Vod, CancellationToken.None);

        // An empty bucket is a menu entry that leads to an empty screen, which is the thing
        // the count on every other entry exists to prevent.
        Assert.DoesNotContain(categories, c => c.Name == CategoryRepository.UncategorisedName);
    }

    [Fact]
    public async Task Browsing_the_bucket_returns_exactly_the_unnamed_films()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Vod, [("10", "Drama", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "10");
        await AddStreamAsync(connection, "Orphan", "1064");
        await AddStreamAsync(connection, "No category at all", null);

        var films = await LibraryRepository.GetFilmsAsync(
            connection,
            new CatalogueQuery { Category = CategoryRepository.UncategorisedName, Limit = 50 },
            CancellationToken.None);

        // The one with no category at all belongs here too: it is exactly as unbrowsable as
        // the one whose category has no name.
        Assert.Equal(["No category at all", "Orphan"], films.Select(f => f.Title).Order());
    }

    [Fact]
    public async Task The_count_agrees_with_the_listing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, "Orphan", "1064");
        await AddStreamAsync(connection, "No category at all", null);

        var query = new CatalogueQuery { Category = CategoryRepository.UncategorisedName, Limit = 50 };

        var count = await LibraryRepository.CountFilmsAsync(connection, query, CancellationToken.None);
        var films = await LibraryRepository.GetFilmsAsync(connection, query, CancellationToken.None);

        // The count is its own statement, and one that disagreed with the list would show
        // "2 of 47 films" over two rows.
        Assert.Equal(films.Count, count);
    }

    [Fact]
    public async Task A_named_category_still_excludes_the_bucket()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Vod, [("10", "Drama", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "10");
        await AddStreamAsync(connection, "Orphan", "1064");

        var films = await LibraryRepository.GetFilmsAsync(
            connection,
            new CatalogueQuery { Category = "Drama", Limit = 50 },
            CancellationToken.None);

        Assert.Equal(["Named"], films.Select(f => f.Title));
    }

    [Fact]
    public async Task No_category_chosen_still_returns_everything()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Vod, [("10", "Drama", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "10");
        await AddStreamAsync(connection, "Orphan", "1064");

        var films = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery { Limit = 50 }, CancellationToken.None);

        Assert.Equal(2, films.Count);
    }

    [Fact]
    public async Task Live_channels_have_the_same_bucket()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Live, [("1", "News", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Named", "1", kind: "live");
        await AddStreamAsync(connection, "Orphan", "999", kind: "live");

        var channels = await ChannelRepository.GetChannelsAsync(
            connection,
            new ChannelQuery { Category = CategoryRepository.UncategorisedName, Limit = 50 },
            Now,
            CancellationToken.None);

        Assert.Equal("Orphan", Assert.Single(channels).DisplayName);
    }

    [Fact]
    public async Task A_live_category_does_not_name_a_vod_stream()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // One provider's live "10" has nothing to do with its VOD "10". A film filed under
        // VOD 10 is unnamed even though a live category 10 exists.
        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Live, [("10", "News", 0)], CancellationToken.None);

        await AddStreamAsync(connection, "Film", "10");

        var films = await LibraryRepository.GetFilmsAsync(
            connection,
            new CatalogueQuery { Category = CategoryRepository.UncategorisedName, Limit = 50 },
            CancellationToken.None);

        Assert.Equal(["Film"], films.Select(f => f.Title));
    }

    [Fact]
    public async Task Series_get_the_bucket_too()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await CategoryRepository.ReplaceAsync(
            connection, 1, CategoryKind.Series, [("10", "Drama", 0)], CancellationToken.None);

        await SeriesSync.SyncAsync(connection, 1,
            [
                new SeriesRecord
                {
                    ProviderId = 1, ProviderSeriesId = "s1", Title = "Named",
                    NormalizedTitle = "named", SeriesKey = "k:named", CategoryId = "10",
                },
                new SeriesRecord
                {
                    ProviderId = 1, ProviderSeriesId = "s2", Title = "Orphan",
                    NormalizedTitle = "orphan", SeriesKey = "k:orphan", CategoryId = "999",
                },
            ],
            CancellationToken.None);

        var series = await LibraryRepository.GetSeriesAsync(
            connection,
            new CatalogueQuery { Category = CategoryRepository.UncategorisedName, Limit = 50 },
            CancellationToken.None);

        Assert.Equal(["Orphan"], series.Select(s => s.Title));
    }
}
