using Iptv.Core.Epg;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// XMLTV timestamps are <c>YYYYMMDDHHMMSS ±HHMM</c>, with most fields optional in
/// practice. Parsed by hand rather than with <see cref="DateTime.ParseExact(string, string[], IFormatProvider, System.Globalization.DateTimeStyles)"/>:
/// this runs once per programme across a couple of million rows, and trying several
/// candidate formats in that loop is measurably slow.
/// </summary>
public sealed class XmltvTimestampTests
{
    // 2026-09-04 18:30:00 UTC. Cross-checked against the reference panel, which reported
    // timestamp_now 1788546335 for "2026-09-04 18:25:35".
    private const long Reference = 1_788_546_600;

    private static long Parse(string value)
    {
        Assert.True(XmltvTimestamp.TryParse(value, out var seconds), $"Failed to parse '{value}'.");
        return seconds;
    }

    [Fact]
    public void Parses_a_full_timestamp_with_a_utc_offset()
        => Assert.Equal(Reference, Parse("20260904183000 +0000"));

    [Fact]
    public void Applies_a_negative_offset()
    {
        // -0500 means local time is five hours behind UTC, so the UTC instant is later.
        Assert.Equal(Reference + (5 * 3600), Parse("20260904183000 -0500"));
    }

    [Fact]
    public void Applies_a_positive_offset_with_minutes()
        => Assert.Equal(Reference - (5 * 3600) - 1800, Parse("20260904183000 +0530"));

    [Fact]
    public void Accepts_a_colon_separated_offset()
        => Assert.Equal(Reference, Parse("20260904183000 +00:00"));

    [Fact]
    public void Accepts_an_offset_with_no_separating_space()
        => Assert.Equal(Reference, Parse("20260904183000+0000"));

    [Fact]
    public void Treats_a_missing_offset_as_utc()
    {
        // The PRD's guidance: store UTC, display local, and expose a per-provider
        // correction, because XMLTV offsets are frequently wrong or absent. Guessing the
        // machine's local zone here would make the stored data depend on where it was
        // ingested.
        Assert.Equal(Reference, Parse("20260904183000"));
    }

    [Fact]
    public void Accepts_a_timestamp_without_seconds()
        => Assert.Equal(Reference, Parse("202609041830"));

    [Fact]
    public void Accepts_a_date_only_timestamp()
        => Assert.Equal(1_788_480_000, Parse("20260904"));

    [Fact]
    public void Tolerates_surrounding_whitespace()
        => Assert.Equal(Reference, Parse("  20260904183000 +0000  "));

    [Fact]
    public void Handles_a_leap_day()
    {
        // 2024-02-29 00:00:00 UTC.
        Assert.Equal(1_709_164_800, Parse("20240229000000 +0000"));
    }

    [Fact]
    public void Handles_the_end_of_a_year()
        => Assert.Equal(1_767_225_599, Parse("20251231235959 +0000"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("2026")]
    [InlineData("202613041830")]   // month 13
    [InlineData("20260932183000")] // day 32
    [InlineData("20260904253000")] // hour 25
    [InlineData("20260904186100")] // minute 61
    public void Rejects_malformed_input(string value)
        => Assert.False(XmltvTimestamp.TryParse(value, out _));

    [Fact]
    public void Rejects_a_february_thirtieth()
    {
        // Guards against arithmetic that accepts any day 1-31 regardless of month, which
        // would silently place programmes on days that do not exist.
        Assert.False(XmltvTimestamp.TryParse("20260230120000", out _));
    }

    [Fact]
    public void Rejects_a_leap_day_in_a_non_leap_year()
        => Assert.False(XmltvTimestamp.TryParse("20260229120000", out _));

    [Fact]
    public void Accepts_a_leap_second_style_sixtieth_second_by_clamping()
    {
        // Some generators emit :60. Rejecting the programme outright would lose it; the
        // second of precision is worth nothing in a TV guide.
        Assert.True(XmltvTimestamp.TryParse("20260904183060 +0000", out var seconds));
        Assert.Equal(Reference + 59, seconds);
    }
}
