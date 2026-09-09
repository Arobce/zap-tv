using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Presentation;
using Microsoft.Data.Sqlite;

namespace Iptv.Presentation.Tests;

/// <summary>
/// The search box, which searches everything rather than the open tab.
/// </summary>
public sealed class LibraryBrowserSearchTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"zap-search-{Guid.NewGuid():N}");

    private readonly SqliteConnectionFactory _factory;

    public LibraryBrowserSearchTests()
        => _factory = new SqliteConnectionFactory(Path.Combine(_directory, "s.db"), pooled: false);

    private LibraryBrowser Browser() => new(_factory, (_, _) =>
        Task.FromResult<IReadOnlyList<EpisodeRecord>>([]));

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = await _factory.OpenAsync(CancellationToken.None);
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    private static async Task StreamAsync(
        SqliteConnection connection,
        string key,
        string title,
        string kind = "live")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO channels (channel_key, display_name) VALUES (@key, @title);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (1, @sid, @kind, @title, @title, 'http://h/' || @sid, @key, 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task IndexAsync(SqliteConnection connection)
        => SearchRepository.RebuildStreamIndexAsync(connection, CancellationToken.None);

    [Fact]
    public async Task A_tab_searches_only_what_it_lists()
    {
        await using var connection = await OpenAsync();

        await StreamAsync(connection, "tvg:a", "Sky Sports");
        await StreamAsync(connection, "name:f", "Sports Movie", kind: "vod");
        await IndexAsync(connection);

        var browser = Browser();
        await browser.SwitchViewAsync(LibraryView.Films, CancellationToken.None);

        // Someone on the film list looking for a film does not want channels. Each tab
        // searches what it lists.
        var films = await browser.SetSearchAsync("sports", CancellationToken.None);

        Assert.Equal("Sports Movie", Assert.Single(films.Rows).Title);

        await browser.SwitchViewAsync(LibraryView.Live, CancellationToken.None);
        var channels = await browser.SetSearchAsync("sports", CancellationToken.None);

        Assert.Equal("Sky Sports", Assert.Single(channels.Rows).Title);
    }

    [Fact]
    public async Task The_All_tab_searches_everything()
    {
        await using var connection = await OpenAsync();

        await StreamAsync(connection, "tvg:a", "Sky Sports");
        await StreamAsync(connection, "name:f", "Sports Movie", kind: "vod");
        await IndexAsync(connection);

        var browser = Browser();
        await browser.SwitchViewAsync(LibraryView.All, CancellationToken.None);

        var result = await browser.SetSearchAsync("sports", CancellationToken.None);

        Assert.Contains(result.Rows, r => r.Title == "Sky Sports");
        Assert.Contains(result.Rows, r => r.Title == "Sports Movie");
    }

    [Fact]
    public async Task The_All_tab_says_what_to_do_before_anything_is_typed()
    {
        await using var connection = await OpenAsync();

        // It is a search and nothing else, so it has nothing to list until a term arrives.
        var result = await Browser().SwitchViewAsync(LibraryView.All, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Contains("Type to search", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_scoped_search_points_at_the_All_tab()
    {
        await using var connection = await OpenAsync();

        await StreamAsync(connection, "name:f", "Sports Movie", kind: "vod");
        await IndexAsync(connection);

        var browser = Browser();
        await browser.SwitchViewAsync(LibraryView.Live, CancellationToken.None);

        // "Nothing found" on the Live tab, when the thing is a film, reads as the library
        // being wrong rather than as the search being scoped.
        var result = await browser.SetSearchAsync("sports", CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Contains("All tab", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_counts_by_kind()
    {
        await using var connection = await OpenAsync();

        await StreamAsync(connection, "tvg:a", "News One");
        await StreamAsync(connection, "name:f", "News Film", kind: "vod");
        await IndexAsync(connection);

        var browser = Browser();
        await browser.SwitchViewAsync(LibraryView.All, CancellationToken.None);
        var result = await browser.SetSearchAsync("news", CancellationToken.None);

        // Knowing there is one channel among nine hundred films is the value; an
        // undifferentiated count is not.
        Assert.Contains("1 channel", result.Summary, StringComparison.Ordinal);
        Assert.Contains("1 film", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_the_box_returns_to_the_tab()
    {
        await using var connection = await OpenAsync();

        await StreamAsync(connection, "tvg:a", "BBC One");
        await IndexAsync(connection);

        var browser = Browser();
        await browser.SetSearchAsync("bbc", CancellationToken.None);

        var back = await browser.SetSearchAsync(null, CancellationToken.None);

        Assert.Null(browser.Search);
        Assert.Equal(BrowseLevel.Catalogue, back.Level);
        Assert.Contains("with guide", back.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_found_says_how_to_get_back()
    {
        await using var connection = await OpenAsync();
        await IndexAsync(connection);

        var browser = Browser();
        await browser.SwitchViewAsync(LibraryView.All, CancellationToken.None);
        var result = await browser.SetSearchAsync("nothingmatchesthis", CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Contains("nothing found", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_series_result_is_not_playable()
    {
        await using var connection = await OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO series (provider_id, provider_series_id, title, normalized_title, series_key)
                VALUES (1, 's1', 'The Wire', 'the wire', 'name:thewire');
                INSERT INTO series_fts(series_fts) VALUES('rebuild');
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var browser = Browser();
        await browser.SwitchViewAsync(LibraryView.Series, CancellationToken.None);
        var result = await browser.SetSearchAsync("wire", CancellationToken.None);

        // A series is a container. Clicking it opens the episode list rather than playing.
        var row = Assert.Single(result.Rows);
        Assert.Equal(LibraryKind.Series, row.Kind);
        Assert.False(row.Playable);
        Assert.True(row.SeriesRowId > 0);
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
