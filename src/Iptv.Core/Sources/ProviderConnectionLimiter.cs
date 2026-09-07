namespace Iptv.Core.Sources;

/// <summary>
/// Holds one of a provider's connection slots until disposed.
/// </summary>
/// <remarks>
/// Disposal is idempotent. A double release would let the active count drift below zero
/// and eventually permit more concurrent streams than the account allows, which is the
/// failure this whole type exists to prevent.
/// </remarks>
public sealed class ConnectionLease : IDisposable
{
    private ProviderConnectionLimiter? _owner;

    internal ConnectionLease(ProviderConnectionLimiter owner) => _owner = owner;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}

/// <summary>
/// Enforces a provider's concurrent-connection limit and a floor on how fast streams may
/// be opened.
/// </summary>
/// <remarks>
/// <para>
/// The PRD says to respect <c>max_connections</c> because exceeding it gets the account
/// temporarily blocked. During development a latency benchmark opened 30 streams in quick
/// succession against an account permitting one, and the provider blocked it for hours.
/// Nothing in the code enforced the limit; it existed only in the document.
/// </para>
/// <para>
/// Two separate protections, because the incident needed both. The connection count stops
/// concurrent streams. The minimum interval stops rapid sequential ones, which stay within
/// the count at any instant and still look like abuse.
/// </para>
/// </remarks>
public sealed class ProviderConnectionLimiter
{
    private readonly int _maxConnections;
    private readonly TimeSpan _minimumInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private int _active;
    private DateTimeOffset? _lastAcquired;

    /// <param name="maxConnections">
    /// From the provider's account info. Zero or negative is treated as one: panels
    /// sometimes report 0, and taking that literally would refuse to play anything.
    /// </param>
    /// <param name="minimumInterval">Floor on the gap between opening streams.</param>
    public ProviderConnectionLimiter(
        int maxConnections,
        TimeSpan minimumInterval,
        TimeProvider? timeProvider = null)
    {
        _maxConnections = Math.Max(1, maxConnections);
        _minimumInterval = minimumInterval < TimeSpan.Zero ? TimeSpan.Zero : minimumInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Slots currently held.</summary>
    public int Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// How long until another stream may be opened, or zero when one may be opened now.
    /// </summary>
    /// <remarks>
    /// Exposed so the UI can say "please wait" rather than silently doing nothing, which
    /// is indistinguishable from a broken button.
    /// </remarks>
    public TimeSpan TimeUntilNextOpen
    {
        get
        {
            lock (_gate)
            {
                if (_lastAcquired is not { } last)
                {
                    return TimeSpan.Zero;
                }

                var remaining = _minimumInterval - (_timeProvider.GetUtcNow() - last);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Takes a slot if the account permits one right now.
    /// </summary>
    /// <returns>
    /// False when the limit is reached or the minimum interval has not elapsed. Callers
    /// must treat this as "not yet", not as an error.
    /// </returns>
    public bool TryAcquire(out ConnectionLease? lease)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();

            // The interval applies between opens, not before the first one: a cold start
            // must not wait for a limit that has never been approached.
            if (_lastAcquired is { } last && now - last < _minimumInterval)
            {
                lease = null;
                return false;
            }

            if (_active >= _maxConnections)
            {
                lease = null;
                return false;
            }

            _active++;
            _lastAcquired = now;
            lease = new ConnectionLease(this);
            return true;
        }
    }

    /// <summary>
    /// Takes a slot, waiting for one rather than refusing.
    /// </summary>
    /// <remarks>
    /// For failover, where <see cref="TryAcquire"/> is the wrong shape. A stream that
    /// fails 200ms after opening is still inside the minimum interval, so the fallback
    /// would be refused and the user shown an error while a working alternative sat there
    /// unused. A user clicking a channel still gets <see cref="TryAcquire"/>: silently
    /// waiting on a click reads as a dead button, whereas failover has no button to be
    /// dead and a spinner is the honest response.
    /// <para>
    /// Polled rather than signalled. Slot releases are rare and human-paced, and a
    /// condition variable here would buy microseconds on a path already budgeted seconds.
    /// </para>
    /// </remarks>
    public async Task<ConnectionLease> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryAcquire(out var lease))
            {
                return lease!;
            }

            // Zero means the refusal was the connection ceiling rather than the clock, and
            // no amount of waiting on the clock will change that. Poll instead.
            var wait = TimeUntilNextOpen;
            await Task.Delay(
                wait > TimeSpan.Zero ? wait : PollInterval,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How often to re-check when waiting on a busy slot rather than the clock.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    internal void Release()
    {
        lock (_gate)
        {
            if (_active > 0)
            {
                _active--;
            }
        }
    }
}
