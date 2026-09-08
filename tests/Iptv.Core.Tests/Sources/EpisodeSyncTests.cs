using System.Text.Json;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Episodes, which arrive in the most awkward shape the Xtream API has.
/// </summary>
/// <remarks>
/// <c>get_series_info</c> returns <c>episodes</c> as an object keyed by season number
/// rather than an array, and panels disagree on almost every detail inside it. These tests
/// use raw JSON rather than constructed objects, because the parsing is most of the risk.
/// </remarks>
public sealed class EpisodeSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly XtreamCredentials Credentials =
        new(new Uri("http://host.invalid"), "ACCT7X2", "SECRET99");

    private static IReadOnlyList<EpisodeRecord> Parse(string json)
    {
        var info = JsonSerializer.Deserialize<XtreamSeriesInfo>(json, XtreamJson.Options)!;
        return EpisodeSync.Map(info, Credentials);
    }

    [Fact]
    public void Episodes_are_read_from_the_season_keyed_object()
    {
        var episodes = Parse(
            """
            {"episodes":{
              "1":[{"id":"11","episode_num":1,"title":"Pilot","container_extension":"mkv"},
                   {"id":"12","episode_num":2,"title":"Second","container_extension":"mkv"}],
              "2":[{"id":"21","episode_num":1,"title":"Return","container_extension":"mp4"}]}}
            """);

        Assert.Equal(3, episodes.Count);
        Assert.Equal([1, 1, 2], episodes.Select(e => e.SeasonNumber));
        Assert.Equal(["Pilot", "Second", "Return"], episodes.Select(e => e.Title));
    }

    [Fact]
    public void An_empty_array_instead_of_an_object_is_no_episodes_not_an_error()
    {
        // Several panels send [] for a series with nothing in it. The default dictionary
        // converter throws on that, which would turn "no episodes" into a broken app.
        Assert.Empty(Parse("""{"episodes":[]}"""));
    }

    [Fact]
    public void A_missing_episodes_property_is_no_episodes()
    {
        Assert.Empty(Parse("""{"seasons":[]}"""));
    }

    [Fact]
    public void The_season_comes_from_the_key_when_the_episode_omits_it()
    {
        // Panels populate one or the other. Defaulting to zero for every episode would be
        // worse than either.
        var episodes = Parse("""{"episodes":{"3":[{"id":"31","episode_num":4,"title":"X"}]}}""");

        Assert.Equal(3, Assert.Single(episodes).SeasonNumber);
    }

    [Fact]
    public void The_episodes_own_season_wins_over_the_key()
    {
        var episodes = Parse(
            """{"episodes":{"1":[{"id":"31","season":7,"episode_num":4,"title":"X"}]}}""");

        Assert.Equal(7, Assert.Single(episodes).SeasonNumber);
    }

    [Fact]
    public void A_numeric_id_is_accepted_as_well_as_a_string()
    {
        // The same field is quoted on one panel and bare on another.
        var episodes = Parse("""{"episodes":{"1":[{"id":99,"episode_num":1,"title":"X"}]}}""");

        Assert.Equal(99, Assert.Single(episodes).ProviderEpisodeId);
    }

    [Fact]
    public void An_episode_with_no_id_is_skipped()
    {
        // No id means no playable URL. Better dropped than stored as a row that fails only
        // when the user clicks it.
        var episodes = Parse(
            """
            {"episodes":{"1":[{"id":"0","episode_num":1,"title":"Broken"},
                              {"id":"5","episode_num":2,"title":"Fine"}]}}
            """);

        Assert.Equal("Fine", Assert.Single(episodes).Title);
    }

    [Fact]
    public void An_untitled_episode_gets_a_season_and_episode_label()
    {
        var episodes = Parse("""{"episodes":{"2":[{"id":"7","episode_num":7,"title":""}]}}""");

        Assert.Equal("S02E07", Assert.Single(episodes).Title);
    }

    [Fact]
    public void A_malformed_season_does_not_lose_the_others()
    {
        var episodes = Parse(
            """
            {"episodes":{"1":"not an array",
                         "2":[{"id":"21","episode_num":1,"title":"Kept"}]}}
            """);

        Assert.Equal("Kept", Assert.Single(episodes).Title);
    }

    [Fact]
    public void Episodes_are_ordered_by_season_then_number()
    {
        var episodes = Parse(
            """
            {"episodes":{
              "2":[{"id":"22","episode_num":2,"title":"b"},{"id":"21","episode_num":1,"title":"a"}],
              "1":[{"id":"12","episode_num":2,"title":"y"},{"id":"11","episode_num":1,"title":"x"}]}}
            """);

        Assert.Equal(["x", "y", "a", "b"], episodes.Select(e => e.Title));
    }

    [Fact]
    public void The_playback_url_uses_the_series_path_and_the_container()
    {
        var episodes = Parse(
            """{"episodes":{"1":[{"id":"42","episode_num":1,"title":"X","container_extension":"mkv"}]}}""");

        Assert.EndsWith("/series/ACCT7X2/SECRET99/42.mkv", Assert.Single(episodes).Url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_container_falls_back_rather_than_producing_a_dotless_url()
    {
        var episodes = Parse("""{"episodes":{"1":[{"id":"42","episode_num":1,"title":"X"}]}}""");

        Assert.EndsWith(".mp4", Assert.Single(episodes).Url, StringComparison.Ordinal);
    }

    // --- storage ---

    private static async Task<(SqliteConnection Connection, long SeriesId)> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');
            INSERT INTO series (provider_id, provider_series_id, title, normalized_title, series_key)
            VALUES (1, 's1', 'The Show', 'the show', 'name:theshow')
            RETURNING id;
            """;

        return (connection, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    private static Task<int> StoreAsync(SqliteConnection connection, long seriesId, string json)
        => EpisodeSync.ReplaceAsync(connection, 1, seriesId, Parse(json), Now, CancellationToken.None);

    [Fact]
    public async Task Stored_episodes_read_back_in_order()
    {
        await using var db = new TempDatabase();
        var (connection, seriesId) = await OpenAsync(db);
        await using var _ = connection;

        await StoreAsync(connection, seriesId,
            """
            {"episodes":{"2":[{"id":"21","episode_num":1,"title":"b"}],
                         "1":[{"id":"11","episode_num":1,"title":"a"}]}}
            """);

        var stored = await EpisodeSync.GetEpisodesAsync(connection, seriesId, CancellationToken.None);

        Assert.Equal(["a", "b"], stored.Select(e => e.Title));
    }

    [Fact]
    public async Task Refetching_replaces_rather_than_duplicating()
    {
        await using var db = new TempDatabase();
        var (connection, seriesId) = await OpenAsync(db);
        await using var _ = connection;

        const string json = """{"episodes":{"1":[{"id":"11","episode_num":1,"title":"a"}]}}""";

        await StoreAsync(connection, seriesId, json);
        await StoreAsync(connection, seriesId, json);

        Assert.Single(await EpisodeSync.GetEpisodesAsync(connection, seriesId, CancellationToken.None));
    }

    [Fact]
    public async Task A_withdrawn_season_disappears_on_refetch()
    {
        await using var db = new TempDatabase();
        var (connection, seriesId) = await OpenAsync(db);
        await using var _ = connection;

        await StoreAsync(connection, seriesId,
            """
            {"episodes":{"1":[{"id":"11","episode_num":1,"title":"a"}],
                         "2":[{"id":"21","episode_num":1,"title":"b"}]}}
            """);

        await StoreAsync(connection, seriesId,
            """{"episodes":{"1":[{"id":"11","episode_num":1,"title":"a"}]}}""");

        Assert.Single(await EpisodeSync.GetEpisodesAsync(connection, seriesId, CancellationToken.None));
    }

    [Fact]
    public async Task Two_episodes_sharing_a_title_stay_two_rows()
    {
        // Keyed on the provider's episode id, not the title. "Part 1" appears twice in a
        // series often enough that a title-derived key would silently merge them.
        await using var db = new TempDatabase();
        var (connection, seriesId) = await OpenAsync(db);
        await using var _ = connection;

        await StoreAsync(connection, seriesId,
            """
            {"episodes":{"1":[{"id":"11","episode_num":1,"title":"Part 1"},
                              {"id":"12","episode_num":2,"title":"Part 1"}]}}
            """);

        var stored = await EpisodeSync.GetEpisodesAsync(connection, seriesId, CancellationToken.None);
        Assert.Equal(2, stored.Count);

        // Asserted on channel_key, not on the row count. streams has no unique constraint
        // on channel_key, so a title-derived key would still store two rows - and then two
        // episodes would share one resume position, which is the failure that matters.
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(DISTINCT channel_key) FROM streams WHERE kind = 'series_episode';";

        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task Episodes_do_not_appear_in_the_film_catalogue()
    {
        await using var db = new TempDatabase();
        var (connection, seriesId) = await OpenAsync(db);
        await using var _ = connection;

        await StoreAsync(connection, seriesId,
            """{"episodes":{"1":[{"id":"11","episode_num":1,"title":"Pilot"}]}}""");

        Assert.Empty(await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task Storing_one_series_leaves_another_alone()
    {
        await using var db = new TempDatabase();
        var (connection, seriesId) = await OpenAsync(db);
        await using var _ = connection;

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO series (provider_id, provider_series_id, title, normalized_title, series_key)
            VALUES (1, 's2', 'Other', 'other', 'name:other') RETURNING id;
            """;
        var otherId = (long)(await insert.ExecuteScalarAsync(CancellationToken.None))!;

        await StoreAsync(connection, seriesId, """{"episodes":{"1":[{"id":"11","episode_num":1,"title":"a"}]}}""");
        await StoreAsync(connection, otherId, """{"episodes":{"1":[{"id":"91","episode_num":1,"title":"z"}]}}""");

        Assert.Single(await EpisodeSync.GetEpisodesAsync(connection, seriesId, CancellationToken.None));
        Assert.Single(await EpisodeSync.GetEpisodesAsync(connection, otherId, CancellationToken.None));
    }
}
