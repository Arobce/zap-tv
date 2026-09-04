using System.Text;

namespace Iptv.Core.Xtream;

/// <summary>
/// Removes account credentials from text before it reaches a log, a diagnostics export, or
/// an error message.
/// </summary>
/// <remarks>
/// <para>
/// Xtream carries credentials in three places: as path segments in stream URLs
/// (<c>/live/{user}/{pass}/{id}.ts</c>), as query parameters on the API
/// (<c>?username=...&amp;password=...</c>), and occasionally as URL userinfo
/// (<c>http://user:pass@host</c>). All three are handled here.
/// </para>
/// <para>
/// Span-based rather than regex, consistent with the conventions: this runs on every log
/// line, including the per-attempt health records written during failover.
/// </para>
/// <para>
/// The failure modes are asymmetric - a redacted log line is a mild inconvenience, a
/// leaked credential hands over a working subscription - so this errs towards
/// over-scrubbing.
/// </para>
/// </remarks>
public static class CredentialScrubber
{
    /// <summary>Replacement written in place of any redacted value.</summary>
    public const string Redacted = "***";

    /// <summary>Path segments after which the next two segments are the credentials.</summary>
    private static readonly string[] StreamPathKinds = ["live", "movie", "series", "timeshift"];

    /// <summary>Query parameters whose values are credentials.</summary>
    private static readonly string[] SecretParameters = ["username", "password", "pass", "user"];

    /// <summary>Removes credentials from any URLs found in <paramref name="text"/>.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = ScrubStreamPaths(text);
        result = ScrubQueryParameters(result);
        return ScrubUserInfo(result);
    }

    /// <summary>
    /// Removes specific known secrets wherever they appear, URL-shaped or not.
    /// </summary>
    /// <remarks>
    /// Belt and braces for text the structural rules cannot recognise, such as an
    /// exception message quoting a configuration value. Apply alongside
    /// <see cref="Scrub"/>, not instead of it: this only knows the secrets it is given.
    /// </remarks>
    public static string ScrubSecrets(string? text, IReadOnlyCollection<string> secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        ArgumentNullException.ThrowIfNull(secrets);

        var result = text;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                result = result.Replace(secret, Redacted, StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <summary>
    /// Rewrites <c>/live/{user}/{pass}/</c> and friends, leaving the stream id intact.
    /// </summary>
    private static string ScrubStreamPaths(string text)
    {
        StringBuilder? builder = null;
        var index = 0;

        while (index < text.Length)
        {
            var kindStart = IndexOfStreamKind(text, index, out var kindLength);
            if (kindStart < 0)
            {
                break;
            }

            // Position just past "/live", expecting "/user/pass/".
            var cursor = kindStart + kindLength;
            if (cursor >= text.Length || text[cursor] != '/')
            {
                // "…/api/live" with nothing after it is not a stream URL.
                index = kindStart + kindLength;
                continue;
            }

            var firstStart = cursor + 1;
            var firstEnd = IndexOfSegmentEnd(text, firstStart);
            if (firstEnd >= text.Length || text[firstEnd] != '/')
            {
                index = kindStart + kindLength;
                continue;
            }

            var secondStart = firstEnd + 1;
            var secondEnd = IndexOfSegmentEnd(text, secondStart);
            if (secondEnd >= text.Length || text[secondEnd] != '/')
            {
                // Needs a third segment (the stream id) to be a stream URL.
                index = kindStart + kindLength;
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, index, firstStart - index);
            builder.Append(Redacted).Append('/').Append(Redacted);

            index = secondEnd;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    private static int IndexOfStreamKind(string text, int from, out int kindLength)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] != '/')
            {
                continue;
            }

            var span = text.AsSpan(i + 1);
            foreach (var kind in StreamPathKinds)
            {
                if (span.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
                {
                    kindLength = kind.Length + 1;
                    return i;
                }
            }
        }

        kindLength = 0;
        return -1;
    }

    /// <summary>Finds the end of a path segment: the next '/', '?', '#', or whitespace.</summary>
    private static int IndexOfSegmentEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '/' or '?' or '#' || char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>Rewrites <c>username=…</c> and <c>password=…</c> values.</summary>
    private static string ScrubQueryParameters(string text)
    {
        StringBuilder? builder = null;
        var index = 0;

        while (index < text.Length)
        {
            var match = IndexOfSecretParameter(text, index, out var valueStart);
            if (match < 0)
            {
                break;
            }

            var valueEnd = valueStart;
            while (valueEnd < text.Length &&
                   text[valueEnd] is not ('&' or '#' or '"' or '\'') &&
                   !char.IsWhiteSpace(text[valueEnd]))
            {
                valueEnd++;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, index, valueStart - index).Append(Redacted);
            index = valueEnd;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    private static int IndexOfSecretParameter(string text, int from, out int valueStart)
    {
        for (var i = from; i < text.Length; i++)
        {
            // A parameter starts after '?' or '&'.
            if (text[i] is not ('?' or '&'))
            {
                continue;
            }

            var span = text.AsSpan(i + 1);
            foreach (var parameter in SecretParameters)
            {
                if (span.StartsWith(parameter, StringComparison.OrdinalIgnoreCase) &&
                    span.Length > parameter.Length &&
                    span[parameter.Length] == '=')
                {
                    valueStart = i + 1 + parameter.Length + 1;
                    return i;
                }
            }
        }

        valueStart = -1;
        return -1;
    }

    /// <summary>Rewrites <c>scheme://user:pass@host</c>.</summary>
    private static string ScrubUserInfo(string text)
    {
        const string marker = "://";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        StringBuilder? builder = null;
        var copied = 0;

        while (index >= 0)
        {
            var authorityStart = index + marker.Length;
            var authorityEnd = IndexOfSegmentEnd(text, authorityStart);

            var at = text.LastIndexOf('@', Math.Max(authorityStart, authorityEnd - 1), authorityEnd - authorityStart);
            if (at > authorityStart)
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, authorityStart - copied);
                builder.Append(Redacted).Append(':').Append(Redacted);
                copied = at;
            }

            index = text.IndexOf(marker, authorityStart, StringComparison.Ordinal);
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }
}
