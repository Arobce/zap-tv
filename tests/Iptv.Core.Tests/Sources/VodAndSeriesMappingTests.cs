using System.Text.Json;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Mapping the two catalogue kinds the live path did not cover.
/// </summary>
/// <remarks>
/// The reference provider lists 49,748 series in a 75MB payload. Each carries its seasons
/// inline but no episodes: those need one <c>get_series_info</c> call per series, so
/// fetching them during sync would mean ~50,000 requests against an account permitting one
/// connection. Sync stores the series; episodes are fetched when a series is opened.
/// </remarks>
public sealed class VodAndSeriesMappingTests
{
    private static readonly XtreamCredentials Credentials = new(
        new Uri("http://host.invalid"), "ACCT7X2", "SECRET99");

    private static T ReadFixture<T>(string name)
        => JsonSerializer.Deserialize<T>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xtream", name)),
            XtreamJson.Options)!;

    // --- VOD ----------------------------------------------------------------------

    [Fact]
    public void Reads_vod_streams_from_the_real_shape()
    {
        var movies = ReadFixture<List<XtreamVodStream>>("get_vod_streams.json");

        Assert.Equal(3, movies.Count);
        Assert.Equal(889001, movies[0].StreamId);
        Assert.Equal("mkv", movies[0].ContainerExtension);
        Assert.Equal("512", movies[0].CategoryId);
    }

    [Fact]
    public void Maps_a_vod_stream_to_its_playback_url()
    {
        var movie = ReadFixture<List<XtreamVodStream>>("get_vod_streams.json")[0];
        var record = StreamMapper.FromXtreamVod(movie, providerId: 1, Credentials);

        // VOD uses /movie/ and the provider's container extension, not the /live/ path
        // and .ts that live channels use.
        Assert.Equal(StreamKind.Vod, record.Kind);
        Assert.Equal(
            "http://host.invalid/movie/ACCT7X2/SECRET99/889001.mkv",
            record.Url);
        Assert.Equal("mkv", record.Container);
    }

    [Fact]
    public void Vod_titles_still_yield_quality_and_a_channel_key()
    {
        var movie = ReadFixture<List<XtreamVodStream>>("get_vod_streams.json")[1];
        var record = StreamMapper.FromXtreamVod(movie, providerId: 1, Credentials);

        // The same identity rules apply: a film listed by two providers should dedup, and
        // playback_state is keyed on channel_key so resume works across them.
        Assert.Equal(Quality.Uhd, record.Quality);
        Assert.StartsWith("name:", record.ChannelKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Vod_separators_are_flagged_like_live_ones()
    {
        var separator = ReadFixture<List<XtreamVodStream>>("get_vod_streams.json")[2];
        var record = StreamMapper.FromXtreamVod(separator, providerId: 1, Credentials);

        // The VOD catalogue is padded with the same decorative rows as the live one.
        Assert.True(record.IsSeparator);
    }

    [Fact]
    public void A_missing_container_extension_falls_back_to_mp4()
    {
        var movie = ReadFixture<List<XtreamVodStream>>("get_vod_streams.json")[0]
            with { ContainerExtension = null };

        // Guessing beats refusing to build a URL: mp4 is right far more often than not,
        // and a wrong guess fails at playback where it is visible, rather than silently
        // dropping the film from the library.
        var record = StreamMapper.FromXtreamVod(movie, providerId: 1, Credentials);
        Assert.EndsWith(".mp4", record.Url, StringComparison.Ordinal);
    }

    // --- Series -------------------------------------------------------------------

    [Fact]
    public void Reads_series_from_the_real_shape()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json");

        Assert.Equal(3, series.Count);
        Assert.Equal(55894, series[0].SeriesId);
        Assert.Equal("AR - In My Prime", series[0].Name);
        Assert.Equal("Family, Drama", series[0].Genre);
    }

    [Fact]
    public void Reads_the_seasons_a_series_declares()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[0];

        // Seasons come inline; episodes do not. Knowing the season count without a
        // per-series request is what makes the browse view usable before anything is
        // fetched.
        var season = Assert.Single(series.Seasons);
        Assert.Equal(1, season.SeasonNumber);
        Assert.Equal(28, season.EpisodeCount);
    }

    [Fact]
    public void A_series_with_no_seasons_array_reads_as_empty_rather_than_null()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[2];

        // The third fixture omits "seasons" entirely, which real payloads do. Callers
        // should not have to null-check a collection.
        Assert.NotNull(series.Seasons);
        Assert.Empty(series.Seasons);
    }

    [Fact]
    public void An_explicitly_null_seasons_array_reads_as_empty()
    {
        // Distinct from the omitted case, and the distinction is not academic: a property
        // initializer only applies when the key is absent. With "seasons": null present,
        // System.Text.Json overwrites it, and the initializer never runs. Real payloads
        // send explicit nulls, and this crashed a full sync after 158,255 films had
        // already been fetched.
        var series = JsonSerializer.Deserialize<XtreamSeries>(
            """{"name":"X","series_id":1,"seasons":null}""", XtreamJson.Options)!;

        Assert.NotNull(series.Seasons);
        Assert.Empty(series.Seasons);
        Assert.Equal(0, SeriesMapper.FromXtream(series, providerId: 1).SeasonCount);
    }

    [Fact]
    public void Maps_a_series_to_a_record()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[0];
        var record = SeriesMapper.FromXtream(series, providerId: 1);

        Assert.Equal(1, record.ProviderId);
        Assert.Equal("55894", record.ProviderSeriesId);
        Assert.Equal("AR - In My Prime", record.Title);
        Assert.Equal(2026, record.Year);
    }

    [Fact]
    public void Series_share_the_channel_key_derivation()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[0];
        var record = SeriesMapper.FromXtream(series, providerId: 1);

        // "AR - " is a country prefix and is stripped, exactly as for live channels, so
        // the same show from two providers merges.
        Assert.Equal("name:inmyprime", record.SeriesKey);
    }

    [Fact]
    public void An_unparseable_release_date_leaves_the_year_absent()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[2];
        var record = SeriesMapper.FromXtream(series, providerId: 1);

        // Better absent than 0001 or 1970, either of which would sort to one end of the
        // library and look like a data bug.
        Assert.Null(record.Year);
    }

    [Fact]
    public void Reads_a_rating_that_arrives_as_a_string()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[1];
        var record = SeriesMapper.FromXtream(series, providerId: 1);

        Assert.Equal(7.4, record.Rating!.Value, 2);
    }

    [Fact]
    public void An_empty_rating_is_absent_rather_than_zero()
    {
        var series = ReadFixture<List<XtreamSeries>>("get_series.json")[2];
        var record = SeriesMapper.FromXtream(series, providerId: 1);

        // Zero is a rating. Empty means nobody rated it, and showing that as 0.0 would
        // sort every unrated series below genuinely bad ones.
        Assert.Null(record.Rating);
    }
}
