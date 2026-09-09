using System.Globalization;

namespace Iptv.Presentation;

/// <summary>What a track carries.</summary>
public enum TrackKind
{
    Audio,
    Subtitle,
}

/// <summary>
/// One audio or subtitle track, as the picker shows it.
/// </summary>
/// <remarks>
/// The identifier is mpv's own track id, not the index in any list. They differ as soon as
/// a file has both kinds — audio and subtitle ids are numbered separately — and selecting
/// by index would pick the wrong track on anything with more than one of each.
/// </remarks>
public sealed record MediaTrack
{
    public required int Id { get; init; }

    public required TrackKind Kind { get; init; }

    /// <summary>The track's own name, when the file carries one.</summary>
    public string? Title { get; init; }

    /// <summary>An ISO code, usually. Providers are not consistent about which one.</summary>
    public string? Language { get; init; }

    public bool Selected { get; init; }

    /// <summary>What to put in the menu.</summary>
    /// <remarks>
    /// Title first, because a track named "Commentary" is more use than one labelled "ENG"
    /// when both are English. The language is kept alongside it rather than dropped: two
    /// tracks sharing a title and differing only in language are common on films, and
    /// without it the menu offers the same entry twice.
    /// </remarks>
    public string Label
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim();
            var language = NormaliseLanguage(Language);

            return (title, language) switch
            {
                (not null, not null) => $"{title} ({language})",
                (not null, null) => title,
                (null, not null) => language,
                _ => $"Track {Id.ToString(CultureInfo.InvariantCulture)}",
            };
        }
    }

    /// <summary>
    /// Uppercases a short language code and leaves a written-out name alone.
    /// </summary>
    /// <remarks>
    /// "eng" reads as a code and belongs in capitals; "Brazilian Portuguese" is a name and
    /// shouting it would be wrong. Length is the only signal available, and three
    /// characters is where codes stop.
    /// </remarks>
    private static string? NormaliseLanguage(string? language)
    {
        var trimmed = language?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= 3
            ? trimmed.ToUpperInvariant()
            : trimmed;
    }
}

/// <summary>The tracks of one kind, and which is playing.</summary>
public sealed record TrackMenu
{
    /// <summary>The identifier that means "no subtitles".</summary>
    /// <remarks>
    /// mpv spells this "no" rather than a number, so it cannot be a track id. Negative
    /// one keeps the menu a list of ids and puts the translation in one place.
    /// </remarks>
    public const int NoTrack = -1;

    public required TrackKind Kind { get; init; }

    public required IReadOnlyList<MediaTrack> Tracks { get; init; }

    /// <summary>
    /// Whether the menu is worth showing.
    /// </summary>
    /// <remarks>
    /// One audio track is not a choice, so the button is hidden rather than opening a menu
    /// with a single item and no alternative. Subtitles are shown from one, because turning
    /// them off is itself the second option.
    /// </remarks>
    public bool IsUseful => Kind == TrackKind.Subtitle
        ? Tracks.Count > 0
        : Tracks.Count > 1;

    /// <summary>The selected track's id, or <see cref="NoTrack"/>.</summary>
    public int SelectedId
    {
        get
        {
            foreach (var track in Tracks)
            {
                if (track.Selected)
                {
                    return track.Id;
                }
            }

            return NoTrack;
        }
    }

    /// <summary>Builds the menu for one kind out of everything mpv reported.</summary>
    public static TrackMenu For(TrackKind kind, IReadOnlyList<MediaTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var matching = new List<MediaTrack>();

        foreach (var track in tracks)
        {
            if (track.Kind == kind)
            {
                matching.Add(track);
            }
        }

        return new TrackMenu { Kind = kind, Tracks = matching };
    }

    /// <summary>The mpv property value that selects <paramref name="id"/>.</summary>
    public static string PropertyValue(int id)
        => id == NoTrack ? "no" : id.ToString(CultureInfo.InvariantCulture);
}
