using System;
using System.Globalization;
using System.Threading.Tasks;
using Iptv.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

// Both namespaces declare one, and this file needs VirtualKey from Windows.System as well.
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Iptv.App;

/// <summary>
/// The transport bar, seeking, and typing a number to jump.
/// </summary>
/// <remarks>
/// Everything here is about a viewer holding a keyboard or a remote rather than a mouse.
/// A Windows HTPC remote presents as a keyboard, which is why the media keys sit in the
/// same switch as the letters rather than behind a separate input path.
/// </remarks>
public sealed partial class MainWindow
{
    private readonly NumberEntry _numbers = new();
    private DispatcherQueueTimer? _numberTimer;
    private DispatcherQueueTimer? _transportTick;

    /// <summary>True while the viewer is dragging the seek bar.</summary>
    /// <remarks>
    /// The tick writes the slider's position once a second, and a drag writes it many
    /// times a second. Without this the thumb springs back to where playback still is
    /// every time the tick lands, which reads as a slider fighting the hand on it.
    /// </remarks>
    private bool _scrubbing;

    private TransportState _transport = TransportState.Hidden;

    /// <summary>Starts the two timers this file owns.</summary>
    private void StartTransport()
    {
        // A second. The bar shows whole seconds, so anything faster redraws the same text.
        _transportTick = DispatcherQueue.CreateTimer();
        _transportTick.Interval = TimeSpan.FromSeconds(1);
        _transportTick.Tick += (_, _) => RefreshTransport();
        _transportTick.Start();

        _numberTimer = DispatcherQueue.CreateTimer();
        _numberTimer.Interval = NumberEntry.Timeout;
        _numberTimer.IsRepeating = false;
        _numberTimer.Tick += async (_, _) =>
            await CommitNumberAsync(_numbers.Expire(DateTimeOffset.UtcNow));

        // Handled even when the thumb has already handled it: the thumb marks the press
        // handled, so a plain bubbling handler would never see a drag start.
        SeekSlider.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSeekStarted),
            handledEventsToo: true);
    }

    /// <summary>Reads mpv's clock and redraws the bar.</summary>
    private void RefreshTransport()
    {
        // No resume key means live, which is the same condition that decides whether a
        // position is worth writing down. Both are asking "does a position mean anything
        // here", so they read one answer rather than two that can drift apart.
        var live = _resumeKey is null;

        var position = TryReadSeconds("time-pos", out var seconds) ? seconds : (int?)null;
        var duration = TryReadSeconds("duration", out var length) ? length : (int?)null;

        _transport = _handle is null || !_firstFrameSeen
            ? TransportState.Hidden
            : TransportState.For(live, position, duration);

        TransportBar.Visibility = _transport.IsVisible ? Visibility.Visible : Visibility.Collapsed;

        if (!_transport.IsVisible)
        {
            return;
        }

        PositionText.Text = _transport.PositionText;
        DurationText.Text = _transport.DurationText;
        SeekSlider.IsEnabled = _transport.CanSeek;

        if (!_scrubbing && _transport.CanSeek)
        {
            SeekSlider.Value = _transport.Fraction * SeekSlider.Maximum;
        }
    }

    private void OnSeekStarted(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

    /// <summary>Seeks to where the thumb was let go.</summary>
    /// <remarks>
    /// On release rather than on every value change: a seek is a demuxer flush and a
    /// keyframe hunt, and issuing one per pixel of drag makes the picture unwatchable for
    /// the length of the gesture and lands late on top of it.
    /// </remarks>
    private void OnSeekCommitted(object sender, PointerRoutedEventArgs e) => CommitSeek();

    private void OnSeekKeyUp(object sender, KeyRoutedEventArgs e) => CommitSeek();

    private void CommitSeek()
    {
        _scrubbing = false;

        if (_handle is null || !_transport.CanSeek)
        {
            return;
        }

        var target = _transport.SeekTarget(SeekSlider.Value / SeekSlider.Maximum);

        Seek(target, relative: false);
        Toast(TransportState.Format(target));
    }

    /// <summary>Moves playback, absolutely or by an offset.</summary>
    private void Seek(int seconds, bool relative)
    {
        if (_handle is null || _resumeKey is null)
        {
            // Live is not seekable. The provider serves the edge of the broadcast, so a
            // seek there either does nothing or drops the connection.
            return;
        }

        try
        {
            _handle.Command(
                "seek",
                seconds.ToString(CultureInfo.InvariantCulture),
                relative ? "relative" : "absolute");
        }
        catch (Exception exception)
        {
            // Seeking past the end of a stream whose duration the provider overstated
            // throws rather than clamping. Losing playback over it would be worse.
            Log($"seek failed: {exception.Message}");
        }
    }

    /// <summary>J and L, and a remote's skip buttons.</summary>
    private void SeekRelative(int seconds)
    {
        if (_resumeKey is null)
        {
            Toast("Live");
            return;
        }

        Seek(seconds, relative: true);
        Toast(seconds > 0 ? $"+{seconds}s" : $"{seconds}s");
        RefreshTransport();
    }

    /// <summary>The digit a key stands for, or null if it is not a digit key.</summary>
    /// <remarks>
    /// The number pad counts. It is the half of the keyboard an HTPC remote actually maps
    /// its number buttons onto, and a viewer typing 42 on the pad means the same thing as
    /// one typing it on the top row.
    /// </remarks>
    private static int? DigitOf(VirtualKey key) => key switch
    {
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => key - VirtualKey.Number0,
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => key - VirtualKey.NumberPad0,
        _ => null,
    };

    /// <summary>Takes a typed digit and either shows it or acts on it.</summary>
    private async Task PressDigitAsync(int digit)
    {
        var result = _numbers.Press(digit, _rows.Count, DateTimeOffset.UtcNow);

        _numberTimer?.Stop();

        if (result.Committed)
        {
            await CommitNumberAsync(result);
            return;
        }

        NumberEntryText.Text = result.Display;
        NumberEntryPanel.Visibility = result.Display.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (result.Display.Length > 0)
        {
            _numberTimer?.Start();
        }
    }

    /// <summary>Jumps to a committed position, if it is one that can be opened.</summary>
    private async Task CommitNumberAsync(NumberEntryResult result)
    {
        _numberTimer?.Stop();
        NumberEntryPanel.Visibility = Visibility.Collapsed;

        if (!result.Committed)
        {
            return;
        }

        var index = result.Position - 1;

        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index];

        BringRowIntoView(index);

        if (!row.Playable)
        {
            // A separator: the provider's own heading, with nothing behind it. Scrolled to
            // anyway, because that is still where the viewer asked to be.
            Toast(row.Title);
            return;
        }

        await PlayAsync(row);
    }

    /// <summary>Scrolls a row into view in a virtualised list.</summary>
    /// <remarks>
    /// ItemsRepeater has no ScrollIntoView. Realising the element and asking it to bring
    /// itself in is the way, and it is the cost of the repeater being used at all: a row
    /// four thousand down has no container until something asks for one.
    /// </remarks>
    private void BringRowIntoView(int index)
    {
        try
        {
            if (ChannelList.GetOrCreateElement(index) is { } element)
            {
                // Without this the element has not been measured, so it brings its
                // unmeasured self and the scroll lands in the wrong place.
                ChannelList.UpdateLayout();
                element.StartBringIntoView();
            }
        }
        catch (Exception exception)
        {
            // Scrolling is a convenience. It must not cost the jump that asked for it.
            Log($"scrolling to row {index} failed: {exception.Message}");
        }
    }
}
