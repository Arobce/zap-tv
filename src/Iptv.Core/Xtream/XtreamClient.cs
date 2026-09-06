using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Iptv.Core.Xtream;

/// <summary>The panel accepted the request but refused the account.</summary>
public sealed class XtreamAuthenticationException(string message) : Exception(message);

/// <summary>The panel returned something that is not a valid Xtream response.</summary>
public sealed class XtreamProtocolException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Client for the Xtream Codes <c>player_api.php</c> interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validate the payload, never the status code.</b> Panels return <c>200 OK</c> with an
/// HTML error page, or with <c>{"user_info":{"auth":0}}</c>, rather than a meaningful
/// status. Trusting the status code means treating a rejected login as a successful sync
/// that happens to contain no channels.
/// </para>
/// <para>
/// Collection endpoints are streamed with <see cref="JsonSerializer.DeserializeAsyncEnumerable{T}(Stream, JsonSerializerOptions, CancellationToken)"/>.
/// This is structural rather than an optimisation: the reference provider's
/// <c>get_series</c> response is 75MB.
/// </para>
/// </remarks>
public sealed class XtreamClient
{
    private readonly HttpClient _httpClient;
    private readonly XtreamCredentials _credentials;

    public XtreamClient(HttpClient httpClient, XtreamCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);

        _httpClient = httpClient;
        _credentials = credentials;
    }

    /// <summary>Fetches account info and verifies the account is usable.</summary>
    /// <exception cref="XtreamAuthenticationException">Credentials rejected, or account not active.</exception>
    /// <exception cref="XtreamProtocolException">Response was not a valid Xtream payload.</exception>
    public async Task<XtreamUserInfo> GetAccountInfoAsync(CancellationToken cancellationToken)
    {
        var body = await GetStringAsync(action: null, cancellationToken).ConfigureAwait(false);

        XtreamAccountResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<XtreamAccountResponse>(body, XtreamJson.Options);
        }
        catch (JsonException exception)
        {
            // Overwhelmingly an HTML error or captive-portal page served with a 200.
            throw new XtreamProtocolException(
                "The provider did not return JSON. This usually means an HTML error page was " +
                "served with a 200 status, or the host is not an Xtream panel.",
                exception);
        }

        if (response?.UserInfo is not { } info)
        {
            throw new XtreamProtocolException(
                "The provider returned JSON without a user_info object, so it is not a " +
                "recognisable Xtream account response.");
        }

        if (!info.Auth)
        {
            throw new XtreamAuthenticationException(
                "The provider rejected the credentials. Check the username and password, and " +
                "that the account has not expired.");
        }

        if (!info.IsUsable)
        {
            // auth:1 with a non-Active status is a live account that cannot stream: expired,
            // banned, or over its connection limit.
            throw new XtreamAuthenticationException(
                $"The account authenticated but its status is '{info.Status ?? "unknown"}' " +
                $"rather than Active, so it cannot stream.");
        }

        return info;
    }

    /// <summary>Streams the live categories.</summary>
    public IAsyncEnumerable<XtreamCategory> GetLiveCategoriesAsync(CancellationToken cancellationToken)
        => StreamAsync<XtreamCategory>("get_live_categories", cancellationToken);

    /// <summary>Streams the VOD categories.</summary>
    public IAsyncEnumerable<XtreamCategory> GetVodCategoriesAsync(CancellationToken cancellationToken)
        => StreamAsync<XtreamCategory>("get_vod_categories", cancellationToken);

    /// <summary>Streams the series categories.</summary>
    public IAsyncEnumerable<XtreamCategory> GetSeriesCategoriesAsync(CancellationToken cancellationToken)
        => StreamAsync<XtreamCategory>("get_series_categories", cancellationToken);

    /// <summary>Streams the VOD catalogue.</summary>
    public IAsyncEnumerable<XtreamVodStream> GetVodStreamsAsync(CancellationToken cancellationToken)
        => StreamAsync<XtreamVodStream>("get_vod_streams", cancellationToken);

    /// <summary>
    /// Streams the series listing. 49,748 on the reference provider, 75MB.
    /// </summary>
    /// <remarks>
    /// Seasons arrive inline; episodes do not. Fetching episodes needs one
    /// <c>get_series_info</c> call per series, so they are loaded when a series is opened
    /// rather than during sync.
    /// </remarks>
    public IAsyncEnumerable<XtreamSeries> GetSeriesAsync(CancellationToken cancellationToken)
        => StreamAsync<XtreamSeries>("get_series", cancellationToken);

    /// <summary>Streams the live channels. 28,285 on the reference provider.</summary>
    public IAsyncEnumerable<XtreamLiveStream> GetLiveStreamsAsync(CancellationToken cancellationToken)
        => StreamAsync<XtreamLiveStream>("get_live_streams", cancellationToken);

    /// <summary>
    /// Streams a JSON array from an endpoint without materialising the response body.
    /// </summary>
    private async IAsyncEnumerable<T> StreamAsync<T>(
        string action,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = _credentials.BuildApiUrl(action);

        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        // Buffered so the whole body can be inspected on failure. Streaming means the first
        // sign of an HTML error page is a JsonException part-way through enumeration, which
        // would otherwise surface as a truncated sync rather than an error.
        IAsyncEnumerator<T?> enumerator;
        try
        {
            enumerator = JsonSerializer
                .DeserializeAsyncEnumerable<T>(stream, XtreamJson.Options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new XtreamProtocolException(
                $"The provider returned a non-JSON response for '{action}'.", exception);
        }

        try
        {
            while (true)
            {
                T? item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    item = enumerator.Current;
                }
                catch (JsonException exception)
                {
                    throw new XtreamProtocolException(
                        $"The provider's response for '{action}' was not a valid JSON array. " +
                        $"This usually means an HTML error page was served with a 200 status.",
                        exception);
                }

                if (item is not null)
                {
                    yield return item;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string> GetStringAsync(string? action, CancellationToken cancellationToken)
    {
        var url = _credentials.BuildApiUrl(action);
        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new XtreamProtocolException(
                "The provider returned an empty response where an account payload was expected.");
        }

        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Scrubbed: the URL carries the credentials, and this message reaches logs.
            throw new XtreamProtocolException(
                $"Could not reach the provider: {Scrub(exception.Message)}", exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new XtreamProtocolException(
                $"The provider returned HTTP {status}. Note that a 200 does not imply success " +
                $"either; Xtream panels report failures in the payload.");
        }

        return response;
    }

    private string Scrub(string message)
        => CredentialScrubber.ScrubSecrets(CredentialScrubber.Scrub(message), _credentials.Secrets);
}
