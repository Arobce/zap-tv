using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;

namespace Iptv.Core.Epg;

/// <summary>
/// Streaming pull-parser over an XMLTV document.
/// </summary>
/// <remarks>
/// <para>
/// A 120MB guide is normal, so nothing is materialised: no <c>XDocument</c>, no
/// <c>XmlDocument</c>, no <c>XmlSerializer</c> over the whole document. Channels and
/// programmes are emitted as they are read, and peak memory stays flat regardless of file
/// size.
/// </para>
/// <para>
/// DTD processing is prohibited. An XMLTV file is fetched from a URL the user supplied but
/// does not control the contents of, which makes external entity expansion and
/// billion-laughs real exposure rather than theoretical.
/// </para>
/// </remarks>
public static class XmltvReader
{
    /// <summary>Streams channels and programmes from an XMLTV document.</summary>
    /// <remarks>Gzip is detected from the magic bytes and decompressed transparently.</remarks>
    public static IAsyncEnumerable<XmltvItem> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Iterate(stream, cancellationToken);
    }

    private static async IAsyncEnumerable<XmltvItem> Iterate(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var input = await WrapIfCompressedAsync(stream, cancellationToken)
            .ConfigureAwait(false);

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(input, settings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "channel":
                    yield return await ReadChannelAsync(reader).ConfigureAwait(false);
                    break;

                case "programme":
                    if (await ReadProgrammeAsync(reader).ConfigureAwait(false) is { } programme)
                    {
                        yield return programme;
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Detects gzip from the magic bytes and wraps the stream if present.
    /// </summary>
    /// <remarks>
    /// Most EPG URLs serve gzip, frequently without a <c>Content-Encoding</c> header and
    /// sometimes without a <c>.gz</c> suffix, so the bytes are the only reliable signal.
    /// </remarks>
    private static async Task<Stream> WrapIfCompressedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // A network stream cannot be rewound, so buffer enough to peek and replay.
        var buffered = stream.CanSeek ? stream : new BufferedStream(stream, 8192);

        var header = new byte[2];
        var read = await ReadExactlyAsync(buffered, header, cancellationToken).ConfigureAwait(false);

        Stream replayed = read == 0
            ? buffered
            : new ConcatStream(header.AsMemory(0, read), buffered);

        // 0x1F 0x8B is the gzip magic number.
        return read == 2 && header[0] == 0x1F && header[1] == 0x8B
            ? new GZipStream(replayed, CompressionMode.Decompress)
            : replayed;
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<EpgChannel> ReadChannelAsync(XmlReader reader)
    {
        var id = reader.GetAttribute("id") ?? string.Empty;
        var displayNames = new List<string>();
        string? iconUrl = null;

        if (reader.IsEmptyElement)
        {
            return new EpgChannel { Id = id, DisplayNames = displayNames, IconUrl = iconUrl };
        }

        // ReadElementContentAsStringAsync consumes through the end element and leaves the
        // reader on the *next* node. Advancing again at the top of the loop would then skip
        // it, which silently drops every second child - the bug that made sub-title vanish
        // and swallowed the channel that followed.
        var alreadyAdvanced = false;

        while (alreadyAdvanced || await reader.ReadAsync().ConfigureAwait(false))
        {
            alreadyAdvanced = false;

            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "channel")
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "display-name":
                    // Every one of them, not just the first. Phase 4 matches on display
                    // names, and where 72.7% of a provider's channels carry no tvg_id, an
                    // extra alias is often the only thing that produces a match.
                    var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    alreadyAdvanced = true;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        displayNames.Add(name.Trim());
                    }

                    break;

                case "icon":
                    iconUrl ??= reader.GetAttribute("src");
                    break;

                default:
                    break;
            }
        }

        return new EpgChannel { Id = id, DisplayNames = displayNames, IconUrl = iconUrl };
    }

    /// <summary>Reads one programme, or null when it cannot be placed in the guide.</summary>
    private static async Task<EpgProgramme?> ReadProgrammeAsync(XmlReader reader)
    {
        var channelId = reader.GetAttribute("channel") ?? string.Empty;
        var startText = reader.GetAttribute("start");
        var stopText = reader.GetAttribute("stop");

        string? title = null;
        string? subtitle = null;
        string? description = null;
        string? category = null;
        string? episodeNum = null;

        if (!reader.IsEmptyElement)
        {
            // See the note in ReadChannelAsync: reading element content advances the
            // reader, so the loop must not advance again on the following iteration.
            var alreadyAdvanced = false;

            while (alreadyAdvanced || await reader.ReadAsync().ConfigureAwait(false))
            {
                alreadyAdvanced = false;

                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "programme")
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var element = reader.Name;
                if (element is not ("title" or "sub-title" or "desc" or "category" or "episode-num"))
                {
                    continue;
                }

                var content = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                alreadyAdvanced = true;

                switch (element)
                {
                    case "title":
                        title ??= content;
                        break;
                    case "sub-title":
                        subtitle ??= content;
                        break;
                    case "desc":
                        description ??= content;
                        break;
                    case "category":
                        category ??= content;
                        break;
                    default:
                        episodeNum ??= content;
                        break;
                }
            }
        }

        // A programme with no valid start cannot be placed in the grid. Storing it with a
        // zero start would put it at 1970 and corrupt the first window the user opens.
        if (!XmltvTimestamp.TryParse(startText, out var startUtc))
        {
            return null;
        }

        // A missing stop is recoverable: assume an hour rather than lose the programme.
        // The grid needs a width, not an accurate one.
        if (!XmltvTimestamp.TryParse(stopText, out var stopUtc) || stopUtc <= startUtc)
        {
            stopUtc = startUtc + 3600;
        }

        return new EpgProgramme
        {
            ChannelId = channelId,
            StartUtc = startUtc,
            StopUtc = stopUtc,
            Title = title?.Trim() ?? string.Empty,
            Subtitle = Clean(subtitle),
            Description = Clean(description),
            Category = Clean(category),
            EpisodeNum = Clean(episodeNum),
        };
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Replays a peeked prefix ahead of the remainder of a stream.
    /// </summary>
    /// <remarks>
    /// Needed because gzip detection has to consume the first two bytes, and an HTTP
    /// response stream cannot be rewound.
    /// </remarks>
    private sealed class ConcatStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private int _prefixPosition;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefixPosition < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
                prefix.Span.Slice(_prefixPosition, take).CopyTo(buffer);
                _prefixPosition += take;
                return take;
            }

            return rest.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
                prefix.Slice(_prefixPosition, take).CopyTo(buffer);
                _prefixPosition += take;
                return take;
            }

            return await rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() => rest.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
