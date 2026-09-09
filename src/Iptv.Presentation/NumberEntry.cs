using System.Globalization;

namespace Iptv.Presentation;

/// <summary>The outcome of one key press against a <see cref="NumberEntry"/>.</summary>
public readonly record struct NumberEntryResult
{
    /// <summary>Nothing to show and nothing to do.</summary>
    public static readonly NumberEntryResult Idle = new()
    {
        Committed = false,
        Position = 0,
        Display = string.Empty,
    };

    /// <summary>Whether the caller should now jump to <see cref="Position"/>.</summary>
    public required bool Committed { get; init; }

    /// <summary>The one-based list position, or zero when nothing was committed.</summary>
    public required int Position { get; init; }

    /// <summary>What to show the viewer while they are still typing. Empty when idle.</summary>
    public required string Display { get; init; }
}

/// <summary>
/// Digits typed in a row, addressing a position in the visible list.
/// </summary>
/// <remarks>
/// <para>
/// A position, not the provider's channel number. Xtream does not publish one — there is
/// no numbering in the API to honour — so the number that means something here is the one
/// the viewer can see: the row's place in the list they are looking at.
/// </para>
/// <para>
/// The timeout is what makes multi-digit entry work at all. A digit cannot be acted on
/// when it arrives, because it might be the first of two, so the entry waits — but it
/// stops waiting early whenever no further digit could address anything, which is the
/// difference between a set that feels instant and one that always costs a second and a
/// half.
/// </para>
/// </remarks>
public sealed class NumberEntry
{
    /// <summary>How long an unfinished number waits for another digit.</summary>
    /// <remarks>
    /// A second and a half, matching what television sets have settled on. Shorter breaks
    /// two-digit entry for anyone hunting for the keys; longer is felt as a hang.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1.5);

    private int _value;
    private DateTimeOffset _lastPress;

    /// <summary>Whether a number is part-typed and waiting.</summary>
    public bool IsActive => _value > 0;

    /// <summary>Takes one digit and says whether the number is now finished.</summary>
    /// <param name="digit">0 to 9.</param>
    /// <param name="count">How many rows the list holds.</param>
    /// <param name="now">The press time, for the timeout.</param>
    public NumberEntryResult Press(int digit, int count, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digit, 9);

        if (count <= 0)
        {
            // Nothing to address. Swallowed rather than buffered, so a digit typed against
            // an empty list does not turn up attached to the next one loaded.
            Reset();
            return NumberEntryResult.Idle;
        }

        // A gap longer than the timeout starts a new number. Without this a digit typed
        // now would extend one abandoned minutes ago, and the caller cannot be relied on
        // to have delivered the expiry tick first.
        if (IsActive && now - _lastPress > Timeout)
        {
            Reset();
        }

        _lastPress = now;

        var candidate = (_value * 10) + digit;

        // A leading zero is not a position, so 0 then 7 is 7 rather than an entry that can
        // never commit.
        if (candidate == 0)
        {
            return NumberEntryResult.Idle;
        }

        if (candidate > count)
        {
            // The digit cannot belong to this number, so it begins the next one. Treating
            // it as the start rather than dropping it is what makes a mistyped first digit
            // recoverable without waiting out the timeout.
            candidate = digit;

            if (candidate == 0 || candidate > count)
            {
                Reset();
                return NumberEntryResult.Idle;
            }
        }

        _value = candidate;

        // Commit as soon as no further digit could address anything: with 60 rows, 7 can
        // only ever mean 7, so waiting for a second digit would only add delay.
        return candidate * 10 > count ? Commit() : Pending();
    }

    /// <summary>Commits a part-typed number because the timeout has passed.</summary>
    /// <returns>The jump to make, or <see cref="NumberEntryResult.Idle"/> if there is none.</returns>
    public NumberEntryResult Expire(DateTimeOffset now)
        => IsActive && now - _lastPress >= Timeout ? Commit() : NumberEntryResult.Idle;

    /// <summary>Commits whatever has been typed, for an explicit Enter.</summary>
    public NumberEntryResult Commit()
    {
        if (!IsActive)
        {
            return NumberEntryResult.Idle;
        }

        var position = _value;
        Reset();

        return new NumberEntryResult
        {
            Committed = true,
            Position = position,
            Display = string.Empty,
        };
    }

    /// <summary>Abandons a part-typed number.</summary>
    public void Reset() => _value = 0;

    private NumberEntryResult Pending() => new()
    {
        Committed = false,
        Position = 0,
        Display = _value.ToString(CultureInfo.InvariantCulture),
    };
}
