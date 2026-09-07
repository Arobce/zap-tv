using System.Diagnostics;
using Iptv.Core.Sources;

namespace Iptv.Core.Tests.Sources;

/// <summary>
/// Enforces a provider's concurrent-connection limit and a floor on how fast streams may
/// be opened.
/// </summary>
/// <remarks>
/// This exists because of a real incident during development: a latency benchmark opened
/// 30 streams in quick succession against an account permitting one, and the provider
/// blocked it. The PRD warned about exactly that — "exceeding it gets the account
/// temporarily blocked, which users will blame on your app" — and nothing in the code
/// enforced it.
/// <para>
/// A limit that only exists in a document is not a limit.
/// </para>
/// </remarks>
public sealed class ProviderConnectionLimiterTests
{
    private static (ProviderConnectionLimiter Limiter, FakeTimeProvider Time) Create(
        int maxConnections = 1,
        int minimumIntervalMs = 1000)
    {
        var time = new FakeTimeProvider();
        return (new ProviderConnectionLimiter(
            maxConnections, TimeSpan.FromMilliseconds(minimumIntervalMs), time), time);
    }

    [Fact]
    public void Grants_a_lease_within_the_limit()
    {
        // Zero interval: this test is about the concurrency count, and leaving the rate
        // limit in would make it fail for an unrelated reason.
        var (limiter, _) = Create(maxConnections: 2, minimumIntervalMs: 0);

        Assert.True(limiter.TryAcquire(out var first));
        Assert.True(limiter.TryAcquire(out var second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, limiter.Active);
    }

    [Fact]
    public void The_interval_applies_even_when_a_slot_is_free()
    {
        var (limiter, time) = Create(maxConnections: 4, minimumIntervalMs: 1000);

        Assert.True(limiter.TryAcquire(out _));

        // Deliberate: the count governs concurrency, the interval governs rate, and both
        // must hold. Opening four streams in the same instant is within the count and
        // still looks like abuse to a provider.
        Assert.False(limiter.TryAcquire(out _));

        time.Advance(TimeSpan.FromMilliseconds(1001));
        Assert.True(limiter.TryAcquire(out _));
        Assert.Equal(2, limiter.Active);
    }

    [Fact]
    public void Refuses_a_lease_beyond_the_limit()
    {
        var (limiter, _) = Create(maxConnections: 1);

        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out var refused));

        // Refusing is the whole point: a second concurrent stream on a single-connection
        // account is what gets the account blocked.
        Assert.Null(refused);
        Assert.Equal(1, limiter.Active);
    }

    [Fact]
    public void Releasing_frees_a_slot()
    {
        var (limiter, time) = Create(maxConnections: 1);

        Assert.True(limiter.TryAcquire(out var lease));
        lease!.Dispose();

        Assert.Equal(0, limiter.Active);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryAcquire(out _));
    }

    [Fact]
    public void Disposing_a_lease_twice_does_not_free_two_slots()
    {
        var (limiter, _) = Create(maxConnections: 2);

        Assert.True(limiter.TryAcquire(out var lease));
        lease!.Dispose();
        lease.Dispose();

        // A double release would let the count drift below zero and eventually permit
        // more concurrent streams than the account allows.
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public void Enforces_a_minimum_interval_between_opens()
    {
        var (limiter, time) = Create(maxConnections: 4, minimumIntervalMs: 1000);

        Assert.True(limiter.TryAcquire(out var first));
        first!.Dispose();

        // Immediately again: within the interval, so refused even though a slot is free.
        Assert.False(limiter.TryAcquire(out _));

        time.Advance(TimeSpan.FromMilliseconds(1001));
        Assert.True(limiter.TryAcquire(out _));
    }

    [Fact]
    public void The_first_acquisition_is_never_delayed()
    {
        var (limiter, _) = Create(minimumIntervalMs: 60_000);

        // A cold start must not wait a minute for a limit that has never been approached.
        Assert.True(limiter.TryAcquire(out _));
    }

    [Fact]
    public void Reports_how_long_until_the_next_open_is_allowed()
    {
        var (limiter, time) = Create(maxConnections: 4, minimumIntervalMs: 1000);

        Assert.True(limiter.TryAcquire(out var lease));
        lease!.Dispose();

        time.Advance(TimeSpan.FromMilliseconds(400));

        // The UI needs this to say "please wait" rather than silently doing nothing,
        // which is indistinguishable from a broken button.
        Assert.InRange(limiter.TimeUntilNextOpen, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(700));
    }

    [Fact]
    public void A_zero_limit_is_treated_as_one()
    {
        // Panels sometimes report 0 for max_connections. Taking that literally would make
        // the app refuse to play anything.
        var limiter = new ProviderConnectionLimiter(0, TimeSpan.Zero, new FakeTimeProvider());
        Assert.True(limiter.TryAcquire(out _));
    }

    [Fact]
    public void Concurrent_acquisition_never_exceeds_the_limit()
    {
        var limiter = new ProviderConnectionLimiter(3, TimeSpan.Zero, new FakeTimeProvider());
        var granted = 0;

        // Channel changes can be issued from a UI thread while a failover retry runs on
        // another; the count has to hold under both.
        // The lambda parameter is named "index" rather than "_": inside a lambda whose
        // parameter is "_", the expression "out _" binds to that parameter instead of
        // being a discard, and the code silently fails to compile for a confusing reason.
        Parallel.For(0, 100, index =>
        {
            if (limiter.TryAcquire(out _))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(3, granted);
        Assert.Equal(3, limiter.Active);
    }

    // AcquireAsync waits on a real clock. The fake above has no timer, so these use the
    // system provider with short intervals - a few tens of milliseconds of real waiting,
    // which is the cost of testing the thing that actually waits.

    [Fact]
    public async Task Acquiring_waits_out_the_interval_instead_of_refusing()
    {
        // The failover case: a stream that dies almost immediately is still inside the
        // interval, and refusing there would show an error while a fallback sat unused.
        var limiter = new ProviderConnectionLimiter(1, TimeSpan.FromMilliseconds(150));

        Assert.True(limiter.TryAcquire(out var first));
        first!.Dispose();

        Assert.False(limiter.TryAcquire(out _));

        var started = Stopwatch.StartNew();
        using var second = await limiter.AcquireAsync(CancellationToken.None);
        started.Stop();

        // Lower bound only. That it waited out the interval is the behaviour under test;
        // how long it took beyond that is the machine's business, and an upper bound here
        // is a test that fails when the build is busy rather than when the code is wrong.
        Assert.NotNull(second);
        Assert.True(
            started.ElapsedMilliseconds >= 100,
            $"returned after {started.ElapsedMilliseconds}ms, which is inside the interval");
    }

    [Fact]
    public async Task Acquiring_waits_for_a_busy_slot_to_be_released()
    {
        // No interval, so the only thing in the way is the connection ceiling. The clock
        // will never clear that on its own - only a release will.
        var limiter = new ProviderConnectionLimiter(1, TimeSpan.Zero);
        Assert.True(limiter.TryAcquire(out var held));

        var waiting = limiter.AcquireAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        held!.Dispose();

        using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, limiter.Active);
    }

    [Fact]
    public async Task Acquiring_is_cancellable_while_waiting()
    {
        // Closing the window during a failover must not leave a task parked forever.
        var limiter = new ProviderConnectionLimiter(1, TimeSpan.Zero);
        Assert.True(limiter.TryAcquire(out _));

        using var cancellation = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    /// <summary>Deterministic clock, so interval tests do not sleep.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
