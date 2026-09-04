namespace Iptv.Core.Sources;

/// <summary>
/// Classifies catalogue entries that are not really channels.
/// </summary>
/// <remarks>
/// Providers pad their listings with decorative section headings - on the reference
/// provider, 1,094 of 28,285 live entries. These are kept and shown, because they are the
/// provider's own grouping and users navigate by them, but they are flagged so they stay
/// out of search results, cross-provider dedup, failover candidacy, and the EPG coverage
/// denominator. A divider offered as a failover target is a stream that can never play.
/// </remarks>
public static class StreamClassifier
{
    /// <summary>
    /// Minimum run length of one repeated symbol before a title reads as decorative.
    /// </summary>
    /// <remarks>
    /// Three, not two. "Channel -- Two" is plausibly a real name, and a false positive
    /// hides a channel the user paid for, which is far worse than leaving a divider
    /// unflagged. The failure is deliberately asymmetric.
    /// </remarks>
    private const int MinimumRun = 3;

    /// <summary>
    /// Returns whether a title is a decorative divider rather than a channel name.
    /// </summary>
    /// <remarks>
    /// Detection is structural - a run of repeated punctuation or symbols - rather than a
    /// keyword list. Keywords would need a per-provider vocabulary, would not survive a
    /// provider changing its formatting, and would misfire on real channels.
    /// </remarks>
    public static bool IsSeparator(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var span = title.AsSpan().Trim();

        var runCharacter = '\0';
        var runLength = 0;

        foreach (var c in span)
        {
            // Letters and digits break any run. "CA - TSN" and "UK| BBC One" - the most
            // common naming convention on these panels - must never match.
            if (char.IsLetterOrDigit(c))
            {
                runLength = 0;
                runCharacter = '\0';
                continue;
            }

            if (c == ' ')
            {
                continue;
            }

            if (c == runCharacter)
            {
                runLength++;
                if (runLength >= MinimumRun)
                {
                    return true;
                }

                continue;
            }

            runCharacter = c;
            runLength = 1;
        }

        return false;
    }
}
