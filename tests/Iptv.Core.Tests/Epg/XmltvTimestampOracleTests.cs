using System.Globalization;
using Iptv.Core.Epg;
using Xunit.Abstractions;

namespace Iptv.Core.Tests.Epg;

/// <summary>
/// Differential test: the hand-written parser against .NET's own date handling.
/// </summary>
/// <remarks>
/// Hand-picked cases test what their author thought to check, and a date arithmetic bug
/// tends to hide in exactly the cases nobody thought of - a specific month boundary, a
/// century leap rule, an offset that crosses midnight. Comparing against
/// <see cref="DateTimeOffset"/> across thousands of generated timestamps covers the space
/// the examples do not.
/// <para>
/// This is why the parser is hand-written but not hand-verified: the fast path is ours,
/// the correctness oracle is the framework's.
/// </para>
/// </remarks>
public sealed class XmltvTimestampOracleTests
{
    private readonly ITestOutputHelper _output;

    public XmltvTimestampOracleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Agrees_with_dotnet_across_the_supported_range()
    {
        // Fixed seed: a failure must be reproducible, and a randomised test that cannot be
        // re-run against the same input is close to useless when it fails in CI.
        var random = new Random(20260904);
        var checkedCount = 0;

        for (var i = 0; i < 20_000; i++)
        {
            var year = random.Next(1971, 2100);
            var month = random.Next(1, 13);
            var day = random.Next(1, DateTime.DaysInMonth(year, month) + 1);
            var hour = random.Next(0, 24);
            var minute = random.Next(0, 60);
            var second = random.Next(0, 60);
            // Capped at ±13:30. Real zones span -12:00 to +14:00, but DateTimeOffset
            // rejects anything beyond ±14:00, so +14:30 would fail in the oracle rather
            // than in the parser.
            var offsetHours = random.Next(-12, 14);
            var offsetMinutes = random.Next(0, 2) == 0 ? 0 : 30;

            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"{year:D4}{month:D2}{day:D2}{hour:D2}{minute:D2}{second:D2} " +
                $"{(offsetHours < 0 ? '-' : '+')}{Math.Abs(offsetHours):D2}{offsetMinutes:D2}");

            var offset = new TimeSpan(offsetHours, offsetHours < 0 ? -offsetMinutes : offsetMinutes, 0);
            var expected = new DateTimeOffset(year, month, day, hour, minute, second, offset)
                .ToUnixTimeSeconds();

            Assert.True(XmltvTimestamp.TryParse(text, out var actual), $"Failed to parse '{text}'.");
            Assert.Equal(expected, actual);
            checkedCount++;
        }

        _output.WriteLine($"{checkedCount:N0} timestamps agreed with DateTimeOffset");
    }

    [Fact]
    public void Agrees_with_dotnet_on_every_day_of_several_leap_and_century_years()
    {
        // The years where leap arithmetic actually differs: a normal leap year, the
        // century that is not a leap year, and the century that is.
        foreach (var year in new[] { 1972, 1900 + 100, 2000, 2024, 2100 })
        {
            if (year < 1971)
            {
                continue;
            }

            for (var month = 1; month <= 12; month++)
            {
                for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
                {
                    var text = $"{year:D4}{month:D2}{day:D2}120000 +0000";
                    var expected = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero)
                        .ToUnixTimeSeconds();

                    Assert.True(XmltvTimestamp.TryParse(text, out var actual), text);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Fact]
    public void Rejects_every_impossible_day_of_february()
    {
        for (var year = 1971; year <= 2100; year++)
        {
            var invalidDay = DateTime.IsLeapYear(year) ? 30 : 29;
            var text = $"{year:D4}02{invalidDay:D2}120000 +0000";

            Assert.False(
                XmltvTimestamp.TryParse(text, out _),
                $"'{text}' is not a real date but was accepted.");
        }
    }
}
