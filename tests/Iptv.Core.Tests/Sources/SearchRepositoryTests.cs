using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// One search across channels, films, series and the guide.
/// </summary>
/// <remarks>
/// The FTS indexes are external-content: they hold no data of their own and are empty
/// until rebuilt. They had never been rebuilt, so the headline search feature was querying
/// an index that had never held a row.
/// </remarks>
public sealed class SearchRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 20, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO providers (id, name, kind, base_url) VALUES
              (1, 'A', 'xtream', 'http://a.invalid'),
              (2, 'Off', 'xtream', 'http://b.invalid');
            UPDATE providers SET enabled = 0 WHERE id = 2;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    private static async Task StreamAsync(
        SqliteConnection connection,
        string key,
        string title,
        string kind = "live",
        long providerId = 1,
        bool active = true,
        bool separator = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO channels (channel_key, display_name) VALUES (@key, @title);
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (@provider, @sid, @kind, @title, @title,
                    'http://host.invalid/' || @sid, @key, @active, @separator, 0);
            """;

        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@active", active ? 1 : 0);
        command.Parameters.AddWithValue("@separator", separator ? 1 : 0);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task SeriesAsync(SqliteConnection connection, string title, int? year = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series (provider_id, provider_series_id, title, normalized_title, series_key, year)
            VALUES (1, @sid, @title, @title, 'name:' || @sid, @year);
            """;

        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@year", (object?)year ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ProgrammeAsync(
        SqliteConnection connection,
        string channelKey,
        string title,
        DateTimeOffset start,
        DateTimeOffset stop)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO epg_map (channel_key, epg_channel_id, confidence, method, updated_utc)
            VALUES (@key, @key || '.epg', 1.0, 'tvg_id', 0);
            INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
            VALUES (@key || '.epg', @start, @stop, @title);
            """;

        command.Parameters.AddWithValue("@key", channelKey);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@start", start.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@stop", stop.ToUnixTimeSeconds());

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Rebuilds every index, as a sync does. Nothing matches before this runs.</summary>
    private static async Task IndexAsync(SqliteConnection connection)
    {
        await SearchRepository.RebuildStreamIndexAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO programmes_fts(programmes_fts) VALUES('rebuild');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static Task<IReadOnlyList<SearchHit>> SearchAsync(SqliteConnection connection, string term)
        => SearchRepository.SearchAsync(connection, term, 20, Now, CancellationToken.None);

    // --- turning free text into a query ---

    [Theory]
    [InlineData("bbc", "\"bbc\"*")]
    [InlineData("bbc one", "\"bbc\"* \"one\"*")]
    [InlineData("  spaced   out  ", "\"spaced\"* \"out\"*")]
    public void Tokens_are_quoted_and_given_a_prefix(string term, string expected)
        => Assert.Equal(expected, SearchRepository.BuildMatch(term));

    [Theory]
    [InlineData("news OR sport")]
    [InlineData("bbc*")]
    [InlineData("\"quoted\"")]
    [InlineData("-excluded")]
    [InlineData("a NEAR b")]
    public void Fts_operators_typed_into_a_search_box_are_literal(string term)
    {
        // A search box is free text, not a query language. Left unquoted these are FTS5
        // syntax, and the malformed ones are a hard error rather than no results.
        var match = SearchRepository.BuildMatch(term);

        Assert.NotNull(match);
        Assert.DoesNotContain("\"\"", match, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Nothing_typed_is_no_query(string? term)
        => Assert.Null(SearchRepository.BuildMatch(term));

    [Fact]
    public async Task An_empty_term_searches_nothing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        Assert.Empty(await SearchAsync(connection, "   "));
    }

    // --- what comes back ---

    [Fact]
    public async Task Channels_films_series_and_programmes_come_back_together()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "Sky Sports Main");
        await StreamAsync(connection, "name:f", "Sports Day", kind: "vod");
        await SeriesAsync(connection, "Sports Night", 2024);
        await ProgrammeAsync(connection, "tvg:a", "Sports Roundup", Now, Now.AddHours(1));
        await IndexAsync(connection);

        var hits = await SearchAsync(connection, "sports");

        // The point of a unified search: one term, every kind, rather than a term that
        // only works on whichever tab happens to be open.
        Assert.Contains(hits, h => h.Kind == SearchHitKind.Channel);
        Assert.Contains(hits, h => h.Kind == SearchHitKind.Film);
        Assert.Contains(hits, h => h.Kind == SearchHitKind.Series);
        Assert.Contains(hits, h => h.Kind == SearchHitKind.Programme);
    }

    [Fact]
    public async Task A_prefix_matches_while_the_word_is_still_being_typed()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "Eurosport");
        await StreamAsync(connection, "tvg:b", "Sportsnet");
        await IndexAsync(connection);

        // Results as you type is the requirement. Whole-word matching would show nothing
        // until the last keystroke.
        Assert.Contains(await SearchAsync(connection, "sports"), h => h.Title == "Sportsnet");
    }

    [Fact]
    public async Task A_channel_carried_by_three_providers_is_one_result()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        for (var i = 0; i < 3; i++)
        {
            await StreamAsync(connection, "tvg:a", "BBC One");
        }

        await IndexAsync(connection);

        var hit = Assert.Single(await SearchAsync(connection, "bbc"));

        Assert.Equal("BBC One", hit.Title);
        Assert.Contains("3 sources", hit.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inactive_separator_and_disabled_provider_rows_are_excluded()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:keep", "News Keep");
        await StreamAsync(connection, "tvg:old", "News Old", active: false);
        await StreamAsync(connection, "tvg:sep", "===== NEWS =====", separator: true);
        await StreamAsync(connection, "tvg:off", "News Disabled", providerId: 2);
        await IndexAsync(connection);

        var hits = await SearchAsync(connection, "news");

        Assert.Equal("News Keep", Assert.Single(hits).Title);
    }

    [Fact]
    public async Task A_finished_programme_is_not_offered()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "Channel");
        await ProgrammeAsync(connection, "tvg:a", "Match Over", Now.AddHours(-3), Now.AddHours(-2));
        await ProgrammeAsync(connection, "tvg:a", "Match Later", Now.AddHours(2), Now.AddHours(3));
        await IndexAsync(connection);

        var programmes = (await SearchAsync(connection, "match"))
            .Where(h => h.Kind == SearchHitKind.Programme)
            .ToList();

        // A guide holds a day of the past as well as the future. Offering to watch
        // something that ended this morning is worse than offering nothing.
        Assert.Equal("Match Later", Assert.Single(programmes).Title);
    }

    [Fact]
    public async Task A_programme_on_now_says_so_and_names_its_channel()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "BBC One");
        await ProgrammeAsync(connection, "tvg:a", "The Match", Now.AddMinutes(-10), Now.AddHours(1));
        await IndexAsync(connection);

        var hit = (await SearchAsync(connection, "match")).Single(h => h.Kind == SearchHitKind.Programme);

        // A programme title alone says neither where to watch it nor whether it started.
        Assert.Contains("on now", hit.Subtitle, StringComparison.Ordinal);
        Assert.Contains("BBC One", hit.Subtitle, StringComparison.Ordinal);
        Assert.Equal("tvg:a", hit.Key);
    }

    [Fact]
    public async Task A_series_hit_carries_the_row_id_needed_to_open_it()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SeriesAsync(connection, "The Show", 2023);
        await IndexAsync(connection);

        var hit = Assert.Single(await SearchAsync(connection, "show"));

        Assert.Equal(SearchHitKind.Series, hit.Kind);
        Assert.True(hit.SeriesRowId > 0);
        Assert.Contains("2023", hit.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_words_must_match()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "BBC One");
        await StreamAsync(connection, "tvg:b", "BBC Two");
        await IndexAsync(connection);

        // Implicit AND. Two words that each match hundreds are useful precisely because
        // together they match one.
        Assert.Equal("BBC One", Assert.Single(await SearchAsync(connection, "bbc one")).Title);
    }

    [Fact]
    public async Task Nothing_matches_before_the_index_is_rebuilt()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "BBC One");

        // External-content FTS holds no data of its own. This is exactly the state the app
        // shipped in: an index that had never had a row written to it.
        Assert.Empty(await SearchAsync(connection, "bbc"));

        await IndexAsync(connection);
        Assert.Single(await SearchAsync(connection, "bbc"));
    }

    [Fact]
    public async Task Rebuilding_twice_does_not_duplicate_results()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "BBC One");
        await IndexAsync(connection);
        await IndexAsync(connection);

        Assert.Single(await SearchAsync(connection, "bbc"));
    }

    [Fact]
    public async Task Diacritics_are_ignored()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await StreamAsync(connection, "tvg:a", "Télé Monte Carlo");
        await IndexAsync(connection);

        // The tokenizer strips them, which matters on a library where most channel names
        // are not English.
        Assert.Single(await SearchAsync(connection, "tele"));
    }
}
