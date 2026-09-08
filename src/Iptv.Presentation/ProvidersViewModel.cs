using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;

namespace Iptv.Presentation;

/// <summary>What testing a provider's credentials found out.</summary>
/// <remarks>
/// The PRD is specific that Test should explain rather than pass or fail: it is the
/// user's first impression of whether the app works, and "failed" tells them nothing they
/// can act on.
/// </remarks>
public sealed record ProviderTestResult
{
    public required bool Ok { get; init; }

    /// <summary>One line, safe to show. Credentials are already scrubbed out.</summary>
    public required string Message { get; init; }

    public int? MaxConnections { get; init; }

    public string? Status { get; init; }

    public DateTimeOffset? Expires { get; init; }
}

/// <summary>
/// The Providers view: what is configured, and adding, testing and syncing one.
/// </summary>
/// <remarks>
/// The client is supplied rather than constructed, so tests drive the real
/// <see cref="XtreamClient"/> over a fake message handler. An interface here would let the
/// tests pass against a stub while the actual JSON handling stayed unexercised, and that
/// handling is where every provider bug so far has been.
/// </remarks>
public sealed class ProvidersViewModel
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ISecretProtector _protector;
    private readonly Func<XtreamCredentials, XtreamClient> _clientFor;

    public ProvidersViewModel(
        SqliteConnectionFactory factory,
        ISecretProtector protector,
        Func<XtreamCredentials, XtreamClient> clientFor)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clientFor);

        _factory = factory;
        _protector = protector;
        _clientFor = clientFor;
    }

    /// <summary>Lists the configured providers, in failover order.</summary>
    public async Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        return await ProviderRepository.ListAsync(connection, _protector, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Checks credentials against the provider and reports what it learned.
    /// </summary>
    /// <remarks>
    /// Stores nothing. Testing before adding is the point: a provider row whose credentials
    /// have never worked is worse than no row, because every later failure looks like a
    /// different problem.
    /// </remarks>
    public async Task<ProviderTestResult> TestAsync(
        XtreamCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        try
        {
            var account = await _clientFor(credentials)
                .GetAccountInfoAsync(cancellationToken)
                .ConfigureAwait(false);

            var expires = account.ExpiresAtUnix is { } unix
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : (DateTimeOffset?)null;

            var detail = expires is { } date
                ? $"expires {date:yyyy-MM-dd}"
                : "no expiry given";

            // The connection limit is the number that changes how the app behaves, so it
            // is said plainly rather than buried: on a single-connection account channel
            // changes are rate limited and prebuffering is impossible.
            var connections = account.MaxConnections == 1
                ? "1 connection, so channel changes are paced"
                : $"{account.MaxConnections} connections";

            return new ProviderTestResult
            {
                Ok = true,
                Message = $"Connected. {account.Status ?? "Active"}, {connections}, {detail}.",
                MaxConnections = account.MaxConnections,
                Status = account.Status,
                Expires = expires,
            };
        }
        catch (XtreamAuthenticationException exception)
        {
            // Separated from a protocol error because the fix differs: this one is the
            // username, the password or an expired subscription.
            return new ProviderTestResult { Ok = false, Message = exception.Message };
        }
        catch (XtreamProtocolException exception)
        {
            return new ProviderTestResult { Ok = false, Message = exception.Message };
        }
        catch (Exception exception)
        {
            // Scrubbed: the URL carries the credentials, and this reaches the screen.
            return new ProviderTestResult
            {
                Ok = false,
                Message = CredentialScrubber.Scrub(exception.Message),
            };
        }
    }

    /// <summary>Adds a provider, or updates the one already on that host.</summary>
    public async Task<int> AddAsync(
        string name,
        XtreamCredentials credentials,
        int priority,
        CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);

        return await ProviderRepository
            .UpsertAsync(connection, name, credentials, _protector, cancellationToken, priority)
            .ConfigureAwait(false);
    }

    /// <summary>Refreshes one provider's whole library.</summary>
    /// <remarks>
    /// Reads the credentials back rather than taking them, so a sync started from a list
    /// row works without asking for the password again — which is the normal case, since
    /// the provider was added once and synced many times.
    /// </remarks>
    public async Task<LibrarySyncReport> SyncAsync(
        int providerId,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await Migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        await SqliteFeatures.EnsureFts5AvailableAsync(connection, cancellationToken).ConfigureAwait(false);

        var credentials = await ProviderCredentialStore
            .LoadAsync(connection, providerId, _protector, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "This provider has no readable credentials on this machine. Re-enter them.");

        var client = _clientFor(credentials);

        // Read first, and recorded whatever happens next: the connection limit governs the
        // rate limiter, and a sync that fails halfway should still have taught the app how
        // hard it may push.
        var account = await client.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);
        await ProviderRepository
            .RecordAccountAsync(connection, providerId, account.MaxConnections, null, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var report = await LibrarySync
            .RunAsync(connection, client, credentials, providerId, now, progress, cancellationToken)
            .ConfigureAwait(false);

        await ProviderRepository
            .RecordAccountAsync(connection, providerId, account.MaxConnections, now, cancellationToken)
            .ConfigureAwait(false);

        return report;
    }

    public async Task SetEnabledAsync(int providerId, bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ProviderRepository.SetEnabledAsync(connection, providerId, enabled, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(int providerId, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ProviderRepository.DeleteAsync(connection, providerId, cancellationToken)
            .ConfigureAwait(false);
    }
}
