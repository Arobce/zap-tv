using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>What a sync is doing right now, for a progress display.</summary>
public sealed record SyncProgress
{
    /// <summary>A short phase name: categories, live, films, series.</summary>
    public required string Stage { get; init; }

    /// <summary>Human-readable detail, already counted.</summary>
    public required string Detail { get; init; }
}

/// <summary>What a completed sync did.</summary>
public sealed record LibrarySyncReport
{
    public required int LiveCategories { get; init; }

    public required int VodCategories { get; init; }

    public required SyncSummary Live { get; init; }

    public required SyncSummary Vod { get; init; }

    public required int Series { get; init; }

    public required TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Runs a full provider refresh: categories, live channels, films and series.
/// </summary>
/// <remarks>
/// <para>
/// This orchestration lived in the harness, which meant the app could browse a library it
/// had no way to populate — the whole reason setting ZapTV up still required a command
/// line.
/// </para>
/// <para>
/// Episodes are deliberately not fetched. <c>get_series_info</c> is one request per series
/// and the reference provider lists 49,783 of them; they are loaded when a series is
/// opened instead.
/// </para>
/// </remarks>
public static class LibrarySync
{
    /// <summary>Refreshes everything for one provider.</summary>
    /// <param name="progress">
    /// Reported per stage. A full sync on the reference provider moves a quarter of a
    /// million rows and takes minutes; without this the UI would have nothing to say for
    /// the duration.
    /// </param>
    public static async Task<LibrarySyncReport> RunAsync(
        SqliteConnection connection,
        XtreamClient client,
        XtreamCredentials credentials,
        int providerId,
        DateTimeOffset now,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(credentials);

        var total = System.Diagnostics.Stopwatch.StartNew();

        // Categories first, and cheap: a few hundred rows against the streams' hundreds of
        // thousands. Without them every stream carries a category id that names nothing.
        progress?.Report(new SyncProgress { Stage = "categories", Detail = "fetching..." });

        var live = await ReplaceCategoriesAsync(
            connection, client.GetLiveCategoriesAsync(cancellationToken),
            providerId, CategoryKind.Live, cancellationToken).ConfigureAwait(false);

        var vod = await ReplaceCategoriesAsync(
            connection, client.GetVodCategoriesAsync(cancellationToken),
            providerId, CategoryKind.Vod, cancellationToken).ConfigureAwait(false);

        progress?.Report(new SyncProgress
        {
            Stage = "categories",
            Detail = $"{live:N0} live, {vod:N0} film categories",
        });

        // Live.
        progress?.Report(new SyncProgress { Stage = "live", Detail = "fetching channels..." });

        var channels = new List<StreamRecord>(capacity: 32_000);
        await foreach (var stream in client.GetLiveStreamsAsync(cancellationToken).ConfigureAwait(false))
        {
            channels.Add(StreamMapper.FromXtreamLive(stream, providerId, credentials));
        }

        progress?.Report(new SyncProgress { Stage = "live", Detail = $"storing {channels.Count:N0}..." });

        var liveSummary = await ProviderSync
            .SyncAsync(connection, providerId, channels, now, cancellationToken)
            .ConfigureAwait(false);

        await ProviderSync.RefreshChannelsAsync(connection, cancellationToken).ConfigureAwait(false);

        // Films. A separate endpoint and a separate catalogue: deactivation is scoped per
        // kind, so syncing one cannot deactivate another.
        progress?.Report(new SyncProgress { Stage = "films", Detail = "fetching..." });

        var films = new List<StreamRecord>(capacity: 16_000);
        await foreach (var movie in client.GetVodStreamsAsync(cancellationToken).ConfigureAwait(false))
        {
            films.Add(StreamMapper.FromXtreamVod(movie, providerId, credentials));
        }

        progress?.Report(new SyncProgress { Stage = "films", Detail = $"storing {films.Count:N0}..." });

        var vodSummary = await ProviderSync
            .SyncAsync(connection, providerId, films, StreamKind.Vod, now, cancellationToken)
            .ConfigureAwait(false);

        // Series listings only.
        progress?.Report(new SyncProgress { Stage = "series", Detail = "fetching..." });

        var shows = new List<SeriesRecord>(capacity: 64_000);
        await foreach (var show in client.GetSeriesAsync(cancellationToken).ConfigureAwait(false))
        {
            shows.Add(SeriesMapper.FromXtream(show, providerId));
        }

        progress?.Report(new SyncProgress { Stage = "series", Detail = $"storing {shows.Count:N0}..." });
        await SeriesSync.SyncAsync(connection, providerId, shows, cancellationToken).ConfigureAwait(false);

        total.Stop();

        return new LibrarySyncReport
        {
            LiveCategories = live,
            VodCategories = vod,
            Live = liveSummary,
            Vod = vodSummary,
            Series = shows.Count,
            Elapsed = total.Elapsed,
        };
    }

    private static async Task<int> ReplaceCategoriesAsync(
        SqliteConnection connection,
        IAsyncEnumerable<XtreamCategory> source,
        int providerId,
        CategoryKind kind,
        CancellationToken cancellationToken)
    {
        var categories = new List<(string, string, int)>();
        await foreach (var category in source.ConfigureAwait(false))
        {
            categories.Add((
                category.CategoryId ?? string.Empty,
                category.CategoryName ?? string.Empty,
                category.ParentId));
        }

        return await CategoryRepository
            .ReplaceAsync(connection, providerId, kind, categories, cancellationToken)
            .ConfigureAwait(false);
    }
}
