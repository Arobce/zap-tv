namespace Iptv.Core.Epg;

/// <summary>
/// Parses XMLTV timestamps (<c>YYYYMMDDHHMMSS ±HHMM</c>) to unix seconds.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than <c>DateTime.ParseExact</c> with candidate formats. This runs
/// once per programme across a couple of million rows per ingest, and trying several
/// formats in that loop is measurably slow at this volume.
/// </para>
/// <para>
/// Days are converted to a unix day count directly, with no <see cref="DateTime"/>
/// construction and no allocation.
/// </para>
/// </remarks>
public static class XmltvTimestamp
{
    /// <summary>Cumulative days before each month in a non-leap year.</summary>
    private static ReadOnlySpan<int> DaysBeforeMonth =>
        [0, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];

    /// <summary>Attempts to parse a timestamp to unix seconds.</summary>
    /// <remarks>
    /// A missing offset is treated as UTC rather than as the machine's local zone. XMLTV
    /// offsets are frequently absent or wrong, and guessing the local zone would make the
    /// stored value depend on where the ingest happened to run. The PRD's answer is to
    /// store UTC and expose a per-provider correction in settings.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<char> value, out long unixSeconds)
    {
        unixSeconds = 0;

        var span = value.Trim();
        if (span.Length < 8)
        {
            return false;
        }

        // The date and time run together; the offset, if present, starts at the first
        // sign or space.
        var offsetStart = IndexOfOffset(span);
        var stamp = offsetStart < 0 ? span : span[..offsetStart].TrimEnd();

        if (!TryReadDigits(stamp, 0, 4, out var year) ||
            !TryReadDigits(stamp, 4, 2, out var month) ||
            !TryReadDigits(stamp, 6, 2, out var day))
        {
            return false;
        }

        var hour = 0;
        var minute = 0;
        var second = 0;

        if (stamp.Length >= 12)
        {
            if (!TryReadDigits(stamp, 8, 2, out hour) ||
                !TryReadDigits(stamp, 10, 2, out minute))
            {
                return false;
            }
        }

        if (stamp.Length >= 14 && !TryReadDigits(stamp, 12, 2, out second))
        {
            return false;
        }

        // Some generators emit :60 for a leap second. Clamping rather than rejecting:
        // one second of precision is worth nothing in a TV guide, and dropping the
        // programme would leave a hole in the grid.
        if (second == 60)
        {
            second = 59;
        }

        if (!IsValidDate(year, month, day) ||
            hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var days = DaysSinceEpoch(year, month, day);
        var utc = (days * 86400L) + (hour * 3600L) + (minute * 60L) + second;

        if (offsetStart >= 0)
        {
            if (!TryParseOffset(span[offsetStart..], out var offsetSeconds))
            {
                return false;
            }

            // The stamp is local time; subtracting the offset yields the UTC instant.
            utc -= offsetSeconds;
        }

        unixSeconds = utc;
        return true;
    }

    /// <summary>Finds where the trailing offset begins, or -1 when there is none.</summary>
    private static int IndexOfOffset(ReadOnlySpan<char> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is '+' or '-' or ' ')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Parses <c>+HHMM</c>, <c>-HH:MM</c>, or the same after a space.</summary>
    private static bool TryParseOffset(ReadOnlySpan<char> span, out long offsetSeconds)
    {
        offsetSeconds = 0;

        var trimmed = span.Trim();
        if (trimmed.IsEmpty)
        {
            // A trailing space with nothing after it: treat as UTC rather than failing.
            return true;
        }

        var negative = trimmed[0] == '-';
        if (trimmed[0] is '+' or '-')
        {
            trimmed = trimmed[1..];
        }

        // Tolerate the colon form, which is not strictly XMLTV but appears in the wild.
        Span<char> digits = stackalloc char[4];
        var written = 0;
        foreach (var c in trimmed)
        {
            if (c == ':')
            {
                continue;
            }

            if (!char.IsAsciiDigit(c) || written == 4)
            {
                return false;
            }

            digits[written++] = c;
        }

        if (written != 4)
        {
            return false;
        }

        var hours = ((digits[0] - '0') * 10) + (digits[1] - '0');
        var minutes = ((digits[2] - '0') * 10) + (digits[3] - '0');
        if (hours > 14 || minutes > 59)
        {
            return false;
        }

        offsetSeconds = (hours * 3600L) + (minutes * 60L);
        if (negative)
        {
            offsetSeconds = -offsetSeconds;
        }

        return true;
    }

    private static bool TryReadDigits(ReadOnlySpan<char> span, int start, int length, out int value)
    {
        value = 0;
        if (start + length > span.Length)
        {
            return false;
        }

        for (var i = start; i < start + length; i++)
        {
            if (!char.IsAsciiDigit(span[i]))
            {
                return false;
            }

            value = (value * 10) + (span[i] - '0');
        }

        return true;
    }

    /// <summary>
    /// Validates the calendar date, month length included.
    /// </summary>
    /// <remarks>
    /// Checking only that the day is 1-31 would silently accept 30 February and place
    /// programmes on days that do not exist, which then sort into the guide at a time no
    /// user can reach.
    /// </remarks>
    private static bool IsValidDate(int year, int month, int day)
    {
        if (year is < 1970 or > 2200 || month is < 1 or > 12 || day < 1)
        {
            return false;
        }

        return day <= DaysInMonth(year, month);
    }

    private static int DaysInMonth(int year, int month) => month switch
    {
        2 => IsLeapYear(year) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    private static bool IsLeapYear(int year)
        => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    /// <summary>Days from 1970-01-01 to the given date.</summary>
    private static long DaysSinceEpoch(int year, int month, int day)
    {
        // Whole years, counting leap days via the standard century rules.
        var previous = year - 1;
        var daysToYear = (previous * 365L)
                         + (previous / 4)
                         - (previous / 100)
                         + (previous / 400);

        const long DaysTo1970 = 719_162; // 1969 whole years by the same formula.

        var dayOfYear = DaysBeforeMonth[month] + day - 1;
        if (month > 2 && IsLeapYear(year))
        {
            dayOfYear++;
        }

        return daysToYear - DaysTo1970 + dayOfYear;
    }
}
