using System.Globalization;
using Iptv.Core.Xtream;

namespace Iptv.Core.Sources;

/// <summary>A series as stored, without its episodes.</summary>
/// <remarks>
/// Episodes are deliberately absent. They require one <c>get_series_info</c> call per
/// series, and the reference provider lists 49,748 series against an account permitting a
/// single connection, so they are fetched when a series is opened rather than during sync.
/// <see cref="SeasonCount"/> comes inline with the listing, which is enough to render a
/// browse view before anything is fetched.
/// </remarks>
public sealed record SeriesRecord
{
    public required int ProviderId { get; init; }

    public required string ProviderSeriesId { get; init; }

    public required string Title { get; init; }

    public required string NormalizedTitle { get; init; }

    /// <summary>Cross-provider identity, derived exactly as <c>channel_key</c> is.</summary>
    public required string SeriesKey { get; init; }

    public string? Plot { get; init; }

    public string? CoverUrl { get; init; }

    public string? Genre { get; init; }

    /// <summary>Release year, or null when the provider's date cannot be read.</summary>
    public int? Year { get; init; }

    /// <summary>Rating out of ten, or null when unrated.</summary>
    public double? Rating { get; init; }

    /// <summary>Seasons the listing declares, before any episode fetch.</summary>
    public int SeasonCount { get; init; }

    /// <summary>The provider category this series is listed under, or null.</summary>
    /// <remarks>
    /// The provider's own id, not a name. Two providers reuse the same numeric ids for
    /// different things, so it only means anything alongside the provider it came from -
    /// which is why the categories table is keyed on both.
    /// </remarks>
    public string? CategoryId { get; init; }
}

/// <summary>Maps a provider's series listing to storage records.</summary>
public static class SeriesMapper
{
    public static SeriesRecord FromXtream(XtreamSeries series, int providerId)
    {
        ArgumentNullException.ThrowIfNull(series);

        var title = series.Name ?? string.Empty;
        var normalized = ChannelNormalizer.Normalize(title);
        var providerSeriesId = series.SeriesId.ToString(CultureInfo.InvariantCulture);

        return new SeriesRecord
        {
            ProviderId = providerId,
            ProviderSeriesId = providerSeriesId,
            Title = title,
            NormalizedTitle = normalized,

            // Same derivation as channel_key, so the same show from two providers merges
            // and a country prefix like "AR - " does not split it.
            SeriesKey = ChannelIdentity.DeriveChannelKey(tvgId: null, normalized)
                        ?? $"series:{providerId.ToString(CultureInfo.InvariantCulture)}:{providerSeriesId}",

            Plot = Clean(series.Plot),
            CoverUrl = Clean(series.Cover),
            Genre = Clean(series.Genre),
            Year = ParseYear(series.ReleaseDate),
            Rating = ParseRating(series.Rating),
            SeasonCount = series.Seasons.Count,
            CategoryId = Clean(series.CategoryId),
        };
    }

    /// <summary>
    /// Reads the year from a provider release date.
    /// </summary>
    /// <remarks>
    /// Null rather than a fallback when it cannot be read. Year 0001 or 1970 would sort to
    /// one end of the library and read as a data bug rather than as missing information.
    /// </remarks>
    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return null;
        }

        return int.TryParse(
            releaseDate.AsSpan(0, 4),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var year) && year is >= 1870 and <= 2200
            ? year
            : null;
    }

    /// <summary>
    /// Reads a rating that arrives as a string.
    /// </summary>
    /// <remarks>
    /// Empty means unrated, which is not the same as zero. Storing it as 0.0 would sort
    /// every unrated series below genuinely bad ones.
    /// </remarks>
    private static double? ParseRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        return double.TryParse(rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
               && value > 0
            ? value
            : null;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
