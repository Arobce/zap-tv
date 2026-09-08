using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Iptv.Presentation;
using Microsoft.Data.Sqlite;

namespace Iptv.Presentation.Tests;

/// <summary>
/// What the app reports about itself.
/// </summary>
/// <remarks>
/// Every number here already existed somewhere and had nowhere to be seen. The work being
/// tested is not the arithmetic but the judgement: which readings mean something is wrong,
/// and which pairs of readings are misleading apart.
/// </remarks>
public sealed class DiagnosticsViewModelTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"zap-diag-{Guid.NewGuid():N}");

    private readonly SqliteConnectionFactory _factory;

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    public DiagnosticsViewModelTests()
        => _factory = new SqliteConnectionFactory(Path.Combine(_directory, "diag.db"), pooled: false);

    private sealed class FakeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();

        public byte[]? Unprotect(byte[] ciphertext) => ciphertext.Reverse().ToArray();
    }

    private DiagnosticsViewModel Model() => new(_factory, new FakeProtector());

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = await _factory.OpenAsync(CancellationToken.None);
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private async Task<IReadOnlyList<DiagnosticsRow>> SectionAsync(string title)
    {
        var sections = await Model().BuildAsync(Now, CancellationToken.None);
        return sections.Single(s => s.Title.StartsWith(title, StringComparison.Ordinal)).Rows;
    }

    private static async Task ProviderAsync(SqliteConnection connection, string name = "Main")
        => await ProviderRepository.UpsertAsync(
            connection,
            name,
            new XtreamCredentials(new Uri("http://a.invalid"), "ACCT7X2", "SECRET99"),
            new FakeProtector(),
            CancellationToken.None);

    private static async Task<long> StreamAsync(
        SqliteConnection connection,
        string key,
        string kind = "live")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO channels (channel_key, display_name) VALUES (@key, @key);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (1, @key, @kind, @key, @key, 'http://h/' || @key, @key, 1, 0, 0)
            RETURNING id;
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@kind", kind);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static async Task ProgrammeAsync(
        SqliteConnection connection,
        string key,
        DateTimeOffset start,
        DateTimeOffset stop)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO epg_map (channel_key, epg_channel_id, confidence, method, updated_utc)
            VALUES (@key, @key || '.epg', 1.0, 'tvg_id', 0);
            INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
            VALUES (@key || '.epg', @start, @stop, 'Programme');
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@start", start.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@stop", stop.ToUnixTimeSeconds());

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_install_reports_the_absences_as_warnings()
    {
        await using var connection = await OpenAsync();

        var guide = await SectionAsync("Guide");
        var providers = await SectionAsync("Providers");

        Assert.True(Assert.Single(guide).IsWarning);
        Assert.True(Assert.Single(providers).IsWarning);
    }

    [Fact]
    public async Task Live_channels_are_counted_as_the_list_counts_them()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);

        await StreamAsync(connection, "tvg:a");
        await StreamAsync(connection, "tvg:b");
        await StreamAsync(connection, "name:film", "vod");

        // Counting rows in channels once reported 118,763 above a list holding 20,479,
        // because VOD keys have channels rows too.
        var library = await SectionAsync("Library");

        Assert.Equal("2", library.Single(r => r.Label == "Live channels").Value);
        Assert.Equal("1", library.Single(r => r.Label == "Films").Value);
    }

    [Fact]
    public async Task Episodes_say_why_the_number_is_small()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);

        // Otherwise a near-zero count beside 49,783 series reads as a failed sync rather
        // than as the deliberate design it is.
        var library = await SectionAsync("Library");

        Assert.Contains("fetched on open", library.Single(r => r.Label == "Episodes fetched").Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_guide_running_out_is_flagged()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        await StreamAsync(connection, "tvg:a");
        await ProgrammeAsync(connection, "tvg:a", Now.AddHours(-1), Now.AddHours(2));

        var guide = await SectionAsync("Guide");
        var ahead = guide.Single(r => r.Label == "Reaches ahead");

        Assert.True(ahead.IsWarning);
        Assert.Contains("2.0h", ahead.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_healthy_guide_is_not_flagged()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        await StreamAsync(connection, "tvg:a");
        await ProgrammeAsync(connection, "tvg:a", Now, Now.AddHours(30));

        Assert.False((await SectionAsync("Guide")).Single(r => r.Label == "Reaches ahead").IsWarning);
    }

    [Fact]
    public async Task An_expired_guide_says_expired_rather_than_a_negative_number()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        await StreamAsync(connection, "tvg:a");
        await ProgrammeAsync(connection, "tvg:a", Now.AddDays(-2), Now.AddHours(-3));

        var ahead = (await SectionAsync("Guide")).Single(r => r.Label == "Reaches ahead");

        Assert.Equal("expired", ahead.Value);
        Assert.True(ahead.IsWarning);
    }

    [Fact]
    public async Task A_mapped_guide_with_nothing_on_air_is_flagged()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);

        for (var i = 0; i < 4; i++)
        {
            await StreamAsync(connection, $"tvg:{i}");

            // Mapped, and all in the past. This is exactly what a stale guide looks like,
            // and it is the case a coverage percentage alone reports as healthy.
            await ProgrammeAsync(connection, $"tvg:{i}", Now.AddDays(-2), Now.AddDays(-2).AddHours(1));
        }

        var guide = await SectionAsync("Guide");

        Assert.Equal("4", guide.Single(r => r.Label == "Channels mapped").Value);

        var onAir = guide.Single(r => r.Label == "On air right now");
        Assert.Equal("0", onAir.Value);
        Assert.True(onAir.IsWarning);
    }

    [Fact]
    public async Task A_provider_with_no_attempts_is_not_reported_as_failing()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        await StreamAsync(connection, "tvg:a");

        // No attempts is an absence of evidence, not a bad rate. Showing 0% would read as
        // broken.
        var provider = Assert.Single(await SectionAsync("Providers"));

        Assert.False(provider.IsWarning);
        Assert.Contains("no attempts", provider.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_failing_repeatedly_is_flagged()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        var stream = await StreamAsync(connection, "tvg:a");

        for (var i = 0; i < 8; i++)
        {
            await StreamHealthRepository.RecordAsync(
                connection,
                new HealthAttempt
                {
                    StreamId = stream,
                    Outcome = i < 6 ? PlaybackOutcome.Timeout : PlaybackOutcome.Ok,
                    TimeToFirstFrameMs = i < 6 ? null : 900,
                },
                Now.AddHours(-1),
                CancellationToken.None);
        }

        Assert.True(Assert.Single(await SectionAsync("Providers")).IsWarning);
    }

    [Fact]
    public async Task A_provider_with_one_bad_evening_is_not_flagged()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        var stream = await StreamAsync(connection, "tvg:a");

        // Two attempts, one failed. A 50% rate on a sample of two is noise, and flagging
        // it would make the warning meaningless.
        foreach (var outcome in new[] { PlaybackOutcome.Ok, PlaybackOutcome.Timeout })
        {
            await StreamHealthRepository.RecordAsync(
                connection,
                new HealthAttempt { StreamId = stream, Outcome = outcome },
                Now.AddHours(-1),
                CancellationToken.None);
        }

        Assert.False(Assert.Single(await SectionAsync("Providers")).IsWarning);
    }

    [Fact]
    public async Task Playback_is_grouped_by_outcome_with_failures_flagged()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        var stream = await StreamAsync(connection, "tvg:a");

        await StreamHealthRepository.RecordAsync(
            connection,
            new HealthAttempt { StreamId = stream, Outcome = PlaybackOutcome.Ok, TimeToFirstFrameMs = 800 },
            Now.AddHours(-1), CancellationToken.None);

        await StreamHealthRepository.RecordAsync(
            connection,
            new HealthAttempt { StreamId = stream, Outcome = PlaybackOutcome.Stall },
            Now.AddHours(-1), CancellationToken.None);

        var playback = await SectionAsync("Playback");

        Assert.False(playback.Single(r => r.Label == "ok").IsWarning);
        Assert.True(playback.Single(r => r.Label == "stall").IsWarning);
        Assert.Contains("800ms", playback.Single(r => r.Label == "ok").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Playback_with_no_history_says_so_rather_than_showing_nothing()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);

        Assert.Contains("none", Assert.Single(await SectionAsync("Playback")).Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_auto_refresh_decision_is_shown_with_its_reason()
    {
        await using var connection = await OpenAsync();
        await ProviderAsync(connection);
        await StreamAsync(connection, "tvg:a");
        await ProgrammeAsync(connection, "tvg:a", Now, Now.AddHours(30));
        await EpgRefreshPolicy.RecordAttemptAsync(connection, Now.AddMinutes(-30), CancellationToken.None);

        var guide = await SectionAsync("Guide");

        // A background job nobody can see the reasoning of is a background job nobody
        // trusts when the guide is empty.
        Assert.NotEmpty(guide.Single(r => r.Label == "Auto refresh").Value);
        Assert.Contains("30m ago", guide.Single(r => r.Label == "Last refresh attempt").Value,
            StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory is disposable either way.
        }
    }
}
