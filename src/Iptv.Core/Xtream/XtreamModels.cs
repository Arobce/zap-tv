using System.Text.Json;
using System.Text.Json.Serialization;
using Iptv.Core.Xtream.Json;

namespace Iptv.Core.Xtream;

/// <summary>Serializer options carrying the tolerant converters.</summary>
/// <remarks>
/// Every numeric, boolean and id field goes through a converter that accepts both a JSON
/// number and a JSON string, because panels are inconsistent within a single object.
/// </remarks>
public static class XtreamJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new FlexibleInt32Converter(),
            new FlexibleNullableInt32Converter(),
            new FlexibleInt64Converter(),
            new FlexibleNullableInt64Converter(),
            new FlexibleBooleanConverter(),
            new FlexibleStringConverter(),
        },
    };
}

/// <summary>Top-level response for the credentials-only <c>player_api.php</c> call.</summary>
public sealed record XtreamAccountResponse
{
    [JsonPropertyName("user_info")]
    public XtreamUserInfo? UserInfo { get; init; }

    [JsonPropertyName("server_info")]
    public XtreamServerInfo? ServerInfo { get; init; }
}

/// <summary>Account state and limits.</summary>
public sealed record XtreamUserInfo
{
    /// <summary>Arrives as the number 1 on the reference provider, as "1" on others.</summary>
    [JsonPropertyName("auth")]
    public bool Auth { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// Concurrent streams the account permits.
    /// </summary>
    /// <remarks>
    /// Load-bearing for Phase 7: two mpv handles consume two connections, so prebuffering
    /// must be disabled automatically when this is 1. The reference provider reports 1.
    /// </remarks>
    [JsonPropertyName("max_connections")]
    public int MaxConnections { get; init; }

    [JsonPropertyName("active_cons")]
    public int ActiveConnections { get; init; }

    [JsonPropertyName("is_trial")]
    public bool IsTrial { get; init; }

    /// <summary>Unix seconds. Sent as a string.</summary>
    [JsonPropertyName("exp_date")]
    public long? ExpiresAtUnix { get; init; }

    [JsonPropertyName("created_at")]
    public long? CreatedAtUnix { get; init; }

    [JsonPropertyName("allowed_output_formats")]
    public IReadOnlyList<string> AllowedOutputFormats { get; init; } = [];

    /// <summary>Whether the account is usable right now.</summary>
    /// <remarks>
    /// <c>auth</c> alone is not enough: a panel returns <c>auth: 1</c> for an expired or
    /// banned account and reports the problem in <c>status</c>.
    /// </remarks>
    public bool IsUsable =>
        Auth && string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Panel host details, used to detect a redirect to a different edge.</summary>
public sealed record XtreamServerInfo
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("port")]
    public string? Port { get; init; }

    [JsonPropertyName("https_port")]
    public string? HttpsPort { get; init; }

    [JsonPropertyName("server_protocol")]
    public string? ServerProtocol { get; init; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }
}

/// <summary>A live, VOD or series category.</summary>
public sealed record XtreamCategory
{
    /// <summary>A string here and a number on some other endpoints.</summary>
    [JsonPropertyName("category_id")]
    public string? CategoryId { get; init; }

    [JsonPropertyName("category_name")]
    public string? CategoryName { get; init; }

    [JsonPropertyName("parent_id")]
    public int ParentId { get; init; }
}

/// <summary>One entry from <c>get_live_streams</c>.</summary>
public sealed record XtreamLiveStream
{
    [JsonPropertyName("num")]
    public int Num { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("stream_type")]
    public string? StreamType { get; init; }

    /// <summary>A JSON number, sitting beside <c>category_id</c> as a JSON string.</summary>
    [JsonPropertyName("stream_id")]
    public long StreamId { get; init; }

    [JsonPropertyName("stream_icon")]
    public string? StreamIcon { get; init; }

    /// <summary>
    /// Null for roughly seven channels in ten on the reference provider.
    /// </summary>
    /// <remarks>
    /// Deliberately nullable rather than defaulted to empty: "has no EPG id" and "has an
    /// empty EPG id" must stay distinguishable, because the Phase 4 coverage metric counts
    /// on the difference.
    /// </remarks>
    [JsonPropertyName("epg_channel_id")]
    public string? EpgChannelId { get; init; }

    /// <summary>Unix seconds, sent as a string.</summary>
    [JsonPropertyName("added")]
    public long? AddedUnix { get; init; }

    [JsonPropertyName("category_id")]
    public string? CategoryId { get; init; }

    /// <summary>Whether catchup/timeshift is available.</summary>
    [JsonPropertyName("tv_archive")]
    public bool TvArchive { get; init; }

    [JsonPropertyName("tv_archive_duration")]
    public int TvArchiveDuration { get; init; }

    /// <summary>Set when the panel wants the client to bypass URL construction.</summary>
    [JsonPropertyName("direct_source")]
    public string? DirectSource { get; init; }
}
