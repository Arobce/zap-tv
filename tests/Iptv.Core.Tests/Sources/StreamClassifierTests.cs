using Iptv.Core.Sources;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Providers pad their catalogues with decorative section headings. On the reference
/// provider 1,094 of 28,285 live entries are dividers rather than channels.
/// </summary>
/// <remarks>
/// These are kept and shown - they are the provider's own grouping - but flagged, so they
/// do not enter search, dedup, failover candidacy, or the EPG coverage denominator.
/// </remarks>
public sealed class StreamClassifierTests
{
    [Theory]
    [InlineData("##### GOLDEN EVENTS #####")]
    [InlineData("### SPORTS ###")]
    [InlineData("--- UK CHANNELS ---")]
    [InlineData("=== ENTERTAINMENT ===")]
    [InlineData("*** VIP ***")]
    [InlineData("___ MOVIES ___")]
    [InlineData("▬▬▬▬▬ SERIES ▬▬▬▬▬")]
    [InlineData("##### GOLDEN EVENTS")]
    public void Decorative_rows_are_separators(string title)
        => Assert.True(StreamClassifier.IsSeparator(title));

    [Theory]
    [InlineData("BBC One HD")]
    [InlineData("UK| BBC One")]
    [InlineData("US: ESPN FHD")]
    [InlineData("CA - TSN")]
    [InlineData("VIP - NO EVENT")]
    [InlineData("Sky Sports F1")]
    [InlineData("TV5*MONDE")]
    [InlineData("A&E")]
    [InlineData("E! Entertainment")]
    public void Real_channels_are_not_separators(string title)
        => Assert.False(StreamClassifier.IsSeparator(title));

    [Fact]
    public void A_single_dash_country_prefix_is_not_a_separator()
    {
        // "CA - TSN" and "UK - Sky" are the single most common channel naming convention
        // on these panels. A rule keying on any dash would flag the entire catalogue.
        Assert.False(StreamClassifier.IsSeparator("CA - TSN"));
        Assert.False(StreamClassifier.IsSeparator("UK - Sky Sports"));
    }

    [Fact]
    public void Two_repeated_symbols_are_not_enough()
    {
        // Deliberately conservative: a false positive hides a real channel, which is far
        // worse than leaving a divider unflagged.
        Assert.False(StreamClassifier.IsSeparator("Channel -- Two"));
        Assert.True(StreamClassifier.IsSeparator("Channel --- Three"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_titles_are_not_separators(string? title)
        => Assert.False(StreamClassifier.IsSeparator(title));

    [Fact]
    public void Punctuation_only_titles_are_separators()
        => Assert.True(StreamClassifier.IsSeparator("#########"));
}
