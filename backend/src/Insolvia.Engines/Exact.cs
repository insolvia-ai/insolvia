using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Insolvia.Engines;

/// <summary>
/// Python's <c>decimal.quantize</c> rounding modes, named as Python names them.
/// Every <c>quantize</c> call in the ported modules states its mode; one with
/// no <c>rounding=</c> argument rounds with the context's default,
/// <see cref="HalfEven"/>.
/// </summary>
public enum Rounding
{
    /// <summary><c>ROUND_HALF_UP</c>: a half rounds away from zero (<see cref="MidpointRounding.AwayFromZero"/>).</summary>
    HalfUp,

    /// <summary><c>ROUND_HALF_EVEN</c>, the context default: banker's rounding (<see cref="MidpointRounding.ToEven"/>).</summary>
    HalfEven,

    /// <summary><c>ROUND_DOWN</c>: toward zero (<see cref="MidpointRounding.ToZero"/>).</summary>
    Down,

    /// <summary><c>ROUND_UP</c>: away from zero whenever anything is discarded.</summary>
    Up,
}

/// <summary>
/// The exact rules the engines print by, in one place, defined as Python's:
/// decimal quantization and formatting byte-equal to CPython's, <c>repr</c> of a
/// string (error messages quote values with it), and <c>str(KeyError)</c>.
/// These strings end up on filed PDFs and in API bodies, so "close" is wrong.
/// </summary>
public static class Exact
{
    /// <summary>Two places — Python's <c>Decimal("0.01")</c>.</summary>
    public const int Cents = 2;

    /// <summary>
    /// <c>value.quantize(Decimal("1e-places"), rounding=mode)</c>: rounded with
    /// the mode, then carried at exactly <paramref name="places"/> digits
    /// (Python keeps trailing zeros; so does this).
    /// </summary>
    public static decimal Quantize(decimal value, Rounding mode, int places = Cents)
    {
        var rounded = mode switch
        {
            Rounding.HalfUp => Math.Round(value, places, MidpointRounding.AwayFromZero),
            Rounding.HalfEven => Math.Round(value, places, MidpointRounding.ToEven),
            Rounding.Down => Math.Round(value, places, MidpointRounding.ToZero),
            Rounding.Up => RoundUp(value, places),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        // Python keeps the sign of a negative value that quantizes to zero
        // (Decimal("-0.004").quantize(...) is Decimal("-0.00")).
        return rounded == 0 && (value < 0 || IsNegativeZero(value))
            ? new decimal(0, 0, 0, true, (byte)places)
            : WithScale(rounded, places);
    }

    /// <summary>The value carried at exactly <paramref name="places"/> digits (no rounding: the value must already fit).</summary>
    public static decimal WithScale(decimal value, int places)
    {
        if (value.Scale > places)
        {
            throw new ArgumentException($"{value} does not fit {places} places", nameof(value));
        }

        // A sum's scale is the larger of the two: adding a zero carried at
        // `places` pads the trailing zeros without changing the value.
        return value + new decimal(0, 0, 0, false, (byte)places);
    }

    /// <summary><c>f"{value:f}"</c> — the decimal's own digits, no exponent, sign kept.</summary>
    public static string F(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return IsNegativeZero(value) && !text.StartsWith('-') ? "-" + text : text;
    }

    /// <summary><c>f"{value.quantize(Decimal('0.01'), rounding=mode):f}"</c>.</summary>
    public static string Money(decimal value, Rounding mode) => F(Quantize(value, mode));

    /// <summary>
    /// <c>f"{value:,.2f}"</c>: thousands-grouped, two places. Python's format
    /// spec rounds with the context's default (half-even) when it drops digits.
    /// </summary>
    public static string Grouped(decimal value)
    {
        var rounded = Quantize(value, Rounding.HalfEven);
        var negative = rounded < 0 || IsNegativeZero(rounded);
        var text = Math.Abs(rounded).ToString("#,##0.00", CultureInfo.InvariantCulture);
        return negative ? "-" + text : text;
    }

    /// <summary><c>date.isoformat()</c>.</summary>
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary><c>date.fromisoformat</c> over the strict <c>YYYY-MM-DD</c> shape; null when it is not one.</summary>
    public static DateOnly? ParseIso(string? text) =>
        text is not null
        && text.Length == 10
        && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>Python's <c>max(a, b)</c>: the first argument unless the second is strictly greater.</summary>
    public static decimal Max(decimal a, decimal b) => b > a ? b : a;

    /// <summary>Python's <c>min(a, b)</c>: the first argument unless the second is strictly smaller.</summary>
    public static decimal Min(decimal a, decimal b) => b < a ? b : a;

    /// <summary>Python's <c>Decimal(int)</c> — an integer as a scale-0 decimal.</summary>
    public static decimal Dec(int value) => value;

    /// <summary>Python's <c>Decimal(str)</c> over a canonical decimal string — the scale is kept (<c>"1.50"</c> stays 1.50).</summary>
    public static decimal Dec(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>(base) ** exponent</c> for a non-negative integer exponent, by
    /// repeated squaring — Python's <c>Decimal.__pow__</c> with an integral
    /// power. Both round each product to the type's precision (28 digits in
    /// Python's context, 28-29 here), so the last digits may differ; every
    /// caller quantizes to the cent afterwards.
    /// </summary>
    public static decimal Pow(decimal value, int exponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);
        var result = 1m;
        var square = value;
        var remaining = exponent;
        while (remaining > 0)
        {
            if ((remaining & 1) == 1)
            {
                result *= square;
            }

            remaining >>= 1;
            if (remaining > 0)
            {
                square *= square;
            }
        }

        return result;
    }

    /// <summary>Python's <c>repr(str)</c>: single quotes unless the text holds one and no double quote.</summary>
    public static string Repr(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var quote = value.Contains('\'', StringComparison.Ordinal) && !value.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder(value.Length + 2);
        builder.Append(quote);
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (rune.Value == quote)
                    {
                        builder.Append('\\').Append(quote);
                    }
                    else if (rune.Value < 0x20 || rune.Value == 0x7f)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\x{rune.Value:x2}");
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }

                    break;
            }
        }

        builder.Append(quote);
        return builder.ToString();
    }

    /// <summary>
    /// Python's <c>repr</c> of a parsed JSON value, for the loaders' messages
    /// that quote what they refused (<c>f"{value!r}"</c>).
    /// </summary>
    public static string Repr(JsonNode? value) => value switch
    {
        null => "None",
        JsonArray list => "[" + string.Join(", ", list.Select(Repr)) + "]",
        JsonObject map => "{" + string.Join(", ", map.Select(pair => Repr(pair.Key) + ": " + Repr(pair.Value))) + "}",
        JsonValue scalar => scalar.GetValueKind() switch
        {
            JsonValueKind.String => Repr(scalar.GetValue<string>()),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => NumberRepr(scalar),
            _ => scalar.ToJsonString(),
        },
        _ => value.ToJsonString(),
    };

    /// <summary>
    /// Python's <c>str(error)</c>: a <c>KeyError</c> prints the repr of its
    /// message (quotes and all), every other error its message.
    /// </summary>
    public static string Str(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is EngineKeyException key ? Repr(key.Message) : error.Message;
    }

    private static string NumberRepr(JsonValue scalar)
    {
        var raw = scalar.ToJsonString();
        if (Core.Json.TryGetWholeNumber(scalar, out var whole))
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? FloatRepr(number)
            : raw;
    }

    private static string FloatRepr(double number)
    {
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static decimal RoundUp(decimal value, int places)
    {
        var truncated = Math.Round(value, places, MidpointRounding.ToZero);
        if (truncated == value)
        {
            return truncated;
        }

        var step = new decimal(1, 0, 0, false, (byte)places);
        return value > 0 ? truncated + step : truncated - step;
    }

    private static bool IsNegativeZero(decimal value) =>
        value == 0 && (decimal.GetBits(value)[3] & int.MinValue) != 0;
}
