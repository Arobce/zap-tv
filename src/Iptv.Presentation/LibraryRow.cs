using Iptv.Core.Sources;

namespace Iptv.Presentation;

/// <summary>Which catalogue a row came from.</summary>
public enum LibraryKind
{
    Live,
    Film,
    Series,

    /// <summary>One season inside an opened series.</summary>
    Season,

    /// <summary>One episode inside an opened season.</summary>
    Episode,
}

/// <summary>
/// One row of the library list, whatever catalogue it came from.
/// </summary>
/// <remarks>
/// <para>
/// One type for every list so the view needs a single template. The presentation decisions
/// live here rather than in XAML converters, because the awkward cases differ per kind and
/// are easier to see together: most live channels have no guide, a series cannot be played
/// at all, and a part-watched film needs its position in words.
/// </para>
/// <para>
/// No <c>Visibility</c>. That type is XAML's, and depending on it would drag this whole
/// layer back into a project that cannot be tested without the Windows workload. The view
/// converts <see cref="ShowProgress"/> at the binding.
/// </para>
/// </remarks>
public sealed class LibraryRow
{
    private LibraryRow(string key, string title, string subtitle, LibraryKind kind, bool playable)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Kind = kind;
        Playable = playable;
    }

    public string Key { get; }

    public string Title { get; }

    /// <summary>Now/next for a channel, a length for a film, a position for a resume.</summary>
    public string Subtitle { get; }

    public LibraryKind Kind { get; }

    /// <summary>
    /// Whether clicking this row can start playback.
    /// </summary>
    /// <remarks>
    /// False for a series and a season: both are containers. Opening one lists what is
    /// inside it rather than playing anything.
    /// </remarks>
    public bool Playable { get; }

    /// <summary>The <c>series.id</c> to fetch episodes for. Zero unless this is a series.</summary>
    public long SeriesRowId { get; private init; }

    /// <summary>Which season this row selects. Meaningful only for a season row.</summary>
    public int SeasonNumber { get; private init; }

    /// <summary>An episode opened from a season plays from its own URL.</summary>
    /// <remarks>
    /// Null for an episode reached from continue-watching, which was built from a stored
    /// position rather than from a fetch, and so goes through the <c>channel_key</c> lookup.
    /// </remarks>
    public string? EpisodeUrl { get; private init; }

    public double ProgressPercent { get; private init; }

    /// <summary>Whether there is a measured position worth drawing a bar for.</summary>
    public bool ShowProgress { get; private init; }

    /// <summary>Artwork, already checked. Null when the provider gave nothing usable.</summary>
    public string? ImageUrl { get; private init; }

    /// <summary>Whether there is artwork to draw instead of a lettered placeholder.</summary>
    public bool HasImage => ImageUrl is not null;

    /// <summary>The first letter of the title, for a poster with no artwork.</summary>
    /// <remarks>
    /// A letter rather than a generic film icon. In a grid where a third of the tiles have
    /// no cover, identical icons make the gaps look like one repeated item; initials keep
    /// each tile distinguishable at a glance.
    /// </remarks>
    public string Initial => Title.Length > 0
        ? Title[..1].ToUpperInvariant()
        : "?";

    /// <summary>
    /// Accepts an artwork URL, or rejects it.
    /// </summary>
    /// <remarks>
    /// Checked here rather than at the binding. Providers put all sorts in these fields -
    /// empty strings, bare filenames, and occasionally a <c>file://</c> path from whatever
    /// machine built their catalogue - and an Image handed one of those either throws or
    /// reaches for a local file. Only absolute http and https get through.
    /// </remarks>
    private static string? Artwork(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? trimmed : null;
    }

    public static LibraryRow FromChannel(ChannelListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Explicit rather than blank. "No guide data" is information; an empty line is just
        // a hole, and it is the majority case on this library.
        var subtitle = Describe(item);

        return new LibraryRow(item.ChannelKey, item.DisplayName, subtitle, LibraryKind.Live, playable: true)
        {
            ProgressPercent = (item.NowProgress ?? 0) * 100,
            ShowProgress = item.NowProgress is not null,
            ImageUrl = Artwork(item.LogoUrl),
        };
    }

    private static string Describe(ChannelListItem item)
    {
        if (item.NowTitle is null)
        {
            return "no guide data";
        }

        return item.NextTitle is null ? item.NowTitle : $"{item.NowTitle}  →  {item.NextTitle}";
    }

    public static LibraryRow FromFilm(CatalogueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var subtitle = item.Subtitle is null ? "film" : $"film · {item.Subtitle}";
        return new LibraryRow(item.Key, item.Title, subtitle, LibraryKind.Film, playable: true)
        {
            ImageUrl = Artwork(item.ImageUrl),
        };
    }

    public static LibraryRow FromSeries(CatalogueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var year = item.Year is { } y ? $"{y} · " : string.Empty;
        return new LibraryRow(
            item.Key,
            item.Title,
            $"{year}series · open for episodes",
            LibraryKind.Series,
            playable: false)
        {
            SeriesRowId = item.SeriesRowId,
            ImageUrl = Artwork(item.ImageUrl),
        };
    }

    /// <summary>Something started and not finished.</summary>
    /// <remarks>
    /// The resume position is not carried here. It is read at play time, so a row built
    /// minutes ago cannot resume to a point that has since moved.
    /// </remarks>
    public static LibraryRow FromContinueWatching(ContinueWatchingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var position = Clock(item.PositionSeconds);

        var subtitle = item.DurationSeconds is > 0
            ? $"{position} of {Clock(item.DurationSeconds.Value)}"
            : $"{position} in";

        return new LibraryRow(
            item.ContentKey,
            item.Title,
            subtitle,
            item.IsEpisode ? LibraryKind.Episode : LibraryKind.Film,
            playable: true)
        {
            ProgressPercent = (item.Progress ?? 0) * 100,
            ShowProgress = item.Progress is not null,
        };
    }

    /// <summary>One result from a search across everything.</summary>
    /// <remarks>
    /// A programme becomes a live row: what a viewer wants from finding a programme is to
    /// watch the channel showing it, and the key already is that channel.
    /// </remarks>
    public static LibraryRow FromSearchHit(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        var kind = hit.Kind switch
        {
            SearchHitKind.Film => LibraryKind.Film,
            SearchHitKind.Series => LibraryKind.Series,
            _ => LibraryKind.Live,
        };

        return new LibraryRow(
            hit.Key,
            hit.Title,
            hit.Subtitle,
            kind,

            // A series is a container: opening it lists episodes rather than playing.
            playable: kind != LibraryKind.Series)
        {
            SeriesRowId = hit.SeriesRowId,
        };
    }

    /// <summary>One season, inside an opened series.</summary>
    /// <remarks>
    /// Season 0 is what providers use for specials and for episodes whose season they did
    /// not record. Labelling it "Season 0" would be technically right and useless.
    /// </remarks>
    public static LibraryRow FromSeason(int seasonNumber, int episodeCount)
    {
        var title = seasonNumber <= 0 ? "Specials & unsorted" : $"Season {seasonNumber}";

        return new LibraryRow(
            $"season:{seasonNumber}",
            title,
            episodeCount == 1 ? "1 episode" : $"{episodeCount:N0} episodes",
            LibraryKind.Season,
            playable: false)
        {
            SeasonNumber = seasonNumber,
        };
    }

    /// <summary>One episode, inside an opened season.</summary>
    public static LibraryRow FromEpisode(EpisodeRecord episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        // The number is the subtitle, not a prefix on the title. Prefixing makes every row
        // start with the same shape and pushes the actual name out of a narrow list.
        return new LibraryRow(
            $"ep:{episode.ProviderEpisodeId}",
            episode.Title,
            $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
            LibraryKind.Episode,
            playable: true)
        {
            EpisodeUrl = episode.Url,
        };
    }

    /// <summary>Whole seconds as h:mm:ss, dropping the hour when there is none.</summary>
    private static string Clock(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{span:h\\:mm\\:ss}" : $"{span:mm\\:ss}";
    }
}
