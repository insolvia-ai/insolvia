using System.Globalization;

namespace Insolvia.Forms.Pdf;

/// <summary>
/// A Python number — an <c>int</c> or a <c>float</c> — with Python's
/// arithmetic and printing. pypdf writes its appearance-stream operands with
/// f-strings, so <c>2</c> and <c>2.0</c> are different bytes; the port keeps
/// the type each operand would have in Python, and the streams come out
/// byte-equal (research note 2026-10-27).
/// </summary>
internal readonly record struct TypedNumber(double Value, bool IsInt)
{
    /// <summary>A Python <c>int</c>.</summary>
    public static TypedNumber Int(long value) => new(value, true);

    /// <summary>A Python <c>float</c>.</summary>
    public static TypedNumber Float(double value) => new(value, false);

    public static TypedNumber operator +(TypedNumber a, TypedNumber b) => new(a.Value + b.Value, a.IsInt && b.IsInt);

    public static TypedNumber operator -(TypedNumber a, TypedNumber b) => new(a.Value - b.Value, a.IsInt && b.IsInt);

    public static TypedNumber operator *(TypedNumber a, TypedNumber b) => new(a.Value * b.Value, a.IsInt && b.IsInt);

    /// <summary>Python's true division: always a float.</summary>
    public static TypedNumber operator /(TypedNumber a, TypedNumber b) => new(a.Value / b.Value, false);

    public static TypedNumber operator -(TypedNumber a) => new(-a.Value, a.IsInt);

    /// <summary>Named alternative to the + operator.</summary>
    public static TypedNumber Add(TypedNumber a, TypedNumber b) => a + b;

    /// <summary>Named alternative to the - operator.</summary>
    public static TypedNumber Subtract(TypedNumber a, TypedNumber b) => a - b;

    /// <summary>Named alternative to the * operator.</summary>
    public static TypedNumber Multiply(TypedNumber a, TypedNumber b) => a * b;

    /// <summary>Named alternative to the / operator.</summary>
    public static TypedNumber Divide(TypedNumber a, TypedNumber b) => a / b;

    /// <summary>Named alternative to the unary - operator.</summary>
    public static TypedNumber Negate(TypedNumber a) => -a;

    /// <summary>Python's <c>max(a, b)</c>: the first argument wins a tie, keeping its type.</summary>
    public static TypedNumber Max(TypedNumber a, TypedNumber b) => b.Value > a.Value ? b : a;

    /// <summary>Python's <c>min(a, b)</c>: the first argument wins a tie.</summary>
    public static TypedNumber Min(TypedNumber a, TypedNumber b) => b.Value < a.Value ? b : a;

    /// <summary>Python's <c>round(x, 1)</c>: an int stays an int; a float rounds its exact binary value half-to-even.</summary>
    public static TypedNumber Round1(TypedNumber x) => x.IsInt ? x : Float(Round1(x.Value));

    /// <summary>Python's <c>round(x, n)</c> on a float.</summary>
    public static double Round(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }

        // CPython rounds the EXACT binary value (round(0.15, 1) is 0.1, since
        // 0.15 is stored as 0.1499...), half-to-even only on a true tie, then
        // reads the decimal result back correctly rounded. A decimal cast
        // would round to 15 digits first and get 0.2, so this works on the
        // exact rational mantissa * 2^exponent instead.
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponentBits == 0)
        {
            exponentBits = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        var exponent = exponentBits - 1075; // |value| = mantissa * 2^exponent
        var numerator = new System.Numerics.BigInteger(mantissa) * System.Numerics.BigInteger.Pow(10, digits);
        var denominator = System.Numerics.BigInteger.One;
        if (exponent >= 0)
        {
            numerator <<= exponent;
        }
        else
        {
            denominator <<= -exponent;
        }

        var quotient = System.Numerics.BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven))
        {
            quotient += 1;
        }

        var text = quotient.ToString(CultureInfo.InvariantCulture);
        if (digits > 0)
        {
            text = text.PadLeft(digits + 1, '0');
            text = text[..^digits] + "." + text[^digits..];
        }

        var result = double.Parse(text, CultureInfo.InvariantCulture);
        return negative ? -result : result;
    }

    private static double Round1(double value) => Round(value, 1);

    /// <summary>The f-string rendering: <c>str(int)</c> or <c>repr(float)</c>.</summary>
    public override string ToString() => IsInt ? ((long)Value).ToString(CultureInfo.InvariantCulture) : FloatRepr(Value);

    /// <summary>
    /// Python's <c>repr(float)</c>: the shortest round-trip digits, ".0" on an
    /// integral value, scientific notation below 1e-4 or from 1e16, with a
    /// signed two-digit exponent.
    /// </summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var r = value.ToString("R", CultureInfo.InvariantCulture);
        var mantissa = r;
        var exponent = 0;
        var e = r.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            mantissa = r[..e];
            exponent = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
        }

        var negative = mantissa.StartsWith('-');
        if (negative)
        {
            mantissa = mantissa[1..];
        }

        // Normalise to digits and a decimal exponent: value = 0.d1d2... * 10^point.
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? mantissa : mantissa[..dot] + mantissa[(dot + 1)..];
        var point = (dot < 0 ? mantissa.Length : dot) + exponent;
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        point -= leading;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return negative ? "-0.0" : "0.0";
        }

        // Python's float_repr_style 'short': fixed notation when -4 <= exp10 < 16.
        var exp10 = point - 1;
        string body;
        if (exp10 is >= -4 and < 16)
        {
            if (point <= 0)
            {
                body = "0." + new string('0', -point) + digits;
            }
            else if (point >= digits.Length)
            {
                body = digits + new string('0', point - digits.Length) + ".0";
            }
            else
            {
                body = digits[..point] + "." + digits[point..];
            }
        }
        else
        {
            var fraction = digits.Length > 1 ? "." + digits[1..] : string.Empty;
            body = digits[0] + fraction + "e" + (exp10 < 0 ? "-" : "+") + Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture);
        }

        return negative ? "-" + body : body;
    }
}
