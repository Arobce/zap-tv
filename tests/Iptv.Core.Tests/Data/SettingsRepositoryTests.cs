using Iptv.Core.Data;
using Iptv.Core.Tests.Data;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Tests.Data;

/// <summary>
/// Settings, stored one row per value in the meta table.
/// </summary>
public sealed class SettingsRepositoryTests
{
    private static async Task<SqliteConnection> OpenAsync(TempDatabase db)
    {
        var connection = await db.OpenAsync();
        await Migrator.MigrateAsync(connection, CancellationToken.None);
        return connection;
    }

    private static Task<AppSettings> LoadAsync(SqliteConnection connection)
        => SettingsRepository.LoadAsync(connection, CancellationToken.None);

    private static Task SaveAsync(SqliteConnection connection, AppSettings settings)
        => SettingsRepository.SaveAsync(connection, settings, CancellationToken.None);

    [Fact]
    public async Task An_install_that_has_never_opened_settings_gets_the_old_behaviour()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // The defaults are the values the app was already running with, so this changes
        // nothing for anyone who does not go looking.
        var settings = await LoadAsync(connection);

        Assert.Equal(70, settings.Volume);
        Assert.True(settings.HardwareDecode);
        Assert.Equal(2, settings.ReadaheadSeconds);
        Assert.True(settings.Deinterlace);
        Assert.True(settings.AutoRefreshGuide);
        Assert.Equal(2, settings.MinimumChannelIntervalSeconds);
    }

    [Fact]
    public async Task Everything_round_trips()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        var saved = new AppSettings
        {
            Volume = 45,
            Muted = true,
            HardwareDecode = false,
            ReadaheadSeconds = 10,
            Deinterlace = false,
            AutoRefreshGuide = false,
            MinimumChannelIntervalSeconds = 5,
        };

        await SaveAsync(connection, saved);

        Assert.Equal(saved, await LoadAsync(connection));
    }

    [Fact]
    public async Task Saving_twice_updates_rather_than_failing()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await SaveAsync(connection, new AppSettings { Volume = 30 });
        await SaveAsync(connection, new AppSettings { Volume = 90 });

        Assert.Equal(90, (await LoadAsync(connection)).Volume);
    }

    [Theory]
    [InlineData("settings.volume", "9999", 130)]
    [InlineData("settings.volume", "-5", 0)]
    public async Task An_out_of_range_value_is_clamped_on_read(string key, string stored, int expected)
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await WriteAsync(connection, key, stored);

        // Clamped on read, not only on write. A value edited in by hand or left behind by
        // an older build must not put mpv into a state the UI cannot get it out of.
        Assert.Equal(expected, (await LoadAsync(connection)).Volume);
    }

    [Fact]
    public async Task A_value_that_is_not_a_number_falls_back_to_the_default()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        await WriteAsync(connection, "settings.readahead_secs", "not a number");

        Assert.Equal(2, (await LoadAsync(connection)).ReadaheadSeconds);
    }

    [Fact]
    public async Task Settings_do_not_disturb_the_other_meta_rows()
    {
        await using var db = new TempDatabase();
        await using var connection = await OpenAsync(db);

        // normalization_version and the EPG refresh timestamp live here too. A settings
        // save that cleared the table would silently reset both.
        await SaveAsync(connection, new AppSettings { Volume = 55 });

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'normalization_version';";

        Assert.Equal("1", await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task WriteAsync(SqliteConnection connection, string key, string value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO meta (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
