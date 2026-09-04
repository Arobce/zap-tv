namespace Iptv.Core.Epg;

/// <summary>Base for items streamed out of an XMLTV document.</summary>
public abstract record XmltvItem;

/// <summary>A <c>&lt;channel&gt;</c> declaration.</summary>
public sealed record EpgChannel : XmltvItem
{
    public required string Id { get; init; }

    /// <summary>Every display-name, in document order. Phase 4 matches on all of them.</summary>
    public required IReadOnlyList<string> DisplayNames { get; init; }

    public string? IconUrl { get; init; }
}

/// <summary>A <c>&lt;programme&gt;</c> entry, times already normalized to unix seconds.</summary>
public sealed record EpgProgramme : XmltvItem
{
    public required string ChannelId { get; init; }
    public required long StartUtc { get; init; }
    public required long StopUtc { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? EpisodeNum { get; init; }
}
