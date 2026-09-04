using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Iptv.Core.Playlists;

/// <summary>
/// Streaming parser for M3U/M3U8 playlists.
/// </summary>
/// <remarks>
/// <para>
/// Forward-only over the response stream, yielding entries as they are read. A 50k-entry
/// playlist is common and the whole body is never materialised.
/// </para>
/// <para>
/// Attribute scanning is span-based rather than regex. This runs once per playlist line,
/// which is the per-record hot path the conventions forbid regex in.
/// </para>
/// </remarks>
public static class M3uParser
{
    private const string ExtInfPrefix = "#EXTINF:";

    /// <summary>Reads entries from an M3U stream. The caller owns the stream.</summary>
    public static IAsyncEnumerable<M3uEntry> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Iterate(stream, cancellationToken);
    }

    private static async IAsyncEnumerable<M3uEntry> Iterate(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // detectEncodingFromByteOrderMarks strips a UTF-8 BOM. Without it the first line
        // reads as "﻿#EXTM3U", and any strict header check silently yields nothing.
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: true);

        ExtInf? pending = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var span = line.AsSpan().Trim();
            if (span.IsEmpty)
            {
                continue;
            }

            if (span[0] == '#')
            {
                // A second #EXTINF before a URL discards the first: the entry was
                // truncated or the playlist is malformed, and emitting it with an empty
                // URL would surface a channel that always fails at playback.
                pending = span.StartsWith(ExtInfPrefix, StringComparison.OrdinalIgnoreCase)
                    ? ParseExtInf(span)
                    : pending;
                continue;
            }

            if (pending is not { } info)
            {
                // A URL with no preceding #EXTINF has no title or attributes; there is
                // nothing useful to record.
                continue;
            }

            pending = null;
            yield return new M3uEntry
            {
                Title = info.Title,
                Url = span.ToString(),
                Duration = info.Duration,
                TvgId = info.TvgId,
                TvgName = info.TvgName,
                TvgLogo = info.TvgLogo,
                GroupTitle = info.GroupTitle,
                CatchupKind = info.CatchupKind,
                CatchupSource = info.CatchupSource,
                CatchupDays = info.CatchupDays,
            };
        }
    }

    /// <summary>
    /// Parses <c>#EXTINF:&lt;duration&gt; &lt;attributes&gt;,&lt;title&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Synchronous and span-only so no span crosses an await in the iterator above.
    /// </remarks>
    private static ExtInf ParseExtInf(ReadOnlySpan<char> line)
    {
        var rest = line[ExtInfPrefix.Length..];

        // The title is separated by the first comma that is *outside* quotes. Splitting
        // on the first comma mangles group-title="Sports, USA"; splitting on the last
        // mangles titles like "Movie, The Sequel".
        var separator = IndexOfUnquotedComma(rest);

        var head = separator < 0 ? rest : rest[..separator];
        var title = separator < 0 ? ReadOnlySpan<char>.Empty : rest[(separator + 1)..].Trim();

        // Duration runs to the first space; attributes, if any, follow it.
        var durationEnd = head.IndexOf(' ');
        var durationSpan = durationEnd < 0 ? head : head[..durationEnd];
        var attributes = durationEnd < 0 ? ReadOnlySpan<char>.Empty : head[(durationEnd + 1)..];

        var entry = new ExtInf
        {
            Title = title.ToString(),
            Duration = double.TryParse(
                durationSpan.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration)
                ? duration
                : 0,
        };

        return ReadAttributes(attributes, entry);
    }

    private static int IndexOfUnquotedComma(ReadOnlySpan<char> span)
    {
        var inQuotes = false;
        for (var i = 0; i < span.Length; i++)
        {
            switch (span[i])
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ',' when !inQuotes:
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Scans <c>key="value"</c> and bare <c>key=value</c> pairs.
    /// </summary>
    private static ExtInf ReadAttributes(ReadOnlySpan<char> span, ExtInf entry)
    {
        var index = 0;
        while (index < span.Length)
        {
            while (index < span.Length && span[index] == ' ')
            {
                index++;
            }

            var keyStart = index;
            while (index < span.Length && span[index] != '=' && span[index] != ' ')
            {
                index++;
            }

            if (index >= span.Length || span[index] != '=')
            {
                // Not a key=value pair. Skip the token rather than abandoning the rest of
                // the line; providers emit stray words in the attribute region.
                continue;
            }

            var key = span[keyStart..index];
            index++;

            ReadOnlySpan<char> value;
            if (index < span.Length && span[index] == '"')
            {
                index++;
                var valueStart = index;
                while (index < span.Length && span[index] != '"')
                {
                    index++;
                }

                value = span[valueStart..index];
                if (index < span.Length)
                {
                    index++;
                }
            }
            else
            {
                var valueStart = index;
                while (index < span.Length && span[index] != ' ')
                {
                    index++;
                }

                value = span[valueStart..index];
            }

            entry = Assign(entry, key, value);
        }

        return entry;
    }

    private static ExtInf Assign(ExtInf entry, ReadOnlySpan<char> key, ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            // An attribute present but empty carries no more information than an absent
            // one, and null keeps the "missing" case single-valued downstream.
            return entry;
        }

        if (key.Equals("tvg-id", StringComparison.OrdinalIgnoreCase))
        {
            return entry with { TvgId = value.ToString() };
        }

        if (key.Equals("tvg-name", StringComparison.OrdinalIgnoreCase))
        {
            return entry with { TvgName = value.ToString() };
        }

        if (key.Equals("tvg-logo", StringComparison.OrdinalIgnoreCase))
        {
            return entry with { TvgLogo = value.ToString() };
        }

        if (key.Equals("group-title", StringComparison.OrdinalIgnoreCase))
        {
            return entry with { GroupTitle = value.ToString() };
        }

        if (key.Equals("catchup", StringComparison.OrdinalIgnoreCase))
        {
            return entry with { CatchupKind = value.ToString() };
        }

        if (key.Equals("catchup-source", StringComparison.OrdinalIgnoreCase))
        {
            return entry with { CatchupSource = value.ToString() };
        }

        if (key.Equals("catchup-days", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return entry with { CatchupDays = days };
        }

        // Unknown attributes are ignored rather than collected. Playlists carry a long
        // tail of provider-specific keys that nothing downstream reads.
        return entry;
    }

    /// <summary>Fields carried from an <c>#EXTINF</c> line until its URL arrives.</summary>
    private readonly record struct ExtInf
    {
        public string Title { get; init; }
        public double Duration { get; init; }
        public string? TvgId { get; init; }
        public string? TvgName { get; init; }
        public string? TvgLogo { get; init; }
        public string? GroupTitle { get; init; }
        public string? CatchupKind { get; init; }
        public string? CatchupSource { get; init; }
        public int? CatchupDays { get; init; }
    }
}
