using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>What slice of the channel list to fetch.</summary>
public sealed record ChannelQuery
{
    /// <summary>Substring match on the display name. Null shows everything.</summary>
    public string? Search { get; init; }

    public int Limit { get; init; } = 200;

    public int Offset { get; init; }

    public bool FavouritesOnly { get; init; }

    /// <summary>Provider category name to restrict to. Null shows every category.</summary>
    public string? Category { get; init; }
}

/// <summary>One row of the channel list, with its guide.</summary>
public sealed record ChannelListItem
{
    public required string ChannelKey { get; init; }

    public required string DisplayName { get; init; }

    public string? LogoUrl { get; init; }

    public bool IsFavorite { get; init; }

    /// <summary>Title of the programme on now, or null when the guide has none.</summary>
    public string? NowTitle { get; init; }

    /// <summary>Title of the programme after it.</summary>
    public string? NextTitle { get; init; }

    /// <summary>How far through the current programme, 0 to 1, for the row's progress bar.</summary>
    public double? NowProgress { get; init; }
}

/// <summary>
/// Reads the channel list and resolves which stream to play.
/// </summary>
/// <remarks>
/// The list query returns each row together with its now/next programme in one statement.
/// Fetching the guide per row would issue one query per visible channel against a library
/// of 20,478, which is the shape of mistake that makes a virtualized list feel slower
/// than an unvirtualized one.
/// </remarks>
public static class ChannelRepository
{
    /// <summary>Fetches a page of the channel list.</summary>
    public static async Task<IReadOnlyList<ChannelListItem>> GetChannelsAsync(
        SqliteConnection connection,
        ChannelQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        var at = now.ToUnixTimeSeconds();

        await using var command = connection.CreateCommand();

        // Correlated subqueries rather than joins: a channel usually has no guide at all
        // (82% of the reference library), and joins to programmes would multiply rows for
        // the ones that do before being collapsed again.
        command.CommandText =
            """
            SELECT
                c.channel_key,
                c.display_name,
                c.logo_url,
                c.is_favorite,
                (SELECT p.title FROM programmes p
                   JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
                  WHERE m.channel_key = c.channel_key
                    AND p.start_utc <= @at AND p.stop_utc > @at
                  ORDER BY p.start_utc DESC LIMIT 1)                        AS now_title,
                (SELECT p.start_utc FROM programmes p
                   JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
                  WHERE m.channel_key = c.channel_key
                    AND p.start_utc <= @at AND p.stop_utc > @at
                  ORDER BY p.start_utc DESC LIMIT 1)                        AS now_start,
                (SELECT p.stop_utc FROM programmes p
                   JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
                  WHERE m.channel_key = c.channel_key
                    AND p.start_utc <= @at AND p.stop_utc > @at
                  ORDER BY p.start_utc DESC LIMIT 1)                        AS now_stop,
                (SELECT p.title FROM programmes p
                   JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
                  WHERE m.channel_key = c.channel_key
                    AND p.start_utc > @at
                  ORDER BY p.start_utc ASC LIMIT 1)                         AS next_title
            FROM channels c
            WHERE c.is_hidden = 0
              AND (@search IS NULL OR c.display_name LIKE '%' || @search || '%')
              AND (@favourites = 0 OR c.is_favorite = 1)
              -- A channel exists only if some active, non-separator stream backs it, and
              -- when a category is chosen, only if one of those streams is in it. Matched
              -- on the category's name rather than its id, because two providers use
              -- different ids for the same "Sports" and the user picked the name.
              AND EXISTS (
                  SELECT 1 FROM streams s
                   WHERE s.channel_key = c.channel_key
                     AND s.is_active = 1
                     AND s.is_separator = 0
                     AND s.kind = 'live'
                     AND (@category IS NULL OR EXISTS (
                            SELECT 1 FROM categories cat
                             WHERE cat.provider_id = s.provider_id
                               AND cat.kind        = 'live'
                               AND cat.category_id = s.category_id
                               AND cat.name        = @category)))
            ORDER BY
                c.is_favorite DESC,
                COALESCE(c.user_sort_order, 2147483647),
                c.display_name
            LIMIT @limit OFFSET @offset;
            """;

        command.Parameters.AddWithValue("@at", at);
        command.Parameters.AddWithValue("@search", (object?)query.Search ?? DBNull.Value);
        command.Parameters.AddWithValue("@favourites", query.FavouritesOnly ? 1 : 0);
        command.Parameters.AddWithValue("@category", (object?)query.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("@limit", query.Limit);
        command.Parameters.AddWithValue("@offset", query.Offset);

        var results = new List<ChannelListItem>(query.Limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ChannelListItem
            {
                ChannelKey = reader.GetString(0),
                DisplayName = reader.GetString(1),
                LogoUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsFavorite = reader.GetInt64(3) == 1,
                NowTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
                NowProgress = Progress(reader, at),
                NextTitle = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }

        return results;
    }

    /// <summary>
    /// Chooses which of a channel's streams to play.
    /// </summary>
    /// <remarks>
    /// Provider priority first, then quality. Quality is retained through normalization
    /// specifically so this choice exists: users want the UHD copy when there is one, and
    /// still want the HD copy to be reachable when it fails.
    /// <para>
    /// This is the first candidate only. Phase 8 turns the same ordering into a candidate
    /// list and walks it on failure.
    /// </para>
    /// </remarks>
    /// <param name="kind">
    /// Which catalogue the key was taken from.
    /// </param>
    /// <remarks>
    /// Required, not optional. <c>channel_key</c> comes from the normalized title alone,
    /// so a film and a live channel of the same name share one, and without the kind this
    /// query would happily answer a film with a live stream's URL — the user clicks a
    /// movie and gets a television channel.
    /// </remarks>
    public static async Task<string?> GetPlaybackUrlAsync(
        SqliteConnection connection,
        string channelKey,
        StreamKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.url
            FROM streams s
            JOIN providers pr ON pr.id = s.provider_id
            WHERE s.channel_key = @key
              AND s.kind = @kind
              AND s.is_active = 1
              AND s.is_separator = 0
              AND pr.enabled = 1
            ORDER BY
                pr.priority,
                CASE s.quality
                    WHEN 'Uhd' THEN 0
                    WHEN 'Fhd' THEN 1
                    WHEN 'Hd'  THEN 2
                    WHEN 'Sd'  THEN 3
                    ELSE 4
                END,
                s.id
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("@key", channelKey);
        command.Parameters.AddWithValue("@kind", StreamKindStorage(kind));

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    /// <summary>How far through the current programme, or null when there is none.</summary>
    /// <summary>The stored spelling of a kind. Renaming one is a migration.</summary>
    internal static string StreamKindStorage(StreamKind kind) => kind switch
    {
        StreamKind.Live => "live",
        StreamKind.Vod => "vod",
        StreamKind.SeriesEpisode => "series_episode",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped stream kind"),
    };

    private static double? Progress(SqliteDataReader reader, long at)
    {
        if (reader.IsDBNull(5) || reader.IsDBNull(6))
        {
            return null;
        }

        var start = reader.GetInt64(5);
        var stop = reader.GetInt64(6);
        var duration = stop - start;

        // A zero or inverted duration is a malformed guide entry rather than a programme
        // of no length; reporting it as null keeps the progress bar off rather than
        // dividing by zero.
        return duration <= 0 ? null : Math.Clamp((at - start) / (double)duration, 0, 1);
    }
}
