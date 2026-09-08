using System;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Mpv;
using Iptv.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Iptv.App;

/// <summary>
/// The diagnostics panel.
/// </summary>
/// <remarks>
/// Most of it comes from the view model, which is tested. What is added here is the part
/// only a running window knows: which decoder mpv actually chose, and whether the render
/// thread is alive.
/// </remarks>
public sealed partial class MainWindow
{
    private DiagnosticsViewModel? _diagnostics;

    private DiagnosticsViewModel Diagnostics => _diagnostics ??= new DiagnosticsViewModel(
        new SqliteConnectionFactory(_databasePath),
        new DpapiSecretProtector());

    private async Task ShowDiagnosticsAsync()
    {
        DiagnosticsPanel.Visibility = Visibility.Visible;
        await RefreshDiagnosticsAsync();
    }

    private async Task RefreshDiagnosticsAsync()
    {
        DiagnosticsContent.Children.Clear();

        try
        {
            var sections = await Diagnostics.BuildAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            foreach (var section in sections)
            {
                AddSection(section.Title, section.Rows);
            }

            AddSection("Playback engine", DescribeEngine());
        }
        catch (Exception exception)
        {
            Log($"diagnostics failed: {exception}");
            DiagnosticsContent.Children.Add(new TextBlock
            {
                Text = exception.Message,
                Foreground = (Brush)RootGrid.Resources["ZapAccent"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    /// <summary>What the running player is actually doing.</summary>
    /// <remarks>
    /// Read live rather than stored. Measurement showed hardware decode is not
    /// deterministic — one run in five falls back to software on the same stream — so a
    /// value captured at startup would misreport the current state.
    /// </remarks>
    private DiagnosticsRow[] DescribeEngine()
    {
        if (_presenter is not { } presenter)
        {
            return [new DiagnosticsRow { Label = "Video", Value = "not started", IsWarning = true }];
        }

        var decoder = _handle?.GetProperty("hwdec-current");
        var hardware = !string.IsNullOrWhiteSpace(decoder) &&
                       !decoder.Equals("no", StringComparison.OrdinalIgnoreCase);

        return
        [
            new DiagnosticsRow
            {
                Label = "Presentation",
                Value = $"{presenter.Backend} · {presenter.BackendDetail}",
                IsWarning = presenter.Backend != VideoBackend.Hardware,
            },
            new DiagnosticsRow
            {
                Label = "Decoder",

                // Named rather than reduced to hardware or software. "nvdec" and
                // "d3d11va-copy" are both hardware and behave very differently, and a
                // check that matched only d3d11 once reported nvdec as SOFTWARE.
                Value = hardware ? $"{decoder} (hardware)" : "software",
                IsWarning = !hardware && presenter.FramesPresented > 0,
            },
            new DiagnosticsRow
            {
                Label = "Frames presented",
                Value = $"{presenter.FramesPresented:N0}",
            },
            new DiagnosticsRow
            {
                Label = "Render thread",

                // A dead render thread and an idle one look identical from outside: no
                // frames, no error. This is the difference.
                Value = presenter.Fault is { } fault ? fault.Message : "healthy",
                IsWarning = presenter.Fault is not null,
            },
            new DiagnosticsRow
            {
                Label = "Composition scale",
                Value = presenter.ScaleFailed is { } scaleFault
                    ? scaleFault.Message
                    : $"{presenter.CompositionScaleApplied:F2}x",
                IsWarning = presenter.ScaleFailed is not null,
            },
        ];
    }

    private void AddSection(string title, System.Collections.Generic.IReadOnlyList<DiagnosticsRow> rows)
    {
        DiagnosticsContent.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 4),
            Foreground = (Brush)RootGrid.Resources["ZapText"],
        });

        foreach (var row in rows)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = row.Label,
                FontSize = 13,
                Foreground = (Brush)RootGrid.Resources["ZapTextDim"],
            };

            var value = new TextBlock
            {
                Text = row.Value,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,

                // The accent, on the readings that mean something is wrong. A page of
                // numbers where nothing stands out is a page nobody reads.
                Foreground = row.IsWarning
                    ? (Brush)RootGrid.Resources["ZapAccent"]
                    : (Brush)RootGrid.Resources["ZapText"],
            };

            Grid.SetColumn(value, 1);
            line.Children.Add(label);
            line.Children.Add(value);

            DiagnosticsContent.Children.Add(line);
        }
    }

    private async void OnOpenDiagnosticsClicked(object sender, RoutedEventArgs e)
        => await ShowDiagnosticsAsync();

    private async void OnRefreshDiagnosticsClicked(object sender, RoutedEventArgs e)
        => await RefreshDiagnosticsAsync();

    private void OnCloseDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        DiagnosticsPanel.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Programmatic);
    }
}
