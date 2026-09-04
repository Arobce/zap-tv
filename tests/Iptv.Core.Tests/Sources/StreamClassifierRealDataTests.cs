using System.Text.Json;
using Iptv.Core.Sources;
using Xunit.Abstractions;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Runs the separator heuristic over a real captured catalogue when one is present.
/// </summary>
/// <remarks>
/// Curated examples confirm the rule does what its author intended; only real data shows
/// what it does to 28,285 titles nobody chose. The capture lives in a gitignored
/// <c>.local/</c> because it identifies a provider, so this test no-ops on any machine
/// without it - including CI. It is a diagnostic, not a gate.
/// <para>
/// Measured against the reference provider: 1,137 separators of 28,285 entries (4.0%),
/// with no false positives. Cross-checked by counting titles containing a run of three or
/// more '#' characters, which returned exactly 1,137 - so every flagged entry is a
/// genuine divider and no real channel was caught. That provider happens to use only
/// hashes; the rule covers other symbol runs for providers that do not.
/// </para>
/// </remarks>
public sealed class StreamClassifierRealDataTests
{
    private readonly ITestOutputHelper _output;

    public StreamClassifierRealDataTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Separator_rate_on_a_real_catalogue_is_plausible()
    {
        var capture = FindCapture("get_live_streams.json");
        if (capture is null)
        {
            _output.WriteLine("No local capture present; skipping.");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(capture));

        var total = 0;
        var separators = 0;
        var samples = new List<string>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            total++;

            if (!StreamClassifier.IsSeparator(name))
            {
                continue;
            }

            separators++;
            if (samples.Count < 15)
            {
                samples.Add(name!);
            }
        }

        _output.WriteLine($"{separators:N0} separators of {total:N0} entries " +
                          $"({separators / (double)total:P1})");
        foreach (var sample in samples)
        {
            _output.WriteLine($"  {sample}");
        }

        Assert.True(total > 0, "The capture should contain entries.");

        // A rule that flags almost nothing is not working; one that flags a large slice of
        // the catalogue is hiding real channels. Both failures are silent in production.
        var rate = separators / (double)total;
        Assert.InRange(rate, 0.001, 0.15);
    }

    private static string? FindCapture(string fileName)
    {
        // Walk up from the test output directory to the repository root.
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
