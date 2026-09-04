using Iptv.Core.Sources;
using Iptv.Core.Xtream;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Turns a provider's payload into the row the database stores, deriving identity,
/// quality, country and separator status on the way in.
/// </summary>
/// <remarks>
/// Derived once at ingest rather than on every read: normalization runs across 28,285
/// entries per sync, and the EPG grid queries these columns on every scroll frame.
/// </remarks>
public sealed class StreamMapperTests
{
    private static readonly XtreamCredentials Credentials = new(
        new Uri("http://host.invalid"), "ACCT7X2", "SECRET99");

    private static StreamRecord Map(XtreamLiveStream stream)
        => StreamMapper.FromXtreamLive(stream, providerId: 1, Credentials);

    private static XtreamLiveStream Live(
        string name,
        long streamId = 100,
        string? epgChannelId = null,
        bool tvArchive = false)
        => new()
        {
            Name = name,
            StreamId = streamId,
            EpgChannelId = epgChannelId,
            CategoryId = "1382",
            StreamType = "live",
            TvArchive = tvArchive,
            TvArchiveDuration = tvArchive ? 7 : 0,
        };

    [Fact]
    public void Maps_the_basics()
    {
        var record = Map(Live("BBC One HD", streamId: 682951, epgChannelId: "bbc1.uk"));

        Assert.Equal(1, record.ProviderId);
        Assert.Equal("682951", record.ProviderStreamId);
        Assert.Equal(StreamKind.Live, record.Kind);
        Assert.Equal("BBC One HD", record.Title);
        Assert.Equal("bbc1.uk", record.TvgId);
        Assert.Equal("1382", record.CategoryId);
    }

    [Fact]
    public void Builds_the_playback_url_from_credentials()
        => Assert.Equal(
            "http://host.invalid/live/ACCT7X2/SECRET99/682951.ts",
            Map(Live("BBC One", streamId: 682951)).Url);

    [Fact]
    public void Derives_the_channel_key_from_tvg_id_when_present()
        => Assert.Equal("tvg:bbc1.uk", Map(Live("BBC One HD", epgChannelId: "bbc1.uk")).ChannelKey);

    [Fact]
    public void Falls_back_to_the_normalized_name_when_tvg_id_is_absent()
    {
        // 72.7% of the reference provider's channels take this path, so it is the primary
        // route rather than a fallback.
        Assert.Equal("name:bbcone", Map(Live("UK| BBC One HD")).ChannelKey);
    }

    [Fact]
    public void Retains_quality_separately_from_the_normalized_title()
    {
        var record = Map(Live("BBC One UHD"));

        Assert.Equal(Quality.Uhd, record.Quality);
        Assert.Equal("bbc one", record.NormalizedTitle);
    }

    [Fact]
    public void Retains_the_country_prefix()
        => Assert.Equal("US", Map(Live("US: ESPN FHD")).Country);

    [Fact]
    public void Flags_separator_rows()
    {
        var record = Map(Live("##### GOLDEN EVENTS #####"));

        Assert.True(record.IsSeparator);
        // Still mapped rather than dropped: the user sees it as a section heading.
        Assert.Equal("##### GOLDEN EVENTS #####", record.Title);
    }

    [Fact]
    public void Real_channels_are_not_flagged()
        => Assert.False(Map(Live("BBC One HD")).IsSeparator);

    [Fact]
    public void Carries_catchup_availability()
    {
        var record = Map(Live("BBC One", tvArchive: true));

        Assert.Equal(7, record.CatchupDays);
        Assert.NotNull(record.CatchupKind);
    }

    [Fact]
    public void Leaves_catchup_absent_when_the_provider_does_not_offer_it()
    {
        var record = Map(Live("BBC One"));

        Assert.Null(record.CatchupDays);
        Assert.Null(record.CatchupKind);
    }

    [Fact]
    public void Live_streams_use_the_transport_stream_container()
        => Assert.Equal("ts", Map(Live("BBC One")).Container);

    [Fact]
    public void An_unidentifiable_title_gets_a_provider_scoped_key_rather_than_being_dropped()
    {
        // A title that normalizes to nothing and carries no tvg_id cannot be merged with
        // anything. It still belongs to the user, so it gets a key unique to this provider
        // and stream rather than being discarded or sharing one bogus key with every other
        // unidentifiable entry.
        var record = Map(Live("!!!", streamId: 999));

        Assert.Equal("stream:1:999", record.ChannelKey);
    }

    [Fact]
    public void Unidentifiable_entries_from_different_providers_do_not_collide()
    {
        var a = StreamMapper.FromXtreamLive(Live("???", streamId: 5), providerId: 1, Credentials);
        var b = StreamMapper.FromXtreamLive(Live("???", streamId: 5), providerId: 2, Credentials);

        Assert.NotEqual(a.ChannelKey, b.ChannelKey);
    }

    [Fact]
    public void A_direct_source_url_overrides_construction()
    {
        // Some panels serve channels from a different edge and expect the client to use
        // the URL they supply rather than building one.
        var stream = Live("BBC One") with { DirectSource = "http://edge.invalid/stream.ts" };

        Assert.Equal("http://edge.invalid/stream.ts", Map(stream).Url);
    }

    [Fact]
    public void An_empty_direct_source_does_not_override_construction()
    {
        // The reference provider sends "" for direct_source on every entry.
        var stream = Live("BBC One", streamId: 7) with { DirectSource = string.Empty };

        Assert.Equal("http://host.invalid/live/ACCT7X2/SECRET99/7.ts", Map(stream).Url);
    }
}
