using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Resume positions for films and episodes.
/// </summary>
/// <remarks>
/// The schema has carried <c>playback_state</c> since the first migration and nothing has
/// ever written to it, so watching half a film and closing the app lost the place entirely.
/// </remarks>
public sealed class PlaybackStateRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    private const string Key = "vod:the-film";

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private static Task<bool> SaveAsync(
        SqliteConnection connection,
        int position,
        int? duration = 7200,
        string key = Key,
        TimeSpan ago = default)
        => PlaybackStateRepository.SaveAsync(
            connection, key, position, duration, Now - ago, CancellationToken.None);

    private static Task<int?> ResumeAsync(SqliteConnection connection, string key = Key)
        => PlaybackStateRepository.GetResumeSecondsAsync(connection, key, CancellationToken.None);

    [Fact]
    public async Task Nothing_stored_means_start_at_the_beginning()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        Assert.Null(await ResumeAsync(connection));
    }

    [Fact]
    public async Task A_position_is_stored_and_resumed()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        Assert.True(await SaveAsync(connection, 1800));
        Assert.Equal(1800, await ResumeAsync(connection));
    }

    [Fact]
    public async Task A_glance_is_not_watching()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // Opening something and closing it again should not appear in continue-watching.
        Assert.False(await SaveAsync(connection, 5));
        Assert.Null(await ResumeAsync(connection));
    }

    [Fact]
    public async Task Abandoning_a_restart_does_not_destroy_last_nights_position()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 3600);

        // Reopened, watched ten seconds, closed. The old position must survive: clearing
        // it would lose an hour of progress to a misclick.
        Assert.False(await SaveAsync(connection, 10));
        Assert.Equal(3600, await ResumeAsync(connection));
    }

    [Fact]
    public async Task Saving_again_moves_the_position()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 600);
        await SaveAsync(connection, 1200);

        Assert.Equal(1200, await ResumeAsync(connection));
    }

    [Fact]
    public async Task Watching_to_the_end_starts_from_the_beginning_next_time()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, position: 7150, duration: 7200);

        // The position is still stored - it is what makes the row "watched" - but resuming
        // 50 seconds from the end is not what anyone wants.
        Assert.Null(await ResumeAsync(connection));

        var stored = await PlaybackStateRepository.GetAsync(connection, Key, CancellationToken.None);
        Assert.True(stored!.Completed);
    }

    [Theory]
    [InlineData(7200, 7080, true)]   // two minutes from the end of a two-hour film
    [InlineData(7200, 7000, false)]  // three minutes from the end: still watching
    [InlineData(720, 690, true)]     // 95% of a twelve-minute episode
    [InlineData(720, 600, false)]
    public void Completion_requires_being_near_the_end_by_both_measures(
        int duration,
        int position,
        bool expected)
    {
        // Both measures, not either. A fraction alone marks a two-hour film watched with
        // six minutes left; a fixed tail alone marks a twelve-minute episode watched at 83%.
        Assert.Equal(expected, PlaybackStateRepository.IsComplete(position, duration));
    }

    [Fact]
    public void An_unknown_duration_is_never_complete()
    {
        // Nothing to be near the end of. Worst case the user is offered a resume they
        // decline; the alternative marks a film watched after ninety seconds.
        Assert.False(PlaybackStateRepository.IsComplete(100_000, null));
        Assert.False(PlaybackStateRepository.IsComplete(100_000, 0));
    }

    [Fact]
    public async Task A_duration_learned_later_is_not_lost_by_a_save_without_one()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 600, duration: 7200);

        // mpv reports no duration for a stream it is still probing. That must not wipe a
        // length already known, or the entry silently stops being completable.
        await SaveAsync(connection, 900, duration: null);

        var stored = await PlaybackStateRepository.GetAsync(connection, Key, CancellationToken.None);
        Assert.Equal(7200, stored!.DurationSeconds);
    }

    [Fact]
    public async Task Progress_is_null_when_the_length_is_unknown()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 600, duration: null);

        var stored = await PlaybackStateRepository.GetAsync(connection, Key, CancellationToken.None);
        Assert.Null(stored!.Progress);
    }

    [Fact]
    public async Task Progress_is_reported_when_the_length_is_known()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 1800, duration: 7200);

        var stored = await PlaybackStateRepository.GetAsync(connection, Key, CancellationToken.None);
        Assert.Equal(0.25, stored!.Progress);
    }

    [Fact]
    public async Task Continue_watching_lists_the_unfinished_newest_first()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 600, key: "vod:old", ago: TimeSpan.FromDays(3));
        await SaveAsync(connection, 600, key: "vod:new", ago: TimeSpan.FromHours(1));
        await SaveAsync(connection, 7150, key: "vod:finished");

        var list = await PlaybackStateRepository.GetContinueWatchingAsync(
            connection, 10, CancellationToken.None);

        // The finished one is excluded: offering to continue something already watched is
        // the main way these lists become useless.
        Assert.Equal(["vod:new", "vod:old"], list.Select(p => p.ContentKey));
    }

    [Fact]
    public async Task Continue_watching_respects_its_limit()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        for (var i = 0; i < 8; i++)
        {
            await SaveAsync(connection, 600, key: $"vod:{i}", ago: TimeSpan.FromHours(i));
        }

        var list = await PlaybackStateRepository.GetContinueWatchingAsync(
            connection, 3, CancellationToken.None);

        Assert.Equal(3, list.Count);
        Assert.Equal("vod:0", list[0].ContentKey);
    }

    [Fact]
    public async Task Clearing_forgets_the_position()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 1800);
        await PlaybackStateRepository.ClearAsync(connection, Key, CancellationToken.None);

        Assert.Null(await ResumeAsync(connection));
    }

    [Fact]
    public async Task Positions_are_independent_per_content_key()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, 600, key: "vod:a");
        await SaveAsync(connection, 1200, key: "vod:b");

        Assert.Equal(600, await ResumeAsync(connection, "vod:a"));
        Assert.Equal(1200, await ResumeAsync(connection, "vod:b"));
    }
}
