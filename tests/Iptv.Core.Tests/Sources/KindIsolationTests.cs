using Iptv.Core.Data;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Keeps live television and films out of each other's lists.
/// </summary>
/// <remarks>
/// <c>channel_key</c> is derived from the normalized title and nothing else, so a film
/// called "Honey" and a live channel called "Honey" are the same key. That is right for
/// deduplicating one catalogue across providers and wrong across catalogues. On the
/// reference library 23 keys are shared, and seven live channels were being listed under
/// a film's title before this was scoped.
/// </remarks>
public sealed class KindIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private const string SharedKey = "name:honey";

    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO providers (id, name, kind, base_url) VALUES (1, 'A', 'xtream', 'http://a.invalid');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        return connection;
    }

    /// <summary>Adds a stream under the shared key, so both kinds collide by construction.</summary>
    private static async Task AddAsync(SqliteConnection connection, string kind, string title)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                 url, channel_key, is_active, is_separator, last_seen_utc)
            VALUES (1, @sid, @kind, @title, 'honey',
                    'http://host.invalid/' || @kind || '/' || @sid, @key, 1, 0, 0);
            """;

        command.Parameters.AddWithValue("@sid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@kind", kind);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@key", SharedKey);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_channel_is_never_named_after_a_film_that_shares_its_key()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "live", "Honey");

        // Longer, so the "prefer the longest title" rule would pick it if kinds were mixed.
        await AddAsync(connection, "vod", "Honey (2003) Extended Edition");

        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        var channel = Assert.Single(await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery(), Now, CancellationToken.None));

        Assert.Equal("Honey", channel.DisplayName);
    }

    [Fact]
    public async Task Playing_a_film_never_resolves_to_a_live_stream()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "live", "Honey");
        await AddAsync(connection, "vod", "Honey (2003)");

        var film = await ChannelRepository.GetPlaybackUrlAsync(
            connection, SharedKey, StreamKind.Vod, CancellationToken.None);

        var channel = await ChannelRepository.GetPlaybackUrlAsync(
            connection, SharedKey, StreamKind.Live, CancellationToken.None);

        // The user clicked a film and must get a film. Without the kind the ordering here
        // is by provider and quality alone, and either row can win.
        Assert.Contains("/vod/", film, StringComparison.Ordinal);
        Assert.Contains("/live/", channel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failover_never_offers_a_film_as_a_substitute_for_a_channel()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "live", "Honey");
        await AddAsync(connection, "vod", "Honey (2003)");

        // The Phase 8 safety guard cannot catch this one: it compares countries and titles,
        // and these share a key precisely because their titles match.
        var plan = await StreamHealthRepository.PlanAsync(
            connection, SharedKey, StreamKind.Live, Now, QualityPreference.Highest,
            CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("/live/", plan.Primary!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_film_only_key_never_appears_in_the_channel_list()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "vod", "Honey (2003)");
        await ProviderSync.RefreshChannelsAsync(connection, CancellationToken.None);

        Assert.Empty(await ChannelRepository.GetChannelsAsync(
            connection, new ChannelQuery { Search = "Honey" }, Now, CancellationToken.None));
    }

    [Fact]
    public async Task A_live_only_key_never_appears_in_the_film_list()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await AddAsync(connection, "live", "Honey");

        Assert.Empty(await LibraryRepository.GetFilmsAsync(
            connection, new CatalogueQuery { Search = "Honey" }, CancellationToken.None));
    }
}
