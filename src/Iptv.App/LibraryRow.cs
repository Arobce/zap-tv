using System;
using Iptv.Core.Sources;
using Microsoft.UI.Xaml;

namespace Iptv.App;

/// <summary>Which catalogue a row came from.</summary>
public enum LibraryKind
{
    Live,
    Film,
    Series,
}

/// <summary>
/// One row of the library list, whatever catalogue it came from.
/// </summary>
/// <remarks>
/// One type for all three so the list has a single template. The presentation decisions
/// live here rather than in XAML converters, because the awkward cases differ per kind and
/// are easier to see together: most live channels have no guide, and no series can be
/// played until its episodes are fetched.
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

    /// <summary>Now/next for a channel, container or year for a film, status for a series.</summary>
    public string Subtitle { get; }

    public LibraryKind Kind { get; }

    /// <summary>
    /// Whether clicking this row can start playback.
    /// </summary>
    /// <remarks>
    /// False for series: sync stores the listing but not the episodes, because fetching
    /// them means one request per series and the reference provider lists 49,783.
    /// </remarks>
    public bool Playable { get; }

    public double ProgressPercent { get; private init; }

    /// <summary>Hidden rather than zero-width when there is no programme to measure.</summary>
    public Visibility ProgressVisibility { get; private init; } = Visibility.Collapsed;

    public static LibraryRow FromChannel(ChannelListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Explicit rather than blank. "No guide data" is information; an empty line is
        // just a hole, and it is the majority case on this library.
        var subtitle = item.NowTitle is null
            ? "no guide data"
            : item.NextTitle is null
                ? item.NowTitle
                : $"{item.NowTitle}  →  {item.NextTitle}";

        return new LibraryRow(item.ChannelKey, item.DisplayName, subtitle, LibraryKind.Live, playable: true)
        {
            ProgressPercent = (item.NowProgress ?? 0) * 100,
            ProgressVisibility = item.NowProgress is null ? Visibility.Collapsed : Visibility.Visible,
        };
    }

    public static LibraryRow FromFilm(CatalogueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var subtitle = item.Subtitle is null ? "film" : $"film · {item.Subtitle}";
        return new LibraryRow(item.Key, item.Title, subtitle, LibraryKind.Film, playable: true);
    }

    public static LibraryRow FromSeries(CatalogueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Says why it cannot be played rather than failing silently on click. The reason is
        // a real design constraint, not a missing feature to be embarrassed about.
        var year = item.Year is { } y ? $"{y} · " : string.Empty;
        return new LibraryRow(
            item.Key,
            item.Title,
            $"{year}series · episodes load on open",
            LibraryKind.Series,
            playable: false);
    }
}
