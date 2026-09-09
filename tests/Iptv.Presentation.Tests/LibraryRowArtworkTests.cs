using Iptv.Core.Sources;
using Iptv.Presentation;

namespace Iptv.Presentation.Tests;

public sealed class LibraryRowArtworkTests
{
    private static LibraryRow Film(string? image) => LibraryRow.FromFilm(new CatalogueItem
    {
        Key = "vod:1",
        Title = "The Thing",
        ImageUrl = image,
    });

    [Fact]
    public void An_http_cover_is_kept()
    {
        var row = Film("http://images.example/thing.jpg");

        Assert.True(row.HasImage);
        Assert.Equal("http://images.example/thing.jpg", row.ImageUrl);
    }

    [Fact]
    public void An_https_cover_is_kept()
        => Assert.True(Film("https://images.example/thing.jpg").HasImage);

    [Fact]
    public void Surrounding_whitespace_is_trimmed_rather_than_rejected()
        => Assert.Equal(
            "https://images.example/thing.jpg",
            Film("  https://images.example/thing.jpg\n").ImageUrl);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_a_cover(string? image)
        => Assert.False(Film(image).HasImage);

    [Fact]
    public void A_bare_filename_is_not_a_cover()
    {
        // Providers send these. An Image control handed one reaches for a relative path
        // that does not exist, which is a broken tile rather than a placeholder.
        Assert.False(Film("thing.jpg").HasImage);
    }

    [Fact]
    public void A_local_path_from_the_providers_own_machine_is_refused()
        => Assert.False(Film("file:///D:/covers/thing.jpg").HasImage);

    [Fact]
    public void A_non_web_scheme_is_refused()
        => Assert.False(Film("ftp://images.example/thing.jpg").HasImage);

    [Fact]
    public void A_channel_logo_goes_through_the_same_check()
    {
        var row = LibraryRow.FromChannel(new ChannelListItem
        {
            ChannelKey = "bbcone",
            DisplayName = "BBC One",
            LogoUrl = "file:///C:/logos/bbc.png",
        });

        Assert.False(row.HasImage);
    }

    [Fact]
    public void A_series_cover_goes_through_the_same_check()
    {
        var row = LibraryRow.FromSeries(new CatalogueItem
        {
            Key = "series:1",
            Title = "The Wire",
            ImageUrl = "https://images.example/wire.jpg",
        });

        Assert.True(row.HasImage);
    }

    [Fact]
    public void A_tile_with_no_cover_falls_back_to_its_initial()
        => Assert.Equal("T", Film(null).Initial);

    [Fact]
    public void An_initial_is_a_capital_even_when_the_title_is_not()
        => Assert.Equal(
            "L",
            LibraryRow.FromFilm(new CatalogueItem { Key = "k", Title = "la haine" }).Initial);

    [Fact]
    public void An_untitled_row_still_has_something_to_draw()
        => Assert.Equal(
            "?",
            LibraryRow.FromFilm(new CatalogueItem { Key = "k", Title = string.Empty }).Initial);
}
