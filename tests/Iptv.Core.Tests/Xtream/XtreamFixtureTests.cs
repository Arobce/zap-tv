using System.Text.Json;
using Iptv.Core.Xtream;

namespace Iptv.Core.Tests.Xtream;

/// <summary>
/// Deserializes payloads captured verbatim from a real panel.
/// </summary>
/// <remarks>
/// Hand-written fixtures encode what their author expected a panel to send. These encode
/// what one actually sent, including the type inconsistencies - <c>stream_id</c> as a
/// number beside <c>category_id</c> as a string - that a tidier fixture would have
/// smoothed away.
/// </remarks>
public sealed class XtreamFixtureTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Xtream", name);

    private static T Read<T>(string name)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(FixturePath(name)), XtreamJson.Options)!;

    [Fact]
    public void Reads_account_info()
    {
        var response = Read<XtreamAccountResponse>("account_info.json");
        var info = response.UserInfo!;

        Assert.True(info.Auth);              // arrives as the number 1
        Assert.Equal("Active", info.Status);
        Assert.Equal(1, info.MaxConnections); // arrives as the string "1"
        Assert.False(info.IsTrial);           // arrives as the string "0"
        Assert.Equal(0, info.ActiveConnections);
        Assert.Contains("ts", info.AllowedOutputFormats);
    }

    [Fact]
    public void Reads_account_expiry_as_a_timestamp()
    {
        var info = Read<XtreamAccountResponse>("account_info.json").UserInfo!;

        // exp_date is a string holding unix seconds.
        Assert.Equal(1817010962L, info.ExpiresAtUnix);
        Assert.Equal(2027, DateTimeOffset.FromUnixTimeSeconds(info.ExpiresAtUnix!.Value).Year);
    }

    [Fact]
    public void Reads_server_info()
    {
        var server = Read<XtreamAccountResponse>("account_info.json").ServerInfo!;

        Assert.Equal("80", server.Port);
        Assert.Equal("http", server.ServerProtocol);
    }

    [Fact]
    public void Reads_live_categories()
    {
        var categories = Read<List<XtreamCategory>>("get_live_categories.json");

        Assert.Equal(4, categories.Count);
        Assert.Equal("1382", categories[0].CategoryId);   // string
        Assert.Equal("VIP | GOLDEN EVENTS", categories[0].CategoryName);
        Assert.Equal(0, categories[0].ParentId);          // number
    }

    [Fact]
    public void Reads_live_streams_with_mixed_types_in_one_object()
    {
        var streams = Read<List<XtreamLiveStream>>("get_live_streams.json");

        Assert.Equal(5, streams.Count);

        var first = streams[0];
        Assert.Equal(682950, first.StreamId);   // JSON number
        Assert.Equal("1382", first.CategoryId); // JSON string, same object
        Assert.Equal("live", first.StreamType);
        Assert.Equal(1708096234L, first.AddedUnix); // JSON string holding a number
    }

    [Fact]
    public void Null_epg_channel_id_survives_as_null()
    {
        var streams = Read<List<XtreamLiveStream>>("get_live_streams.json");

        // 71.6% of the reference provider's channels report null here. Turning it into ""
        // would make "has no EPG id" indistinguishable from "has an empty one", and the
        // Phase 4 coverage metric counts on the difference.
        Assert.Null(streams[0].EpgChannelId);
        Assert.Equal("bbc1.uk", streams[2].EpgChannelId);
    }

    [Fact]
    public void Reads_catchup_availability()
    {
        var streams = Read<List<XtreamLiveStream>>("get_live_streams.json");

        Assert.True(streams[2].TvArchive);           // number 1
        Assert.Equal(7, streams[2].TvArchiveDuration);
        Assert.False(streams[0].TvArchive);
    }

    [Fact]
    public void Empty_stream_icon_becomes_null()
    {
        var streams = Read<List<XtreamLiveStream>>("get_live_streams.json");
        Assert.Null(streams[4].StreamIcon);
    }
}
