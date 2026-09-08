using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Epg;

/// <summary>What a refresh did, or why it did nothing.</summary>
public sealed record EpgRefreshReport
{
    public required bool Refreshed { get; init; }

    /// <summary>One line, safe to show. Always says why, including when skipped.</summary>
    public required string Summary { get; init; }

    public EpgIngestResult? Ingest { get; init; }

    public EpgCoverageReport? Coverage { get; init; }
}

/// <summary>
/// Refetches the guide when it is running out.
/// </summary>
/// <remarks>
/// The download is the expensive part — 64MB on the reference provider — so the decision
/// to make it is taken by <see cref="EpgRefreshPolicy"/> before anything is fetched, and
/// the attempt is recorded before rather than after, so a provider that is down is not
/// retried on every launch.
/// </remarks>
public static class EpgRefreshService
{
    /// <summary>Refreshes if the policy says to.</summary>
    /// <param name="download">
    /// Fetches the XMLTV document.
    /// </param>
    /// <remarks>
    /// Injected so this can be tested without a 64MB download, and so the caller owns the
    /// HttpClient rather than this creating one per refresh.
    /// </remarks>
    public static async Task<EpgRefreshReport> RefreshIfDueAsync(
        SqliteConnection connection,
        Func<Uri, CancellationToken, Task<Stream>> download,
        XtreamCredentials credentials,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(credentials);

        var decision = await EpgRefreshPolicy.DecideAsync(connection, now, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.ShouldRefresh)
        {
            return new EpgRefreshReport { Refreshed = false, Summary = $"Guide: {decision.Reason}." };
        }

        // Before the download. A refresh that fails halfway, times out, or is cancelled by
        // the window closing must still count as an attempt.
        await EpgRefreshPolicy.RecordAttemptAsync(connection, now, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using var guide = await download(credentials.BuildEpgUrl(), cancellationToken)
                .ConfigureAwait(false);

            var ingest = await EpgIngest.IngestAsync(
                connection, guide, new EpgIngestOptions(), now, cancellationToken).ConfigureAwait(false);

            // Matching immediately after: an ingested guide nothing points at is invisible,
            // and the two have always been run together.
            var coverage = await EpgMatcher.MatchAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            return new EpgRefreshReport
            {
                Refreshed = true,
                Summary =
                    $"Guide refreshed: {ingest.Programmes:N0} programmes, " +
                    $"{coverage.Matched:N0} channels mapped ({coverage.LibraryCoverage:P1}).",
                Ingest = ingest,
                Coverage = coverage,
            };
        }
        catch (Exception exception)
        {
            // Never fatal. A stale guide is worse than a fresh one and far better than an
            // app that will not start because a provider is down.
            return new EpgRefreshReport
            {
                Refreshed = false,
                Summary = $"Guide refresh failed: {CredentialScrubber.Scrub(exception.Message)}",
            };
        }
    }

    /// <summary>Refreshes using the first enabled provider that has stored credentials.</summary>
    /// <remarks>
    /// One guide, not one per provider. The XMLTV documents overlap heavily — they are
    /// keyed on the same broadcaster ids — and ingest replaces rather than merges, so a
    /// second provider's guide would simply overwrite the first.
    /// </remarks>
    public static async Task<EpgRefreshReport> RefreshIfDueAsync(
        SqliteConnection connection,
        Func<Uri, CancellationToken, Task<Stream>> download,
        ISecretProtector protector,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(protector);

        var providers = await ProviderRepository.ListAsync(connection, protector, cancellationToken)
            .ConfigureAwait(false);

        foreach (var provider in providers.Where(p => p is { Enabled: true, HasCredentials: true }))
        {
            var credentials = await ProviderCredentialStore
                .LoadAsync(connection, provider.Id, protector, cancellationToken)
                .ConfigureAwait(false);

            if (credentials is not null)
            {
                return await RefreshIfDueAsync(connection, download, credentials, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new EpgRefreshReport
        {
            Refreshed = false,
            Summary = "Guide: no enabled provider with stored credentials.",
        };
    }
}
