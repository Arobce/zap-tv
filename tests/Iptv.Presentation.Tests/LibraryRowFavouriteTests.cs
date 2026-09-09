using Iptv.Core.Sources;
using Iptv.Presentation;

namespace Iptv.Presentation.Tests;

public sealed class LibraryRowFavouriteTests
{
    private static LibraryRow Channel(bool favourite) => LibraryRow.FromChannel(new ChannelListItem
    {
        ChannelKey = "bbcone",
        DisplayName = "BBC One",
        IsFavorite = favourite,
    });

    [Fact]
    public void A_channel_can_be_favourited()
        => Assert.True(Channel(false).CanFavourite);

    [Fact]
    public void A_film_cannot()
    {
        // The flag lives on channels.is_favorite and the favourites view lists channels
        // with a live stream behind them, so a favourited film would write a row that the
        // view it was meant to appear in filters straight back out.
        var film = LibraryRow.FromFilm(new CatalogueItem { Key = "vod:1", Title = "Heat" });

        Assert.False(film.CanFavourite);
    }

    [Fact]
    public void Nor_can_a_series_or_a_season()
    {
        var series = LibraryRow.FromSeries(new CatalogueItem { Key = "s:1", Title = "The Wire" });
        var season = LibraryRow.FromSeason(1, 13);

        Assert.False(series.CanFavourite);
        Assert.False(season.CanFavourite);
    }

    [Fact]
    public void A_channel_row_carries_the_stored_state()
    {
        Assert.True(Channel(true).IsFavourite);
        Assert.False(Channel(false).IsFavourite);
    }

    [Fact]
    public void The_glyph_is_filled_only_when_favourited()
    {
        Assert.Equal("★", Channel(true).FavouriteGlyph);
        Assert.Equal("☆", Channel(false).FavouriteGlyph);
    }

    [Fact]
    public void The_glyph_follows_the_state_being_changed()
    {
        // The one mutable thing on a row. Clicking the star must redraw it without
        // reloading the page, which would lose the scroll position.
        var row = Channel(false);

        row.IsFavourite = true;

        Assert.Equal("★", row.FavouriteGlyph);
    }

    [Fact]
    public void A_live_search_hit_can_be_favourited_too()
    {
        // Search hits carry no stored favourite state, so the row starts unfilled. The
        // toggle reads the database rather than the row, so acting on one is still correct.
        var hit = LibraryRow.FromSearchHit(new SearchHit
        {
            Kind = SearchHitKind.Channel,
            Key = "bbcone",
            Title = "BBC One",
            Subtitle = "live",
        });

        Assert.True(hit.CanFavourite);
        Assert.False(hit.IsFavourite);
    }
}
