namespace Iptv.Core.Playlists;

/// <summary>One <c>#EXTINF</c> entry and the URL that follows it.</summary>
public sealed record M3uEntry
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public double Duration { get; init; }
    public string? TvgId { get; init; }
    public string? TvgName { get; init; }
    public string? TvgLogo { get; init; }
    public string? GroupTitle { get; init; }
    public string? CatchupKind { get; init; }
    public string? CatchupSource { get; init; }
    public int? CatchupDays { get; init; }
}
