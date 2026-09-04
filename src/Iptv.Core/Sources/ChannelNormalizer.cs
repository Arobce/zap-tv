using System.Buffers;
using System.Globalization;
using System.Text;

namespace Iptv.Core.Sources;

/// <summary>Picture quality parsed out of a stream title.</summary>
public enum Quality
{
    Sd = 0,
    Hd = 1,
    Fhd = 2,
    Uhd = 3,
}

/// <summary>
/// Reduces provider-supplied titles to a canonical form for cross-provider dedup and
/// EPG matching.
/// </summary>
/// <remarks>
/// <para>
/// No regular expressions: this runs once per stream across playlists of 50k entries or
/// more, which is exactly the per-record hot path the conventions forbid regex in.
/// Everything here is span-based with a single pooled buffer.
/// </para>
/// <para>
/// <b>Changing any rule here is a schema migration.</b> <c>channel_key</c> is derived
/// from <see cref="Normalize"/>, and favourites, hidden state, sort order, EPG mappings
/// and resume positions are all keyed on it. Bump <see cref="Version"/> and ship a
/// migration that rewrites the keys; do not quietly adjust a rule.
/// </para>
/// </remarks>
public static class ChannelNormalizer
{
    /// <summary>
    /// Algorithm version, mirrored into <c>meta.normalization_version</c>.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Tokens dropped wholesale. Matched on token boundaries, never as substrings, so
    /// "HDNet" and "SHD Sport" keep their names.
    /// </summary>
    /// <remarks>
    /// A switch over an already-lowercased span rather than a <c>SearchValues&lt;string&gt;</c>
    /// lookup, whose <c>Contains</c> takes a <c>string</c> and would force an allocation
    /// per token on a path that runs millions of times per playlist import.
    /// </remarks>
    private static bool IsQualityToken(ReadOnlySpan<char> token) => token switch
    {
        "hd" or "fhd" or "uhd" or "4k" or "sd" => true,
        "h265" or "h264" or "hevc" or "raw" => true,
        "1080p" or "720p" => true,
        _ => false,
    };

    /// <summary>Normalizes a title to lowercase alphanumeric tokens separated by single spaces.</summary>
    /// <remarks>
    /// Token boundaries are preserved rather than removed entirely, because Phase 4's
    /// fuzzy EPG matching needs to compare token sets. <see cref="ChannelIdentity"/>
    /// strips the spaces when deriving a key, so "ESPN 2" and "ESPN2" still dedup.
    /// </remarks>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var stripped = StripCountryPrefix(title.AsSpan());

        var buffer = ArrayPool<char>.Shared.Rent(stripped.Length);
        try
        {
            var length = FoldToTokens(stripped, buffer);
            return JoinRemainingTokens(buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Returns the country prefix a title carries, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Kept rather than discarded: <see cref="Normalize"/> deliberately collapses
    /// "US: ESPN" and "UK| ESPN" onto the same key, and failover needs the country to
    /// avoid silently substituting a genuinely different channel.
    /// </remarks>
    public static string? ExtractCountry(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var span = title.AsSpan().TrimStart();
        var separator = IndexOfPrefixSeparator(span);
        if (separator < 0)
        {
            return null;
        }

        var candidate = span[..separator].Trim();
        return IsCountryCode(candidate) ? candidate.ToString().ToUpperInvariant() : null;
    }

    /// <summary>Returns the quality marker a title carries, or <see langword="null"/>.</summary>
    public static Quality? ExtractQuality(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Highest wins: "BBC One HD UHD" is a UHD stream mislabelled, not an HD one.
        Quality? best = null;
        foreach (var range in new TokenEnumerator(title.AsSpan()))
        {
            var token = title.AsSpan()[range];
            var quality = QualityOf(token);
            if (quality is not null && (best is null || quality > best))
            {
                best = quality;
            }
        }

        return best;
    }

    private static Quality? QualityOf(ReadOnlySpan<char> token) => token switch
    {
        _ when token.Equals("uhd", StringComparison.OrdinalIgnoreCase) => Quality.Uhd,
        _ when token.Equals("4k", StringComparison.OrdinalIgnoreCase) => Quality.Uhd,
        _ when token.Equals("fhd", StringComparison.OrdinalIgnoreCase) => Quality.Fhd,
        _ when token.Equals("1080p", StringComparison.OrdinalIgnoreCase) => Quality.Fhd,
        _ when token.Equals("hd", StringComparison.OrdinalIgnoreCase) => Quality.Hd,
        _ when token.Equals("720p", StringComparison.OrdinalIgnoreCase) => Quality.Hd,
        _ when token.Equals("sd", StringComparison.OrdinalIgnoreCase) => Quality.Sd,
        _ => null,
    };

    /// <summary>
    /// Removes a leading country prefix such as "US:", "UK|" or "CA -".
    /// </summary>
    private static ReadOnlySpan<char> StripCountryPrefix(ReadOnlySpan<char> title)
    {
        var span = title.TrimStart();
        var separator = IndexOfPrefixSeparator(span);
        if (separator < 0)
        {
            return span;
        }

        return IsCountryCode(span[..separator].Trim()) ? span[(separator + 1)..] : span;
    }

    private static int IndexOfPrefixSeparator(ReadOnlySpan<char> span)
    {
        // Only look at the very start of the title; a colon later in the name is part of
        // the name ("Storage Wars: Texas").
        var limit = Math.Min(span.Length, 6);
        for (var i = 0; i < limit; i++)
        {
            if (span[i] is ':' or '|' or '-')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A country code here is two or three ASCII letters. Anything else before a
    /// separator is part of the channel name.
    /// </summary>
    private static bool IsCountryCode(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length is < 2 or > 3)
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lowercases, removes diacritics and bracketed segments, and reduces every run of
    /// non-alphanumeric characters to a single space.
    /// </summary>
    /// <returns>Number of characters written to <paramref name="destination"/>.</returns>
    private static int FoldToTokens(ReadOnlySpan<char> source, Span<char> destination)
    {
        var written = 0;
        var depth = 0;
        var pendingSeparator = false;

        foreach (var raw in source)
        {
            // Bracketed segments carry provider bookkeeping ("[VIP]", "(1080p)"), never
            // the channel identity.
            if (raw is '[' or '(' or '{')
            {
                depth++;
                pendingSeparator = true;
                continue;
            }

            if (raw is ']' or ')' or '}')
            {
                if (depth > 0)
                {
                    depth--;
                }

                pendingSeparator = true;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            var folded = Fold(raw);
            if (folded == '\0')
            {
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && written > 0)
            {
                destination[written++] = ' ';
            }

            pendingSeparator = false;
            destination[written++] = folded;
        }

        return written;
    }

    /// <summary>
    /// Maps one character to its normalized form, or <c>'\0'</c> if it is a separator.
    /// </summary>
    private static char Fold(char c)
    {
        if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
        {
            return c;
        }

        if (char.IsAsciiLetterUpper(c))
        {
            return (char)(c + 32);
        }

        if (char.IsLetterOrDigit(c))
        {
            // Non-ASCII letter: decompose and keep the base character, dropping the
            // combining marks. "é" becomes "e", "š" becomes "s".
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            foreach (var part in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark &&
                    char.IsAsciiLetterOrDigit(part))
                {
                    return char.ToLowerInvariant(part);
                }
            }

            // A letter with no ASCII base (CJK, Cyrillic, Greek). Keep it lowercased
            // rather than dropping it, or entire non-Latin providers would normalize
            // to empty and collapse into one bogus channel.
            return char.ToLowerInvariant(c);
        }

        return '\0';
    }

    /// <summary>
    /// Drops quality tokens and joins what is left with single spaces.
    /// </summary>
    private static string JoinRemainingTokens(ReadOnlySpan<char> folded)
    {
        var builder = new StringBuilder(folded.Length);
        foreach (var range in new TokenEnumerator(folded))
        {
            var token = folded[range];
            if (IsQualityToken(token))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(token);
        }

        return builder.ToString();
    }

    /// <summary>Enumerates space-delimited token ranges without allocating.</summary>
    private ref struct TokenEnumerator
    {
        private readonly ReadOnlySpan<char> _span;
        private int _index;

        public TokenEnumerator(ReadOnlySpan<char> span)
        {
            _span = span;
            _index = 0;
            Current = default;
        }

        public Range Current { get; private set; }

        public readonly TokenEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_index < _span.Length && !IsTokenChar(_span[_index]))
            {
                _index++;
            }

            if (_index >= _span.Length)
            {
                return false;
            }

            var start = _index;
            while (_index < _span.Length && IsTokenChar(_span[_index]))
            {
                _index++;
            }

            Current = start.._index;
            return true;
        }

        private static bool IsTokenChar(char c) => c != ' ';
    }
}
