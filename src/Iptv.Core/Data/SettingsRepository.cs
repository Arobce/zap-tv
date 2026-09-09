using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Data;

/// <summary>Everything the user can change.</summary>
/// <remarks>
/// Defaults are the values the app has been running with, so an install that has never
/// opened Settings behaves exactly as it did before this existed.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>0 to 130. Above 100 is mpv's soft gain, for quiet channels.</summary>
    public int Volume { get; init; } = 70;

    public bool Muted { get; init; }

    /// <summary>Whether to ask mpv for hardware decoding.</summary>
    /// <remarks>
    /// On by default and worth turning off only to diagnose: measurement showed decode is
    /// not deterministic, with one run in five falling back to software on the same stream,
    /// so a driver problem shows up as intermittent rather than absolute.
    /// </remarks>
    public bool HardwareDecode { get; init; } = true;

    /// <summary>How far ahead the demuxer reads, in seconds.</summary>
    /// <remarks>
    /// Two seconds is the low-latency profile's own figure and what the channel-change
    /// measurements were taken against. Raising it trades startup time for resilience to a
    /// provider that stutters.
    /// </remarks>
    public int ReadaheadSeconds { get; init; } = 2;

    public bool Deinterlace { get; init; } = true;

    /// <summary>Whether the guide is refetched when it runs out.</summary>
    public bool AutoRefreshGuide { get; init; } = true;

    /// <summary>
    /// The floor between opening streams, in seconds.
    /// </summary>
    /// <remarks>
    /// Not a preference so much as a safety limit with a preference attached. Two seconds
    /// exists because a benchmark opened 30 streams in quick succession and the provider
    /// blocked the account for hours; it can be shortened, and the reason not to is
    /// written into the settings screen.
    /// </remarks>
    public int MinimumChannelIntervalSeconds { get; init; } = 2;
}

/// <summary>
/// Reads and writes settings, one row per value.
/// </summary>
/// <remarks>
/// The <c>meta</c> table rather than a new one: it already exists for exactly this, and a
/// settings table with one row and a column per setting is a migration every time a
/// setting is added.
/// </remarks>
public static class SettingsRepository
{
    private const string VolumeKey = "settings.volume";
    private const string MutedKey = "settings.muted";
    private const string HardwareDecodeKey = "settings.hwdec";
    private const string ReadaheadKey = "settings.readahead_secs";
    private const string DeinterlaceKey = "settings.deinterlace";
    private const string AutoRefreshKey = "settings.auto_refresh_guide";
    private const string ChannelIntervalKey = "settings.channel_interval_secs";

    /// <summary>Reads the settings, falling back to defaults for anything unset.</summary>
    public static async Task<AppSettings> LoadAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM meta WHERE key LIKE 'settings.%';";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var defaults = new AppSettings();

        return new AppSettings
        {
            // Clamped on read, not only on write. A value edited into the database by hand,
            // or left behind by an older build with a wider range, must not put mpv into a
            // state the UI cannot get it out of.
            Volume = Math.Clamp(Int(values, VolumeKey, defaults.Volume), 0, 130),
            Muted = Bool(values, MutedKey, defaults.Muted),
            HardwareDecode = Bool(values, HardwareDecodeKey, defaults.HardwareDecode),
            ReadaheadSeconds = Math.Clamp(Int(values, ReadaheadKey, defaults.ReadaheadSeconds), 0, 60),
            Deinterlace = Bool(values, DeinterlaceKey, defaults.Deinterlace),
            AutoRefreshGuide = Bool(values, AutoRefreshKey, defaults.AutoRefreshGuide),
            MinimumChannelIntervalSeconds = Math.Clamp(
                Int(values, ChannelIntervalKey, defaults.MinimumChannelIntervalSeconds), 0, 60),
        };
    }

    /// <summary>Writes every setting.</summary>
    public static async Task SaveAsync(
        SqliteConnection connection,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(settings);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO meta (key, value) VALUES (@key, @value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;

            command.Parameters.Add("@key", SqliteType.Text);
            command.Parameters.Add("@value", SqliteType.Text);

            foreach (var (key, value) in Flatten(settings))
            {
                command.Parameters["@key"].Value = key;
                command.Parameters["@value"].Value = value;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<(string Key, string Value)> Flatten(AppSettings settings)
    {
        yield return (VolumeKey, settings.Volume.ToString(CultureInfo.InvariantCulture));
        yield return (MutedKey, settings.Muted ? "1" : "0");
        yield return (HardwareDecodeKey, settings.HardwareDecode ? "1" : "0");
        yield return (ReadaheadKey, settings.ReadaheadSeconds.ToString(CultureInfo.InvariantCulture));
        yield return (DeinterlaceKey, settings.Deinterlace ? "1" : "0");
        yield return (AutoRefreshKey, settings.AutoRefreshGuide ? "1" : "0");
        yield return (
            ChannelIntervalKey,
            settings.MinimumChannelIntervalSeconds.ToString(CultureInfo.InvariantCulture));
    }

    private static int Int(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var text) &&
           int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool Bool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var text) ? text == "1" : fallback;
}
