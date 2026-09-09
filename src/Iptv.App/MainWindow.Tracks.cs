using System;
using System.Collections.Generic;
using System.Globalization;
using Iptv.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Iptv.App;

/// <summary>
/// Audio and subtitle track selection.
/// </summary>
/// <remarks>
/// Read from mpv one property at a time rather than as a single node. The handle exposes
/// strings, and mpv answers <c>track-list/0/lang</c> as readily as it answers the whole
/// list — so the alternative would be a second P/Invoke surface and a node parser to save
/// a handful of reads that happen once per file.
/// </remarks>
public sealed partial class MainWindow
{
    private IReadOnlyList<MediaTrack> _tracks = [];

    /// <summary>Re-reads the track list and shows or hides the two buttons.</summary>
    /// <remarks>
    /// Called after the first frame rather than on load: mpv publishes the track list once
    /// the demuxer has read the headers, and asking before that reports a count of zero on
    /// a file that has both.
    /// </remarks>
    private void RefreshTracks()
    {
        _tracks = ReadTracks();

        var audio = TrackMenu.For(TrackKind.Audio, _tracks);
        var subtitles = TrackMenu.For(TrackKind.Subtitle, _tracks);

        AudioTrackButton.Visibility = audio.IsUseful ? Visibility.Visible : Visibility.Collapsed;
        SubtitleTrackButton.Visibility = subtitles.IsUseful
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (audio.IsUseful || subtitles.IsUseful)
        {
            Log($"tracks: {audio.Tracks.Count} audio, {subtitles.Tracks.Count} subtitle");
        }
    }

    /// <summary>Forgets the previous file's tracks.</summary>
    /// <remarks>
    /// Called when a stream is opened, not when one ends. Between the two, the buttons
    /// would otherwise offer the last channel's languages against the new one's audio and
    /// silently select nothing.
    /// </remarks>
    private void ClearTracks()
    {
        _tracks = [];
        AudioTrackButton.Visibility = Visibility.Collapsed;
        SubtitleTrackButton.Visibility = Visibility.Collapsed;
    }

    private IReadOnlyList<MediaTrack> ReadTracks()
    {
        if (_handle is null)
        {
            return [];
        }

        try
        {
            var raw = _handle.GetProperty("track-list/count");

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return [];
            }

            var tracks = new List<MediaTrack>(count);

            for (var index = 0; index < count; index++)
            {
                if (ReadTrack(index) is { } track)
                {
                    tracks.Add(track);
                }
            }

            return tracks;
        }
        catch (Exception exception)
        {
            // A track list is a convenience on top of playback that is already working.
            Log($"reading tracks failed: {exception.Message}");
            return [];
        }
    }

    private MediaTrack? ReadTrack(int index)
    {
        var prefix = $"track-list/{index.ToString(CultureInfo.InvariantCulture)}/";

        var kind = _handle!.GetProperty(prefix + "type") switch
        {
            "audio" => TrackKind.Audio,
            "sub" => TrackKind.Subtitle,

            // Video included. There is nothing to choose between video tracks here and
            // offering the choice would only be a way to turn the picture off.
            _ => (TrackKind?)null,
        };

        if (kind is not { } trackKind ||
            !int.TryParse(
                _handle.GetProperty(prefix + "id"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var id))
        {
            return null;
        }

        return new MediaTrack
        {
            Id = id,
            Kind = trackKind,
            Title = _handle.GetProperty(prefix + "title"),
            Language = _handle.GetProperty(prefix + "lang"),
            Selected = string.Equals(
                _handle.GetProperty(prefix + "selected"), "yes", StringComparison.Ordinal),
        };
    }

    private void OnAudioTracksClicked(object sender, RoutedEventArgs e)
        => ShowTrackMenu(sender, TrackKind.Audio);

    private void OnSubtitleTracksClicked(object sender, RoutedEventArgs e)
        => ShowTrackMenu(sender, TrackKind.Subtitle);

    /// <summary>Opens the picker for one kind.</summary>
    /// <remarks>
    /// Built on each click rather than kept: the list changes with every file, and a menu
    /// assembled once would offer the previous channel's languages on this one.
    /// </remarks>
    private void ShowTrackMenu(object sender, TrackKind kind)
    {
        if (sender is not FrameworkElement anchor)
        {
            return;
        }

        // Re-read first. The button was shown on the strength of a list that may be a
        // channel change old by the time it is pressed.
        _tracks = ReadTracks();

        var menu = TrackMenu.For(kind, _tracks);
        var flyout = new MenuFlyout();

        if (kind == TrackKind.Subtitle)
        {
            flyout.Items.Add(Entry("Off", TrackMenu.NoTrack, menu.SelectedId == TrackMenu.NoTrack));
        }

        foreach (var track in menu.Tracks)
        {
            flyout.Items.Add(Entry(track.Label, track.Id, track.Selected));
        }

        if (flyout.Items.Count == 0)
        {
            return;
        }

        flyout.ShowAt(anchor);

        MenuFlyoutItem Entry(string label, int id, bool selected) =>
            CreateTrackItem(kind, label, id, selected);
    }

    /// <summary>One row of the picker.</summary>
    /// <remarks>
    /// The tick is a prefix rather than a ToggleMenuFlyoutItem because these are radio
    /// choices, and a menu of checkboxes invites unticking the one that is playing — which
    /// would mean nothing for audio.
    /// </remarks>
    private MenuFlyoutItem CreateTrackItem(TrackKind kind, string label, int id, bool selected)
    {
        var item = new MenuFlyoutItem { Text = selected ? $"✓  {label}" : $"     {label}" };

        item.Click += (_, _) => SelectTrack(kind, id, label);

        return item;
    }

    private void SelectTrack(TrackKind kind, int id, string label)
    {
        if (_handle is null)
        {
            return;
        }

        var property = kind == TrackKind.Audio ? "aid" : "sid";

        try
        {
            _handle.SetProperty(property, TrackMenu.PropertyValue(id));

            Toast(kind == TrackKind.Audio ? $"Audio: {label}" : $"Subtitles: {label}");
            Log($"{property} set to {TrackMenu.PropertyValue(id)}");
        }
        catch (Exception exception)
        {
            // A track mpv listed but cannot open - a subtitle codec it lacks, usually.
            Log($"selecting {property} {id} failed: {exception.Message}");
            Toast("That track could not be opened");
        }
    }
}
