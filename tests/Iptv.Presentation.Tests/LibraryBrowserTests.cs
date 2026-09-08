using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Presentation;
using Microsoft.Data.Sqlite;

namespace Iptv.Presentation.Tests;

/// <summary>
/// The library list and where the user is in it.
/// </summary>
/// <remarks>
/// This was 1,631 lines of window code-behind with no tests. The navigation had accumulated
/// enough rules — one season skips its own menu, a search leaves an opened series, specials
/// sort last, escape unwinds one level — that checking it by clicking was no longer honest.
/// </remarks>
public sealed class LibraryBrowserTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"zap-browser-{Guid.NewGuid():N}");

    private readonly SqliteConnectionFactory _factory;

    private int _fetchCalls;
    private IReadOnlyList<EpisodeRecord> _fetchResult = [];

    public LibraryBrowserTests()
    {
        // Unpooled, so the file handle is released and this directory can be deleted
        // without the global ClearAllPools that made unrelated tests fail.
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "library.db"), pooled: false);
    }

    private LibraryBrowser Browser() => new(_factory, (_, _) =>
    {
        _fetchCalls++;
        return Task.FromResult(_fetchResult);
    });

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = await _factory.OpenAsync(CancellationToken.None);
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private static async Task SeedProviderAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task AddChannelAsync(SqliteConnection connection, string key, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO channels (channel_key, display_name) VALUES (@key, @name);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (1, @sid, 'live', @name, @name, 'http://host.invalid/' || @sid, @key, 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> AddSeriesAsync(SqliteConnection connection, string title)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series (provider_id, provider_series_id, title, normalized_title, series_key)
            VALUES (1, @sid, @title, @title, 'name:' || @sid)
            RETURNING id;
            """;

        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@title", title);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static EpisodeRecord Episode(int season, int number) => new()
    {
        ProviderEpisodeId = (season * 100) + number,
        Title = $"S{season}E{number}",
        SeasonNumber = season,
        EpisodeNumber = number,
        Url = $"http://host.invalid/series/{season}/{number}.mkv",
    };

    [Fact]
    public async Task The_live_catalogue_loads_by_default()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        await AddChannelAsync(connection, "tvg:a", "BBC One");

        var browser = Browser();
        var result = await browser.LoadAsync(CancellationToken.None);

        Assert.Equal(LibraryView.Live, browser.View);
        Assert.Equal(BrowseLevel.Catalogue, result.Level);
        Assert.Equal("BBC One", Assert.Single(result.Rows).Title);
    }

    [Fact]
    public async Task Switching_view_clears_the_category_and_the_search()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);

        var browser = Browser();
        await browser.SetSearchAsync("sport", CancellationToken.None);
        await browser.SetCategoryAsync("EU | UK", CancellationToken.None);

        await browser.SwitchViewAsync(LibraryView.Films, CancellationToken.None);

        // Both are scoped to the view being left. A term that matched channels usually
        // matches no films, and an empty list reads as a broken tab.
        Assert.Null(browser.Search);
        Assert.Null(browser.Category);
    }

    [Fact]
    public async Task Favourites_says_how_to_add_one_when_empty()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);

        var browser = Browser();
        var result = await browser.SwitchViewAsync(LibraryView.Favourites, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Contains("press B", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Favourites_shows_only_marked_channels()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        await AddChannelAsync(connection, "tvg:a", "Kept");
        await AddChannelAsync(connection, "tvg:b", "Ignored");

        await ChannelRepository.SetFavouriteAsync(connection, "tvg:a", true, CancellationToken.None);

        var result = await Browser().SwitchViewAsync(LibraryView.Favourites, CancellationToken.None);

        Assert.Equal("Kept", Assert.Single(result.Rows).Title);
    }

    [Fact]
    public async Task Only_live_and_films_have_categories()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);

        var browser = Browser();

        Assert.True(browser.SupportsCategories);
        Assert.Equal(CategoryKind.Live, browser.CategoryKindForView);

        await browser.SwitchViewAsync(LibraryView.Films, CancellationToken.None);
        Assert.Equal(CategoryKind.Vod, browser.CategoryKindForView);

        // Series have no column to join categories to; the other two are already filtered
        // lists, and a category on top would be a filter on a filter.
        foreach (var view in new[] { LibraryView.Series, LibraryView.Favourites, LibraryView.Continue })
        {
            await browser.SwitchViewAsync(view, CancellationToken.None);
            Assert.False(browser.SupportsCategories);
            Assert.Null(browser.CategoryKindForView);
        }
    }

    // --- series navigation ---

    private async Task<LibraryRow> OpenableSeriesAsync(SqliteConnection connection, string title = "The Show")
    {
        var id = await AddSeriesAsync(connection, title);
        var series = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery { Limit = 10 }, CancellationToken.None);

        return LibraryRow.FromSeries(series.Single(s => s.SeriesRowId == id));
    }

    [Fact]
    public async Task A_multi_season_series_opens_to_its_seasons()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1), Episode(1, 2), Episode(2, 1)];

        var browser = Browser();
        var result = await browser.OpenSeriesAsync(row, CancellationToken.None);

        Assert.Equal(BrowseLevel.Seasons, result.Level);
        Assert.Equal(["Season 1", "Season 2"], result.Rows.Select(r => r.Title));
        Assert.Equal(["2 episodes", "1 episode"], result.Rows.Select(r => r.Subtitle));
    }

    [Fact]
    public async Task A_single_season_series_opens_straight_to_its_episodes()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1), Episode(1, 2)];

        // One season is not a menu. Clicking "Season 1" to reach the only season there is
        // adds a step and tells the user nothing.
        var result = await Browser().OpenSeriesAsync(row, CancellationToken.None);

        Assert.Equal(BrowseLevel.Episodes, result.Level);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public async Task Specials_sort_after_the_numbered_seasons()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(0, 1), Episode(1, 1), Episode(2, 1)];

        // Providers put unsorted episodes in season 0. Ordering numerically would open
        // every long-running show on its odds and ends.
        var result = await Browser().OpenSeriesAsync(row, CancellationToken.None);

        Assert.Equal(
            ["Season 1", "Season 2", "Specials & unsorted"],
            result.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Opening_a_season_shows_only_its_episodes()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1), Episode(2, 1), Episode(2, 2)];

        var browser = Browser();
        var seasons = await browser.OpenSeriesAsync(row, CancellationToken.None);
        var episodes = browser.OpenSeason(seasons.Rows[1]);

        Assert.Equal(BrowseLevel.Episodes, episodes.Level);
        Assert.Equal(["S2E1", "S2E2"], episodes.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Back_unwinds_episodes_to_seasons_then_to_the_catalogue()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1), Episode(2, 1)];

        var browser = Browser();
        var seasons = await browser.OpenSeriesAsync(row, CancellationToken.None);
        browser.OpenSeason(seasons.Rows[0]);

        var back = await browser.BackAsync(CancellationToken.None);
        Assert.Equal(BrowseLevel.Seasons, back!.Level);

        var out_ = await browser.BackAsync(CancellationToken.None);
        Assert.Equal(BrowseLevel.Catalogue, out_!.Level);

        // Nothing left to unwind. Null so the caller can do something else with Escape,
        // such as leaving fullscreen.
        Assert.Null(await browser.BackAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Back_from_a_single_season_series_goes_straight_to_the_catalogue()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1)];

        var browser = Browser();
        await browser.OpenSeriesAsync(row, CancellationToken.None);

        // There is no season level to return to, so stopping at one would strand the user
        // on a list they cannot leave with the same key that got them there.
        var back = await browser.BackAsync(CancellationToken.None);
        Assert.Equal(BrowseLevel.Catalogue, back!.Level);
    }

    [Fact]
    public async Task Episodes_are_fetched_once_and_then_read_from_storage()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1)];

        var browser = Browser();
        await browser.OpenSeriesAsync(row, CancellationToken.None);
        Assert.Equal(1, _fetchCalls);

        // Stored by the fetch, so reopening must not spend another provider request. The
        // 464-episode case took 9.3 seconds.
        await EpisodeSync.ReplaceAsync(
            connection, 1, row.SeriesRowId, _fetchResult, DateTimeOffset.UtcNow, CancellationToken.None);

        await browser.OpenSeriesAsync(row, CancellationToken.None);
        Assert.Equal(1, _fetchCalls);
    }

    [Fact]
    public async Task A_search_leaves_an_opened_series()
    {
        await using var connection = await OpenAsync();
        await SeedProviderAsync(connection);
        await AddChannelAsync(connection, "tvg:a", "BBC One");
        var row = await OpenableSeriesAsync(connection);

        _fetchResult = [Episode(1, 1)];

        var browser = Browser();
        await browser.OpenSeriesAsync(row, CancellationToken.None);

        // Searching is a request for the catalogue, not for the episode list that happens
        // to be showing.
        var result = await browser.SetSearchAsync("BBC", CancellationToken.None);

        Assert.Equal(BrowseLevel.Catalogue, result.Level);
        Assert.Null(browser.OpenSeries);
    }

    public async ValueTask DisposeAsync()
    {
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
            // A temp directory is disposable either way.
        }
    }
}
