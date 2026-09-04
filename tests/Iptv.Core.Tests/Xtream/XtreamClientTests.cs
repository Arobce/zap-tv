using System.Net;
using System.Text;
using Iptv.Core.Xtream;

namespace Iptv.Core.Tests.Xtream;

/// <summary>
/// The PRD's central warning about Xtream panels: they return <c>200 OK</c> with an HTML
/// error page, or with <c>{"user_info":{"auth":0}}</c>, instead of a meaningful status
/// code. Validate the payload shape, never the status code.
/// </summary>
public sealed class XtreamClientTests
{
    private static readonly XtreamCredentials Credentials = new(
        new Uri("http://host.invalid"), "ACCT7X2", "SECRET99");

    private static XtreamClient ClientReturning(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(new HttpClient(new StubHandler(body, status)), Credentials);

    [Fact]
    public async Task Authenticates_against_a_valid_payload()
    {
        var client = ClientReturning(
            """{"user_info":{"auth":1,"status":"Active","max_connections":"2"},"server_info":{}}""");

        var info = await client.GetAccountInfoAsync(CancellationToken.None);

        Assert.True(info.Auth);
        Assert.Equal(2, info.MaxConnections);
    }

    [Fact]
    public async Task Rejects_an_auth_zero_payload_despite_a_200()
    {
        var client = ClientReturning("""{"user_info":{"auth":0},"server_info":{}}""");

        var exception = await Assert.ThrowsAsync<XtreamAuthenticationException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));

        Assert.Contains("credential", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_disabled_account()
    {
        var client = ClientReturning(
            """{"user_info":{"auth":1,"status":"Disabled"},"server_info":{}}""");

        await Assert.ThrowsAsync<XtreamAuthenticationException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_an_html_error_page_served_with_a_200()
    {
        var client = ClientReturning("<html><body>403 Forbidden</body></html>");

        var exception = await Assert.ThrowsAsync<XtreamProtocolException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));

        // The message has to say the response was not JSON, or every panel misconfiguration
        // looks like a parser bug.
        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_an_empty_body()
    {
        var client = ClientReturning(string.Empty);
        await Assert.ThrowsAsync<XtreamProtocolException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_valid_json_of_the_wrong_shape()
    {
        // Some panels answer an unknown action with [] rather than an object.
        var client = ClientReturning("[]");
        await Assert.ThrowsAsync<XtreamProtocolException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Surfaces_a_real_http_failure()
    {
        var client = ClientReturning("Service Unavailable", HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<XtreamProtocolException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Error_messages_never_contain_credentials()
    {
        var client = ClientReturning("<html>error</html>");

        var exception = await Assert.ThrowsAsync<XtreamProtocolException>(
            () => client.GetAccountInfoAsync(CancellationToken.None));

        // Exception messages quote the request URL, which carries the credentials.
        Assert.DoesNotContain("ACCT7X2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET99", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streams_live_entries_without_buffering_the_body()
    {
        var body = "[" + string.Join(
            ",",
            Enumerable.Range(0, 500).Select(i =>
                $$"""{"num":{{i}},"name":"Channel {{i}}","stream_type":"live","stream_id":{{i}},"category_id":"1","epg_channel_id":null,"added":"1708096234","tv_archive":0}""")) + "]";

        var client = ClientReturning(body);

        var count = 0;
        await foreach (var stream in client.GetLiveStreamsAsync(CancellationToken.None))
        {
            Assert.Equal("live", stream.StreamType);
            count++;
        }

        Assert.Equal(500, count);
    }

    [Fact]
    public async Task Enumeration_is_cancellable_partway_through()
    {
        var body = "[" + string.Join(
            ",",
            Enumerable.Range(0, 500).Select(i =>
                $$"""{"stream_id":{{i}},"name":"C","stream_type":"live"}""")) + "]";

        using var cts = new CancellationTokenSource();
        var client = ClientReturning(body);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetLiveStreamsAsync(cts.Token))
            {
                await cts.CancelAsync();
            }
        });
    }

    [Theory]
    [InlineData("live", 1001, "ts", "http://host.invalid/live/ACCT7X2/SECRET99/1001.ts")]
    [InlineData("movie", 2001, "mp4", "http://host.invalid/movie/ACCT7X2/SECRET99/2001.mp4")]
    [InlineData("series", 3001, "mkv", "http://host.invalid/series/ACCT7X2/SECRET99/3001.mkv")]
    public void Builds_stream_urls(string kind, long id, string extension, string expected)
    {
        var url = kind switch
        {
            "live" => Credentials.BuildLiveUrl(id),
            "movie" => Credentials.BuildVodUrl(id, extension),
            _ => Credentials.BuildSeriesUrl(id, extension),
        };

        Assert.Equal(expected, url.ToString());
    }

    [Fact]
    public void Live_urls_default_to_the_transport_stream_container()
    {
        // .ts is what panels serve for live; m3u8 exists but adds segment latency, which
        // works against the sub-second channel change target.
        Assert.EndsWith(".ts", Credentials.BuildLiveUrl(1).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_with_url_unsafe_characters_are_escaped()
    {
        var awkward = new XtreamCredentials(new Uri("http://host.invalid"), "user name", "p@ss/word");

        // AbsoluteUri, not ToString(). ToString() returns the display form with escaping
        // undone, which is not what goes on the wire; asserting on it would either fail
        // spuriously or, worse, pass while the request was malformed.
        var wire = awkward.BuildLiveUrl(7).AbsoluteUri;

        Assert.Contains("user%20name", wire, StringComparison.Ordinal);

        // The critical one: an unescaped '/' in a password shifts the stream id into the
        // wrong path segment and yields a 404 that presents as a dead channel.
        Assert.Contains("p%40ss%2Fword", wire, StringComparison.Ordinal);
        Assert.Equal(
            "http://host.invalid/live/user%20name/p%40ss%2Fword/7.ts",
            wire);
    }

    [Fact]
    public void The_escaped_form_survives_the_uri_round_trip()
    {
        // .NET's Uri normalises some escapes on construction. If %2F were unescaped back
        // to '/', every credential containing a slash would silently produce a broken URL,
        // and the only symptom would be a channel that never plays.
        var awkward = new XtreamCredentials(new Uri("http://host.invalid"), "u", "a/b");
        var wire = awkward.BuildLiveUrl(1).AbsoluteUri;

        Assert.Equal("http://host.invalid/live/u/a%2Fb/1.ts", wire);
    }

    [Fact]
    public void Base_urls_with_a_trailing_slash_do_not_double_it()
    {
        var trailing = new XtreamCredentials(new Uri("http://host.invalid/"), "u", "p");
        Assert.Equal("http://host.invalid/live/u/p/5.ts", trailing.BuildLiveUrl(5).ToString());
    }

    private sealed class StubHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
