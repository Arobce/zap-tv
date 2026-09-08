using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// The read model for the VOD and series catalogues.
/// </summary>
/// <remarks>
/// Separate from the channel list because the shapes differ: a film is one playable
/// stream, a series is a container with no stream until its episodes are fetched. On the
/// reference library this is 158,255 films and 49,783 series against 20,478 live channels,
/// so paging is not optional.
/// </remarks>
public sealed class LibraryRepositoryTests
{
    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        await ExecuteAsync(connection,
            "INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');");
        return connection;
    }

    private static async Task AddFilmAsync(
        SqliteConnection connection,
        string key,
        string title,
        bool separator = false,
        string container = "mkv")
    {
        await ExecuteAsync(connection,
            $"""
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, container, is_separator, is_active, last_seen_utc)
            VALUES (1, '{key}', 'vod', '{title}', '{title.ToLowerInvariant()}',
                    'http://host.invalid/movie/u/p/{key}.{container}', '{key}',
                    '{container}', {(separator ? 1 : 0)}, 1, 0);
            """);
    }

    private static async Task AddSeriesAsync(
        SqliteConnection connection,
        string providerSeriesId,
        string title,
        int? year = null,
        string? genre = null)
    {
        await ExecuteAsync(connection,
            $"""
            INSERT INTO series (provider_id, provider_series_id, title, normalized_title,
                                series_key, year, plot, cover_url)
            VALUES (1, '{providerSeriesId}', '{title}', '{title.ToLowerInvariant()}',
                    'name:{title.ToLowerInvariant().Replace(" ", string.Empty)}',
                    {(year is null ? "NULL" : year.ToString())},
                    {(genre is null ? "NULL" : $"'{genre}'")}, NULL);
            """);
    }

    // --- VOD ----------------------------------------------------------------------

    [Fact]
    public async Task Lists_films()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddFilmAsync(connection, "m1", "Alpha Film");
        await AddFilmAsync(connection, "m2", "Beta Film");

        var films = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery(), CancellationToken.None);

        Assert.Equal(2, films.Count);
    }

    [Fact]
    public async Task Excludes_separator_rows_from_films()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddFilmAsync(connection, "m1", "Alpha Film");
        await AddFilmAsync(connection, "sep", "##### NEW RELEASES #####", separator: true);

        var films = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery(), CancellationToken.None);

        // The VOD catalogue carries the same decorative rows as the live one.
        Assert.Equal("Alpha Film", Assert.Single(films).Title);
    }

    [Fact]
    public async Task Does_not_list_live_channels_as_films()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddFilmAsync(connection, "m1", "Alpha Film");
        await ExecuteAsync(connection,
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, last_seen_utc)
            VALUES (1, 'c1', 'live', 'BBC One', 'bbc one', 'http://x/1.ts', 'tvg:bbc1', 1, 0);
            """);

        Assert.Single(await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task Searches_films_by_title()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddFilmAsync(connection, "m1", "The Quiet Hour");
        await AddFilmAsync(connection, "m2", "Loud Movie");

        var films = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery { Search = "quiet" }, CancellationToken.None);

        Assert.Equal("The Quiet Hour", Assert.Single(films).Title);
    }

    [Fact]
    public async Task Films_are_pageable()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        for (var i = 0; i < 10; i++)
        {
            await AddFilmAsync(connection, $"m{i}", $"Film {i:D2}");
        }

        var first = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery { Limit = 4 }, CancellationToken.None);
        var second = await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery { Limit = 4, Offset = 4 }, CancellationToken.None);

        // 158,255 films on the reference library. Loading them all to show twenty is the
        // difference between a list that opens instantly and one that does not open.
        Assert.Equal(4, first.Count);
        Assert.Empty(first.Select(f => f.Key).Intersect(second.Select(f => f.Key)));
    }

    [Fact]
    public async Task A_film_carries_its_playback_key()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddFilmAsync(connection, "m1", "Alpha Film");

        var film = Assert.Single(await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery(), CancellationToken.None));

        // The same key the live path uses, so playback and resume go through one route -
        // but scoped to the kind, because a live channel of the same name shares that key.
        var url = await ChannelRepository.GetPlaybackUrlAsync(
            connection, film.Key, StreamKind.Vod, CancellationToken.None);

        Assert.NotNull(url);
        Assert.Contains("/movie/", url, StringComparison.Ordinal);
    }

    // --- Series -------------------------------------------------------------------

    [Fact]
    public async Task Lists_series()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddSeriesAsync(connection, "s1", "Alpha Show", year: 2024);
        await AddSeriesAsync(connection, "s2", "Beta Show", year: 2023);

        var series = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery(), CancellationToken.None);

        Assert.Equal(2, series.Count);
    }

    [Fact]
    public async Task Series_are_deduplicated_across_providers()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await ExecuteAsync(connection,
            "INSERT INTO providers (id, name, kind, base_url) VALUES (2, 'B', 'xtream', 'http://b.invalid');");

        await AddSeriesAsync(connection, "s1", "Same Show", year: 2024);
        await ExecuteAsync(connection,
            """
            INSERT INTO series (provider_id, provider_series_id, title, normalized_title,
                                series_key, year)
            VALUES (2, 's99', 'Same Show', 'same show', 'name:sameshow', 2024);
            """);

        var series = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery(), CancellationToken.None);

        // series_key exists for exactly this: the same show from two providers is one
        // entry in the browse view, not two.
        Assert.Single(series);
    }

    [Fact]
    public async Task Searches_series_by_title()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddSeriesAsync(connection, "s1", "The Long Wait");
        await AddSeriesAsync(connection, "s2", "Something Else");

        var series = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery { Search = "long" }, CancellationToken.None);

        Assert.Equal("The Long Wait", Assert.Single(series).Title);
    }

    [Fact]
    public async Task Series_with_a_year_sort_before_those_without()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddSeriesAsync(connection, "s1", "Undated Show");
        await AddSeriesAsync(connection, "s2", "Recent Show", year: 2026);

        var series = await LibraryRepository.GetSeriesAsync(
            connection, new CatalogueQuery(), CancellationToken.None);

        // Newest first is the useful default for a catalogue this size, and an absent year
        // must not sort as year zero and dominate the top of the list.
        Assert.Equal("Recent Show", series[0].Title);
    }

    [Fact]
    public async Task Counts_the_catalogue_without_loading_it()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        for (var i = 0; i < 25; i++)
        {
            await AddFilmAsync(connection, $"m{i}", $"Film {i:D2}");
        }

        // The UI shows "25 films" without materialising 158,255 rows to count them.
        Assert.Equal(25, await LibraryRepository.CountFilmsAsync(
            connection, new CatalogueQuery(), CancellationToken.None));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
