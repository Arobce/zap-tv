using Iptv.Presentation;

namespace Iptv.Presentation.Tests;

public sealed class NumberEntryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_digit_that_cannot_grow_commits_at_once()
    {
        var entry = new NumberEntry();

        // 7 against 60 rows: 70 is already past the end, so no second digit could follow.
        var result = entry.Press(7, count: 60, Start);

        Assert.True(result.Committed);
        Assert.Equal(7, result.Position);
    }

    [Fact]
    public void A_digit_that_could_grow_waits()
    {
        var entry = new NumberEntry();

        // 4 against 60: 40 to 49 are all real positions, so 4 might be the first of two.
        var result = entry.Press(4, count: 60, Start);

        Assert.False(result.Committed);
        Assert.Equal("4", result.Display);
        Assert.True(entry.IsActive);
    }

    [Fact]
    public void Two_digits_address_the_two_digit_position()
    {
        var entry = new NumberEntry();

        entry.Press(4, count: 600, Start);
        var result = entry.Press(2, count: 600, Start.AddMilliseconds(300));

        // 420 is still reachable in 600 rows, so this one is still waiting - the value is
        // what matters, and it must be 42 and not 4 or 2.
        Assert.False(result.Committed);
        Assert.Equal("42", result.Display);

        Assert.Equal(42, entry.Commit().Position);
    }

    [Fact]
    public void The_timeout_commits_what_was_typed()
    {
        var entry = new NumberEntry();
        entry.Press(4, count: 600, Start);

        Assert.False(entry.Expire(Start.AddSeconds(1)).Committed);

        var result = entry.Expire(Start + NumberEntry.Timeout);

        Assert.True(result.Committed);
        Assert.Equal(4, result.Position);
        Assert.False(entry.IsActive);
    }

    [Fact]
    public void A_digit_after_the_timeout_starts_a_new_number()
    {
        var entry = new NumberEntry();
        entry.Press(4, count: 600, Start);

        // No expiry tick was delivered in between. The gap alone has to be enough, or a
        // dropped tick turns two separate presses into one 45.
        var result = entry.Press(5, count: 600, Start.AddSeconds(30));

        Assert.Equal("5", result.Display);
    }

    [Fact]
    public void A_leading_zero_is_not_a_position()
    {
        var entry = new NumberEntry();

        Assert.False(entry.Press(0, count: 60, Start).Committed);
        Assert.False(entry.IsActive);

        var result = entry.Press(7, count: 60, Start.AddMilliseconds(200));

        Assert.True(result.Committed);
        Assert.Equal(7, result.Position);
    }

    [Fact]
    public void A_digit_that_would_overshoot_the_list_starts_over()
    {
        var entry = new NumberEntry();

        entry.Press(5, count: 50, Start);

        // 55 is past the end of a 50 row list, so the 5 begins a new number rather than
        // extending one that could never commit.
        var result = entry.Press(5, count: 50, Start.AddMilliseconds(200));

        Assert.False(result.Committed);
        Assert.Equal("5", result.Display);
    }

    [Fact]
    public void Digits_against_an_empty_list_are_dropped()
    {
        var entry = new NumberEntry();

        var result = entry.Press(4, count: 0, Start);

        Assert.False(result.Committed);
        Assert.False(entry.IsActive);
    }

    [Fact]
    public void Commit_on_nothing_typed_does_nothing()
    {
        var entry = new NumberEntry();

        var result = entry.Commit();

        Assert.False(result.Committed);
        Assert.Equal(0, result.Position);
    }

    [Fact]
    public void Reset_abandons_the_number()
    {
        var entry = new NumberEntry();
        entry.Press(4, count: 600, Start);

        entry.Reset();

        Assert.False(entry.IsActive);
        Assert.False(entry.Expire(Start.AddMinutes(1)).Committed);
    }
}
