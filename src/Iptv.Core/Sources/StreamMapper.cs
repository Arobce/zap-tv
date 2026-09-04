using System.Globalization;
using Iptv.Core.Xtream;

namespace Iptv.Core.Sources;

/// <summary>
/// Turns a provider's payload into the row the database stores.
/// </summary>
/// <remarks>
/// Identity, quality, country and separator status are all derived here, once at ingest,
/// rather than on every read. Normalization runs across 28,285 entries per sync on the
/// reference provider, and the EPG grid queries these columns on every scroll frame.
/// </remarks>
public static class StreamMapper
{
    /// <summary>Maps a live channel from <c>get_live_streams</c>.</summary>
    public static StreamRecord FromXtreamLive(
        XtreamLiveStream stream,
        int providerId,
        XtreamCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(credentials);

        var title = stream.Name ?? string.Empty;
        var providerStreamId = stream.StreamId.ToString(CultureInfo.InvariantCulture);
        var normalized = ChannelNormalizer.Normalize(title);

        return new StreamRecord
        {
            ProviderId = providerId,
            ProviderStreamId = providerStreamId,
            Kind = StreamKind.Live,
            Title = title,
            NormalizedTitle = normalized,
            ChannelKey = DeriveKey(stream.EpgChannelId, normalized, providerId, providerStreamId),
            Url = ResolveUrl(stream, credentials),
            TvgId = stream.EpgChannelId,
            LogoUrl = stream.StreamIcon,
            CategoryId = stream.CategoryId,
            Container = "ts",
            Quality = ChannelNormalizer.ExtractQuality(title),
            Country = ChannelNormalizer.ExtractCountry(title),

            // The Xtream API exposes catchup only as availability plus a window; unlike
            // M3U there is no catchup-source template, so the URL is derived at playback
            // time from the stream id and the requested timestamp.
            CatchupKind = stream.TvArchive ? "default" : null,
            CatchupDays = stream.TvArchive ? stream.TvArchiveDuration : null,

            IsSeparator = StreamClassifier.IsSeparator(title),
        };
    }

    /// <summary>
    /// Resolves the playback URL, preferring a panel-supplied <c>direct_source</c>.
    /// </summary>
    /// <remarks>
    /// Some panels serve channels from a different edge and expect the client to use the
    /// URL they supply. The reference provider sends <c>""</c> on every entry, so an empty
    /// value must not be mistaken for an override.
    /// </remarks>
    private static string ResolveUrl(XtreamLiveStream stream, XtreamCredentials credentials)
        => string.IsNullOrWhiteSpace(stream.DirectSource)
            ? credentials.BuildLiveUrl(stream.StreamId).AbsoluteUri
            : stream.DirectSource;

    /// <summary>
    /// Derives <c>channel_key</c>, falling back to a provider-scoped key.
    /// </summary>
    /// <remarks>
    /// A title that normalizes to nothing and carries no <c>tvg_id</c> cannot be merged
    /// with anything. It still belongs to the user, so rather than dropping it - or giving
    /// every such entry the same bogus key and merging them into one phantom channel - it
    /// gets a key unique to this provider and stream.
    /// </remarks>
    private static string DeriveKey(
        string? tvgId,
        string normalizedTitle,
        int providerId,
        string providerStreamId)
        => ChannelIdentity.DeriveChannelKey(tvgId, normalizedTitle)
           ?? $"stream:{providerId.ToString(CultureInfo.InvariantCulture)}:{providerStreamId}";
}
