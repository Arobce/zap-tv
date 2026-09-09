using Iptv.Core.Data;
using Iptv.Core.Sources;

namespace Iptv.Presentation;

/// <summary>Which list the sidebar is showing.</summary>
/// <remarks>
/// Distinct from <see cref="LibraryKind"/>, which says what a row is. The two looked
/// interchangeable while there were three of each; favourites and continue-watching are
/// views over kinds that already exist, not new kinds of thing.
/// </remarks>
public enum LibraryView
{
    Live,
    Films,
    Series,

    /// <summary>Live channels the user marked.</summary>
    Favourites,

    /// <summary>Films and episodes that were started and not finished.</summary>
    Continue,

    /// <summary>
    /// Search across every catalogue at once.
    /// </summary>
    /// <remarks>
    /// Its own view rather than a mode the other tabs drop into. Someone on the channel
    /// list looking for a channel does not want nine hundred films, so each tab searches
    /// what it lists and this is where searching everything lives. It has nothing to show
    /// until something is typed, which is the one view that is true of.
    /// </remarks>
    All,
}

/// <summary>How deep into a series the list currently is.</summary>
public enum BrowseLevel
{
    /// <summary>A catalogue: live, films, series, favourites or continue-watching.</summary>
    Catalogue,

    /// <summary>The seasons of one series.</summary>
    Seasons,

    /// <summary>The episodes of one season, or of a series with only one.</summary>
    Episodes,
}

/// <summary>The result of a load, for the status line.</summary>
public sealed record BrowseResult
{
    public required IReadOnlyList<LibraryRow> Rows { get; init; }

    /// <summary>What to show under the list. Says how to fill an empty view.</summary>
    public required string Summary { get; init; }

    public required BrowseLevel Level { get; init; }

    /// <summary>
    /// Whether these rows should be drawn as a poster grid rather than a list.
    /// </summary>
    /// <remarks>
    /// Films and series only, and only when browsing. Everything else is either artwork
    /// the catalogue does not carry, or content a grid is the wrong shape for: a channel
    /// list is read by name and now/next, and a grid of 20,000 logos is unreadable.
    /// </remarks>
    public bool UsePosters { get; init; }
}

/// <summary>
/// The library list and where the user is in it.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the window so it can be tested. The navigation is a small state machine
/// — catalogue, seasons, episodes, with a back step from each — and it had accumulated
/// enough rules (one season skips its own menu, a search leaves a series, specials sort
/// last) that verifying it by clicking was no longer honest.
/// </para>
/// <para>
/// Opens a connection per operation rather than holding one. These are user-paced actions,
/// and a long-lived connection would have to be marshalled between the UI thread and the
/// episode fetch.
/// </para>
/// </remarks>
public sealed class LibraryBrowser
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Func<long, CancellationToken, Task<IReadOnlyList<EpisodeRecord>>> _fetchEpisodes;

    private IReadOnlyList<EpisodeRecord> _episodes = [];

    /// <param name="fetchEpisodes">
    /// Fetches a series' episodes from the provider.
    /// </param>
    /// <remarks>
    /// Injected rather than called directly: it is the one operation here that touches the
    /// network, and a test that needed a real provider would not be run.
    /// </remarks>
    public LibraryBrowser(
        SqliteConnectionFactory factory,
        Func<long, CancellationToken, Task<IReadOnlyList<EpisodeRecord>>> fetchEpisodes)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(fetchEpisodes);

        _factory = factory;
        _fetchEpisodes = fetchEpisodes;
    }

    /// <summary>How many rows a catalogue page holds.</summary>
    public const int PageSize = 500;

    /// <summary>How many of each kind a search returns.</summary>
    /// <remarks>
    /// Per kind rather than overall: a term matching ten thousand films must still leave
    /// room for the one channel that matches it.
    /// </remarks>
    public const int SearchPerKind = 25;

    public LibraryView View { get; private set; } = LibraryView.Live;

    /// <summary>Provider category name to restrict to, or null for all.</summary>
    public string? Category { get; private set; }

    public string? Search { get; private set; }

    public BrowseLevel Level { get; private set; } = BrowseLevel.Catalogue;

    /// <summary>The series being browsed, or null at catalogue level.</summary>
    public LibraryRow? OpenSeries { get; private set; }

    /// <summary>Whether the open series has a season level to step back to.</summary>
    public bool HasSeveralSeasons =>
        _episodes.Select(e => e.SeasonNumber).Distinct().Take(2).Count() > 1;

    /// <summary>Whether the category picker applies to the current view.</summary>
    /// <remarks>
    /// Series have no categories to filter by: the provider publishes them but the series
    /// table has no column to join them to. Favourites and continue-watching are already
    /// filtered lists, and a category on top would be a filter on a filter.
    /// </remarks>
    public bool SupportsCategories => View is LibraryView.Live or LibraryView.Films;

    /// <summary>The category kind for the current view, or null when it has none.</summary>
    public CategoryKind? CategoryKindForView => View switch
    {
        LibraryView.Live => CategoryKind.Live,
        LibraryView.Films => CategoryKind.Vod,
        _ => null,
    };

    /// <summary>Switches catalogue, clearing everything scoped to the old one.</summary>
    public async Task<BrowseResult> SwitchViewAsync(LibraryView view, CancellationToken cancellationToken)
    {
        View = view;

        // Both are scoped to the view being left. A category from the film catalogue means
        // nothing in the live one, and a search term that matched films usually matches no
        // channels - which reads as a broken tab rather than an empty search.
        Category = null;
        Search = null;

        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<BrowseResult> SetCategoryAsync(string? category, CancellationToken cancellationToken)
    {
        Category = category;
        return LoadAsync(cancellationToken);
    }

    public Task<BrowseResult> SetSearchAsync(string? search, CancellationToken cancellationToken)
    {
        Search = string.IsNullOrWhiteSpace(search) ? null : search;
        return LoadAsync(cancellationToken);
    }

    /// <summary>Loads the current catalogue, leaving any opened series.</summary>
    public async Task<BrowseResult> LoadAsync(CancellationToken cancellationToken)
    {
        OpenSeries = null;
        _episodes = [];
        Level = BrowseLevel.Catalogue;

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // A search stays inside the tab it was typed into, scoped to whatever that tab
        // lists. Searching everything is the All tab's job.
        if (Search is not null)
        {
            return await LoadSearchAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        if (View == LibraryView.All)
        {
            // Nothing to list: this view is a search and nothing else.
            return Catalogue([], _ => "Type to search channels, films, series and the guide");
        }

        return View switch
        {
            LibraryView.Films => await LoadFilmsAsync(connection, cancellationToken).ConfigureAwait(false),
            LibraryView.Series => await LoadSeriesAsync(connection, cancellationToken).ConfigureAwait(false),
            LibraryView.Favourites => await LoadFavouritesAsync(connection, cancellationToken).ConfigureAwait(false),
            LibraryView.Continue => await LoadContinueAsync(connection, cancellationToken).ConfigureAwait(false),
            _ => await LoadLiveAsync(connection, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Opens a series, fetching its episodes if they are not stored yet.</summary>
    public async Task<BrowseResult> OpenSeriesAsync(LibraryRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var stored = await EpisodeSync
            .GetEpisodesAsync(connection, row.SeriesRowId, cancellationToken)
            .ConfigureAwait(false);

        _episodes = stored.Count > 0
            ? stored
            : await _fetchEpisodes(row.SeriesRowId, cancellationToken).ConfigureAwait(false);

        OpenSeries = row;

        // One season is not a menu. Making the user click "Season 1" to reach the only
        // season there is adds a step and tells them nothing.
        return HasSeveralSeasons ? ShowSeasons() : ShowEpisodes(_episodes);
    }

    /// <summary>Shows one season's episodes.</summary>
    public BrowseResult OpenSeason(LibraryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return ShowEpisodes(_episodes.Where(e => e.SeasonNumber == row.SeasonNumber).ToList());
    }

    /// <summary>
    /// Steps back one level, or reports that there is nowhere left to go.
    /// </summary>
    /// <returns>Null when already at the catalogue, so the caller can do something else.</returns>
    public async Task<BrowseResult?> BackAsync(CancellationToken cancellationToken)
    {
        return Level switch
        {
            // A series with one season has no season level to return to, so episodes go
            // straight back to the catalogue.
            BrowseLevel.Episodes when HasSeveralSeasons => ShowSeasons(),
            BrowseLevel.Episodes or BrowseLevel.Seasons =>
                await LoadAsync(cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private BrowseResult ShowSeasons()
    {
        Level = BrowseLevel.Seasons;

        // Specials last: providers put unsorted episodes in season 0, and ordering
        // numerically would open every long-running show on its odds and ends.
        var rows = _episodes
            .GroupBy(e => e.SeasonNumber)
            .OrderBy(g => g.Key <= 0)
            .ThenBy(g => g.Key)
            .Select(g => LibraryRow.FromSeason(g.Key, g.Count()))
            .ToList();

        return new BrowseResult
        {
            Rows = rows,
            Level = Level,
            Summary = $"{rows.Count} seasons · {_episodes.Count:N0} episodes · Esc to go back",
        };
    }

    private BrowseResult ShowEpisodes(IReadOnlyList<EpisodeRecord> episodes)
    {
        Level = BrowseLevel.Episodes;

        var rows = episodes.Select(LibraryRow.FromEpisode).ToList();

        // Says where Escape goes, because with two levels "go back" is ambiguous.
        var back = HasSeveralSeasons ? "Esc for seasons" : "Esc for the series list";

        return new BrowseResult
        {
            Rows = rows,
            Level = Level,
            Summary = rows.Count == 0
                ? "no episodes · Esc to go back"
                : $"{rows.Count:N0} episodes · {back}",
        };
    }

    /// <summary>Says what was searched, not just that it failed.</summary>
    /// <remarks>
    /// "Nothing found" on the Live tab, when the thing is a film, reads as the library
    /// being wrong rather than as the search being scoped.
    /// </remarks>
    private string EmptySearchMessage() => View switch
    {
        LibraryView.Live => "no channels match · try the All tab",
        LibraryView.Films => "no films match · try the All tab",
        LibraryView.Series => "no series match · try the All tab",
        _ => "nothing found",
    };

    private string EmptyContinueMessage() => Search is null
        ? "nothing part-watched · films and episodes appear here"
        : "nothing part-watched matches";

    /// <summary>What a search covers, given which tab it was typed into.</summary>
    /// <remarks>
    /// Each tab searches what it lists. Favourites and continue-watching are filtered lists
    /// rather than catalogues, so they filter themselves rather than coming through here.
    /// </remarks>
    public SearchScope ScopeForView => View switch
    {
        LibraryView.Live => SearchScope.Channels,
        LibraryView.Films => SearchScope.Films,
        LibraryView.Series => SearchScope.Series,
        _ => SearchScope.All,
    };

    /// <summary>Searches, scoped to whichever tab the term was typed into.</summary>
    private async Task<BrowseResult> LoadSearchAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // These two are filtered lists, not catalogues. Their own queries already take a
        // term, and searching "everything" from inside a filtered list would leave it.
        if (View == LibraryView.Favourites)
        {
            return await LoadFavouritesAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        if (View == LibraryView.Continue)
        {
            return await LoadContinueAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        var hits = await SearchRepository.SearchAsync(
            connection, Search, SearchPerKind, DateTimeOffset.UtcNow, cancellationToken, ScopeForView)
            .ConfigureAwait(false);

        var rows = hits.Select(LibraryRow.FromSearchHit).ToList();

        // Counted by kind, because the value of a unified search is knowing there is one
        // channel among nine hundred films rather than being handed nine hundred and one
        // undifferentiated rows.
        var counts = hits
            .GroupBy(h => h.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Describe(g.Key, g.Count())}")
            .ToList();

        return Catalogue(
            rows,
            count => count > 0 ? string.Join(" · ", counts) : EmptySearchMessage());
    }

    private static string Describe(SearchHitKind kind, int count) => kind switch
    {
        SearchHitKind.Channel => count == 1 ? "channel" : "channels",
        SearchHitKind.Film => count == 1 ? "film" : "films",
        SearchHitKind.Series => "series",
        _ => count == 1 ? "programme" : "programmes",
    };

    private async Task<BrowseResult> LoadLiveAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var channels = await ChannelRepository.GetChannelsAsync(
            connection,
            new ChannelQuery { Search = Search, Category = Category, Limit = PageSize },
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        var withGuide = channels.Count(c => c.NowTitle is not null);

        return Catalogue(
            channels.Select(LibraryRow.FromChannel).ToList(),
            rows => $"{rows:N0} shown · {withGuide:N0} with guide");
    }

    private async Task<BrowseResult> LoadFilmsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var films = await LibraryRepository.GetFilmsAsync(
            connection,
            new CatalogueQuery { Search = Search, Category = Category, Limit = PageSize },
            cancellationToken).ConfigureAwait(false);

        var rows = films.Select(LibraryRow.FromFilm).ToList();

        // The total is counted only for an unfiltered view. It scans the whole VOD table
        // even indexed, and repeating it per keystroke would double the cost of every
        // search for a number nobody reads while typing.
        if (Search is not null)
        {
            return Catalogue(rows, count => $"{count:N0} films matching", posters: true);
        }

        var total = await LibraryRepository.CountFilmsAsync(
            connection, new CatalogueQuery { Category = Category }, cancellationToken).ConfigureAwait(false);

        return Catalogue(rows, count => $"{count:N0} of {total:N0} films", posters: true);
    }

    private async Task<BrowseResult> LoadSeriesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var series = await LibraryRepository.GetSeriesAsync(
            connection,
            new CatalogueQuery { Search = Search, Limit = PageSize },
            cancellationToken).ConfigureAwait(false);

        return Catalogue(
            series.Select(LibraryRow.FromSeries).ToList(),
            count => $"{count:N0} series · newest first",
            posters: true);
    }

    private async Task<BrowseResult> LoadFavouritesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var channels = await ChannelRepository.GetChannelsAsync(
            connection,
            new ChannelQuery { Search = Search, FavouritesOnly = true, Limit = PageSize },
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        return Catalogue(
            channels.Select(LibraryRow.FromChannel).ToList(),

            // Says how to add one. An empty list with no explanation reads as broken.
            count => count == 0
                ? "no favourites yet · press B while watching a channel"
                : $"{count:N0} favourites");
    }

    private async Task<BrowseResult> LoadContinueAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var unfinished = await PlaybackStateRepository.GetContinueWatchingItemsAsync(
            connection, 100, cancellationToken).ConfigureAwait(false);

        // Filtered in memory rather than by a query. This list is capped at 100 by
        // construction, and a term that matches nothing in it should leave it empty rather
        // than reaching out into the catalogue.
        if (Search is { } term)
        {
            unfinished = unfinished
                .Where(i => i.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Catalogue(
            unfinished.Select(LibraryRow.FromContinueWatching).ToList(),
            count => count > 0 ? $"{count:N0} to finish" : EmptyContinueMessage());
    }

    private BrowseResult Catalogue(
        IReadOnlyList<LibraryRow> rows,
        Func<int, string> summary,
        bool posters = false)
        => new()
        {
            Rows = rows,
            Level = BrowseLevel.Catalogue,
            Summary = summary(rows.Count),
            UsePosters = posters,
        };
}
