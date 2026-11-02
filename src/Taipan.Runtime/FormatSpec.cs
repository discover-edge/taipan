using System.Globalization;
using System.Numerics;
using System.Text;

namespace Taipan.Runtime;

/// <summary>What a format spec formats (SPEC §6, M26).</summary>
public enum FormatKind { Int, Float, Str }

/// <summary>
/// Python's format mini-language, <c>[[fill]align][sign]["z"]["#"]["0"][width][grouping]["." precision][type]</c>,
/// for f-string holes (SPEC §6, M26): parsed and checked once at compile time, and applied at run
/// time, giving the text Python's <c>format(value, spec)</c> gives.
/// </summary>
public sealed record FormatSpec(
    string? Fill, char? Align, char? Sign, bool NegativeZero, bool Alternate, bool Zero, int? Width, char? Grouping, int? Precision, char? Type)
{
    private const string Aligns = "<>=^";

    /// <summary>Parses a spec; null if it isn't in the mini-language.</summary>
    public static FormatSpec? Parse(string spec)
    {
        var runes = spec.EnumerateRunes().Select(r => r.ToString()).ToList();
        var i = 0;
        string? fill = null;
        char? align = null;
        if (runes.Count >= 2 && runes[1].Length == 1 && Aligns.Contains(runes[1][0]))
        {
            fill = runes[0];
            align = runes[1][0];
            i = 2;
        }
        else if (runes.Count >= 1 && runes[0].Length == 1 && Aligns.Contains(runes[0][0]))
        {
            align = runes[0][0];
            i = 1;
        }
        char At(int index) => index < runes.Count && runes[index].Length == 1 ? runes[index][0] : '\0';

        char? sign = At(i) is '+' or '-' or ' ' ? At(i++) : null;
        var negativeZero = At(i) == 'z' && i++ >= 0;
        var alternate = At(i) == '#' && i++ >= 0;
        var zero = At(i) == '0' && i++ >= 0;
        var width = Digits(ref i);
        char? grouping = At(i) is ',' or '_' ? At(i++) : null;
        int? precision = null;
        if (At(i) == '.')
        {
            i++;
            precision = Digits(ref i);
            if (precision is null)
                return null;
        }
        char? type = null;
        if (i < runes.Count && At(i) != '\0' && "bcdeEfFgGnosxX%".Contains(At(i)))
            type = At(i++);
        if (i != runes.Count)
            return null;
        return new FormatSpec(fill, align, sign, negativeZero, alternate, zero, width, grouping, precision, type);

        int? Digits(ref int index)
        {
            var start = index;
            while (char.IsAsciiDigit(At(index)))
                index++;
            return index == start ? null : int.Parse(string.Concat(runes[start..index]), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Why this spec can't format a value of <paramref name="kind"/>, phrased as SPEC §6's help
    /// is; null if it can. The locale-dependent <c>n</c> type is never supported.
    /// </summary>
    public string? Problem(FormatKind kind)
    {
        if (Type == 'n')
            return "the 'n' type depends on the locale, which Taipan doesn't use";
        var name = kind switch { FormatKind.Int => "int", FormatKind.Float => "float", _ => "str" };
        switch (kind)
        {
            case FormatKind.Str:
                if (Type is not (null or 's'))
                    return $"'{Type}' formats a number, not str";
                if (Sign is not null)
                    return "a sign isn't allowed for str";
                if (NegativeZero)
                    return "'z' isn't allowed for str";
                if (Alternate)
                    return "'#' isn't allowed for str";
                if (Align == '=')
                    return "'=' alignment isn't allowed for str";
                if (Grouping is { } separator)
                    return $"'{separator}' grouping isn't allowed for str";
                return null;
            case FormatKind.Float:
                if (Type is 'b' or 'c' or 'd' or 'o' or 'x' or 'X' or 's')
                    return $"'{Type}' doesn't format a float";
                return null;
            default:
                if (Type == 's')
                    return "'s' formats a str, not int";
                if (IsFloatType)
                    return null;
                if (Precision is not null)
                    return $"precision isn't allowed for an int formatted with '{Type ?? 'd'}'";
                if (NegativeZero)
                    return "'z' isn't allowed for an int";
                if (Type == 'c' && (Sign is not null || Alternate || Grouping is not null))
                    return "'c' takes no sign, '#' or grouping";
                if (Grouping == ',' && Type is 'b' or 'o' or 'x' or 'X')
                    return $"',' grouping isn't allowed with '{Type}'; use '_'";
                return null;
        }
    }

    private bool IsFloatType => Type is 'e' or 'E' or 'f' or 'F' or 'g' or 'G' or '%';

    // ---- Formatting ----

    public string Format(long value)
    {
        if (IsFloatType)
            return Format((double)value);
        if (Type == 'c')
        {
            if (value is < 0 or > 0x10FFFF)
                throw new OverflowError("%c arg not in range(0x110000)");
            return Pad("", "", char.ConvertFromUtf32((int)value), defaultAlign: '<');
        }
        var magnitude = BigInteger.Abs(value);
        var (digits, prefix) = Type switch
        {
            'b' => (ToBase(magnitude, 2), "0b"),
            'o' => (ToBase(magnitude, 8), "0o"),
            'x' => (ToBase(magnitude, 16), "0x"),
            'X' => (ToBase(magnitude, 16).ToUpperInvariant(), "0X"),
            _ => (magnitude.ToString(CultureInfo.InvariantCulture), ""),
        };
        var sign = SignFor(value < 0);
        return Number(sign, Alternate ? prefix : "", digits, "", groupSize: Type is 'b' or 'o' or 'x' or 'X' ? 4 : 3);
    }

    public string Format(double value)
    {
        var negative = double.IsNegative(value) && !double.IsNaN(value);
        var magnitude = Math.Abs(value);
        string body;
        string suffix = "";
        var upper = Type is 'E' or 'F' or 'G';
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            body = double.IsNaN(value) ? "nan" : "inf";
            if (upper)
                body = body.ToUpperInvariant();
            if (Type == '%')
                suffix = "%";
            return Number(SignFor(negative), "", body, suffix, groupSize: 3, isFinite: false);
        }

        switch (Type)
        {
            case 'f' or 'F':
                body = Fixed(magnitude, Precision ?? 6, Alternate);
                break;
            case '%':
                body = Fixed(magnitude * 100, Precision ?? 6, Alternate);
                suffix = "%";
                break;
            case 'e' or 'E':
                body = Exponent(magnitude, Precision ?? 6, upper, Alternate);
                break;
            case 'g' or 'G':
                body = General(magnitude, Precision ?? 6, upper, Alternate, addDotZero: false);
                break;
            default:
                // No type: repr's digits without a precision, else 'g' that keeps a ".0".
                body = Precision is { } precision
                    ? General(magnitude, precision, upper: false, Alternate, addDotZero: true)
                    : Display.Format(magnitude);
                break;
        }
        // 'z': a negative value that rounds to zero is written as zero.
        if (negative && NegativeZero && body.All(c => c is '0' or '.' or 'e' or 'E' or '+' or '-'))
            negative = false;
        return Number(SignFor(negative), "", body, suffix, groupSize: 3);
    }

    public string Format(string value)
    {
        var runes = value.EnumerateRunes().ToList();
        if (Precision is { } precision && runes.Count > precision)
            value = string.Concat(runes.Take(precision).Select(r => r.ToString()));
        return Pad("", "", value, defaultAlign: '<');
    }

    private string SignFor(bool negative) => negative ? "-" : Sign switch { '+' => "+", ' ' => " ", _ => "" };

    // A number: its digits grouped, then padded. With '0' fill and '=' alignment, the zeros are
    // grouped too, as CPython groups them.
    private string Number(string sign, string prefix, string body, string suffix, int groupSize, bool isFinite = true)
    {
        var point = body.IndexOfAny(['.', 'e', 'E']);
        var whole = point < 0 ? body : body[..point];
        var rest = point < 0 ? "" : body[point..];
        if (Grouping is { } separator && isFinite)
        {
            var zeroFilled = FillFor('>') == "0" && AlignFor('>') == '=' && Width is not null;
            var minimum = zeroFilled ? Width!.Value - sign.Length - prefix.Length - rest.Length - suffix.Length : 0;
            whole = Group(whole, separator, groupSize, minimum);
        }
        return Pad(sign, prefix, whole + rest + suffix, defaultAlign: '>');
    }

    // The fill and alignment in effect: '0' before the width fills with zeros, after the sign for a number.
    private string FillFor(char defaultAlign) => Fill ?? (Zero ? "0" : " ");

    private char AlignFor(char defaultAlign) => Align ?? (Zero && defaultAlign == '>' ? '=' : defaultAlign);

    // Groups digits from the right, padding with zeros (grouped too) to a minimum width, as CPython does.
    private static string Group(string digits, char separator, int size, int minimum)
    {
        var result = new StringBuilder();
        var count = 0;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            if (count > 0 && count % size == 0)
                result.Insert(0, separator);
            result.Insert(0, digits[i]);
            count++;
        }
        while (result.Length < minimum)
        {
            if (count > 0 && count % size == 0)
                result.Insert(0, separator);
            result.Insert(0, '0');
            count++;
        }
        if (result.Length > 0 && result[0] == separator)
            result.Insert(0, '0');
        return result.ToString();
    }

    // Pads to the width with the fill, by code points; '=' puts the padding after the sign and prefix.
    private string Pad(string sign, string prefix, string body, char defaultAlign)
    {
        var length = (sign + prefix + body).EnumerateRunes().Count();
        var fill = FillFor(defaultAlign);
        var align = AlignFor(defaultAlign);
        var padding = Math.Max(0, (Width ?? 0) - length);
        string Repeat(int count) => string.Concat(Enumerable.Repeat(fill, count));
        return align switch
        {
            '<' => sign + prefix + body + Repeat(padding),
            '^' => Repeat(padding / 2) + sign + prefix + body + Repeat(padding - padding / 2),
            '=' => sign + prefix + Repeat(padding) + body,
            _ => Repeat(padding) + sign + prefix + body,
        };
    }

    private static string ToBase(BigInteger value, int radix)
    {
        if (value.IsZero)
            return "0";
        var digits = new StringBuilder();
        while (!value.IsZero)
        {
            digits.Insert(0, "0123456789abcdef"[(int)(value % radix)]);
            value /= radix;
        }
        return digits.ToString();
    }

    // ---- Exact decimal digits of a double, rounded half to even, as Python's formatting rounds ----

    // A finite, non-negative double as digits × 10^exponent, exactly.
    private static (BigInteger Digits, int Exponent) Exact(double value)
    {
        if (value == 0)
            return (BigInteger.Zero, 0);
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent++;
        else
            mantissa |= 1L << 52;
        exponent -= 1075;
        // value = mantissa × 2^exponent; a negative power of two is a power of five over a power of ten.
        return exponent >= 0
            ? (new BigInteger(mantissa) << exponent, 0)
            : (new BigInteger(mantissa) * BigInteger.Pow(5, -exponent), exponent);
    }

    // digits × 10^exponent rounded to a multiple of 10^place, half to even: the multiplier.
    private static BigInteger RoundTo(BigInteger digits, int exponent, int place)
    {
        if (place <= exponent)
            return digits * BigInteger.Pow(10, exponent - place);
        var divisor = BigInteger.Pow(10, place - exponent);
        var quotient = BigInteger.DivRem(digits, divisor, out var remainder);
        var twice = remainder * 2;
        if (twice > divisor || (twice == divisor && !quotient.IsEven))
            quotient++;
        return quotient;
    }

    // '#' keeps the decimal point even with no digits after it.
    private static string Fixed(double value, int precision, bool alternate = false)
    {
        var (digits, exponent) = Exact(value);
        var scaled = RoundTo(digits, exponent, -precision).ToString(CultureInfo.InvariantCulture);
        if (precision == 0)
            return alternate ? scaled + "." : scaled;
        scaled = scaled.PadLeft(precision + 1, '0');
        return $"{scaled[..^precision]}.{scaled[^precision..]}";
    }

    // The significant digits of a value rounded to `count` of them, and the power of ten of the first.
    private static (string Digits, int Power) Significant(double value, int count)
    {
        if (value == 0)
            return (new string('0', count), 0);
        var (digits, exponent) = Exact(value);
        var power = digits.ToString(CultureInfo.InvariantCulture).Length - 1 + exponent;
        var rounded = RoundTo(digits, exponent, power - count + 1).ToString(CultureInfo.InvariantCulture);
        // Rounding up can carry into a new digit (9.99 → 10.0).
        if (rounded.Length > count)
        {
            power++;
            rounded = rounded[..count];
        }
        return (rounded, power);
    }

    private static string Exponent(double value, int precision, bool upper, bool alternate = false)
    {
        var (digits, power) = Significant(value, precision + 1);
        var mantissa = precision == 0 ? digits + (alternate ? "." : "") : $"{digits[0]}.{digits[1..]}";
        return $"{mantissa}{(upper ? 'E' : 'e')}{(power < 0 ? '-' : '+')}{Math.Abs(power):00}";
    }

    // 'g': fixed or exponent by the rounded value's power, without trailing zeros unless '#'.
    private static string General(double value, int precision, bool upper, bool alternate, bool addDotZero)
    {
        precision = precision == 0 ? 1 : precision;
        var (_, power) = Significant(value, precision);
        string body;
        // Without a type, the ".0" it adds would be a digit past the precision, so a value with as
        // many digits before the point as the precision is written with an exponent (CPython's ADD_DOT_0).
        if (power >= -4 && power < precision && !(addDotZero && power == precision - 1))
        {
            body = Fixed(value, precision - 1 - power);
            if (!alternate && body.Contains('.'))
                body = body.TrimEnd('0').TrimEnd('.');
            if (addDotZero && !body.Contains('.'))
                body += ".0";
        }
        else
        {
            body = Exponent(value, precision - 1, upper, alternate);
            if (!alternate)
            {
                var e = body.IndexOf(upper ? 'E' : 'e');
                var mantissa = body[..e];
                if (mantissa.Contains('.'))
                    mantissa = mantissa.TrimEnd('0').TrimEnd('.');
                body = mantissa + body[e..];
            }
        }
        return body;
    }
}
