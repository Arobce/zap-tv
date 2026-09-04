using System.Diagnostics;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;

namespace Iptv.Harness;

/// <summary>
/// Console harness for timing ingest and running playback smoke tests.
/// </summary>
/// <remarks>
/// Credentials are read from <c>.local/provider.env</c>, which is gitignored. They are
/// never printed: every URL and error message goes through <see cref="CredentialScrubber"/>
/// on the way out, because harness output ends up pasted into issues and commit messages.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "help";

        try
        {
            return command switch
            {
                "sync" => await SyncAsync(CancellationToken.None).ConfigureAwait(false),
                _ => Help(),
            };
        }
        catch (XtreamAuthenticationException exception)
        {
            Console.Error.WriteLine($"Authentication failed: {exception.Message}");
            return 2;
        }
        catch (XtreamProtocolException exception)
        {
            Console.Error.WriteLine($"Provider error: {exception.Message}");
            return 3;
        }
    }

    private static int Help()
    {
        Console.WriteLine("Iptv.Harness commands:");
        Console.WriteLine("  sync    Full live-channel sync against the provider in .local/provider.env");
        return 1;
    }

    private static async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        if (LoadCredentials() is not { } credentials)
        {
            Console.Error.WriteLine(
                "No .local/provider.env found. Expected XTREAM_HOST, XTREAM_USER, XTREAM_PASS.");
            return 1;
        }

        // One handler for the process lifetime, per the PRD. The default .NET User-Agent
        // is rejected by some panels.
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IptvPlayer/0.1");

        var client = new XtreamClient(http, credentials);

        var total = Stopwatch.StartNew();

        Console.WriteLine("== account ==");
        var account = await client.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  status           {account.Status}");
        Console.WriteLine($"  max_connections  {account.MaxConnections}");
        Console.WriteLine($"  active_cons      {account.ActiveConnections}");
        if (account.ExpiresAtUnix is { } expiry)
        {
            Console.WriteLine($"  expires          {DateTimeOffset.FromUnixTimeSeconds(expiry):yyyy-MM-dd}");
        }

        if (account.MaxConnections == 1)
        {
            // Phase 7 depends on this: two mpv handles consume two connections.
            Console.WriteLine("  NOTE: single connection - channel-change prebuffering must stay disabled.");
        }

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "harness.db");

        var factory = new SqliteConnectionFactory(databasePath);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        await SqliteFeatures.EnsureFts5AvailableAsync(connection, cancellationToken).ConfigureAwait(false);
        var providerId = await EnsureProviderAsync(connection, credentials, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== fetch ==");
        var fetch = Stopwatch.StartNew();
        var records = new List<StreamRecord>(capacity: 32_000);
        await foreach (var stream in client.GetLiveStreamsAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(StreamMapper.FromXtreamLive(stream, providerId, credentials));
        }

        fetch.Stop();
        Console.WriteLine($"  {records.Count:N0} live streams in {fetch.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"  separators       {records.Count(r => r.IsSeparator):N0}");
        Console.WriteLine($"  no tvg_id        {records.Count(r => r.TvgId is null):N0}");
        Console.WriteLine($"  with catchup     {records.Count(r => r.CatchupKind is not null):N0}");

        Console.WriteLine();
        Console.WriteLine("== merge ==");
        var write = Stopwatch.StartNew();
        var summary = await ProviderSync
            .SyncAsync(connection, providerId, records, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await ProviderSync.RefreshChannelsAsync(connection, cancellationToken).ConfigureAwait(false);
        write.Stop();

        Console.WriteLine($"  added {summary.Added:N0}  updated {summary.Updated:N0}  " +
                          $"reactivated {summary.Reactivated:N0}  deactivated {summary.Deactivated:N0}  " +
                          $"pruned {summary.Pruned:N0}");
        Console.WriteLine($"  wrote in {write.ElapsedMilliseconds:N0}ms");
        Console.WriteLine($"  channels         {await ScalarAsync(connection, "SELECT count(*) FROM channels"):N0}");
        Console.WriteLine($"  distinct keys    {await ScalarAsync(connection, "SELECT count(DISTINCT channel_key) FROM streams"):N0}");

        total.Stop();
        Console.WriteLine();
        Console.WriteLine($"total {total.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"database {CredentialScrubber.Scrub(databasePath)}");
        return 0;
    }

    private static async Task<int> EnsureProviderAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        XtreamCredentials credentials,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // Credentials are not written here. Persisting them is Phase 9's job and requires
        // DPAPI encryption; the harness holds them in memory for the run only.
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url)
            VALUES (1, 'harness', 'xtream', @base_url)
            ON CONFLICT(id) DO UPDATE SET base_url = excluded.base_url;
            SELECT 1;
            """;
        command.Parameters.AddWithValue("@base_url", credentials.BaseUrl.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private static async Task<long> ScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>Reads credentials from the gitignored local env file.</summary>
    private static XtreamCredentials? LoadCredentials()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".local", "provider.env");
            if (File.Exists(candidate))
            {
                var values = File.ReadAllLines(candidate)
                    .Where(line => line.Contains('=', StringComparison.Ordinal) &&
                                   !line.StartsWith('#'))
                    .Select(line => line.Split('=', 2))
                    .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

                if (values.TryGetValue("XTREAM_HOST", out var host) &&
                    values.TryGetValue("XTREAM_USER", out var user) &&
                    values.TryGetValue("XTREAM_PASS", out var pass))
                {
                    return new XtreamCredentials(new Uri(host), user, pass);
                }
            }

            directory = directory.Parent;
        }

        return null;
    }
}
