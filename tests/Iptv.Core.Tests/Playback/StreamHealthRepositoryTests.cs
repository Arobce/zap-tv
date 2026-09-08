using Iptv.Core.Data;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Playback;

/// <summary>
/// The failover read model and the attempt history it ranks candidates by.
/// </summary>
public sealed class StreamHealthRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await ExecuteAsync(connection,
            """
            INSERT INTO providers (id, name, kind, base_url, priority) VALUES
              (1, 'Primary',  'xtream', 'http://a.invalid', 0),
              (2, 'Fallback', 'xtream', 'http://b.invalid', 1),
              (3, 'Disabled', 'xtream', 'http://c.invalid', 0);
            UPDATE providers SET enabled = 0 WHERE id = 3;
            INSERT INTO channels (channel_key, display_name) VALUES ('tvg:espn.us', 'ESPN');
            """);

        return connection;
    }

    private static async Task<long> AddStreamAsync(
        SqliteConnection connection,
        long providerId,
        string title,
        string channelKey = "tvg:espn.us",
        string? quality = "Hd",
        bool active = true,
        bool separator = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, quality, is_active, is_separator, last_seen_utc)
            VALUES (@provider, @sid, 'live', @title, @normalized,
                    'http://host.invalid/live/u/p/' || @sid || '.ts', @key, @quality,
                    @active, @separator, 0)
            RETURNING id;
            """;

        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@normalized", ChannelNormalizer.Normalize(title));
        command.Parameters.AddWithValue("@key", channelKey);
        command.Parameters.AddWithValue("@quality", (object?)quality ?? DBNull.Value);
        command.Parameters.AddWithValue("@active", active ? 1 : 0);
        command.Parameters.AddWithValue("@separator", separator ? 1 : 0);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task RecordAsync(
        SqliteConnection connection,
        long streamId,
        PlaybackOutcome outcome,
        TimeSpan ago,
        int? ttfb = null,
        string? detail = null)
        => StreamHealthRepository.RecordAsync(
            connection,
            new HealthAttempt
            {
                StreamId = streamId,
                Outcome = outcome,

                // Passed through rather than blanked on failure. A stall happens after the
                // picture appeared, so its time-to-first-frame is known and recorded - and
                // a test that never writes one cannot tell whether the average is scoped
                // to successes, because avg() ignores nulls either way.
                TimeToFirstFrameMs = ttfb,
                Detail = detail,
            },
            Now - ago,
            CancellationToken.None);

    [Fact]
    public async Task Candidates_exclude_inactive_separator_and_disabled_provider_streams()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var live = await AddStreamAsync(connection, 1, "ESPN");
        await AddStreamAsync(connection, 1, "ESPN old", active: false);
        await AddStreamAsync(connection, 1, "===== SPORTS =====", separator: true);
        await AddStreamAsync(connection, 3, "ESPN via disabled");

        var candidates = await StreamHealthRepository.GetCandidatesAsync(
            connection, "tvg:espn.us", StreamKind.Live, Now, CancellationToken.None);

        Assert.Equal(live, Assert.Single(candidates).StreamId);
    }

    [Fact]
    public async Task A_stream_with_no_history_reports_no_attempts()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddStreamAsync(connection, 1, "ESPN");

        var candidate = Assert.Single(await StreamHealthRepository.GetCandidatesAsync(
            connection, "tvg:espn.us", StreamKind.Live, Now, CancellationToken.None));

        Assert.Equal(0, candidate.Attempts);
        Assert.Equal(0, candidate.Successes);
    }

    [Fact]
    public async Task Only_attempts_inside_the_rolling_window_count()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var stream = await AddStreamAsync(connection, 1, "ESPN");

        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromDays(1));
        await RecordAsync(connection, stream, PlaybackOutcome.Timeout, TimeSpan.FromDays(3));

        // Outside the seven-day window. A provider that was broken last month is not
        // broken now, and holding it against them forever makes the ranking useless.
        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromDays(30));
        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromDays(8));

        var candidate = Assert.Single(await StreamHealthRepository.GetCandidatesAsync(
            connection, "tvg:espn.us", StreamKind.Live, Now, CancellationToken.None));

        Assert.Equal(2, candidate.Attempts);
        Assert.Equal(1, candidate.Successes);
    }

    [Fact]
    public async Task The_country_comes_from_each_stream_title_not_the_channel_row()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddStreamAsync(connection, 1, "US: ESPN");
        await AddStreamAsync(connection, 2, "UK| ESPN");

        var candidates = await StreamHealthRepository.GetCandidatesAsync(
            connection, "tvg:espn.us", StreamKind.Live, Now, CancellationToken.None);

        Assert.Equal(["UK", "US"], candidates.Select(c => c.Country).Order());
    }

    [Fact]
    public async Task The_plan_ranks_a_reliable_fallback_below_the_priority_provider()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var primary = await AddStreamAsync(connection, 1, "ESPN");
        var fallback = await AddStreamAsync(connection, 2, "ESPN");

        await RecordAsync(connection, primary, PlaybackOutcome.Stall, TimeSpan.FromHours(1));
        await RecordAsync(connection, fallback, PlaybackOutcome.Ok, TimeSpan.FromHours(1));

        var plan = await StreamHealthRepository.PlanAsync(
            connection, "tvg:espn.us", StreamKind.Live, Now, QualityPreference.Highest, CancellationToken.None);

        Assert.Equal([primary, fallback], plan.Candidates.Select(c => c.StreamId));
    }

    [Fact]
    public async Task Recorded_detail_is_stripped_of_credentials()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var stream = await AddStreamAsync(connection, 1, "ESPN");

        await RecordAsync(
            connection,
            stream,
            PlaybackOutcome.HttpError,
            TimeSpan.Zero,
            detail: "failed to open http://host.invalid/live/ACCT7X2/SECRET99/511.ts");

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM stream_health;";
        var detail = (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;

        Assert.DoesNotContain("ACCT7X2", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET99", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_health_averages_time_to_first_frame_over_successes_only()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var stream = await AddStreamAsync(connection, 1, "ESPN");

        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromHours(1), ttfb: 800);
        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromHours(2), ttfb: 1200);

        // A stall that opened quickly and then died. Averaged in, it would report 733ms
        // and make a provider that fails fast look quicker than one that works.
        await RecordAsync(connection, stream, PlaybackOutcome.Stall, TimeSpan.FromHours(3), ttfb: 200);

        var health = Assert.Single(await StreamHealthRepository.GetProviderHealthAsync(
            connection, Now, CancellationToken.None));

        Assert.Equal("Primary", health.ProviderName);
        Assert.Equal(3, health.Attempts);
        Assert.Equal(2, health.Successes);
        Assert.Equal(1000, health.AverageTimeToFirstFrameMs);
    }

    [Fact]
    public async Task Pruning_drops_rows_past_the_retention_age()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var stream = await AddStreamAsync(connection, 1, "ESPN");

        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromDays(91));
        await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromDays(89));

        var deleted = await StreamHealthRepository.PruneAsync(connection, Now, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.Equal(1, await CountAsync(connection));
    }

    [Fact]
    public async Task Pruning_caps_the_rows_kept_per_stream()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var stream = await AddStreamAsync(connection, 1, "ESPN");

        // All well inside the retention age: the cap is what has to do the work here.
        // Without it a channel retried in a loop grows without bound inside the window.
        for (var i = 0; i < StreamHealthRepository.MaxRowsPerStream + 25; i++)
        {
            await RecordAsync(connection, stream, PlaybackOutcome.Ok, TimeSpan.FromMinutes(i));
        }

        var deleted = await StreamHealthRepository.PruneAsync(connection, Now, CancellationToken.None);

        Assert.Equal(25, deleted);
        Assert.Equal(StreamHealthRepository.MaxRowsPerStream, await CountAsync(connection));
    }

    [Fact]
    public async Task Pruning_keeps_the_newest_rows()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var stream = await AddStreamAsync(connection, 1, "ESPN");

        for (var i = 0; i < StreamHealthRepository.MaxRowsPerStream + 5; i++)
        {
            // The oldest five are failures, so keeping the wrong end is visible in the
            // success count rather than only in a row total.
            var outcome = i >= StreamHealthRepository.MaxRowsPerStream
                ? PlaybackOutcome.Timeout
                : PlaybackOutcome.Ok;

            await RecordAsync(connection, stream, outcome, TimeSpan.FromMinutes(i));
        }

        await StreamHealthRepository.PruneAsync(connection, Now, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM stream_health WHERE outcome = 'timeout';";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task Pruning_counts_each_stream_separately()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var first = await AddStreamAsync(connection, 1, "ESPN");
        var second = await AddStreamAsync(connection, 2, "ESPN");

        for (var i = 0; i < 300; i++)
        {
            await RecordAsync(connection, first, PlaybackOutcome.Ok, TimeSpan.FromMinutes(i));
            await RecordAsync(connection, second, PlaybackOutcome.Ok, TimeSpan.FromMinutes(i));
        }

        // 600 rows total, but 300 per stream, which is under the cap. A cap applied
        // globally would delete 100 of them.
        Assert.Equal(0, await StreamHealthRepository.PruneAsync(connection, Now, CancellationToken.None));
        Assert.Equal(600, await CountAsync(connection));
    }

    private static async Task<int> CountAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM stream_health;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
