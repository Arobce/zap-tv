using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Iptv.Core.Data;
using Iptv.Presentation;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Iptv.App;

/// <summary>
/// The EPG grid, virtualized on both axes.
/// </summary>
/// <remarks>
/// <para>
/// A Canvas sized to the whole guide inside a ScrollViewer, with only the visible blocks
/// realized onto it. The alternative — binding a control to the programmes — would
/// materialise 164,661 items to draw perhaps forty.
/// </para>
/// <para>
/// Containers are pooled and reused. Allocating a Border and two TextBlocks per block per
/// scroll frame is the difference between a grid that scrolls and one that stutters, and
/// it is the specific thing the PRD asks for.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private EpgGridViewModel? _epg;

    /// <summary>Reused block containers. Never shrinks; a scroll never allocates.</summary>
    private readonly List<Border> _epgBlockPool = [];

    /// <summary>Reused channel-name containers, on the same principle.</summary>
    private readonly List<TextBlock> _epgChannelPool = [];

    private readonly List<TextBlock> _epgHourPool = [];

    /// <summary>Guards re-entrant realization while a query is in flight.</summary>
    /// <remarks>
    /// ViewChanged fires many times per scroll. Without this, a slow frame would start a
    /// second read before the first finished and the two would race to position the same
    /// pooled containers.
    /// </remarks>
    private bool _epgRealizing;

    private bool _epgRealizePending;

    private EpgGridViewModel Epg => _epg ??= new EpgGridViewModel(
        new SqliteConnectionFactory(_databasePath));

    private async Task ShowEpgAsync()
    {
        EpgPanel.Visibility = Visibility.Visible;

        try
        {
            await Epg.LoadAsync(CancellationToken.None);

            if (!Epg.HasGuide)
            {
                // Says which of the two reasons it is. "No guide" covering both an
                // un-ingested library and a provider that publishes none is unhelpful.
                EpgEmptyText.Text =
                    "No guide loaded. Sync a provider, then ingest its EPG. If the guide is "
                    + "more than a day or two old it will also read as empty: this provider "
                    + "publishes about 24 hours ahead.";
                EpgEmptyText.Visibility = Visibility.Visible;
                EpgSummary.Text = string.Empty;
                return;
            }

            EpgEmptyText.Visibility = Visibility.Collapsed;

            EpgBodyCanvas.Width = Epg.TotalWidth;
            EpgBodyCanvas.Height = Epg.TotalHeight;
            EpgHeaderCanvas.Width = Epg.TotalWidth;
            EpgChannelCanvas.Height = Epg.TotalHeight;

            var span = Epg.End!.Value - Epg.Start!.Value;
            EpgSummary.Text =
                $"{Epg.Channels.Count:N0} channels · {Epg.Start:ddd HH:mm} to {Epg.End:ddd HH:mm} " +
                $"({span.TotalHours:F0}h)";

            ScrollEpgToNow();
            await RealizeEpgAsync();
        }
        catch (Exception exception)
        {
            Log($"opening the guide failed: {exception}");
            EpgEmptyText.Text = exception.Message;
            EpgEmptyText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Puts now a little in from the left edge.</summary>
    /// <remarks>
    /// Not hard against it: the programme on air started before now, and pinning now to
    /// zero would clip the block the viewer is most likely looking for.
    /// </remarks>
    private void ScrollEpgToNow()
    {
        var x = Math.Max(0, Epg.XOf(DateTimeOffset.UtcNow) - 120);
        EpgBodyScroll.ChangeView(x, 0, null, disableAnimation: true);
    }

    private async void OnEpgViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // The header and column follow the body rather than scrolling themselves, so they
        // cannot drift out of alignment with it.
        EpgHeaderScroll.ChangeView(EpgBodyScroll.HorizontalOffset, null, null, true);
        EpgChannelScroll.ChangeView(null, EpgBodyScroll.VerticalOffset, null, true);

        await RealizeEpgAsync();
    }

    private async Task RealizeEpgAsync()
    {
        if (_epgRealizing)
        {
            // Coalesced rather than queued: only the latest scroll position matters, and
            // draining a backlog of stale ones would draw the guide repeatedly behind the
            // pointer.
            _epgRealizePending = true;
            return;
        }

        _epgRealizing = true;

        try
        {
            do
            {
                _epgRealizePending = false;

                var viewport = await Epg.RealizeAsync(
                    EpgBodyScroll.HorizontalOffset,
                    EpgBodyScroll.VerticalOffset,
                    EpgBodyScroll.ViewportWidth,
                    EpgBodyScroll.ViewportHeight,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);

                Draw(viewport);
            }
            while (_epgRealizePending);
        }
        catch (Exception exception)
        {
            Log($"realizing the guide failed: {exception}");
        }
        finally
        {
            _epgRealizing = false;
        }
    }

    private void Draw(EpgViewport viewport)
    {
        DrawBlocks(viewport);
        DrawChannels(viewport);
        DrawHours(viewport);
    }

    private void DrawBlocks(EpgViewport viewport)
    {
        for (var i = 0; i < viewport.Blocks.Count; i++)
        {
            var layout = viewport.Blocks[i];
            var container = BlockAt(i);

            Canvas.SetLeft(container, layout.X);
            Canvas.SetTop(container, layout.Y);
            container.Width = layout.Width;
            container.Height = layout.Height;
            container.Visibility = Visibility.Visible;

            // The one place the accent is earned on this screen: what is on now.
            container.Background = layout.Block.IsNow
                ? (Brush)RootGrid.Resources["ZapAccent"]
                : (Brush)RootGrid.Resources["ZapSurface"];

            var stack = (StackPanel)container.Child;
            var title = (TextBlock)stack.Children[0];
            var time = (TextBlock)stack.Children[1];

            title.Text = layout.Block.Title;
            title.Foreground = layout.Block.IsNow
                ? new SolidColorBrush(Colors.Black)
                : (Brush)RootGrid.Resources["ZapText"];

            time.Text = $"{layout.Block.Start.ToLocalTime():HH:mm}";
            time.Foreground = layout.Block.IsNow
                ? new SolidColorBrush(Colors.Black)
                : (Brush)RootGrid.Resources["ZapTextFaint"];
        }

        Hide(_epgBlockPool, viewport.Blocks.Count);
    }

    private void DrawChannels(EpgViewport viewport)
    {
        for (var i = 0; i < viewport.Rows.Count; i++)
        {
            var (channel, y) = viewport.Rows[i];
            var label = ChannelAt(i);

            Canvas.SetTop(label, y + 6);
            label.Text = channel.DisplayName;
            label.Foreground = channel.IsFavorite
                ? (Brush)RootGrid.Resources["ZapAccent"]
                : (Brush)RootGrid.Resources["ZapText"];

            label.Visibility = Visibility.Visible;
        }

        Hide(_epgChannelPool, viewport.Rows.Count);
    }

    private void DrawHours(EpgViewport viewport)
    {
        for (var i = 0; i < viewport.HourMarks.Count; i++)
        {
            var (time, x) = viewport.HourMarks[i];
            var mark = HourAt(i);

            Canvas.SetLeft(mark, x + 4);
            mark.Text = time.ToLocalTime().ToString("HH:mm");
            mark.Visibility = Visibility.Visible;
        }

        Hide(_epgHourPool, viewport.HourMarks.Count);
    }

    private static void Hide<T>(List<T> pool, int used)
        where T : UIElement
    {
        // Hidden rather than removed. Taking them off the canvas would mean adding them
        // back on the next frame, which is the allocation this pool exists to avoid.
        for (var i = used; i < pool.Count; i++)
        {
            pool[i].Visibility = Visibility.Collapsed;
        }
    }

    private Border BlockAt(int index)
    {
        while (_epgBlockPool.Count <= index)
        {
            var title = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };

            var time = new TextBlock { FontSize = 10 };

            var container = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 3, 4, 3),
                Margin = new Thickness(0, 0, 2, 0),
                Child = new StackPanel { Children = { title, time } },
            };

            _epgBlockPool.Add(container);
            EpgBodyCanvas.Children.Add(container);
        }

        return _epgBlockPool[index];
    }

    private TextBlock ChannelAt(int index)
    {
        while (_epgChannelPool.Count <= index)
        {
            var label = new TextBlock
            {
                FontSize = 12,
                Width = 206,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };

            _epgChannelPool.Add(label);
            EpgChannelCanvas.Children.Add(label);
        }

        return _epgChannelPool[index];
    }

    private TextBlock HourAt(int index)
    {
        while (_epgHourPool.Count <= index)
        {
            var mark = new TextBlock
            {
                FontSize = 11,
                Margin = new Thickness(0, 7, 0, 0),
                Foreground = (Brush)RootGrid.Resources["ZapTextDim"],
            };

            _epgHourPool.Add(mark);
            EpgHeaderCanvas.Children.Add(mark);
        }

        return _epgHourPool[index];
    }

    private async void OnEpgNowClicked(object sender, RoutedEventArgs e)
    {
        ScrollEpgToNow();
        await RealizeEpgAsync();
    }

    private async void OnOpenEpgClicked(object sender, RoutedEventArgs e)
        => await ShowEpgAsync();

    private void OnCloseEpgClicked(object sender, RoutedEventArgs e)
    {
        EpgPanel.Visibility = Visibility.Collapsed;
        RootGrid.Focus(FocusState.Programmatic);
    }
}
