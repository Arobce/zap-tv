using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Iptv.App;

/// <summary>
/// Settings, and applying them to a running player.
/// </summary>
/// <remarks>
/// Most of these are mpv properties that can be set on a live handle, so they take effect
/// without restarting playback. The two that cannot — the connection limit and the guide
/// refresh — are read where they are used instead.
/// </remarks>
public sealed partial class MainWindow
{
    private AppSettings _settings = new();

    /// <summary>Loads settings and applies everything that can be applied now.</summary>
    private async Task LoadSettingsAsync()
    {
        try
        {
            await using var connection = await OpenAsync();
            _settings = await SettingsRepository.LoadAsync(connection, CancellationToken.None);

            _volume = _settings.Volume;
            _muted = _settings.Muted;

            // The account's real limit, not the hardcoded one. An account permitting four
            // was being throttled to a quarter of what it allows while the number sat
            // recorded and unread.
            var limit = await ProviderRepository.GetSafeConnectionLimitAsync(
                connection, CancellationToken.None);

            _connections = new ProviderConnectionLimiter(
                limit,
                TimeSpan.FromSeconds(_settings.MinimumChannelIntervalSeconds));

            Log($"settings: volume {_volume} hwdec {_settings.HardwareDecode} " +
                $"connections {limit} interval {_settings.MinimumChannelIntervalSeconds}s");
        }
        catch (Exception exception)
        {
            // Defaults are the values the app already ran with, so a failure here is not
            // worth stopping for.
            Log($"loading settings failed: {exception.Message}");
        }
    }

    /// <summary>Pushes the settings mpv can change on a live handle.</summary>
    private void ApplySettingsToPlayer()
    {
        if (_handle is null)
        {
            return;
        }

        _handle.SetProperty("volume", _volume.ToString(CultureInfo.InvariantCulture));
        _handle.SetProperty("mute", _muted ? "yes" : "no");
        _handle.SetProperty("deinterlace", _settings.Deinterlace ? "auto" : "no");
        _handle.SetProperty(
            "demuxer-readahead-secs",
            _settings.ReadaheadSeconds.ToString(CultureInfo.InvariantCulture));

        // hwdec is settable live, and mpv applies it to the next file rather than the one
        // playing. Said in the settings screen rather than left as a surprise.
        _handle.SetProperty("hwdec", _settings.HardwareDecode ? "auto-safe" : "no");
    }

    private async Task ShowSettingsAsync()
    {
        SettingsPanel.Visibility = Visibility.Visible;

        await using var connection = await OpenAsync();
        _settings = await SettingsRepository.LoadAsync(connection, CancellationToken.None);

        VolumeSlider.Value = _settings.Volume;
        HardwareDecodeToggle.IsOn = _settings.HardwareDecode;
        DeinterlaceToggle.IsOn = _settings.Deinterlace;
        AutoRefreshToggle.IsOn = _settings.AutoRefreshGuide;
        ReadaheadSlider.Value = _settings.ReadaheadSeconds;
        IntervalSlider.Value = _settings.MinimumChannelIntervalSeconds;

        var limit = await ProviderRepository.GetSafeConnectionLimitAsync(
            connection, CancellationToken.None);

        // Shown, not editable. It comes from the account and inventing a larger one is how
        // an account gets blocked.
        ConnectionLimitText.Text = limit == 1
            ? "Your account allows 1 concurrent stream, so channel changes are paced."
            : $"Your account allows {limit} concurrent streams.";
    }

    private async void OnSaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        _settings = _settings with
        {
            Volume = (int)VolumeSlider.Value,
            HardwareDecode = HardwareDecodeToggle.IsOn,
            Deinterlace = DeinterlaceToggle.IsOn,
            AutoRefreshGuide = AutoRefreshToggle.IsOn,
            ReadaheadSeconds = (int)ReadaheadSlider.Value,
            MinimumChannelIntervalSeconds = (int)IntervalSlider.Value,
        };

        try
        {
            await using var connection = await OpenAsync();
            await SettingsRepository.SaveAsync(connection, _settings, CancellationToken.None);

            _volume = _settings.Volume;

            // Rebuilt rather than mutated: the interval is fixed at construction, and a
            // limiter that disagreed with the setting would be worse than one that ignored
            // it, because it would look applied.
            var limit = await ProviderRepository.GetSafeConnectionLimitAsync(
                connection, CancellationToken.None);

            _connections = new ProviderConnectionLimiter(
                limit, TimeSpan.FromSeconds(_settings.MinimumChannelIntervalSeconds));

            ApplySettingsToPlayer();

            SettingsResultText.Text = "Saved.";
            Log($"settings saved: {_settings}");
        }
        catch (Exception exception)
        {
            Log($"saving settings failed: {exception}");
            SettingsResultText.Text = exception.Message;
        }
    }

    private void OnResetSettingsClicked(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        VolumeSlider.Value = defaults.Volume;
        HardwareDecodeToggle.IsOn = defaults.HardwareDecode;
        DeinterlaceToggle.IsOn = defaults.Deinterlace;
        AutoRefreshToggle.IsOn = defaults.AutoRefreshGuide;
        ReadaheadSlider.Value = defaults.ReadaheadSeconds;
        IntervalSlider.Value = defaults.MinimumChannelIntervalSeconds;

        // Not saved yet. Reset fills the form; Save is still the thing that commits, so a
        // misclick is one click from being undone.
        SettingsResultText.Text = "Defaults filled in. Save to apply.";
    }

    private async void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
        => await ShowSettingsAsync();

    private void OnCloseSettingsClicked(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Programmatic);
    }
}
