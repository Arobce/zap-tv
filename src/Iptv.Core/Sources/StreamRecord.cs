namespace Iptv.Core.Sources;

public enum StreamKind { Live, Vod, SeriesEpisode }

public sealed record StreamRecord
{
    public required int ProviderId { get; init; }
    public required string ProviderStreamId { get; init; }
    public required StreamKind Kind { get; init; }
    public required string Title { get; init; }
    public required string NormalizedTitle { get; init; }
    public required string ChannelKey { get; init; }
    public required string Url { get; init; }
    public string? TvgId { get; init; }
    public string? LogoUrl { get; init; }
    public string? CategoryId { get; init; }
    public string? Container { get; init; }
    public Quality? Quality { get; init; }
    public string? Country { get; init; }
    public string? CatchupKind { get; init; }
    public string? CatchupSource { get; init; }
    public int? CatchupDays { get; init; }
    public bool IsSeparator { get; init; }
    public long? SeriesId { get; init; }
    public int? SeasonNum { get; init; }
    public int? EpisodeNum { get; init; }
}
