using System;
using Iptv.Core.Sources;
using Microsoft.UI.Xaml;

namespace Iptv.App;

/// <summary>
/// A channel list row, shaped for binding.
/// </summary>
/// <remarks>
/// The presentation decisions live here rather than in XAML converters, so the awkward
/// cases are visible in one place: 82% of the reference library has no guide data, and a
/// row that renders as a blank gap for four channels in five looks broken rather than
/// empty.
/// </remarks>
public sealed class ChannelRow
{
    public ChannelRow(ChannelListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        ChannelKey = item.ChannelKey;
        DisplayName = item.DisplayName;
        IsFavorite = item.IsFavorite;

        // Explicit rather than blank. "No guide data" is information; an empty line is
        // just a hole, and it is the majority case here.
        NowLine = item.NowTitle is null
            ? "no guide data"
            : item.NextTitle is null
                ? item.NowTitle
                : $"{item.NowTitle}  →  {item.NextTitle}";

        ProgressPercent = (item.NowProgress ?? 0) * 100;
        ProgressVisibility = item.NowProgress is null ? Visibility.Collapsed : Visibility.Visible;
        NowTitle = item.NowTitle;
    }

    public string ChannelKey { get; }

    public string DisplayName { get; }

    public bool IsFavorite { get; }

    /// <summary>Now and next on one line, or an explicit absence.</summary>
    public string NowLine { get; }

    public string? NowTitle { get; }

    public double ProgressPercent { get; }

    /// <summary>Hidden rather than zero-width when there is no programme to measure.</summary>
    public Visibility ProgressVisibility { get; }
}
