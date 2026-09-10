using Iptv.Mpv;
using Iptv.Mpv.Native;
using Xunit.Abstractions;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Smoke tests against the real libmpv binary.
/// </summary>
/// <remarks>
/// These load a 120MB native library that is deliberately not committed, so every test
/// no-ops when it is absent - including in CI, which builds this project without native
/// binaries. Skipping is reported to test output rather than silently passing, because a
/// suite that quietly tests nothing is worse than one that fails.
/// </remarks>
public sealed class MpvHandleTests
{
    private readonly ITestOutputHelper _output;

    public MpvHandleTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void Reports_the_client_api_version()
    {
        Requires.LibMpv();

        var (major, minor) = MpvHandle.ClientApiVersion;
        _output.WriteLine($"libmpv client API {major}.{minor}");

        // Spike 0.1 inspected client API 2.5. Anything below 2.0 predates the render API
        // this design is built on.
        Assert.True(major >= 2, $"Client API {major}.{minor} is older than the render API requires.");
    }

    [SkippableFact]
    public void Creates_and_disposes_a_handle()
    {
        Requires.LibMpv();

        using var handle = MpvHandle.Create();
        Assert.False(handle.IsDisposed);
    }

    [SkippableFact]
    public void Applies_options_before_initialising()
    {
        Requires.LibMpv();

        // vo=libmpv is the render-API output; it cannot be changed after initialise, which
        // is why options are applied between create and initialise.
        using var handle = MpvHandle.Create(new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["idle"] = "yes",
            ["keep-open"] = "yes",
        });

        Assert.Equal("yes", handle.GetProperty("idle"));
    }

    [SkippableFact]
    public void Reports_the_live_playback_profile_options()
    {
        Requires.LibMpv();

        using var handle = MpvHandle.Create(new Dictionary<string, string>
        {
            ["vo"] = "libmpv",
            ["idle"] = "yes",
            ["hwdec"] = "auto-safe",
            ["profile"] = "low-latency",
            ["demuxer-max-bytes"] = "32MiB",
        });

        // Confirms the PRD's live profile is accepted by this build rather than silently
        // ignored - an unknown option fails loudly at set time.
        Assert.Equal("auto-safe", handle.GetProperty("hwdec"));
    }

    [SkippableFact]
    public void Rejects_an_unknown_option_loudly()
    {
        Requires.LibMpv();

        var exception = Assert.Throws<MpvException>(
            () => MpvHandle.Create(new Dictionary<string, string> { ["not-a-real-option"] = "1" }));

        Assert.Equal(MpvError.OptionNotFound, exception.Error);

        // The message has to name the option, or a typo in a playback profile becomes a
        // silent behaviour change.
        Assert.Contains("not-a-real-option", exception.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Disposing_twice_is_safe()
    {
        Requires.LibMpv();

        var handle = MpvHandle.Create();
        handle.Dispose();
        handle.Dispose();

        Assert.True(handle.IsDisposed);
    }

    [SkippableFact]
    public void Using_a_disposed_handle_throws_rather_than_crashing()
    {
        Requires.LibMpv();

        var handle = MpvHandle.Create();
        handle.Dispose();

        // Calling into a freed native context is an access violation that takes the whole
        // process down, so the managed guard is load-bearing rather than tidiness.
        Assert.Throws<ObjectDisposedException>(() => handle.SetProperty("pause", "yes"));
    }

    [SkippableFact]
    public void Creating_many_handles_does_not_leak()
    {
        Requires.LibMpv();

        // A crude check for the Phase 5 exit criterion about render context lifetime. If
        // handles leaked, 200 contexts would exhaust something well before the end.
        for (var i = 0; i < 200; i++)
        {
            using var handle = MpvHandle.Create(new Dictionary<string, string> { ["idle"] = "yes" });
            Assert.False(handle.IsDisposed);
        }
    }
}
