namespace Iptv.Core.Xtream;

/// <summary>
/// A provider's base URL and account credentials, and the stream URLs built from them.
/// </summary>
/// <remarks>
/// Xtream puts the credentials in the stream path rather than a header, so every stream
/// URL is a bearer token. Anything that logs, hashes, exports or displays one must pass it
/// through <see cref="CredentialScrubber"/> first.
/// </remarks>
public sealed class XtreamCredentials
{
    private readonly string _baseUrl;
    private readonly string _escapedUsername;
    private readonly string _escapedPassword;

    public XtreamCredentials(Uri baseUrl, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        BaseUrl = baseUrl;
        Username = username;
        Password = password;

        // Trimmed so a base URL with a trailing slash does not produce "//live".
        _baseUrl = baseUrl.ToString().TrimEnd('/');

        // Escaped because a '/' or space in a credential would otherwise shift the stream
        // id into the wrong path segment, producing a 404 that reads as a dead channel.
        _escapedUsername = Uri.EscapeDataString(username);
        _escapedPassword = Uri.EscapeDataString(password);
    }

    public Uri BaseUrl { get; }

    public string Username { get; }

    public string Password { get; }

    /// <summary>The API endpoint for an action, or for account info when null.</summary>
    public Uri BuildApiUrl(string? action)
    {
        var url = $"{_baseUrl}/player_api.php?username={_escapedUsername}&password={_escapedPassword}";
        return new Uri(string.IsNullOrEmpty(action) ? url : $"{url}&action={Uri.EscapeDataString(action)}");
    }

    /// <summary>
    /// The playback URL for a live channel.
    /// </summary>
    /// <remarks>
    /// <c>.ts</c> rather than <c>.m3u8</c>: HLS adds segment latency, which works directly
    /// against the sub-second channel change target.
    /// </remarks>
    public Uri BuildLiveUrl(long streamId)
        => new($"{_baseUrl}/live/{_escapedUsername}/{_escapedPassword}/{streamId}.ts");

    /// <summary>The playback URL for a VOD item.</summary>
    public Uri BuildVodUrl(long streamId, string containerExtension)
        => new($"{_baseUrl}/movie/{_escapedUsername}/{_escapedPassword}/{streamId}.{Normalize(containerExtension)}");

    /// <summary>The playback URL for a series episode.</summary>
    public Uri BuildSeriesUrl(long episodeId, string containerExtension)
        => new($"{_baseUrl}/series/{_escapedUsername}/{_escapedPassword}/{episodeId}.{Normalize(containerExtension)}");

    /// <summary>The values that must never appear in a log or export.</summary>
    public IReadOnlyList<string> Secrets => [Username, Password, _escapedUsername, _escapedPassword];

    private static string Normalize(string containerExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerExtension);

        // Panels report the container both as "mp4" and ".mp4".
        return containerExtension.TrimStart('.');
    }
}
