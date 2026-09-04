using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iptv.Core.Xtream.Json;

/// <summary>
/// Shared parsing for the tolerant converters below.
/// </summary>
/// <remarks>
/// Xtream panels are inconsistent about JSON types, and inconsistent <em>within a single
/// object</em>: the reference provider returns <c>stream_id</c> as a number directly
/// alongside <c>category_id</c> as a string. There is no global rule to apply, so each
/// field that can vary gets a converter that accepts both forms.
/// <para>
/// Tolerance stops at values that cannot be interpreted. An unparseable value in a numeric
/// field means the payload is a shape we do not understand; coercing it to zero would hide
/// that behind wrong data, which is worse than a loud failure.
/// </para>
/// </remarks>
internal static class FlexibleReader
{
    /// <summary>Reads a number that may be encoded as a JSON number or string.</summary>
    /// <returns><see langword="null"/> for JSON null and for the empty string.</returns>
    public static long? ReadOptionalInt64(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var number))
                {
                    return number;
                }

                // Some panels send numeric ids in scientific notation or with a decimal
                // point. Truncating is correct for an id; failing is not.
                return (long)reader.GetDouble();

            case JsonTokenType.True:
                return 1;

            case JsonTokenType.False:
                return 0;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Panels emit "" to mean "no value".
                    return null;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                {
                    return (long)real;
                }

                throw new JsonException(
                    $"Expected a number but found the non-numeric string '{text}'. " +
                    $"This is an unrecognised payload shape rather than a tolerable variation.");

            default:
                throw new JsonException(
                    $"Expected a number, string, boolean or null but found {reader.TokenType}.");
        }
    }
}

/// <summary>Reads an <see cref="int"/> from a JSON number or string. Absent becomes zero.</summary>
public sealed class FlexibleInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (int)(FlexibleReader.ReadOptionalInt64(ref reader) ?? 0);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Reads an <see cref="int"/>? from a JSON number or string, preserving the difference
/// between absent and zero.
/// </summary>
/// <remarks>
/// <c>catchup_days</c> absent is not the same as <c>catchup_days</c> of zero.
/// </remarks>
public sealed class FlexibleNullableInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (int?)FlexibleReader.ReadOptionalInt64(ref reader);

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

/// <summary>Reads a <see cref="long"/> from a JSON number or string.</summary>
public sealed class FlexibleInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FlexibleReader.ReadOptionalInt64(ref reader) ?? 0;

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Reads a <see cref="long"/>? from a JSON number or string, preserving absent.
/// </summary>
/// <remarks>
/// Used for the unix-seconds fields (<c>added</c>, <c>exp_date</c>), where absent must not
/// collapse to zero: 1970 is a real timestamp and would silently mean "expired".
/// </remarks>
public sealed class FlexibleNullableInt64Converter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => FlexibleReader.ReadOptionalInt64(ref reader);

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

/// <summary>Reads a <see cref="bool"/> from <c>0</c>/<c>1</c>, <c>"0"</c>/<c>"1"</c>, or a literal.</summary>
/// <remarks>
/// <c>auth</c> arrives as a number, <c>tv_archive</c> as a number, and other panels send
/// the same fields as strings or as real booleans.
/// </remarks>
public sealed class FlexibleBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return reader.GetBoolean();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (bool.TryParse(text, out var literal))
            {
                return literal;
            }
        }

        return (FlexibleReader.ReadOptionalInt64(ref reader) ?? 0) != 0;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}

/// <summary>Reads a string that may arrive as a JSON number.</summary>
/// <remarks>
/// <c>category_id</c> is a string on one endpoint and a number on another. Empty is
/// normalized to <see langword="null"/> so that "missing" stays single-valued for
/// everything downstream.
/// </remarks>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);

            case JsonTokenType.True:
                return "true";

            case JsonTokenType.False:
                return "false";

            default:
                throw new JsonException($"Expected a string or number but found {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
