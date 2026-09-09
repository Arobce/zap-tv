using Iptv.Presentation;

namespace Iptv.Presentation.Tests;

public sealed class MediaTrackTests
{
    private static MediaTrack Track(
        int id,
        TrackKind kind = TrackKind.Audio,
        string? title = null,
        string? language = null,
        bool selected = false)
        => new()
        {
            Id = id,
            Kind = kind,
            Title = title,
            Language = language,
            Selected = selected,
        };

    [Fact]
    public void A_named_track_keeps_its_language_alongside_the_name()
        => Assert.Equal(
            "Commentary (ENG)",
            Track(1, title: "Commentary", language: "eng").Label);

    [Fact]
    public void A_track_with_only_a_name_reads_as_the_name()
        => Assert.Equal("Commentary", Track(1, title: "Commentary").Label);

    [Fact]
    public void A_track_with_only_a_language_reads_as_the_code()
        => Assert.Equal("FRA", Track(1, language: "fra").Label);

    [Fact]
    public void A_written_out_language_is_not_shouted()
        => Assert.Equal(
            "Brazilian Portuguese",
            Track(1, language: "Brazilian Portuguese").Label);

    [Fact]
    public void An_anonymous_track_is_named_by_its_id()
        => Assert.Equal("Track 3", Track(3).Label);

    [Fact]
    public void Blank_metadata_counts_as_absent()
    {
        // Providers send empty strings rather than omitting the field, and a track labelled
        // " ()" is worse than one labelled by its number.
        Assert.Equal("Track 2", Track(2, title: "  ", language: "").Label);
    }

    [Fact]
    public void A_menu_holds_only_its_own_kind()
    {
        var menu = TrackMenu.For(
            TrackKind.Subtitle,
            [
                Track(1, TrackKind.Audio),
                Track(1, TrackKind.Subtitle),
                Track(2, TrackKind.Subtitle),
            ]);

        Assert.Equal(2, menu.Tracks.Count);
        Assert.All(menu.Tracks, t => Assert.Equal(TrackKind.Subtitle, t.Kind));
    }

    [Fact]
    public void One_audio_track_is_not_a_choice()
        => Assert.False(TrackMenu.For(TrackKind.Audio, [Track(1, TrackKind.Audio)]).IsUseful);

    [Fact]
    public void Two_audio_tracks_are()
        => Assert.True(TrackMenu
            .For(TrackKind.Audio, [Track(1, TrackKind.Audio), Track(2, TrackKind.Audio)])
            .IsUseful);

    [Fact]
    public void One_subtitle_track_is_a_choice_because_off_is_the_other_one()
        => Assert.True(TrackMenu
            .For(TrackKind.Subtitle, [Track(1, TrackKind.Subtitle)])
            .IsUseful);

    [Fact]
    public void No_subtitle_tracks_is_not()
        => Assert.False(TrackMenu.For(TrackKind.Subtitle, []).IsUseful);

    [Fact]
    public void The_menu_reports_which_track_is_playing()
    {
        var menu = TrackMenu.For(
            TrackKind.Audio,
            [
                Track(1, TrackKind.Audio),
                Track(2, TrackKind.Audio, selected: true),
            ]);

        Assert.Equal(2, menu.SelectedId);
    }

    [Fact]
    public void Nothing_selected_reads_as_no_track()
        => Assert.Equal(
            TrackMenu.NoTrack,
            TrackMenu.For(TrackKind.Subtitle, [Track(1, TrackKind.Subtitle)]).SelectedId);

    [Fact]
    public void Turning_subtitles_off_is_spelled_the_way_mpv_spells_it()
    {
        Assert.Equal("no", TrackMenu.PropertyValue(TrackMenu.NoTrack));
        Assert.Equal("2", TrackMenu.PropertyValue(2));
    }
}
