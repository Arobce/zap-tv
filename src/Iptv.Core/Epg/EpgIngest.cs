using System.Diagnostics;
using System.Threading.Channels;
using Iptv.Core.Sources;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Epg;

/// <summary>The guide could not be ingested, and the existing one was left alone.</summary>
public sealed class EpgIngestException(string message) : Exception(message);

/// <summary>Settings for one EPG refresh.</summary>
public sealed record EpgIngestOptions
{
    /// <summary>How far ahead to keep programmes. Beyond this they are discarded.</summary>
    public int HorizonDays { get; init; } = 14;

    /// <summary>
    /// Per-provider correction applied to every timestamp.
    /// </summary>
    /// <remarks>
    /// XMLTV offsets are frequently wrong or absent, and the error is usually consistent
    /// across a whole source, so the user can nudge the entire provider rather than
    /// reporting individual programmes.
    /// </remarks>
    public int OffsetMinutes { get; init; }

    /// <summary>Provider this guide came from, recorded against its channels.</summary>
    public int? SourceProviderId { get; init; }

    /// <summary>Rows per transaction during the bulk load.</summary>
    public int BatchSize { get; init; } = 5_000;

    /// <summary>
    /// Bound on the parse-to-write queue.
    /// </summary>
    /// <remarks>
    /// The bound is the backpressure: without it the parser runs far ahead of the writer
    /// and the queue becomes an in-memory copy of the guide, which is exactly what
    /// streaming was meant to avoid.
    /// </remarks>
    public int QueueCapacity { get; init; } = 10_000;
}

/// <summary>Counts and timings from one refresh.</summary>
/// <remarks>
/// <see cref="ParseAndInsert"/> and <see cref="Total"/> are separate because a regression
/// in insert throughput is a different problem from a regression in index or FTS rebuild
/// cost, and the reference provider's fetch time alone varies by 3.5x between runs.
/// </remarks>
public sealed record EpgIngestResult(
    int Channels,
    int Programmes,
    TimeSpan ParseAndInsert,
    TimeSpan Total);

/// <summary>
/// Parses an XMLTV document into the library, staging then swapping.
/// </summary>
/// <remarks>
/// <para>
/// The guide is written to a staging table and swapped in at the end. A failed or partial
/// download must never leave the user with a half-empty guide: the guide is the first
/// thing they look at, and a silently truncated one is worse than an obviously stale one.
/// </para>
/// <para>
/// Parsing and writing run concurrently over a bounded channel. The parser is CPU-bound on
/// XML and the writer is bound by SQLite; overlapping them is most of the throughput.
/// </para>
/// </remarks>
public static class EpgIngest
{
    private const string ProgrammesStaging = "programmes_staging";
    private const string ChannelsStaging = "epg_channels_staging";

    /// <summary>Ingests a guide, replacing the current one only on success.</summary>
    public static async Task<EpgIngestResult> IngestAsync(
        SqliteConnection connection,
        Stream xmltv,
        EpgIngestOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(xmltv);
        ArgumentNullException.ThrowIfNull(options);

        var total = Stopwatch.StartNew();
        var parseAndInsert = Stopwatch.StartNew();

        var keepFrom = now.ToUnixTimeSeconds() - (24 * 3600);
        var keepUntil = now.ToUnixTimeSeconds() + (options.HorizonDays * 24L * 3600);
        var offsetSeconds = options.OffsetMinutes * 60L;

        try
        {
            await CreateStagingAsync(connection, cancellationToken).ConfigureAwait(false);

            var queue = Channel.CreateBounded<XmltvItem>(new BoundedChannelOptions(options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var producer = ProduceAsync(xmltv, queue.Writer, cancellationToken);
            var counts = await ConsumeAsync(
                connection, queue.Reader, options, keepFrom, keepUntil, offsetSeconds, cancellationToken)
                .ConfigureAwait(false);

            await producer.ConfigureAwait(false);
            parseAndInsert.Stop();

            if (counts.Programmes == 0)
            {
                // A well-formed but empty document is a provider-side failure, not an
                // instruction to wipe the guide.
                throw new EpgIngestException(
                    "The guide contained no usable programmes, so the existing guide was kept. " +
                    "This usually means the provider served an empty or truncated document.");
            }

            await SwapAsync(connection, cancellationToken).ConfigureAwait(false);
            await RebuildSearchIndexAsync(connection, cancellationToken).ConfigureAwait(false);

            total.Stop();
            return new EpgIngestResult(
                counts.Channels, counts.Programmes, parseAndInsert.Elapsed, total.Elapsed);
        }
        catch
        {
            // Best effort: the guide is intact either way, but a leftover staging table
            // would collide with the next run.
            await TryDropStagingAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the document and publishes items to the writer.</summary>
    private static async Task ProduceAsync(
        Stream xmltv,
        ChannelWriter<XmltvItem> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in XmltvReader.ReadAsync(xmltv, cancellationToken)
                               .ConfigureAwait(false))
            {
                await writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }

            writer.Complete();
        }
        catch (Exception exception)
        {
            // Completing with the fault stops the consumer rather than leaving it blocked
            // on a queue nothing will ever fill.
            writer.Complete(exception);
            throw;
        }
    }

    private static async Task<(int Channels, int Programmes)> ConsumeAsync(
        SqliteConnection connection,
        ChannelReader<XmltvItem> reader,
        EpgIngestOptions options,
        long keepFrom,
        long keepUntil,
        long offsetSeconds,
        CancellationToken cancellationToken)
    {
        var channels = 0;
        var programmes = 0;
        var batched = 0;

        var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One prepared command each, parameter values reset per row rather than the
        // command being rebuilt. This is the difference between an 8s ingest and a 60s one.
        var programmeCommand = CreateProgrammeCommand(connection, transaction);
        var channelCommand = CreateChannelCommand(connection, transaction, options.SourceProviderId);

        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (item)
                {
                    case EpgChannel channel:
                        await WriteChannelAsync(channelCommand, channel).ConfigureAwait(false);
                        channels++;
                        batched++;
                        break;

                    case EpgProgramme programme:
                        var start = programme.StartUtc + offsetSeconds;
                        var stop = programme.StopUtc + offsetSeconds;

                        // Trimmed here rather than after the load: rows outside the window
                        // are never written, never indexed and never deleted again.
                        if (stop < keepFrom || start > keepUntil)
                        {
                            continue;
                        }

                        await WriteProgrammeAsync(programmeCommand, programme, start, stop)
                            .ConfigureAwait(false);
                        programmes++;
                        batched++;
                        break;

                    default:
                        break;
                }

                if (batched < options.BatchSize)
                {
                    continue;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
                batched = 0;

                transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                programmeCommand.Transaction = transaction;
                channelCommand.Transaction = transaction;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (channels, programmes);
        }
        finally
        {
            await programmeCommand.DisposeAsync().ConfigureAwait(false);
            await channelCommand.DisposeAsync().ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static SqliteCommand CreateProgrammeCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {ProgrammesStaging}
                (epg_channel_id, start_utc, stop_utc, title, subtitle, description, category, episode_num)
            VALUES (@channel, @start, @stop, @title, @subtitle, @description, @category, @episode);
            """;

        foreach (var name in new[]
                 {
                     "@channel", "@start", "@stop", "@title", "@subtitle", "@description",
                     "@category", "@episode",
                 })
        {
            command.Parameters.Add(name, SqliteType.Text);
        }

        return command;
    }

    private static SqliteCommand CreateChannelCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int? sourceProviderId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;

        // A guide can declare the same channel twice; the first wins rather than failing
        // the whole ingest over a duplicate id.
        command.CommandText =
            $"""
            INSERT OR IGNORE INTO {ChannelsStaging}
                (epg_channel_id, display_names, normalized_names, icon_url, source_provider_id)
            VALUES (@id, @names, @normalized, @icon, @provider);
            """;

        foreach (var name in new[] { "@id", "@names", "@normalized", "@icon" })
        {
            command.Parameters.Add(name, SqliteType.Text);
        }

        command.Parameters.AddWithValue("@provider", (object?)sourceProviderId ?? DBNull.Value);
        return command;
    }

    private static async Task WriteChannelAsync(SqliteCommand command, EpgChannel channel)
    {
        command.Parameters["@id"].Value = channel.Id;
        command.Parameters["@names"].Value = string.Join('\n', channel.DisplayNames);

        // Normalized once here so Phase 4 matching does not renormalize thousands of names
        // on every run.
        command.Parameters["@normalized"].Value = string.Join(
            '\n',
            channel.DisplayNames.Select(ChannelNormalizer.Normalize));

        command.Parameters["@icon"].Value = (object?)channel.IconUrl ?? DBNull.Value;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task WriteProgrammeAsync(
        SqliteCommand command,
        EpgProgramme programme,
        long start,
        long stop)
    {
        command.Parameters["@channel"].Value = programme.ChannelId;
        command.Parameters["@start"].Value = start;
        command.Parameters["@stop"].Value = stop;
        command.Parameters["@title"].Value = programme.Title;
        command.Parameters["@subtitle"].Value = (object?)programme.Subtitle ?? DBNull.Value;
        command.Parameters["@description"].Value = (object?)programme.Description ?? DBNull.Value;
        command.Parameters["@category"].Value = (object?)programme.Category ?? DBNull.Value;
        command.Parameters["@episode"].Value = (object?)programme.EpisodeNum ?? DBNull.Value;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Creates empty staging tables, without indexes.</summary>
    /// <remarks>
    /// Deliberately unindexed: maintaining the lookup index across a couple of million
    /// inserts costs far more than building it once over the finished table.
    /// </remarks>
    private static async Task CreateStagingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            DROP TABLE IF EXISTS {ProgrammesStaging};
            DROP TABLE IF EXISTS {ChannelsStaging};

            CREATE TABLE {ProgrammesStaging} (
              id             INTEGER PRIMARY KEY,
              epg_channel_id TEXT    NOT NULL,
              start_utc      INTEGER NOT NULL,
              stop_utc       INTEGER NOT NULL,
              title          TEXT    NOT NULL,
              subtitle       TEXT,
              description    TEXT,
              category       TEXT,
              episode_num    TEXT
            ) STRICT;

            CREATE TABLE {ChannelsStaging} (
              epg_channel_id     TEXT PRIMARY KEY,
              display_names      TEXT NOT NULL,
              normalized_names   TEXT NOT NULL,
              icon_url           TEXT,
              source_provider_id INTEGER
            ) STRICT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Swaps staging into place and rebuilds the lookup index.</summary>
    /// <remarks>
    /// One transaction, so the guide is either the old one or the new one and never a
    /// mixture. Dropping a table takes its indexes with it, so the lookup index is rebuilt
    /// here rather than created on the staging table - SQLite has no way to rename an
    /// index, and losing it would leave the grid doing full scans.
    /// </remarks>
    private static async Task SwapAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            DROP TABLE programmes;
            ALTER TABLE {ProgrammesStaging} RENAME TO programmes;
            CREATE INDEX ix_programmes_lookup ON programmes(epg_channel_id, start_utc, stop_utc);
            CREATE INDEX ix_programmes_channel ON programmes(epg_channel_id);

            DROP TABLE epg_channels;
            ALTER TABLE {ChannelsStaging} RENAME TO epg_channels;
            CREATE INDEX ix_epg_channels_id_lower ON epg_channels(lower(epg_channel_id));
            """;

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        command.Transaction = (SqliteTransaction)transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rebuilds the external-content FTS index after the swap.</summary>
    /// <remarks>
    /// <c>programmes_fts</c> is bound to the table by name and rowid, both of which the
    /// swap invalidates. Without this, search silently returns nothing for the entire
    /// guide - a failure with no error and no obvious cause.
    /// </remarks>
    private static async Task RebuildSearchIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO programmes_fts(programmes_fts) VALUES('rebuild');";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryDropStagingAsync(SqliteConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"DROP TABLE IF EXISTS {ProgrammesStaging}; DROP TABLE IF EXISTS {ChannelsStaging};";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Cleanup only. The guide is intact regardless, and the next run drops these
            // before recreating them.
        }
    }
}
