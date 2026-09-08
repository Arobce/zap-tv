using Microsoft.Data.Sqlite;

namespace Iptv.Core.Epg;

/// <summary>One channel row of the guide, with the programmes in the window.</summary>
public sealed record EpgGridRow
{
    public required string ChannelKey { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Empty when the channel has no guide, which is the majority case.</summary>
    public required IReadOnlyList<EpgGridBlock> Programmes { get; init; }
}

/// <summary>One programme block, clipped to nothing but carrying its real times.</summary>
public sealed record EpgGridBlock
{
    public required long Id { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset Stop { get; init; }

    public string? Category { get; init; }

    /// <summary>Whether this programme is on now, at the time the window was read.</summary>
    public required bool IsNow { get; init; }
}

/// <summary>
/// Reads one visible rectangle of the guide.
/// </summary>
/// <remarks>
/// <para>
/// The PRD requires virtualization on both axes and a query per visible window rather
/// than loading programmes into memory. On the reference library that is 181,328
/// programmes against 20,479 channels; a grid that materialised either dimension would
/// not scroll.
/// </para>
/// <para>
/// The channel page is chosen by the caller — it is the vertical realization window — and
/// this reads only the programmes overlapping the horizontal one. Both bounds are half
/// open on the same side as the index: <c>stop &gt; from AND start &lt; to</c> takes
/// programmes straddling either edge, which are exactly the ones a grid must draw.
/// </para>
/// </remarks>
public static class EpgGridRepository
{
    /// <summary>
    /// The most channel rows one read will serve.
    /// </summary>
    /// <remarks>
    /// A guard, not a page size. The caller passes a realization window of a screenful
    /// plus a buffer; this exists so a caller that passes twenty thousand keys gets a
    /// clear failure rather than a query with twenty thousand parameters.
    /// </remarks>
    public const int MaxRows = 200;

    /// <summary>Reads the programmes for a page of channels over a time window.</summary>
    public static async Task<IReadOnlyList<EpgGridRow>> GetWindowAsync(
        SqliteConnection connection,
        IReadOnlyList<string> channelKeys,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(channelKeys);

        if (channelKeys.Count > MaxRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelKeys),
                channelKeys.Count,
                $"The grid reads at most {MaxRows} rows at a time; pass a realization window.");
        }

        // Every requested channel gets a row, in the order asked for, whether or not it has
        // any guide. A grid that silently drops unmapped channels would misalign with the
        // list beside it, and 82% of this library has no guide at all.
        var rows = new Dictionary<string, List<EpgGridBlock>>(StringComparer.Ordinal);
        foreach (var key in channelKeys)
        {
            rows[key] = [];
        }

        if (channelKeys.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();

        // Parameters, not an interpolated IN list: the keys come from the database but the
        // conventions forbid concatenated SQL outright, and a prepared shape is reusable
        // across scroll frames.
        var placeholders = string.Join(", ", channelKeys.Select((_, i) => $"@k{i}"));

        command.CommandText =
            $"""
            SELECT
                m.channel_key,
                p.id,
                p.title,
                p.start_utc,
                p.stop_utc,
                p.category
            FROM programmes p
            JOIN epg_map m ON m.epg_channel_id = p.epg_channel_id
            WHERE m.channel_key IN ({placeholders})
              AND p.stop_utc  > @from
              AND p.start_utc < @to
            ORDER BY m.channel_key, p.start_utc;
            """;

        for (var i = 0; i < channelKeys.Count; i++)
        {
            command.Parameters.AddWithValue($"@k{i}", channelKeys[i]);
        }

        command.Parameters.AddWithValue("@from", from.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@to", to.ToUnixTimeSeconds());

        var at = now.ToUnixTimeSeconds();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            if (!rows.TryGetValue(key, out var list))
            {
                continue;
            }

            var start = reader.GetInt64(3);
            var stop = reader.GetInt64(4);

            list.Add(new EpgGridBlock
            {
                Id = reader.GetInt64(1),
                Title = reader.GetString(2),
                Start = DateTimeOffset.FromUnixTimeSeconds(start),
                Stop = DateTimeOffset.FromUnixTimeSeconds(stop),
                Category = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsNow = start <= at && stop > at,
            });
        }

        return channelKeys
            .Select(key => new EpgGridRow
            {
                ChannelKey = key,
                DisplayName = key,
                Programmes = rows[key],
            })
            .ToList();
    }

    /// <summary>One channel on the grid's vertical axis.</summary>
    public sealed record EpgChannel
    {
        public required string ChannelKey { get; init; }

        public required string DisplayName { get; init; }

        public required bool IsFavorite { get; init; }
    }

    /// <summary>
    /// The channels the grid draws rows for.
    /// </summary>
    /// <remarks>
    /// Only channels with a guide. 82% of this library has none, and a grid of 20,479 rows
    /// where 17,000 are permanently blank is not a guide, it is a way to lose the ones that
    /// work. Favourites first, matching the channel list's own order.
    /// </remarks>
    public static async Task<IReadOnlyList<EpgChannel>> GetGridChannelsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.channel_key, c.display_name, c.is_favorite
            FROM channels c
            JOIN epg_map m ON m.channel_key = c.channel_key
            WHERE c.is_hidden = 0
              AND EXISTS (
                  SELECT 1 FROM streams s
                   JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
                   WHERE s.channel_key = c.channel_key
                     AND s.kind = 'live'
                     AND s.is_active = 1
                     AND s.is_separator = 0)
            ORDER BY c.is_favorite DESC,
                     COALESCE(c.user_sort_order, 2147483647),
                     c.display_name;
            """;

        var results = new List<EpgChannel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new EpgChannel
            {
                ChannelKey = reader.GetString(0),
                DisplayName = reader.GetString(1),
                IsFavorite = reader.GetInt64(2) == 1,
            });
        }

        return results;
    }

    /// <summary>The span the stored guide actually covers.</summary>
    /// <remarks>
    /// The grid needs real bounds rather than a fixed 14 days: scrolling into a week of
    /// empty columns because the provider only publishes two days is a worse answer than
    /// stopping where the data does.
    /// </remarks>
    public static async Task<(DateTimeOffset? From, DateTimeOffset? To)> GetCoverageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT min(start_utc), max(stop_utc) FROM programmes;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return (null, null);
        }

        return (
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)));
    }
}
