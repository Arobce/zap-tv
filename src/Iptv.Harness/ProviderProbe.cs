using Iptv.Core.Xtream;

namespace Iptv.Harness;

/// <summary>
/// Asks a host whether an account works on it.
/// </summary>
/// <remarks>
/// <para>
/// Written to answer one question: the two Phase 8 exit criteria that are still unproven —
/// cross-provider failover and surviving a firewall kill — both need a second provider, and
/// there has only ever been one set of credentials. If the second host is a mirror serving
/// the same account, that is a second provider and the criteria become testable. If it is a
/// separate subscription, nothing here can proceed without its own credentials.
/// </para>
/// <para>
/// Nothing is written. This is a question, not a sync, and a probe that enrolled a provider
/// as a side effect would be a poor thing to run against a host you were unsure of.
/// </para>
/// </remarks>
internal static class ProviderProbe
{
    internal static async Task<int> RunAsync(
        string[] args,
        XtreamCredentials primary,
        CancellationToken cancellationToken)
    {
        var hosts = new List<Uri> { primary.BaseUrl };

        for (var index = 1; index < args.Length; index++)
        {
            if (Uri.TryCreate(args[index], UriKind.Absolute, out var host))
            {
                hosts.Add(host);
            }
            else
            {
                Console.Error.WriteLine($"Not an absolute URL: {args[index]}");
                return 1;
            }
        }

        if (hosts.Count == 1)
        {
            Console.WriteLine("Probing the configured host only. Pass others as arguments:");
            Console.WriteLine("  harness probe http://second.example http://third.example");
            Console.WriteLine();
        }

        var reachable = 0;

        foreach (var host in hosts)
        {
            // Host only. The credentials travel in the query string of every Xtream call,
            // so the URL itself is the thing that must never be printed.
            var label = host.Host;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZapTV/0.1");

            var credentials = new XtreamCredentials(host, primary.Username, primary.Password);
            var client = new XtreamClient(http, credentials);

            try
            {
                var account = await client.GetAccountInfoAsync(cancellationToken)
                    .ConfigureAwait(false);

                var expires = account.ExpiresAtUnix is { } unix
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd")
                    : "never";

                Console.WriteLine(
                    $"{label,-28} ok   status={account.Status ?? "?"} " +
                    $"connections={account.MaxConnections} " +
                    $"active={account.ActiveConnections} expires={expires}");

                reachable++;
            }
            catch (XtreamAuthenticationException)
            {
                // Reached, and said no. A different subscription, not a mirror.
                Console.WriteLine($"{label,-28} reachable, but this account is not valid there");
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"{label,-28} unreachable: {CredentialScrubber.Scrub(exception.Message)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{reachable} of {hosts.Count} host(s) accept this account.");

        if (reachable > 1)
        {
            Console.WriteLine(
                "More than one means the second can be enrolled as a real provider, which is " +
                "what the cross-provider failover and firewall-kill criteria need.");
        }

        return 0;
    }
}
