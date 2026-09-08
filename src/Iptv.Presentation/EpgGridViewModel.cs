using Iptv.Core.Data;
using Iptv.Core.Epg;

namespace Iptv.Presentation;

/// <summary>One programme, positioned.</summary>
public sealed record EpgBlockLayout
{
    public required EpgGridBlock Block { get; init; }

    /// <summary>Index into the channel list, so the view can recycle by row.</summary>
    public required int RowIndex { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }
}

/// <summary>What the view should draw for the current scroll position.</summary>
public sealed record EpgViewport
{
    public required IReadOnlyList<EpgBlockLayout> Blocks { get; init; }

    /// <summary>The channel rows in the vertical window, with their Y positions.</summary>
    public required IReadOnlyList<(EpgGridRepository.EpgChannel Channel, double Y)> Rows { get; init; }

    /// <summary>Hour marks intersecting the horizontal window, with their X positions.</summary>
    public required IReadOnlyList<(DateTimeOffset Time, double X)> HourMarks { get; init; }
}

/// <summary>
/// The EPG grid: what to draw, and where, for a given scroll position.
/// </summary>
/// <remarks>
/// <para>
/// The layout arithmetic lives here rather than in the panel so it can be tested. Deciding
/// which rectangle of a 3,450 by 30-hour surface is visible, and turning times into pixels,
/// is exactly the kind of off-by-one that is invisible on screen until it is not.
/// </para>
/// <para>
/// Both axes are realized explicitly, per the PRD: only the channel rows in view plus a
/// buffer, and only the programmes overlapping the visible time window. Nothing loads the
/// whole guide.
/// </para>
/// </remarks>
public sealed class EpgGridViewModel
{
    private readonly SqliteConnectionFactory _factory;

    private IReadOnlyList<EpgGridRepository.EpgChannel> _channels = [];

    public EpgGridViewModel(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>Horizontal scale. 6px per minute is 360px an hour.</summary>
    /// <remarks>
    /// Chosen so a half-hour programme is 180px — wide enough for a readable title, narrow
    /// enough that two hours fit on a 1080p screen beside the channel column.
    /// </remarks>
    public double PixelsPerMinute { get; init; } = 6;

    public double RowHeight { get; init; } = 44;

    /// <summary>
    /// Extra rows realized above and below the viewport.
    /// </summary>
    /// <remarks>
    /// Without a buffer, a row appears only once its top edge crosses into view, which
    /// reads as blocks popping in at the edge during a scroll.
    /// </remarks>
    public int RowBuffer { get; init; } = 4;

    /// <summary>Extra minutes realized to either side of the visible time window.</summary>
    public int MinuteBuffer { get; init; } = 30;

    /// <summary>The earliest time the guide covers, or null when there is no guide.</summary>
    public DateTimeOffset? Start { get; private set; }

    public DateTimeOffset? End { get; private set; }

    public IReadOnlyList<EpgGridRepository.EpgChannel> Channels => _channels;

    /// <summary>Total scrollable width, in pixels.</summary>
    public double TotalWidth => Start is { } from && End is { } to
        ? Math.Max(0, (to - from).TotalMinutes * PixelsPerMinute)
        : 0;

    public double TotalHeight => _channels.Count * RowHeight;

    /// <summary>Whether there is anything to draw at all.</summary>
    public bool HasGuide => Start is not null && _channels.Count > 0;

    /// <summary>Reads the guide's bounds and the channels that have one.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Bounded to what the guide actually covers rather than to a fixed horizon. This
        // provider publishes about 2.7 days; a fortnight of columns would be thirteen days
        // of blank. See docs/decisions/0010.
        var (from, to) = await EpgGridRepository.GetCoverageAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        Start = from;
        End = to;

        _channels = await EpgGridRepository.GetGridChannelsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Where a time sits on the horizontal axis.</summary>
    public double XOf(DateTimeOffset time)
        => Start is { } from ? (time - from).TotalMinutes * PixelsPerMinute : 0;

    /// <summary>Which time a horizontal offset corresponds to.</summary>
    public DateTimeOffset TimeAt(double x)
        => Start is { } from ? from.AddMinutes(x / PixelsPerMinute) : DateTimeOffset.MinValue;

    /// <summary>
    /// Reads and positions everything visible at this scroll position.
    /// </summary>
    /// <remarks>
    /// Called per scroll frame. The query behind it costs 3ms for a 60-row, 2-hour
    /// rectangle against 164,661 programmes, which is why the window is re-read rather
    /// than cached.
    /// </remarks>
    public async Task<EpgViewport> RealizeAsync(
        double horizontalOffset,
        double verticalOffset,
        double viewportWidth,
        double viewportHeight,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!HasGuide || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return Empty();
        }

        // Vertical window: the rows on screen, plus a buffer, clamped to the list.
        var firstRow = Math.Max(0, (int)Math.Floor(verticalOffset / RowHeight) - RowBuffer);
        var lastRow = Math.Min(
            _channels.Count - 1,
            (int)Math.Ceiling((verticalOffset + viewportHeight) / RowHeight) + RowBuffer);

        if (lastRow < firstRow)
        {
            return Empty();
        }

        var take = Math.Min(lastRow - firstRow + 1, EpgGridRepository.MaxRows);
        var page = _channels.Skip(firstRow).Take(take).ToList();

        // Horizontal window, clamped to the guide so a scroll to the end does not ask for
        // hours the provider never published.
        var from = Clamp(TimeAt(horizontalOffset).AddMinutes(-MinuteBuffer));
        var to = Clamp(TimeAt(horizontalOffset + viewportWidth).AddMinutes(MinuteBuffer));

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await EpgGridRepository.GetWindowAsync(
            connection,
            page.Select(c => c.ChannelKey).ToList(),
            from,
            to,
            now,
            cancellationToken).ConfigureAwait(false);

        var blocks = new List<EpgBlockLayout>();

        for (var i = 0; i < rows.Count; i++)
        {
            var rowIndex = firstRow + i;
            var y = rowIndex * RowHeight;

            foreach (var programme in rows[i].Programmes)
            {
                var x = XOf(programme.Start);
                var width = (programme.Stop - programme.Start).TotalMinutes * PixelsPerMinute;

                blocks.Add(new EpgBlockLayout
                {
                    Block = programme,
                    RowIndex = rowIndex,
                    X = x,
                    Y = y,

                    // A one-minute programme would otherwise be six pixels of unreadable
                    // sliver. Overlapping its neighbour slightly is the better trade.
                    Width = Math.Max(width, 24),
                    Height = RowHeight - 2,
                });
            }
        }

        return new EpgViewport
        {
            Blocks = blocks,
            Rows = page.Select((c, i) => (c, (firstRow + i) * RowHeight)).ToList(),
            HourMarks = HourMarksBetween(from, to).ToList(),
        };
    }

    /// <summary>Hour boundaries inside a window, for the time header and its gridlines.</summary>
    private IEnumerable<(DateTimeOffset Time, double X)> HourMarksBetween(
        DateTimeOffset from,
        DateTimeOffset to)
    {
        // Rounded down to the hour so the marks land on o'clock rather than on wherever
        // the scroll happens to have stopped.
        var mark = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, 0, 0, from.Offset);

        while (mark <= to)
        {
            yield return (mark, XOf(mark));
            mark = mark.AddHours(1);
        }
    }

    private DateTimeOffset Clamp(DateTimeOffset time)
    {
        if (Start is not { } from || End is not { } to)
        {
            return time;
        }

        return time < from ? from : time > to ? to : time;
    }

    private static EpgViewport Empty() => new()
    {
        Blocks = [],
        Rows = [],
        HourMarks = [],
    };
}
