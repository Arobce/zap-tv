namespace Iptv.Core.Tests;

/// <summary>
/// Groups the measurement-based tests so they never run beside other tests.
/// </summary>
/// <remarks>
/// <see cref="GC.GetTotalAllocatedBytes(bool)"/> and wall-clock timings are process-wide,
/// not per-test. With xunit's default parallelism another collection's allocations land
/// inside the measured window, which made the M3U allocation assertion fail roughly one
/// run in three for reasons that had nothing to do with the parser.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}
