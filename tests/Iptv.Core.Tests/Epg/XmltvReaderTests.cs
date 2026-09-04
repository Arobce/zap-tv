using System.IO.Compression;
using System.Text;
using Iptv.Core.Epg;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// Streaming pull-parser over XMLTV.
/// </summary>
/// <remarks>
/// A 120MB guide is normal, so nothing is materialised: no XDocument, no XmlDocument, no
/// XmlSerializer over the whole document. Channels and programmes are emitted as they are
/// read.
/// </remarks>
public sealed class XmltvReaderTests
{
    private static Stream Open(string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    private static async Task<(List<EpgChannel> Channels, List<EpgProgramme> Programmes)> ReadAsync(
        Stream stream)
    {
        var channels = new List<EpgChannel>();
        var programmes = new List<EpgProgramme>();

        await foreach (var item in XmltvReader.ReadAsync(stream, CancellationToken.None))
        {
            switch (item)
            {
                case EpgChannel channel:
                    channels.Add(channel);
                    break;
                case EpgProgramme programme:
                    programmes.Add(programme);
                    break;
                default:
                    break;
            }
        }

        return (channels, programmes);
    }

    private static Task<(List<EpgChannel>, List<EpgProgramme>)> ReadFixtureAsync()
        => ReadAsync(File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xmltv", "sample.xml")));

    [Fact]
    public async Task Reads_channels_with_every_display_name()
    {
        var (channels, _) = await ReadFixtureAsync();

        var bbc = channels.Single(c => c.Id == "bbc1.uk");

        // All of them, not just the first. Phase 4 matches on display names, and for a
        // provider where 72.7% of channels carry no tvg_id, an extra alias is often the
        // only thing that produces a match.
        Assert.Equal(["BBC One", "BBC 1", "BBC One HD"], bbc.DisplayNames);
    }

    [Fact]
    public async Task Reads_channel_icons()
    {
        var (channels, _) = await ReadFixtureAsync();
        Assert.Equal("http://logos.invalid/bbc1.png", channels.Single(c => c.Id == "bbc1.uk").IconUrl);
    }

    [Fact]
    public async Task Keeps_channels_that_have_no_display_name()
    {
        var (channels, _) = await ReadFixtureAsync();

        // Its id can still match a provider tvg_id, so dropping it would lose real
        // coverage even though it can never match by name.
        var bare = channels.Single(c => c.Id == "no-name.xx");
        Assert.Empty(bare.DisplayNames);
    }

    [Fact]
    public async Task Reads_programme_fields()
    {
        var (_, programmes) = await ReadFixtureAsync();

        var news = programmes.First(p => p.Title == "The Six O'Clock News");
        Assert.Equal("bbc1.uk", news.ChannelId);
        Assert.Equal(1_788_546_600, news.StartUtc);
        Assert.Equal(1_788_550_200, news.StopUtc);
        Assert.Equal("Evening Edition", news.Subtitle);
        Assert.Equal("National and international news.", news.Description);
        Assert.Equal("News", news.Category);
        Assert.Equal("0.1.0/1", news.EpisodeNum);
    }

    [Fact]
    public async Task Decodes_xml_entities()
    {
        var (_, programmes) = await ReadFixtureAsync();
        Assert.Contains(programmes, p => p.Title == "Regional News & Weather");
    }

    [Fact]
    public async Task Applies_the_timestamp_offset()
    {
        var (_, programmes) = await ReadFixtureAsync();

        // 18:00 at -0500 is 23:00 UTC on the same day.
        // 2026-09-04 00:00 UTC is 1788480000; +23h is 1788562800.
        var sportsCenter = programmes.Single(p => p.Title == "SportsCenter");
        Assert.Equal(1_788_562_800, sportsCenter.StartUtc);
    }

    [Fact]
    public async Task Skips_programmes_with_an_unparseable_start()
    {
        var (_, programmes) = await ReadFixtureAsync();

        // A programme with no valid start cannot be placed in the grid. Storing it with a
        // zero start would put it at 1970 and corrupt the first window the user opens.
        Assert.DoesNotContain(programmes, p => p.Title == "Unparseable Start");
    }

    [Fact]
    public async Task Keeps_programmes_for_channels_not_declared_in_the_file()
    {
        var (_, programmes) = await ReadFixtureAsync();

        // Many real guides list programmes for channels they never declare. The mapping
        // stage decides what is usable; the reader's job is not to lose data.
        Assert.Contains(programmes, p => p.ChannelId == "unknown.channel");
    }

    [Fact]
    public async Task Reads_gzipped_input_transparently()
    {
        var raw = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xmltv", "sample.xml"));

        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(raw);
        }

        compressed.Position = 0;

        // Most EPG URLs serve gzip, often without a Content-Encoding header, so the
        // magic bytes are what decide.
        var (channels, programmes) = await ReadAsync(compressed);
        Assert.NotEmpty(channels);
        Assert.NotEmpty(programmes);
    }

    [Fact]
    public async Task Accepts_a_document_type_declaration()
    {
        // Real XMLTV opens with <!DOCTYPE tv SYSTEM "xmltv.dtd">; the reference provider's
        // 70MB guide does. Rejecting the declaration outright would reject nearly every
        // real guide, so the DTD is ignored rather than prohibited.
        var declared =
            """
            <?xml version="1.0"?>
            <!DOCTYPE tv SYSTEM "xmltv.dtd">
            <tv><channel id="a"><display-name>Channel A</display-name></channel></tv>
            """;

        var (channels, _) = await ReadAsync(Open(declared));
        Assert.Equal("Channel A", Assert.Single(channels).DisplayNames.Single());
    }

    [Fact]
    public async Task Does_not_expand_internal_entities()
    {
        // Billion-laughs protection. Ignoring the DTD means the entity is never declared
        // as far as the reader is concerned, so the reference fails rather than expanding.
        var hostile =
            """
            <?xml version="1.0"?>
            <!DOCTYPE tv [<!ENTITY x "expanded">]>
            <tv><channel id="a"><display-name>&x;</display-name></channel></tv>
            """;

        await Assert.ThrowsAnyAsync<System.Xml.XmlException>(
            async () => await ReadAsync(Open(hostile)));
    }

    [Fact]
    public async Task Does_not_fetch_an_external_entity()
    {
        // XXE. XmlResolver = null means a SYSTEM identifier is never dereferenced, so a
        // hostile guide cannot read local files or make the app issue requests.
        var hostile =
            """
            <?xml version="1.0"?>
            <!DOCTYPE tv SYSTEM "http://attacker.invalid/evil.dtd">
            <tv><channel id="a"><display-name>Safe</display-name></channel></tv>
            """;

        // Parses fine and simply ignores the external subset rather than fetching it.
        var (channels, _) = await ReadAsync(Open(hostile));
        Assert.Equal("Safe", Assert.Single(channels).DisplayNames.Single());
    }

    [Fact]
    public async Task An_empty_document_yields_nothing()
    {
        var (channels, programmes) = await ReadAsync(Open("<tv></tv>"));

        Assert.Empty(channels);
        Assert.Empty(programmes);
    }

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        using var cts = new CancellationTokenSource();
        await using var stream = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xmltv", "sample.xml"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in XmltvReader.ReadAsync(stream, cts.Token))
            {
                await cts.CancelAsync();
            }
        });
    }
}
