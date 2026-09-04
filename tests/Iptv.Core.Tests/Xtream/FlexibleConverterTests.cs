using System.Text.Json;
using Iptv.Core.Xtream.Json;

namespace Iptv.Core.Tests.Xtream;

/// <summary>
/// Xtream panels are inconsistent about JSON types, and inconsistent within a single
/// object: the reference provider returns <c>stream_id</c> as a number directly alongside
/// <c>category_id</c> as a string. There is no global rule to apply, so every numeric and
/// boolean field goes through a converter that accepts both forms.
/// </summary>
public sealed class FlexibleConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters =
        {
            new FlexibleInt32Converter(),
            new FlexibleNullableInt32Converter(),
            new FlexibleInt64Converter(),
            new FlexibleBooleanConverter(),
            new FlexibleStringConverter(),
        },
    };

    private sealed record Numbers(int Value);

    private sealed record NullableNumbers(int? Value);

    private sealed record Longs(long Value);

    private sealed record Booleans(bool Value);

    private sealed record Strings(string? Value);

    [Theory]
    [InlineData("""{"Value":42}""", 42)]
    [InlineData("""{"Value":"42"}""", 42)]
    [InlineData("""{"Value":-1}""", -1)]
    [InlineData("""{"Value":"-1"}""", -1)]
    public void Int32_accepts_numbers_and_strings(string json, int expected)
        => Assert.Equal(expected, JsonSerializer.Deserialize<Numbers>(json, Options)!.Value);

    [Theory]
    [InlineData("""{"Value":""}""")]
    [InlineData("""{"Value":null}""")]
    public void Int32_treats_empty_and_null_as_zero(string json)
    {
        // Panels emit "" for "no value". Throwing would abort an entire 28k-stream sync
        // over one cosmetic field.
        Assert.Equal(0, JsonSerializer.Deserialize<Numbers>(json, Options)!.Value);
    }

    [Theory]
    [InlineData("""{"Value":7}""", 7)]
    [InlineData("""{"Value":"7"}""", 7)]
    [InlineData("""{"Value":null}""", null)]
    [InlineData("""{"Value":""}""", null)]
    public void Nullable_int32_distinguishes_absent_from_zero(string json, int? expected)
    {
        // catchup_days absent is different from catchup_days = 0.
        Assert.Equal(expected, JsonSerializer.Deserialize<NullableNumbers>(json, Options)!.Value);
    }

    [Theory]
    [InlineData("""{"Value":1708096234}""", 1708096234L)]
    [InlineData("""{"Value":"1708096234"}""", 1708096234L)]
    public void Int64_accepts_both_forms(string json, long expected)
        => Assert.Equal(expected, JsonSerializer.Deserialize<Longs>(json, Options)!.Value);

    [Theory]
    [InlineData("""{"Value":1}""", true)]
    [InlineData("""{"Value":0}""", false)]
    [InlineData("""{"Value":"1"}""", true)]
    [InlineData("""{"Value":"0"}""", false)]
    [InlineData("""{"Value":true}""", true)]
    [InlineData("""{"Value":false}""", false)]
    [InlineData("""{"Value":null}""", false)]
    [InlineData("""{"Value":""}""", false)]
    public void Boolean_accepts_numbers_strings_and_literals(string json, bool expected)
        => Assert.Equal(expected, JsonSerializer.Deserialize<Booleans>(json, Options)!.Value);

    [Theory]
    [InlineData("""{"Value":"1382"}""", "1382")]
    [InlineData("""{"Value":1382}""", "1382")]
    [InlineData("""{"Value":null}""", null)]
    public void String_accepts_numbers_because_ids_arrive_both_ways(string json, string? expected)
    {
        // category_id is a string on one endpoint and a number on another.
        Assert.Equal(expected, JsonSerializer.Deserialize<Strings>(json, Options)!.Value);
    }

    [Fact]
    public void String_normalizes_empty_to_null()
    {
        // "" and absent mean the same thing to every consumer downstream; collapsing them
        // here keeps "missing" single-valued.
        Assert.Null(JsonSerializer.Deserialize<Strings>("""{"Value":""}""", Options)!.Value);
    }

    [Fact]
    public void Int32_rejects_genuinely_unparseable_values()
    {
        // Tolerance has a limit. A non-numeric string in a numeric field is a payload
        // shape we do not understand, and silently coercing it to zero would hide it.
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<Numbers>("""{"Value":"not-a-number"}""", Options));
    }
}
