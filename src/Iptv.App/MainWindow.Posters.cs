using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Iptv.App;

/// <summary>
/// Switching the library list between a list of rows and a grid of posters.
/// </summary>
/// <remarks>
/// One ItemsRepeater with two templates rather than two repeaters. Both views scroll the
/// same rows and answer the same clicks, and a second repeater would mean two copies of
/// the selection, the keyboard stepping and the bring-into-view.
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>The sidebar's width while showing a list.</summary>
    private const double ListSidebarWidth = 380;

    /// <summary>
    /// And while showing posters.
    /// </summary>
    /// <remarks>
    /// Wider because three columns of artwork is not a catalogue, it is a list with
    /// pictures. At 560 the grid holds four across, which is enough for the eye to scan
    /// covers rather than read titles - which is the entire reason to show covers.
    /// </remarks>
    private const double PosterSidebarWidth = 560;

    private DataTemplate? _rowTemplate;
    private bool _posterMode;

    private double SidebarWidth => _posterMode ? PosterSidebarWidth : ListSidebarWidth;

    /// <summary>Draws the current rows as posters, or as a list.</summary>
    private void SetPosterMode(bool posters)
    {
        // Captured on first use rather than declared as a resource. The row template is
        // the repeater's own, and moving it into the dictionary to name it would leave the
        // default view configured from two places.
        _rowTemplate ??= ChannelList.ItemTemplate as DataTemplate;

        if (_posterMode == posters && ChannelList.ItemTemplate is not null)
        {
            return;
        }

        _posterMode = posters;

        ChannelList.ItemTemplate = posters
            ? (DataTemplate)RootGrid.Resources["PosterTemplate"]
            : _rowTemplate;

        ChannelList.Layout = posters
            ? new UniformGridLayout
            {
                // Measured from the first item, so every tile gets the template's own
                // 120px plus its margin instead of a number repeated here.
                ItemsStretch = UniformGridLayoutItemsStretch.None,
                MinItemWidth = 130,
                MinItemHeight = 220,
                MinRowSpacing = 4,
                MinColumnSpacing = 4,
            }
            : new StackLayout();

        // Not while fullscreen: the sidebar is collapsed to zero there, and writing a
        // width would bring it back over the video.
        if (!_fullScreen)
        {
            SidebarColumn.Width = new GridLength(SidebarWidth);
        }
    }
}
