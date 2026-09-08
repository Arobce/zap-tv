using Iptv.Core.Data;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Playback;

/// <summary>
/// The retry sequence: which stream to open next, and what gets written down about the
/// one that just failed.
/// </summary>
public sealed class FailoverSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url, priority) VALUES
              (1, 'Primary',  'xtream', 'http://a.invalid', 0),
              (2, 'Second',   'xtream', 'http://b.invalid', 1),
              (3, 'Third',    'xtream', 'http://c.invalid', 2);
            INSERT INTO channels (channel_key, display_name) VALUES ('tvg:espn.us', 'ESPN');
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    private static async Task<long> AddStreamAsync(SqliteConnection connection, long providerId, string title)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, quality, is_active, is_separator, last_seen_utc)
            VALUES (@provider, @sid, 'live', @title, @normalized,
                    'http://host.invalid/live/u/p/' || @sid || '.ts', 'tvg:espn.us', 'Hd', 1, 0, 0)
            RETURNING id;
            """;

        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@normalized", ChannelNormalizer.Normalize(title));

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static Task<FailoverSession> StartAsync(SqliteConnection connection)
        => FailoverSession.StartAsync(
            connection, "tvg:espn.us", StreamKind.Live, Now, QualityPreference.Highest, CancellationToken.None);

    private static Task<StreamCandidate?> ReportAsync(
        FailoverSession session,
        SqliteConnection connection,
        PlaybackOutcome outcome,
        int? ttfb = null)
        => session.ReportAsync(connection, outcome, Now, CancellationToken.None, ttfb);

    [Fact]
    public async Task A_channel_with_no_streams_has_nothing_to_open()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var session = await StartAsync(connection);

        Assert.Null(session.Current);
        Assert.Equal(0, session.Remaining);
        Assert.False(session.HasFailedOver);
    }

    [Fact]
    public async Task A_failure_advances_to_the_next_candidate()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var first = await AddStreamAsync(connection, 1, "ESPN");
        var second = await AddStreamAsync(connection, 2, "ESPN");

        var session = await StartAsync(connection);
        Assert.Equal(first, session.Current!.StreamId);

        var next = await ReportAsync(session, connection, PlaybackOutcome.HttpError);

        Assert.Equal(second, next!.StreamId);
        Assert.Equal(second, session.Current!.StreamId);
        Assert.True(session.HasFailedOver);
        Assert.Equal(2, session.AttemptNumber);
    }

    [Fact]
    public async Task Success_does_not_advance()
    {
        // A stream that started can still stall later, and that stall has to be blamed on
        // this candidate and failed over from here - not from whatever came after it.
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var first = await AddStreamAsync(connection, 1, "ESPN");
        await AddStreamAsync(connection, 2, "ESPN");

        var session = await StartAsync(connection);
        var next = await ReportAsync(session, connection, PlaybackOutcome.Ok, ttfb: 900);

        Assert.Null(next);
        Assert.Equal(first, session.Current!.StreamId);
        Assert.False(session.HasFailedOver);
    }

    [Fact]
    public async Task A_stall_after_a_successful_open_fails_over()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddStreamAsync(connection, 1, "ESPN");
        var second = await AddStreamAsync(connection, 2, "ESPN");

        var session = await StartAsync(connection);
        await ReportAsync(session, connection, PlaybackOutcome.Ok, ttfb: 700);

        var next = await session.ReportStallAsync(connection, Now, CancellationToken.None, 700);

        Assert.Equal(second, next!.StreamId);
    }

    [Fact]
    public async Task A_stream_that_opens_and_dies_is_recorded_twice()
    {
        // Both rows matter. One says it opened; the other says it did not last. A ranking
        // that only saw the success would keep choosing it first.
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var first = await AddStreamAsync(connection, 1, "ESPN");
        await AddStreamAsync(connection, 2, "ESPN");

        var session = await StartAsync(connection);
        await ReportAsync(session, connection, PlaybackOutcome.Ok, ttfb: 700);
        await session.ReportStallAsync(connection, Now, CancellationToken.None, 700);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome FROM stream_health WHERE stream_id = @id ORDER BY rowid;";
        command.Parameters.AddWithValue("@id", first);

        var outcomes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            outcomes.Add(reader.GetString(0));
        }

        Assert.Equal(["ok", "stall"], outcomes);
    }

    [Fact]
    public async Task Exhausting_every_candidate_leaves_nothing_to_try()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddStreamAsync(connection, 1, "ESPN");
        await AddStreamAsync(connection, 2, "ESPN");

        var session = await StartAsync(connection);
        Assert.NotNull(await ReportAsync(session, connection, PlaybackOutcome.Timeout));
        Assert.Null(await ReportAsync(session, connection, PlaybackOutcome.Timeout));

        Assert.Null(session.Current);
        Assert.Equal(0, session.Remaining);
    }

    [Fact]
    public async Task Reporting_after_exhaustion_is_ignored()
    {
        // mpv events arrive asynchronously and one can land after the session gave up.
        // Attributing it to a stream that was never opened would poison the ranking.
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddStreamAsync(connection, 1, "ESPN");

        var session = await StartAsync(connection);
        await ReportAsync(session, connection, PlaybackOutcome.Timeout);

        Assert.Null(await ReportAsync(session, connection, PlaybackOutcome.Timeout));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM stream_health;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task Every_attempt_is_written_to_the_stream_it_was_made_against()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        var first = await AddStreamAsync(connection, 1, "ESPN");
        var second = await AddStreamAsync(connection, 2, "ESPN");
        var third = await AddStreamAsync(connection, 3, "ESPN");

        var session = await StartAsync(connection);
        await ReportAsync(session, connection, PlaybackOutcome.HttpError);
        await ReportAsync(session, connection, PlaybackOutcome.Timeout);
        await ReportAsync(session, connection, PlaybackOutcome.Ok, ttfb: 1100);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stream_id, outcome FROM stream_health ORDER BY rowid;";

        var rows = new List<(long Id, string Outcome)>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        Assert.Equal(
            [(first, "http_error"), (second, "timeout"), (third, "ok")],
            rows);
    }

    [Fact]
    public async Task The_guard_refusals_are_carried_for_logging()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddStreamAsync(connection, 1, "US: ESPN");
        await AddStreamAsync(connection, 2, "UK| ESPN");

        var session = await StartAsync(connection);

        // A channel whose only alternative was refused looks identical to one that never
        // had an alternative, unless the reason survives the plan.
        Assert.Equal(0, session.Remaining);
        Assert.Single(session.Excluded);
    }
}
