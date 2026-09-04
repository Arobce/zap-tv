using Iptv.Core.Xtream;

namespace Iptv.Core.Tests.Xtream;

/// <summary>
/// Xtream stream URLs carry the account's username and password as path segments, and the
/// API carries them as query parameters. Both end up in logs, diagnostics exports and
/// error messages unless something removes them first.
/// </summary>
/// <remarks>
/// A leak here hands over a working subscription, so these tests lean towards
/// over-scrubbing. The failure modes are asymmetric: a redacted log line is a mild
/// inconvenience, a leaked credential is not.
/// </remarks>
public sealed class CredentialScrubberTests
{
    [Fact]
    public void Scrubs_credentials_from_a_live_stream_path()
    {
        Assert.Equal(
            "http://host.invalid/live/***/***/1001.ts",
            CredentialScrubber.Scrub("http://host.invalid/live/ACCT7X2/SECRET99/1001.ts"));
    }

    [Theory]
    [InlineData("movie", "mp4")]
    [InlineData("series", "mkv")]
    [InlineData("timeshift", "ts")]
    public void Scrubs_credentials_from_every_stream_path_kind(string kind, string extension)
    {
        Assert.Equal(
            $"http://host.invalid/{kind}/***/***/2001.{extension}",
            CredentialScrubber.Scrub($"http://host.invalid/{kind}/user123/pass456/2001.{extension}"));
    }

    [Fact]
    public void Scrubs_query_string_credentials()
    {
        Assert.Equal(
            "http://host.invalid/player_api.php?username=***&password=***&action=get_live_streams",
            CredentialScrubber.Scrub(
                "http://host.invalid/player_api.php?username=ACCT7X2&password=SECRET99&action=get_live_streams"));
    }

    [Fact]
    public void Scrubs_credentials_regardless_of_parameter_order()
    {
        Assert.Equal(
            "http://host.invalid/get.php?password=***&type=m3u_plus&username=***",
            CredentialScrubber.Scrub(
                "http://host.invalid/get.php?password=SECRET99&type=m3u_plus&username=ACCT7X2"));
    }

    [Fact]
    public void Scrubs_url_userinfo()
    {
        Assert.Equal(
            "http://***:***@host.invalid/stream",
            CredentialScrubber.Scrub("http://alice:hunter2@host.invalid/stream"));
    }

    [Fact]
    public void Scrubs_every_url_in_a_longer_message()
    {
        // Log lines interpolate URLs into prose, and failover messages mention two at once.
        var scrubbed = CredentialScrubber.Scrub(
            "Failover: http://a.invalid/live/u1/p1/1.ts stalled, trying " +
            "http://b.invalid/live/u2/p2/2.ts instead");

        Assert.Equal(
            "Failover: http://a.invalid/live/***/***/1.ts stalled, trying " +
            "http://b.invalid/live/***/***/2.ts instead",
            scrubbed);
    }

    [Fact]
    public void Leaves_urls_without_credentials_untouched()
    {
        const string url = "http://host.invalid/player_api.php?action=get_live_categories";
        Assert.Equal(url, CredentialScrubber.Scrub(url));
    }

    [Fact]
    public void Leaves_ordinary_text_untouched()
    {
        const string message = "Ingested 28,285 live streams in 9.8s";
        Assert.Equal(message, CredentialScrubber.Scrub(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_absent_input(string? input)
        => Assert.Equal(input ?? string.Empty, CredentialScrubber.Scrub(input));

    [Fact]
    public void Does_not_scrub_a_path_segment_that_merely_resembles_a_stream_url()
    {
        // "live" appearing elsewhere in a path must not cause the following two segments
        // to be redacted; that would mangle legitimate diagnostic output.
        Assert.Equal(
            "http://host.invalid/api/live",
            CredentialScrubber.Scrub("http://host.invalid/api/live"));
    }

    [Fact]
    public void Scrubbing_is_idempotent()
    {
        // Scrubbed text passes through the formatter again on its way to a second sink.
        var once = CredentialScrubber.Scrub("http://host.invalid/live/u/p/1.ts");
        Assert.Equal(once, CredentialScrubber.Scrub(once));
    }

    [Fact]
    public void Scrubs_a_known_secret_wherever_it_appears()
    {
        // Belt and braces for text that is not URL-shaped at all - an exception message
        // quoting a config value, for instance.
        Assert.Equal(
            "auth failed for *** using ***",
            CredentialScrubber.ScrubSecrets("auth failed for ACCT7X2 using SECRET99", ["ACCT7X2", "SECRET99"]));
    }
}
