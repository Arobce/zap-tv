using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// Associates the user's channels with guide entries.
/// </summary>
/// <remarks>
/// Measured against a real provider, exact id matching recovers 99.1% of everything any
/// matcher could achieve, because the ceiling is set by guide completeness rather than
/// match quality. These tests therefore concentrate on tier 1 being correct and on never
/// producing a wrong mapping, which is the failure that actually harms users.
/// </remarks>
public sealed class EpgMatcherTests
{
    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        await ExecuteAsync(connection,
            "INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');");
        return connection;
    }

    private static async Task AddChannelAsync(
        SqliteConnection connection,
        string channelKey,
        string title,
        string? tvgId,
        string? country = null)
    {
        await ExecuteAsync(connection,
            $"""
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 tvg_id, url, channel_key, country, is_active, last_seen_utc)
            VALUES (1, '{channelKey}', 'live', '{title}', '{title.ToLowerInvariant()}',
                    {Quote(tvgId)}, 'http://x/1.ts', '{channelKey}', {Quote(country)}, 1, 0);
            INSERT INTO channels (channel_key, display_name, country)
            VALUES ('{channelKey}', '{title}', {Quote(country)});
            """);
    }

    private static async Task AddGuideChannelAsync(
        SqliteConnection connection,
        string epgId,
        string displayName,
        bool withProgrammes = true)
    {
        await ExecuteAsync(connection,
            $"""
            INSERT INTO epg_channels (epg_channel_id, display_names, normalized_names)
            VALUES ('{epgId}', '{displayName}', '{displayName.ToLowerInvariant()}');
            """);

        if (withProgrammes)
        {
            await ExecuteAsync(connection,
                $"""
                INSERT INTO programmes (epg_channel_id, start_utc, stop_utc, title)
                VALUES ('{epgId}', 1788546600, 1788550200, 'Something');
                """);
        }
    }

    private static string Quote(string? value) => value is null ? "NULL" : $"'{value}'";

    [Fact]
    public async Task Matches_on_exact_tvg_id()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "bbc1");
        await AddGuideChannelAsync(connection, "bbc1", "BBC One");

        var report = await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        Assert.Equal(1, report.Matched);
        Assert.Equal("bbc1", await TextAsync(connection,
            "SELECT epg_channel_id FROM epg_map WHERE channel_key = 'tvg:bbc1'"));
        Assert.Equal("tvg_id", await TextAsync(connection,
            "SELECT method FROM epg_map WHERE channel_key = 'tvg:bbc1'"));
    }

    [Fact]
    public async Task Id_matching_ignores_case()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "BBC1.UK");
        await AddGuideChannelAsync(connection, "bbc1.uk", "BBC One");

        var report = await EpgMatcher.MatchAsync(connection, CancellationToken.None);
        Assert.Equal(1, report.Matched);
    }

    [Fact]
    public async Task Does_not_map_a_channel_the_guide_has_no_programmes_for()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "bbc1");
        await AddGuideChannelAsync(connection, "bbc1", "BBC One", withProgrammes: false);

        var report = await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        // A declared-but-empty guide entry matches perfectly and shows the user a blank
        // row. Reporting it as covered makes the coverage metric lie.
        Assert.Equal(0, report.Matched);
    }

    [Fact]
    public async Task Never_overwrites_a_locked_manual_mapping()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "bbc1");
        await AddGuideChannelAsync(connection, "bbc1", "BBC One");
        await AddGuideChannelAsync(connection, "manual.choice", "Users Pick");

        await ExecuteAsync(connection,
            """
            INSERT INTO epg_map (channel_key, epg_channel_id, confidence, method, locked, updated_utc)
            VALUES ('tvg:bbc1', 'manual.choice', 1.0, 'manual', 1, 0);
            """);

        await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        // The user corrected this by hand. Automatic matching undoing that is the single
        // most annoying thing a guide-matching feature can do.
        Assert.Equal("manual.choice", await TextAsync(connection,
            "SELECT epg_channel_id FROM epg_map WHERE channel_key = 'tvg:bbc1'"));
    }

    [Fact]
    public async Task Refreshes_an_unlocked_mapping_when_the_guide_changes()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "bbc1");
        await AddGuideChannelAsync(connection, "bbc1", "BBC One");

        await ExecuteAsync(connection,
            """
            INSERT INTO epg_map (channel_key, epg_channel_id, confidence, method, locked, updated_utc)
            VALUES ('tvg:bbc1', 'stale.id', 0.5, 'fuzzy', 0, 0);
            """);

        await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        Assert.Equal("bbc1", await TextAsync(connection,
            "SELECT epg_channel_id FROM epg_map WHERE channel_key = 'tvg:bbc1'"));
    }

    [Fact]
    public async Task Separator_rows_are_excluded_from_matching_and_from_coverage()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "bbc1");
        await ExecuteAsync(connection,
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_separator, is_active, last_seen_utc)
            VALUES (1, 'sep', 'live', '##### SPORTS #####', 'sports',
                    'http://x/2.ts', 'name:sports', 1, 1, 0);
            """);
        await AddGuideChannelAsync(connection, "bbc1", "BBC One");

        var report = await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        // 1,137 non-channels in the denominator would understate coverage by several
        // percent and offer dividers as things needing a guide.
        Assert.Equal(1, report.TotalChannels);
        Assert.Equal(1, report.Matched);
    }

    [Fact]
    public async Task Reports_coverage_against_the_guide_ceiling_as_well_as_the_library()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddChannelAsync(connection, "tvg:a", "A", "a");
        await AddChannelAsync(connection, "tvg:b", "B", "b");
        await AddChannelAsync(connection, "name:c", "C", tvgId: null);
        await AddChannelAsync(connection, "name:d", "D", tvgId: null);
        await AddGuideChannelAsync(connection, "a", "A");
        await AddGuideChannelAsync(connection, "b", "B");

        var report = await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        Assert.Equal(4, report.TotalChannels);
        Assert.Equal(2, report.Matched);
        Assert.Equal(2, report.Ceiling);

        // Half the library, but everything the guide can actually supply. Only the second
        // number says anything about whether the matcher works.
        Assert.Equal(0.5, report.LibraryCoverage, 3);
        Assert.Equal(1.0, report.CeilingRecovery, 3);
    }

    [Fact]
    public async Task Ceiling_recovery_is_one_when_the_guide_is_empty()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:a", "A", "a");

        var report = await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        // Nothing to recover, so nothing was missed. Reporting 0% here would make an
        // absent guide look like a broken matcher.
        Assert.Equal(0, report.Ceiling);
        Assert.Equal(1.0, report.CeilingRecovery, 3);
    }

    [Fact]
    public async Task Matching_twice_is_stable()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);
        await AddChannelAsync(connection, "tvg:bbc1", "BBC One", "bbc1");
        await AddGuideChannelAsync(connection, "bbc1", "BBC One");

        var first = await EpgMatcher.MatchAsync(connection, CancellationToken.None);
        var second = await EpgMatcher.MatchAsync(connection, CancellationToken.None);

        Assert.Equal(first.Matched, second.Matched);
        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM epg_map"));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static async Task<string?> TextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync(CancellationToken.None))?.ToString();
    }
}
