using Iptv.Core.Data;
using Iptv.Core.Epg;
using Iptv.Core.Playback;
using Iptv.Core.Sources;
using Microsoft.Data.Sqlite;

namespace Iptv.Presentation;

/// <summary>One labelled group of readings.</summary>
public sealed record DiagnosticsSection
{
    public required string Title { get; init; }

    public required IReadOnlyList<DiagnosticsRow> Rows { get; init; }
}

/// <summary>One reading.</summary>
public sealed record DiagnosticsRow
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    /// <summary>Whether this reading is the one worth looking at.</summary>
    /// <remarks>
    /// A page of numbers where nothing stands out is a page nobody reads. This marks the
    /// ones that mean something is wrong, so the view can pick them out.
    /// </remarks>
    public bool IsWarning { get; init; }
}

/// <summary>
/// What the app knows about itself.
/// </summary>
/// <remarks>
/// The PRD asks for stream health, EPG coverage, decode info and ingest timings, and notes
/// that nothing on the market has it. Every number here already existed and had nowhere to
/// be seen; the work is in saying which of them mean something is wrong.
/// </remarks>
public sealed class DiagnosticsViewModel
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ISecretProtector _protector;

    public DiagnosticsViewModel(SqliteConnectionFactory factory, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(protector);

        _factory = factory;
        _protector = protector;
    }

    public async Task<IReadOnlyList<DiagnosticsSection>> BuildAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            await LibraryAsync(connection, cancellationToken).ConfigureAwait(false),
            await GuideAsync(connection, now, cancellationToken).ConfigureAwait(false),
            await ProvidersAsync(connection, now, cancellationToken).ConfigureAwait(false),
            await PlaybackAsync(connection, now, cancellationToken).ConfigureAwait(false),
        ];
    }

    private static async Task<DiagnosticsSection> LibraryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<DiagnosticsRow>();

        // Live channels counted as the list counts them, not as rows in the table. VOD
        // keys have channels rows too, and counting those once reported 118,763 channels
        // above a list holding 20,479.
        var channels = await ScalarAsync(connection,
            """
            SELECT count(*) FROM channels c
             WHERE EXISTS (SELECT 1 FROM streams s
                            WHERE s.channel_key = c.channel_key AND s.kind = 'live'
                              AND s.is_active = 1 AND s.is_separator = 0);
            """, cancellationToken).ConfigureAwait(false);

        var films = await ScalarAsync(connection,
            "SELECT count(DISTINCT channel_key) FROM streams WHERE kind = 'vod' AND is_active = 1 AND is_separator = 0;",
            cancellationToken).ConfigureAwait(false);

        var series = await ScalarAsync(connection,
            "SELECT count(DISTINCT series_key) FROM series;", cancellationToken).ConfigureAwait(false);

        var episodes = await ScalarAsync(connection,
            "SELECT count(*) FROM streams WHERE kind = 'series_episode';", cancellationToken)
            .ConfigureAwait(false);

        var favourites = await ScalarAsync(connection,
            "SELECT count(*) FROM channels WHERE is_favorite = 1;", cancellationToken).ConfigureAwait(false);

        rows.Add(new DiagnosticsRow { Label = "Live channels", Value = $"{channels:N0}" });
        rows.Add(new DiagnosticsRow { Label = "Films", Value = $"{films:N0}" });
        rows.Add(new DiagnosticsRow { Label = "Series", Value = $"{series:N0}" });
        rows.Add(new DiagnosticsRow
        {
            Label = "Episodes fetched",

            // Says why it is small rather than looking like a failed sync. Episodes are
            // loaded per series on open, not in bulk.
            Value = $"{episodes:N0} (fetched on open, not by sync)",
        });
        rows.Add(new DiagnosticsRow { Label = "Favourites", Value = $"{favourites:N0}" });

        return new DiagnosticsSection { Title = "Library", Rows = rows };
    }

    private static async Task<DiagnosticsSection> GuideAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = new List<DiagnosticsRow>();

        var (from, to) = await EpgGridRepository.GetCoverageAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (from is null || to is null)
        {
            rows.Add(new DiagnosticsRow { Label = "Guide", Value = "none stored", IsWarning = true });
            return new DiagnosticsSection { Title = "Guide", Rows = rows };
        }

        var ahead = to.Value - now;

        rows.Add(new DiagnosticsRow
        {
            Label = "Covers",
            Value = $"{from:ddd dd MMM HH:mm} to {to:ddd dd MMM HH:mm}",
        });

        // The reading that actually matters, and the one a coverage percentage hides. A
        // guide can cover 16.8% of channels and still have nothing on air.
        rows.Add(new DiagnosticsRow
        {
            Label = "Reaches ahead",
            Value = ahead <= TimeSpan.Zero
                ? "expired"
                : $"{ahead.TotalHours:F1}h",
            IsWarning = ahead < EpgRefreshPolicy.HorizonFloor,
        });

        var mapped = await ScalarAsync(connection, "SELECT count(*) FROM epg_map;", cancellationToken)
            .ConfigureAwait(false);

        var onAir = await ScalarAsync(connection,
            """
            SELECT count(DISTINCT m.channel_key)
            FROM epg_map m
            JOIN programmes p ON p.epg_channel_id = m.epg_channel_id
            WHERE p.start_utc <= @at AND p.stop_utc > @at;
            """, cancellationToken, ("@at", now.ToUnixTimeSeconds())).ConfigureAwait(false);

        var programmes = await ScalarAsync(connection, "SELECT count(*) FROM programmes;", cancellationToken)
            .ConfigureAwait(false);

        rows.Add(new DiagnosticsRow { Label = "Programmes", Value = $"{programmes:N0}" });
        rows.Add(new DiagnosticsRow { Label = "Channels mapped", Value = $"{mapped:N0}" });

        // Reported beside the mapping count on purpose: the two disagree exactly when the
        // guide has gone stale, and either alone is misleading.
        rows.Add(new DiagnosticsRow
        {
            Label = "On air right now",
            Value = $"{onAir:N0}",
            IsWarning = mapped > 0 && onAir < mapped / 2,
        });

        var attempt = await EpgRefreshPolicy.GetLastAttemptAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        rows.Add(new DiagnosticsRow
        {
            Label = "Last refresh attempt",
            Value = attempt is { } when_ ? Ago(now - when_) : "never",
        });

        var decision = EpgRefreshPolicy.Decide(attempt, to, now);
        rows.Add(new DiagnosticsRow { Label = "Auto refresh", Value = decision.Reason });

        return new DiagnosticsSection { Title = "Guide", Rows = rows };
    }

    private async Task<DiagnosticsSection> ProvidersAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = new List<DiagnosticsRow>();

        var providers = await ProviderRepository.ListAsync(connection, _protector, cancellationToken)
            .ConfigureAwait(false);

        if (providers.Count == 0)
        {
            rows.Add(new DiagnosticsRow { Label = "Providers", Value = "none", IsWarning = true });
            return new DiagnosticsSection { Title = "Providers", Rows = rows };
        }

        var health = await StreamHealthRepository
            .GetProviderHealthAsync(connection, now, cancellationToken).ConfigureAwait(false);

        foreach (var provider in providers)
        {
            var stats = health.FirstOrDefault(h => h.ProviderId == provider.Id);

            var parts = new List<string>
            {
                provider.Enabled ? $"{provider.LiveChannels:N0} channels" : "disabled",
            };

            if (stats is null)
            {
                // Distinguished from a bad rate. No attempts is not a problem, it is an
                // absence of evidence, and showing 0% would read as broken.
                parts.Add("no attempts in the last 7 days");
            }
            else
            {
                parts.Add($"{stats.SuccessRate:P0} of {stats.Attempts:N0} attempts");

                if (stats.AverageTimeToFirstFrameMs is { } ttfb)
                {
                    parts.Add($"{ttfb:F0}ms to first frame");
                }
            }

            rows.Add(new DiagnosticsRow
            {
                Label = provider.Name,
                Value = string.Join("  ·  ", parts),
                IsWarning = !provider.HasCredentials || stats is { Attempts: > 4, SuccessRate: < 0.8 },
            });
        }

        return new DiagnosticsSection { Title = "Providers", Rows = rows };
    }

    private static async Task<DiagnosticsSection> PlaybackAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = new List<DiagnosticsRow>();

        await using var command = connection.CreateCommand();

        // Grouped by outcome rather than listed: a log of individual attempts is the log
        // viewer's job, and what belongs here is the shape of them.
        command.CommandText =
            """
            SELECT h.outcome, count(*), avg(h.ttfb_ms)
            FROM stream_health h
            WHERE h.attempted_utc >= @since
            GROUP BY h.outcome
            ORDER BY count(*) DESC;
            """;

        command.Parameters.AddWithValue(
            "@since", (now - StreamHealthRepository.RollingWindow).ToUnixTimeSeconds());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var outcome = reader.GetString(0);
            var count = reader.GetInt32(1);
            var average = reader.IsDBNull(2) ? null : (double?)reader.GetDouble(2);

            rows.Add(new DiagnosticsRow
            {
                Label = outcome,
                Value = average is { } ms ? $"{count:N0}  ·  {ms:F0}ms average" : $"{count:N0}",
                IsWarning = outcome != "ok",
            });
        }

        if (rows.Count == 0)
        {
            rows.Add(new DiagnosticsRow { Label = "Attempts", Value = "none in the last 7 days" });
        }

        return new DiagnosticsSection { Title = "Playback, last 7 days", Rows = rows };
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>A rough age. The question is "is this stale", not "when exactly".</summary>
    private static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return "in the future";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        return elapsed < TimeSpan.FromHours(1)
            ? $"{(int)elapsed.TotalMinutes}m ago"
            : elapsed < TimeSpan.FromDays(1)
                ? $"{(int)elapsed.TotalHours}h ago"
                : $"{(int)elapsed.TotalDays}d ago";
    }
}
