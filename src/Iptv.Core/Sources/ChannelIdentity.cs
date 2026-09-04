namespace Iptv.Core.Sources;

/// <summary>
/// Derives the cross-provider identity that channels are merged and deduplicated on.
/// </summary>
/// <remarks>
/// Every piece of user state - favourites, hidden channels, sort order, EPG mappings,
/// resume positions - is keyed on the value this produces. See the versioning note on
/// <see cref="ChannelNormalizer"/> before changing anything here.
/// </remarks>
public static class ChannelIdentity
{
    private const string TvgPrefix = "tvg:";
    private const string NamePrefix = "name:";

    /// <summary>
    /// Derives a <c>channel_key</c>, or <see langword="null"/> when the stream cannot be
    /// identified at all.
    /// </summary>
    /// <param name="tvgId">The provider's <c>tvg-id</c>, if it supplied one.</param>
    /// <param name="normalizedTitle">Output of <see cref="ChannelNormalizer.Normalize"/>.</param>
    /// <remarks>
    /// The two forms carry distinct prefixes so a <c>tvg-id</c> that happens to look like
    /// a normalized name cannot collide with one.
    /// <para>
    /// Spaces are stripped from the name form so that "ESPN 2" and "ESPN2" - the same
    /// channel spelled differently by two providers - produce one key. The normalized
    /// title keeps its token boundaries for Phase 4's fuzzy matching.
    /// </para>
    /// </remarks>
    public static string? DeriveChannelKey(string? tvgId, string? normalizedTitle)
    {
        if (!string.IsNullOrWhiteSpace(tvgId))
        {
            return string.Concat(TvgPrefix, tvgId.Trim().ToLowerInvariant());
        }

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            // Returning "name:" here would merge every unidentifiable stream across every
            // provider into a single bogus channel.
            return null;
        }

        return string.Concat(NamePrefix, RemoveSpaces(normalizedTitle));
    }

    private static string RemoveSpaces(string value)
    {
        if (!value.Contains(' ', StringComparison.Ordinal))
        {
            return value;
        }

        return string.Create(
            value.Length - value.Count(c => c == ' '),
            value,
            static (destination, source) =>
            {
                var written = 0;
                foreach (var c in source)
                {
                    if (c != ' ')
                    {
                        destination[written++] = c;
                    }
                }
            });
    }
}
