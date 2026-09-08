using System.Diagnostics;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Harness;

/// <summary>
/// Fetches one series' episodes end to end, without the UI.
/// </summary>
/// <remarks>
/// Written because "clicking a series does nothing" has at least four causes — no stored
/// credentials, an unparseable provider series id, a provider error, or a UI wiring fault
/// — and the app reported none of them distinctly. This isolates everything below the UI.
/// </remarks>
internal static class EpisodeProbe
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var search = args.Length > 1 ? args[1] : null;

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        await using var connection = await new SqliteConnectionFactory(databasePath)
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        Console.WriteLine("== credentials ==");
        var credentials = await ProviderCredentialStore
            .LoadAsync(connection, 1, new DpapiSecretProtector(), cancellationToken)
            .ConfigureAwait(false);

        if (credentials is null)
        {
            Console.Error.WriteLine("  none stored for provider 1. Run 'sync' or 'categories' first.");
            return 1;
        }

        Console.WriteLine($"  loaded, host {credentials.BaseUrl}");

        var series = await LibraryRepository.GetSeriesAsync(
            connection,
            new CatalogueQuery { Search = search, Limit = 1 },
            cancellationToken).ConfigureAwait(false);

        if (series.Count == 0)
        {
            Console.Error.WriteLine($"  no series matching '{search ?? "(any)"}'.");
            return 1;
        }

        var chosen = series[0];
        Console.WriteLine();
        Console.WriteLine("== series ==");
        Console.WriteLine($"  {chosen.Title}");
        Console.WriteLine($"  key      {chosen.Key}");
        Console.WriteLine($"  row id   {chosen.SeriesRowId}");

        var info = await EpisodeSync
            .GetFetchInfoAsync(connection, chosen.SeriesRowId, cancellationToken)
            .ConfigureAwait(false);

        if (info is null)
        {
            Console.Error.WriteLine(
                "  no fetch info: the row id resolves to nothing, or its provider is disabled, " +
                "or provider_series_id is not a number.");
            return 1;
        }

        Console.WriteLine($"  provider {info.ProviderId}, series_id {info.ProviderSeriesId}");

        using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ZapTV/0.1");

        Console.WriteLine();
        Console.WriteLine("== fetch ==");
        var stopwatch = Stopwatch.StartNew();

        var response = await new XtreamClient(http, credentials)
            .GetSeriesInfoAsync(info.ProviderSeriesId, cancellationToken)
            .ConfigureAwait(false);

        var episodes = EpisodeSync.Map(response, credentials);
        stopwatch.Stop();

        Console.WriteLine($"  {response.Episodes.Count} seasons, {episodes.Count} episodes " +
                          $"in {stopwatch.ElapsedMilliseconds:N0}ms");

        foreach (var episode in episodes.Take(8))
        {
            Console.WriteLine($"    S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}  {episode.Title}");
        }

        var written = await EpisodeSync.ReplaceAsync(
            connection, info.ProviderId, chosen.SeriesRowId, episodes,
            DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"  stored {written:N0}");
        return 0;
    }
}
