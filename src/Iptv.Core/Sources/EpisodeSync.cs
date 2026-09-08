using System.Globalization;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>One episode, ready to store.</summary>
public sealed record EpisodeRecord
{
    public required long ProviderEpisodeId { get; init; }

    public required string Title { get; init; }

    public required int SeasonNumber { get; init; }

    public required int EpisodeNumber { get; init; }

    public required string Url { get; init; }

    public string? Container { get; init; }

    public string? ImageUrl { get; init; }

    public int? DurationSeconds { get; init; }
}

/// <summary>
/// Turns a <c>get_series_info</c> response into stored episodes.
/// </summary>
/// <remarks>
/// Episodes are rows in <c>streams</c> with kind <c>series_episode</c>, not a table of
/// their own: they are playable streams with a provider id, a URL and a container, which
/// is exactly what that table is. What makes them episodes is <c>series_id</c>,
/// <c>season_num</c> and <c>episode_num</c>, which the schema already carries.
/// </remarks>
public static class EpisodeSync
{
    /// <summary>Flattens the season-keyed response into a flat, ordered list.</summary>
    /// <remarks>
    /// The season number comes from the episode's own <c>season</c> field where it has one,
    /// and from the dictionary key otherwise. Panels disagree about which they populate,
    /// and a season shown as 0 for every episode is worse than either.
    /// </remarks>
    public static IReadOnlyList<EpisodeRecord> Map(
        XtreamSeriesInfo info,
        XtreamCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(credentials);

        var results = new List<EpisodeRecord>();

        foreach (var (seasonKey, episodes) in info.Episodes)
        {
            foreach (var episode in episodes)
            {
                if (episode.Id <= 0)
                {
                    // No id means no playback URL can be built. Skipped rather than stored
                    // as a row that fails only when clicked.
                    continue;
                }

                var season = episode.Season
                    ?? (int.TryParse(seasonKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0);

                var container = string.IsNullOrWhiteSpace(episode.ContainerExtension)
                    ? "mp4"
                    : episode.ContainerExtension;

                var number = episode.EpisodeNum ?? 0;

                results.Add(new EpisodeRecord
                {
                    ProviderEpisodeId = episode.Id,

                    // Providers routinely leave the title empty. "S02E07" is a worse label
                    // than a real name and a far better one than a blank row.
                    Title = string.IsNullOrWhiteSpace(episode.Title)
                        ? $"S{season:00}E{number:00}"
                        : episode.Title.Trim(),

                    SeasonNumber = season,
                    EpisodeNumber = number,
                    Url = credentials.BuildSeriesUrl(episode.Id, container).ToString(),
                    Container = container,
                    ImageUrl = episode.Info?.Image,
                    DurationSeconds = episode.Info?.DurationSecs,
                });
            }
        }

        // Ordered here rather than by the reader, so the stored rows and the fetched ones
        // agree and a caller can show either without a surprise.
        results.Sort(static (left, right) =>
        {
            var bySeason = left.SeasonNumber.CompareTo(right.SeasonNumber);
            return bySeason != 0
                ? bySeason
                : left.EpisodeNumber.CompareTo(right.EpisodeNumber);
        });

        return results;
    }

    /// <summary>Stores a series' episodes, replacing what was there for that series.</summary>
    /// <remarks>
    /// Replace, not merge. Unlike a live channel, an episode carries nothing of the user's
    /// except a resume position, and that hangs off <c>channel_key</c>, which is derived
    /// from the provider's episode id and so survives. A season the provider withdrew
    /// should disappear rather than linger as a row that cannot play.
    /// </remarks>
    public static async Task<int> ReplaceAsync(
        SqliteConnection connection,
        long providerId,
        long seriesRowId,
        IReadOnlyList<EpisodeRecord> episodes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(episodes);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                "DELETE FROM streams WHERE series_id = @series AND kind = 'series_episode';";
            delete.Parameters.AddWithValue("@series", seriesRowId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var written = 0;

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT INTO streams (provider_id, provider_stream_id, kind, title, normalized_title,
                                     url, channel_key, container, logo_url, series_id,
                                     season_num, episode_num, is_active, is_separator, last_seen_utc)
                VALUES (@provider, @sid, 'series_episode', @title, @normalized,
                        @url, @key, @container, @image, @series,
                        @season, @episode, 1, 0, @seen);
                """;

            insert.Parameters.AddWithValue("@provider", providerId);
            insert.Parameters.AddWithValue("@series", seriesRowId);
            insert.Parameters.AddWithValue("@seen", now.ToUnixTimeSeconds());
            insert.Parameters.Add("@sid", SqliteType.Text);
            insert.Parameters.Add("@title", SqliteType.Text);
            insert.Parameters.Add("@normalized", SqliteType.Text);
            insert.Parameters.Add("@url", SqliteType.Text);
            insert.Parameters.Add("@key", SqliteType.Text);
            insert.Parameters.Add("@container", SqliteType.Text);
            insert.Parameters.Add("@image", SqliteType.Text);
            insert.Parameters.Add("@season", SqliteType.Integer);
            insert.Parameters.Add("@episode", SqliteType.Integer);

            foreach (var episode in episodes)
            {
                var providerStreamId = episode.ProviderEpisodeId.ToString(CultureInfo.InvariantCulture);

                insert.Parameters["@sid"].Value = providerStreamId;
                insert.Parameters["@title"].Value = episode.Title;
                insert.Parameters["@normalized"].Value = ChannelNormalizer.Normalize(episode.Title);
                insert.Parameters["@url"].Value = episode.Url;

                // Keyed on the provider's episode id, not the title. Two episodes in a
                // series share a name often enough ("Part 1") that a title-derived key
                // would collapse them into one, and the resume position hangs off this.
                insert.Parameters["@key"].Value = $"ep:{providerId}:{providerStreamId}";
                insert.Parameters["@container"].Value = (object?)episode.Container ?? DBNull.Value;
                insert.Parameters["@image"].Value = (object?)episode.ImageUrl ?? DBNull.Value;
                insert.Parameters["@season"].Value = episode.SeasonNumber;
                insert.Parameters["@episode"].Value = episode.EpisodeNumber;

                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                written++;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>What is needed to ask the provider for a series episodes.</summary>
    public sealed record SeriesFetchInfo
    {
        public required long ProviderId { get; init; }

        /// <summary>The provider own series id, for get_series_info.</summary>
        public required long ProviderSeriesId { get; init; }

        public required string Title { get; init; }
    }

    /// <summary>Looks up how to fetch one series episodes.</summary>
    public static async Task<SeriesFetchInfo?> GetFetchInfoAsync(
        SqliteConnection connection,
        long seriesRowId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.provider_id, s.provider_series_id, s.title
            FROM series s
            JOIN providers pr ON pr.id = s.provider_id AND pr.enabled = 1
            WHERE s.id = @id;
            """;

        command.Parameters.AddWithValue("@id", seriesRowId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return long.TryParse(reader.GetString(1), out var providerSeriesId)
            ? new SeriesFetchInfo
            {
                ProviderId = reader.GetInt64(0),
                ProviderSeriesId = providerSeriesId,
                Title = reader.GetString(2),
            }
            : null;
    }

    /// <summary>Reads the stored episodes for a series, in order.</summary>
    public static async Task<IReadOnlyList<EpisodeRecord>> GetEpisodesAsync(
        SqliteConnection connection,
        long seriesRowId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.provider_stream_id, s.title, s.season_num, s.episode_num,
                   s.url, s.container, s.logo_url
            FROM streams s
            WHERE s.series_id = @series
              AND s.kind = 'series_episode'
              AND s.is_active = 1
            ORDER BY s.season_num, s.episode_num, s.id;
            """;

        command.Parameters.AddWithValue("@series", seriesRowId);

        var results = new List<EpisodeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new EpisodeRecord
            {
                ProviderEpisodeId = long.TryParse(reader.GetString(0), out var id) ? id : 0,
                Title = reader.GetString(1),
                SeasonNumber = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                EpisodeNumber = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Url = reader.GetString(4),
                Container = reader.IsDBNull(5) ? null : reader.GetString(5),
                ImageUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return results;
    }
}
