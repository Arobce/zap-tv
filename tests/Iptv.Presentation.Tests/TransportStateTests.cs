using Iptv.Presentation;

namespace Iptv.Presentation.Tests;

public sealed class TransportStateTests
{
    [Fact]
    public void Live_has_no_transport_bar()
    {
        var state = TransportState.For(live: true, positionSeconds: 120, durationSeconds: 3600);

        Assert.False(state.IsVisible);
        Assert.False(state.CanSeek);
    }

    [Fact]
    public void A_film_is_seekable()
    {
        var state = TransportState.For(live: false, positionSeconds: 1800, durationSeconds: 7200);

        Assert.True(state.IsVisible);
        Assert.True(state.CanSeek);
        Assert.Equal(0.25, state.Fraction, 3);
    }

    [Fact]
    public void An_unknown_duration_shows_the_position_but_refuses_seeking()
    {
        var state = TransportState.For(live: false, positionSeconds: 65, durationSeconds: null);

        Assert.True(state.IsVisible);
        Assert.False(state.CanSeek);
        Assert.Equal("1:05", state.PositionText);
        Assert.Equal("--:--", state.DurationText);
        Assert.Equal(0, state.Fraction);
    }

    [Fact]
    public void A_position_past_a_reported_end_refuses_seeking()
    {
        // Some transcodes report a duration shorter than the file. Scrubbing against that
        // lands nowhere near where the bar says, so the bar does not offer it.
        var state = TransportState.For(live: false, positionSeconds: 4000, durationSeconds: 3600);

        Assert.True(state.IsVisible);
        Assert.False(state.CanSeek);
    }

    [Fact]
    public void Nothing_playing_has_no_transport_bar()
    {
        Assert.False(TransportState.For(live: false, null, 7200).IsVisible);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(65, "1:05")]
    [InlineData(2535, "42:15")]
    [InlineData(3600, "1:00:00")]
    [InlineData(7325, "2:02:05")]
    public void Times_read_the_way_they_are_glanced_at(int seconds, string expected)
        => Assert.Equal(expected, TransportState.Format(seconds));

    [Fact]
    public void A_negative_time_reads_as_zero()
        => Assert.Equal("0:00", TransportState.Format(-5));

    [Fact]
    public void Scrubbing_maps_the_bar_onto_the_duration()
    {
        var state = TransportState.For(live: false, positionSeconds: 0, durationSeconds: 7200);

        Assert.Equal(3600, state.SeekTarget(0.5));
        Assert.Equal(0, state.SeekTarget(-1));
        Assert.Equal(7200, state.SeekTarget(2));
    }

    [Fact]
    public void Scrubbing_without_a_duration_goes_nowhere()
        => Assert.Equal(0, TransportState.For(live: false, 65, null).SeekTarget(0.5));
}
