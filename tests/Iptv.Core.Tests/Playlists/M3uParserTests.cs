using System.Text;
using Iptv.Core.Playlists;

namespace Iptv.Core.Tests.Playlists;

/// <summary>
/// The M3U parser is the entry point for one of the two provider kinds, and real
/// playlists are far messier than the format suggests. These tests encode the messiness
/// that actually shows up in the wild.
/// </summary>
public sealed class M3uParserTests
{
    private static async Task<List<M3uEntry>> ParseAsync(string playlist)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(playlist));
        var entries = new List<M3uEntry>();
        await foreach (var entry in M3uParser.ParseAsync(stream, CancellationToken.None))
        {
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public async Task Parses_a_minimal_entry()
    {
        var entries = await ParseAsync(
            """
            #EXTM3U
            #EXTINF:-1,Channel One
            http://example.invalid/1.ts
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("Channel One", entry.Title);
        Assert.Equal("http://example.invalid/1.ts", entry.Url);
    }

    [Fact]
    public async Task Parses_tvg_attributes()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:-1 tvg-id="bbc1.uk" tvg-name="BBC One" tvg-logo="http://l/1.png" group-title="UK",BBC One HD
            http://example.invalid/1.ts
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("bbc1.uk", entry.TvgId);
        Assert.Equal("BBC One", entry.TvgName);
        Assert.Equal("http://l/1.png", entry.TvgLogo);
        Assert.Equal("UK", entry.GroupTitle);
        Assert.Equal("BBC One HD", entry.Title);
    }

    [Fact]
    public async Task Attribute_values_may_contain_commas()
    {
        // The title is separated from the attributes by a comma, so a naive split on the
        // first or last comma mangles "Sports, USA" and every entry in that group.
        var entries = await ParseAsync(
            """
            #EXTINF:-1 tvg-id="espn.us" group-title="Sports, USA",US: ESPN
            http://example.invalid/1.ts
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("Sports, USA", entry.GroupTitle);
        Assert.Equal("US: ESPN", entry.Title);
    }

    [Fact]
    public async Task Titles_may_contain_commas()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:-1 tvg-id="x",Movie, The Sequel
            http://example.invalid/1.ts
            """);

        Assert.Equal("Movie, The Sequel", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task Parses_unquoted_attribute_values()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:7200 tvg-id=movie.vod tvg-name=Unquoted,Some Movie
            http://example.invalid/1.mp4
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("movie.vod", entry.TvgId);
        Assert.Equal("Unquoted", entry.TvgName);
    }

    [Fact]
    public async Task Parses_catchup_attributes()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:-1 catchup="shift" catchup-source="http://c/1" catchup-days="7",TSN
            http://example.invalid/1.ts
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("shift", entry.CatchupKind);
        Assert.Equal("http://c/1", entry.CatchupSource);
        Assert.Equal(7, entry.CatchupDays);
    }

    [Fact]
    public async Task Parses_duration()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:-1,Live
            http://example.invalid/1.ts
            #EXTINF:7200,Movie
            http://example.invalid/2.mp4
            #EXTINF:1234.5,Fractional
            http://example.invalid/3.mp4
            """);

        Assert.Equal([-1, 7200, 1234.5], entries.Select(e => e.Duration));
    }

    [Fact]
    public async Task Skips_blank_lines_and_unrelated_directives()
    {
        var entries = await ParseAsync(
            """
            #EXTM3U
            #EXTGRP:Some Group

            #EXTVLCOPT:network-caching=1000
            #EXTINF:-1,Channel One

            http://example.invalid/1.ts
            """);

        Assert.Equal("Channel One", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task Handles_windows_line_endings()
    {
        var entries = await ParseAsync(
            "#EXTM3U\r\n#EXTINF:-1,Channel One\r\nhttp://example.invalid/1.ts\r\n");

        Assert.Equal("Channel One", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task Handles_a_byte_order_mark()
    {
        using var stream = new MemoryStream(
            Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:-1,Channel One\nhttp://x/1.ts"))
                .ToArray());

        var entries = new List<M3uEntry>();
        await foreach (var entry in M3uParser.ParseAsync(stream, CancellationToken.None))
        {
            entries.Add(entry);
        }

        // A BOM left on the first line turns "#EXTM3U" into something unrecognised, and
        // with a strict header check that silently yields an empty playlist.
        Assert.Equal("Channel One", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task Ignores_a_trailing_extinf_with_no_url()
    {
        // Truncated downloads are common. A half-written final entry must not surface as
        // a channel with an empty URL that fails at playback time.
        var entries = await ParseAsync(
            """
            #EXTINF:-1,Complete
            http://example.invalid/1.ts
            #EXTINF:-1,Truncated
            """);

        Assert.Equal("Complete", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task Ignores_an_extinf_immediately_followed_by_another()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:-1,Orphaned
            #EXTINF:-1,Real
            http://example.invalid/1.ts
            """);

        Assert.Equal("Real", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task A_url_with_no_preceding_extinf_is_ignored()
    {
        var entries = await ParseAsync(
            """
            http://example.invalid/orphan.ts
            #EXTINF:-1,Real
            http://example.invalid/1.ts
            """);

        Assert.Equal("Real", Assert.Single(entries).Title);
    }

    [Fact]
    public async Task Missing_attributes_are_null_rather_than_empty()
    {
        var entries = await ParseAsync(
            """
            #EXTINF:-1,Bare
            http://example.invalid/1.ts
            """);

        var entry = Assert.Single(entries);
        Assert.Null(entry.TvgId);
        Assert.Null(entry.TvgName);
        Assert.Null(entry.TvgLogo);
        Assert.Null(entry.GroupTitle);
        Assert.Null(entry.CatchupKind);
        Assert.Null(entry.CatchupDays);
    }

    [Fact]
    public async Task An_empty_playlist_yields_nothing()
        => Assert.Empty(await ParseAsync(string.Empty));

    [Fact]
    public async Task A_header_only_playlist_yields_nothing()
        => Assert.Empty(await ParseAsync("#EXTM3U\n"));

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        using var cts = new CancellationTokenSource();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("#EXTINF:-1,C\nhttp://x/1.ts\n", 1_000))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in M3uParser.ParseAsync(stream, cts.Token))
            {
                await cts.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task Parses_the_committed_fixture()
    {
        await using var file = File.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "M3u", "sample.m3u"));

        var entries = new List<M3uEntry>();
        await foreach (var entry in M3uParser.ParseAsync(file, CancellationToken.None))
        {
            entries.Add(entry);
        }

        Assert.Equal(7, entries.Count);
        Assert.Equal("BBC One HD", entries[0].Title);
        Assert.Equal("Sports, USA", entries[3].GroupTitle);
        Assert.Equal(7, entries[4].CatchupDays);
        Assert.Equal("Channel After Directives", entries[6].Title);
    }
}
