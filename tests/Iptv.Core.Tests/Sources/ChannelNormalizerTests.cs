using Iptv.Core.Sources;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Normalization is the foundation of cross-provider dedup and of EPG matching, and
/// <c>channel_key</c> - which carries every piece of user state - is derived from it.
/// These tests pin the behaviour precisely because changing it later is a schema
/// migration, not a tweak.
/// </summary>
public sealed class ChannelNormalizerTests
{
    [Theory]
    [InlineData("BBC One", "bbc one")]
    [InlineData("bbc one", "bbc one")]
    [InlineData("  BBC   One  ", "bbc one")]
    public void Lowercases_and_collapses_whitespace(string input, string expected)
        => Assert.Equal(expected, ChannelNormalizer.Normalize(input));

    [Theory]
    [InlineData("Télé Monté-Carlo", "tele monte carlo")]
    [InlineData("Kanál Šport", "kanal sport")]
    [InlineData("ÖSTERREICH 1", "osterreich 1")]
    public void Strips_diacritics(string input, string expected)
        => Assert.Equal(expected, ChannelNormalizer.Normalize(input));

    [Theory]
    [InlineData("BBC One HD", "bbc one")]
    [InlineData("BBC One FHD", "bbc one")]
    [InlineData("BBC One UHD", "bbc one")]
    [InlineData("BBC One 4K", "bbc one")]
    [InlineData("BBC One SD", "bbc one")]
    [InlineData("BBC One H265", "bbc one")]
    [InlineData("BBC One HEVC", "bbc one")]
    [InlineData("BBC One RAW", "bbc one")]
    public void Removes_quality_markers(string input, string expected)
        => Assert.Equal(expected, ChannelNormalizer.Normalize(input));

    [Theory]
    [InlineData("BBC One [Backup]", "bbc one")]
    [InlineData("BBC One (1080p)", "bbc one")]
    [InlineData("BBC One [VIP] (HD)", "bbc one")]
    public void Removes_bracketed_segments(string input, string expected)
        => Assert.Equal(expected, ChannelNormalizer.Normalize(input));

    [Theory]
    [InlineData("US: ESPN", "espn")]
    [InlineData("UK| BBC One", "bbc one")]
    [InlineData("CA - TSN", "tsn")]
    [InlineData("DE : Sky Sport", "sky sport")]
    public void Strips_country_prefixes(string input, string expected)
        => Assert.Equal(expected, ChannelNormalizer.Normalize(input));

    [Theory]
    [InlineData("Sky Sports F1!", "sky sports f1")]
    [InlineData("A&E", "a e")]
    [InlineData("TV5*MONDE", "tv5 monde")]
    public void Removes_punctuation_but_keeps_token_boundaries(string input, string expected)
        => Assert.Equal(expected, ChannelNormalizer.Normalize(input));

    [Fact]
    public void Keeps_digits_because_they_distinguish_channels()
    {
        // "Sky Sports 1" and "Sky Sports 2" are different channels. Stripping digits
        // would merge an entire provider's sports tier into one entry.
        Assert.NotEqual(
            ChannelNormalizer.Normalize("Sky Sports 1"),
            ChannelNormalizer.Normalize("Sky Sports 2"));
    }

    [Fact]
    public void Does_not_strip_quality_markers_embedded_in_words()
    {
        // "HD" inside a name is not a quality marker. Token-boundary matching, not
        // substring replacement.
        Assert.Equal("hdnet", ChannelNormalizer.Normalize("HDNet"));
        Assert.Equal("shd sport", ChannelNormalizer.Normalize("SHD Sport"));
    }

    [Theory]
    [InlineData("US: ESPN", "US")]
    [InlineData("UK| BBC One", "UK")]
    [InlineData("CA - TSN", "CA")]
    [InlineData("BBC One", null)]
    [InlineData("ESPN", null)]
    public void Extracts_the_country_prefix_when_present(string input, string? expected)
    {
        // Kept rather than discarded: normalization deliberately collapses "US: ESPN" and
        // "UK| ESPN" onto one key, and failover needs the country to avoid substituting
        // a genuinely different channel.
        Assert.Equal(expected, ChannelNormalizer.ExtractCountry(input));
    }

    [Theory]
    [InlineData("BBC One UHD", Quality.Uhd)]
    [InlineData("BBC One 4K", Quality.Uhd)]
    [InlineData("BBC One FHD", Quality.Fhd)]
    [InlineData("BBC One HD", Quality.Hd)]
    [InlineData("BBC One SD", Quality.Sd)]
    [InlineData("BBC One", null)]
    public void Extracts_quality_when_present(string input, Quality? expected)
    {
        // Users want to prefer the UHD copy while still failing over to the HD one, so
        // quality is parsed out and kept rather than simply discarded.
        Assert.Equal(expected, ChannelNormalizer.ExtractQuality(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("HD")]
    public void Degenerate_input_normalizes_to_empty_rather_than_throwing(string input)
        => Assert.Equal(string.Empty, ChannelNormalizer.Normalize(input));

    [Fact]
    public void Cross_provider_variants_of_the_same_channel_collapse_together()
    {
        // The whole point of the function, stated as one test.
        var variants = new[]
        {
            "BBC One HD",
            "UK| BBC One FHD",
            "bbc  one [VIP]",
            "BBC One (H265)",
        };

        Assert.Single(variants.Select(ChannelNormalizer.Normalize).Distinct());
    }
}
