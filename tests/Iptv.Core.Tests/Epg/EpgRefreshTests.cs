using System.Text;
using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Core.Tests.Data;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// When the guide is refetched, and when it deliberately is not.
/// </summary>
/// <remarks>
/// The reference provider publishes about 24 hours ahead, so an unrefreshed guide is empty
/// for anyone who opens the app two days later — 10 of 3,450 channels, measured. The
/// download is 64MB, so the throttle matters as much as the trigger.
/// </remarks>
public sealed class EpgRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    private static readonly XtreamCredentials Credentials =
        new(new Uri("http://a.invalid"), "ACCT7X2", "SECRET99");

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    // --- the decision, without any I/O ---

    [Fact]
    public void No_guide_at_all_is_refreshed()
    {
        var decision = EpgRefreshPolicy.Decide(lastAttempt: null, guideEnd: null, Now);

        Assert.True(decision.ShouldRefresh);
        Assert.Contains("no guide", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expired_guide_is_refreshed()
    {
        var decision = EpgRefreshPolicy.Decide(null, Now.AddHours(-2), Now);

        Assert.True(decision.ShouldRefresh);
        Assert.Contains("expired", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guide_running_out_this_evening_is_refreshed()
    {
        // Waiting until it has actually expired means the grid is empty at the moment
        // somebody opens it, which is the failure this exists to prevent.
        var decision = EpgRefreshPolicy.Decide(null, Now.AddHours(3), Now);

        Assert.True(decision.ShouldRefresh);
        Assert.Contains("runs out", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guide_with_a_day_left_is_not_refreshed()
    {
        var decision = EpgRefreshPolicy.Decide(null, Now.AddHours(24), Now);

        Assert.False(decision.ShouldRefresh);
        Assert.Contains("covers another", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recent_attempt_blocks_a_refresh_however_stale_the_guide()
    {
        // The guard against hammering. A provider whose guide is permanently short would
        // otherwise be refetched every launch, downloading 64MB to learn the same thing.
        var decision = EpgRefreshPolicy.Decide(
            lastAttempt: Now.AddHours(-1),
            guideEnd: null,
            Now);

        Assert.False(decision.ShouldRefresh);
        Assert.Contains("next attempt in", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_attempt_does_not_block_a_refresh()
    {
        var decision = EpgRefreshPolicy.Decide(Now - EpgRefreshPolicy.MinimumInterval, null, Now);

        Assert.True(decision.ShouldRefresh);
    }

    [Fact]
    public void The_reason_is_given_even_when_refusing()
    {
        // Both branches explain themselves. "Nothing happened" with no reason is what
        // makes an automatic background job impossible to trust.
        foreach (var decision in new[]
        {
            EpgRefreshPolicy.Decide(Now.AddMinutes(-5), null, Now),
            EpgRefreshPolicy.Decide(null, Now.AddDays(2), Now),
        })
        {
            Assert.False(decision.ShouldRefresh);
            Assert.NotEmpty(decision.Reason);
        }
    }

    // --- the stored state ---

    [Fact]
    public async Task An_attempt_is_recorded_and_read_back()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        Assert.Null(await EpgRefreshPolicy.GetLastAttemptAsync(connection, CancellationToken.None));

        await EpgRefreshPolicy.RecordAttemptAsync(connection, Now, CancellationToken.None);

        Assert.Equal(Now, await EpgRefreshPolicy.GetLastAttemptAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task Recording_twice_keeps_the_later_time()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await EpgRefreshPolicy.RecordAttemptAsync(connection, Now.AddHours(-5), CancellationToken.None);
        await EpgRefreshPolicy.RecordAttemptAsync(connection, Now, CancellationToken.None);

        Assert.Equal(Now, await EpgRefreshPolicy.GetLastAttemptAsync(connection, CancellationToken.None));
    }

    // --- the service ---

    private const string Guide =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <tv>
          <channel id="bbc.one"><display-name>BBC One</display-name></channel>
          <programme start="20260908210000 +0000" stop="20260908220000 +0000" channel="bbc.one">
            <title>The News</title>
          </programme>
        </tv>
        """;

    private static Func<Uri, CancellationToken, Task<Stream>> Serving(string xml, Action<Uri>? seen = null)
        => (url, _) =>
        {
            seen?.Invoke(url);
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        };

    [Fact]
    public async Task A_due_refresh_ingests_and_matches()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var report = await EpgRefreshService.RefreshIfDueAsync(
            connection, Serving(Guide), Credentials, Now, CancellationToken.None);

        Assert.True(report.Refreshed);
        Assert.Equal(1, report.Ingest!.Programmes);

        // Matched in the same pass: an ingested guide nothing points at is invisible.
        Assert.NotNull(report.Coverage);
    }

    [Fact]
    public async Task A_refresh_that_is_not_due_downloads_nothing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await EpgRefreshPolicy.RecordAttemptAsync(connection, Now.AddMinutes(-10), CancellationToken.None);

        var downloaded = false;
        var report = await EpgRefreshService.RefreshIfDueAsync(
            connection,
            Serving(Guide, _ => downloaded = true),
            Credentials,
            Now,
            CancellationToken.None);

        Assert.False(report.Refreshed);
        Assert.False(downloaded);
    }

    [Fact]
    public async Task The_attempt_is_recorded_even_when_the_download_fails()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var report = await EpgRefreshService.RefreshIfDueAsync(
            connection,
            (_, _) => throw new HttpRequestException("the provider is down"),
            Credentials,
            Now,
            CancellationToken.None);

        Assert.False(report.Refreshed);

        // Otherwise a provider that is down is retried on every launch, which is exactly
        // when it is least helpful to be hammering it.
        Assert.Equal(Now, await EpgRefreshPolicy.GetLastAttemptAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_is_reported_rather_than_thrown()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // A stale guide is worse than a fresh one and far better than an app that will not
        // start because a provider is down.
        var report = await EpgRefreshService.RefreshIfDueAsync(
            connection,
            (_, _) => throw new HttpRequestException("connection refused"),
            Credentials,
            Now,
            CancellationToken.None);

        Assert.Contains("failed", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failure_never_leaks_the_credentials()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var report = await EpgRefreshService.RefreshIfDueAsync(
            connection,
            (_, _) => throw new HttpRequestException(
                "failed to fetch http://a.invalid/xmltv.php?username=ACCT7X2&password=SECRET99"),
            Credentials,
            Now,
            CancellationToken.None);

        Assert.DoesNotContain("ACCT7X2", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET99", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_guide_leaves_the_previous_one_alone()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await EpgRefreshService.RefreshIfDueAsync(
            connection, Serving(Guide), Credentials, Now, CancellationToken.None);

        // Far enough ahead that the throttle does not block it.
        var later = Now.AddHours(6);

        var report = await EpgRefreshService.RefreshIfDueAsync(
            connection, Serving("<tv><this is not"), Credentials, later, CancellationToken.None);

        Assert.False(report.Refreshed);

        // Ingest stages and swaps, so a failed parse must not have emptied the guide.
        var (_, end) = await EpgGridRepository.GetCoverageAsync(connection, CancellationToken.None);
        Assert.NotNull(end);
    }

    [Fact]
    public async Task The_guide_is_fetched_from_the_xmltv_endpoint()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        Uri? requested = null;

        await EpgRefreshService.RefreshIfDueAsync(
            connection, Serving(Guide, url => requested = url), Credentials, Now, CancellationToken.None);

        // A separate endpoint, not an action on player_api.php.
        Assert.Contains("xmltv.php", requested!.ToString(), StringComparison.Ordinal);
    }
}
