using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// The provider's own grouping of its catalogue.
/// </summary>
/// <remarks>
/// 118,763 streams is not a list, it is a haystack. Search only helps someone who already
/// knows the name of what they want; categories are what make the library browsable.
/// </remarks>
public sealed class CategoryRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

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

    private static Task<int> ReplaceAsync(
        SqliteConnection connection,
        long providerId,
        CategoryKind kind,
        params (string CategoryId, string Name, int ParentId)[] categories)
        => CategoryRepository.ReplaceAsync(connection, providerId, kind, categories, CancellationToken.None);

    private static async Task AddChannelAsync(
        SqliteConnection connection,
        long providerId,
        string name,
        string? categoryId,
        string kind = "live",
        bool active = true,
        bool separator = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO channels (channel_key, display_name) VALUES (@key, @name);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, category_id, is_active, is_separator, last_seen_utc)
            VALUES (@provider, @sid, @kind, @name, @normalized,
                    'http://host.invalid/' || @sid, @key, @category, @active, @separator, 0);
            """;

        var key = "name:" + ChannelNormalizer.Normalize(name).Replace(" ", string.Empty);

        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@normalized", ChannelNormalizer.Normalize(name));
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@category", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("@active", active ? 1 : 0);
        command.Parameters.AddWithValue("@separator", separator ? 1 : 0);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task<IReadOnlyList<CategoryListItem>> ListAsync(
        SqliteConnection connection,
        CategoryKind kind = CategoryKind.Live)
        => CategoryRepository.GetCategoriesAsync(connection, kind, CancellationToken.None);

    [Fact]
    public async Task Categories_with_nothing_in_them_are_not_listed()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live,
            ("1", "Sports", 0),
            ("2", "Empty", 0));

        await AddChannelAsync(connection, 1, "Sky Sports", "1");

        // A menu entry that leads to a blank screen is worse than no menu entry. Providers
        // ship these routinely.
        var category = Assert.Single(await ListAsync(connection));
        Assert.Equal("Sports", category.Name);
        Assert.Equal(1, category.Count);
    }

    [Fact]
    public async Task Counts_exclude_inactive_and_separator_streams()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0));

        await AddChannelAsync(connection, 1, "Sky Sports", "1");
        await AddChannelAsync(connection, 1, "Old Sports", "1", active: false);
        await AddChannelAsync(connection, 1, "=== SPORTS ===", "1", separator: true);

        // The count is a promise about what clicking will show. Counting rows the list
        // then filters out makes it a lie.
        Assert.Equal(1, Assert.Single(await ListAsync(connection)).Count);
    }

    [Fact]
    public async Task A_category_shared_by_two_providers_is_one_entry()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // Different ids for the same name, which is the normal case: the ids are each
        // panel's own numbering and mean nothing across providers.
        await ReplaceAsync(connection, 1, CategoryKind.Live, ("7", "Sports", 0));
        await ReplaceAsync(connection, 2, CategoryKind.Live, ("42", "Sports", 0));

        await AddChannelAsync(connection, 1, "Sky Sports", "7");
        await AddChannelAsync(connection, 2, "BT Sport", "42");

        var category = Assert.Single(await ListAsync(connection));
        Assert.Equal("Sports", category.Name);
        Assert.Equal(2, category.Count);
    }

    [Fact]
    public async Task Live_and_vod_categories_do_not_mix()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // The same id means different things per kind on one provider, which is why the
        // primary key includes the kind.
        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0));
        await ReplaceAsync(connection, 1, CategoryKind.Vod, ("1", "Action", 0));

        await AddChannelAsync(connection, 1, "Sky Sports", "1");
        await AddChannelAsync(connection, 1, "Die Hard", "1", kind: "vod");

        Assert.Equal("Sports", Assert.Single(await ListAsync(connection, CategoryKind.Live)).Name);
        Assert.Equal("Action", Assert.Single(await ListAsync(connection, CategoryKind.Vod)).Name);
    }

    [Fact]
    public async Task Replacing_drops_categories_the_provider_stopped_publishing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0), ("2", "News", 0));
        await AddChannelAsync(connection, 1, "Sky Sports", "1");
        await AddChannelAsync(connection, 1, "BBC News", "2");

        Assert.Equal(2, (await ListAsync(connection)).Count);

        // Replace rather than merge: a category has nothing of the user's hanging off it,
        // unlike a channel.
        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0));

        Assert.Equal("Sports", Assert.Single(await ListAsync(connection)).Name);
    }

    [Fact]
    public async Task Replacing_one_provider_leaves_the_other_alone()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0));
        await ReplaceAsync(connection, 2, CategoryKind.Live, ("1", "News", 0));
        await AddChannelAsync(connection, 1, "Sky Sports", "1");
        await AddChannelAsync(connection, 2, "BBC News", "1");

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0));

        Assert.Equal(["News", "Sports"], (await ListAsync(connection)).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Nameless_and_idless_categories_are_skipped()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var written = await ReplaceAsync(connection, 1, CategoryKind.Live,
            ("1", "Sports", 0),
            ("", "Orphan", 0),
            ("3", "   ", 0));

        await AddChannelAsync(connection, 1, "Sky Sports", "1");

        Assert.Equal(1, written);
        Assert.Equal("Sports", Assert.Single(await ListAsync(connection)).Name);
    }

    [Fact]
    public async Task Filtering_channels_by_category_returns_only_that_category()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0), ("2", "News", 0));
        await AddChannelAsync(connection, 1, "Sky Sports", "1");
        await AddChannelAsync(connection, 1, "BBC News", "2");

        var sports = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery { Category = "Sports" }, Now, CancellationToken.None);

        Assert.Equal("Sky Sports", Assert.Single(sports).DisplayName);
    }

    [Fact]
    public async Task Filtering_by_category_spans_providers_that_use_different_ids()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("7", "Sports", 0));
        await ReplaceAsync(connection, 2, CategoryKind.Live, ("42", "Sports", 0));
        await AddChannelAsync(connection, 1, "Sky Sports", "7");
        await AddChannelAsync(connection, 2, "BT Sport", "42");

        // Matching on the id the list happened to show would silently drop the second
        // provider's channels from a category the user believes covers everything.
        var sports = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery { Category = "Sports" }, Now, CancellationToken.None);

        Assert.Equal(2, sports.Count);
    }

    [Fact]
    public async Task No_category_shows_everything()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0));
        await AddChannelAsync(connection, 1, "Sky Sports", "1");
        await AddChannelAsync(connection, 1, "Uncategorised", null);

        var all = await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None);

        // Including the channel with no category at all. A provider leaves category_id
        // empty often enough that hiding those would lose real channels.
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Search_and_category_apply_together()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Live, ("1", "Sports", 0), ("2", "News", 0));
        await AddChannelAsync(connection, 1, "Sky Sports Main", "1");
        await AddChannelAsync(connection, 1, "Sky Sports News", "1");
        await AddChannelAsync(connection, 1, "BBC News", "2");

        var found = await ChannelRepository.GetChannelsAsync(
            connection,
            new ChannelQuery { Category = "Sports", Search = "News" },
            Now,
            CancellationToken.None);

        Assert.Equal("Sky Sports News", Assert.Single(found).DisplayName);
    }

    [Fact]
    public async Task Films_filter_by_their_own_categories()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await ReplaceAsync(connection, 1, CategoryKind.Vod, ("1", "Action", 0), ("2", "Comedy", 0));
        await AddChannelAsync(connection, 1, "Die Hard", "1", kind: "vod");
        await AddChannelAsync(connection, 1, "Airplane", "2", kind: "vod");

        var action = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery { Category = "Action" }, CancellationToken.None);

        Assert.Equal("Die Hard", Assert.Single(action).Title);

        Assert.Equal(1, await LibraryRepository.CountFilmsAsync(
            connection, new CatalogueQuery { Category = "Action" }, CancellationToken.None));
    }
}
