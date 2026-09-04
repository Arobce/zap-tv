using System.Diagnostics;
using System.Text.Json;
using Iptv.Core.Sources;
using Iptv.Core.Xtream;
using Xunit.Abstractions;

namespace Iptv.Core.Tests.Xtream;

/// <summary>
/// Streams the full captured catalogue through the real deserialization path.
/// </summary>
/// <remarks>
/// The fixture tests cover five hand-picked entries. This covers 28,285 the author never
/// looked at, which is where a converter that mishandles one panel quirk actually shows
/// up. Reads a gitignored local capture and no-ops without it, including in CI, so it is
/// a diagnostic rather than a gate.
/// </remarks>
[Collection(PerformanceCollection.Name)]
public sealed class XtreamRealDataTests
{
    private readonly ITestOutputHelper _output;

    public XtreamRealDataTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Deserializes_the_full_live_catalogue()
    {
        var capture = FindCapture("get_live_streams.json");
        if (capture is null)
        {
            _output.WriteLine("No local capture present; skipping.");
            return;
        }

        await using var stream = File.OpenRead(capture);

        var total = 0;
        var nullEpg = 0;
        var separators = 0;
        var withCatchup = 0;
        long maxStreamId = 0;

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable<XtreamLiveStream>(
                           stream, XtreamJson.Options, CancellationToken.None))
        {
            if (entry is null)
            {
                continue;
            }

            total++;
            if (entry.EpgChannelId is null)
            {
                nullEpg++;
            }

            if (StreamClassifier.IsSeparator(entry.Name))
            {
                separators++;
            }

            if (entry.TvArchive)
            {
                withCatchup++;
            }

            maxStreamId = Math.Max(maxStreamId, entry.StreamId);
        }

        stopwatch.Stop();
        var allocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - before) / (1024 * 1024.0);
        var fileMb = new FileInfo(capture).Length / (1024 * 1024.0);

        _output.WriteLine(
            $"{total:N0} streams from {fileMb:F1}MB in {stopwatch.ElapsedMilliseconds}ms, " +
            $"allocated {allocatedMb:F1}MB");
        _output.WriteLine($"  null epg_channel_id: {nullEpg:N0} ({nullEpg / (double)total:P1})");
        _output.WriteLine($"  separators:          {separators:N0}");
        _output.WriteLine($"  catchup enabled:     {withCatchup:N0}");
        _output.WriteLine($"  max stream_id:       {maxStreamId:N0}");

        Assert.True(total > 0, "The capture should contain entries.");

        // Every entry deserialized without a converter throwing. That is the real
        // assertion here: 28,285 rows of provider variance, no tolerated field left
        // unhandled.
        Assert.True(maxStreamId > 0, "stream_id should parse on at least one entry.");

        // Streaming, not buffering: allocation should stay near the payload size rather
        // than scaling with it several times over.
        Assert.True(
            allocatedMb < fileMb * 12,
            $"Allocated {allocatedMb:F1}MB for a {fileMb:F1}MB payload, which suggests the " +
            $"body is being buffered rather than streamed.");
    }

    private static string? FindCapture(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".local", "capture", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
