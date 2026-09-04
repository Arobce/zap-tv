using Iptv.Core.Sources;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// <c>channel_key</c> is the cross-provider identity that favourites, hidden state, sort
/// order, EPG mappings and resume positions all hang off.
/// </summary>
public sealed class ChannelIdentityTests
{
    [Fact]
    public void Prefers_tvg_id_when_present()
    {
        Assert.Equal("tvg:bbc.one.uk", ChannelIdentity.DeriveChannelKey("BBC.One.uk", "bbc one"));
    }

    [Fact]
    public void Falls_back_to_the_normalized_name_when_tvg_id_is_absent()
    {
        Assert.Equal("name:bbcone", ChannelIdentity.DeriveChannelKey(null, "bbc one"));
        Assert.Equal("name:bbcone", ChannelIdentity.DeriveChannelKey("", "bbc one"));
        Assert.Equal("name:bbcone", ChannelIdentity.DeriveChannelKey("   ", "bbc one"));
    }

    [Fact]
    public void Name_keys_ignore_token_boundaries()
    {
        // "ESPN 2" and "ESPN2" are the same channel spelled differently by two providers.
        // The normalized form keeps the space so Phase 4 can do token-set matching; the
        // key drops it so dedup still sees one channel.
        Assert.Equal(
            ChannelIdentity.DeriveChannelKey(null, "espn 2"),
            ChannelIdentity.DeriveChannelKey(null, "espn2"));
    }

    [Fact]
    public void Tvg_id_keys_are_case_insensitive()
    {
        Assert.Equal(
            ChannelIdentity.DeriveChannelKey("BBC.One.UK", "bbc one"),
            ChannelIdentity.DeriveChannelKey("bbc.one.uk", "bbc one"));
    }

    [Fact]
    public void Tvg_and_name_keys_never_collide()
    {
        // Distinct prefixes, so a tvg_id that happens to look like a normalized name
        // cannot be mistaken for one.
        Assert.NotEqual(
            ChannelIdentity.DeriveChannelKey("bbcone", "something else"),
            ChannelIdentity.DeriveChannelKey(null, "bbcone"));
    }

    [Fact]
    public void An_empty_normalized_name_yields_no_key()
    {
        // A stream whose title normalizes away entirely cannot be identified. Returning
        // a "name:" key for it would merge every such stream across all providers into
        // one bogus channel.
        Assert.Null(ChannelIdentity.DeriveChannelKey(null, ""));
        Assert.Null(ChannelIdentity.DeriveChannelKey("", "   "));
    }
}
