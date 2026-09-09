using Iptv.Core.Data;
using Iptv.Core.Sources;
using Iptv.Presentation;

namespace Iptv.Harness;

/// <summary>
/// Prints the diagnostics panel to the console.
/// </summary>
/// <remarks>
/// <para>
/// The Phase 8 exit criterion is that the diagnostics view shows accurate per-provider
/// stats. Checking that by opening a panel and reading it off a screenshot is how the guide
/// coverage number went unquestioned for two days while being measured against the wrong
/// denominator.
/// </para>
/// <para>
/// This calls <see cref="DiagnosticsViewModel"/> — the same object the panel renders — so
/// the numbers here are the panel's numbers rather than a second implementation that could
/// agree with itself and disagree with the UI. The one section it cannot show is the
/// playback engine, which the window adds live from a running mpv handle.
/// </para>
/// </remarks>
internal static class DiagnosticsDump
{
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer", "harness.db");

        var model = new DiagnosticsViewModel(
            new SqliteConnectionFactory(databasePath),
            new DpapiSecretProtector());

        var sections = await model.BuildAsync(DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        foreach (var section in sections)
        {
            Console.WriteLine($"== {section.Title} ==");

            foreach (var row in section.Rows)
            {
                // The panel marks these in the accent colour; a console has no colour to
                // rely on, so the flag is spelled out.
                var flag = row.IsWarning ? " (!)" : string.Empty;
                Console.WriteLine($"  {row.Label,-22} {row.Value}{flag}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Playback engine is omitted: it is read from a running mpv handle.");

        return 0;
    }
}
