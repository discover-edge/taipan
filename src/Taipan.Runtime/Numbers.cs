using System.Globalization;
using System.Numerics;

namespace Taipan.Runtime;

/// <summary>The everyday built-ins of M35 (SPEC §6): min, max, sum, any, all, round, divmod and abs.</summary>
public static class Numbers
{
    // ---- min and max ----

    /// <summary>
    /// <c>min(xs)</c> or <c>max(xs)</c>: of equal values the first, by <paramref name="key"/> when
    /// given; an empty iterable raises <c>ValueError</c> (SPEC §6).
    /// </summary>
    public static T Extreme<T, TKey>(IEnumerable<T> values, Func<T, TKey> key, Func<TKey, TKey, bool>? less, bool max, string name) =>
        TryExtreme(values, key, less, max, out var best) ? best : throw new ValueError($"{name}() iterable argument is empty");

    public static T ExtremeOr<T, TKey>(IEnumerable<T> values, Func<T, TKey> key, Func<TKey, TKey, bool>? less, bool max, T fallback) =>
        TryExtreme(values, key, less, max, out var best) ? best : fallback;

    /// <summary>With <c>default=None</c>: a value type's extreme or none.</summary>
    public static T? ExtremeValueOrNone<T, TKey>(IEnumerable<T> values, Func<T, TKey> key, Func<TKey, TKey, bool>? less, bool max) where T : struct =>
        TryExtreme(values, key, less, max, out var best) ? best : null;

    public static T? ExtremeOrNone<T, TKey>(IEnumerable<T> values, Func<T, TKey> key, Func<TKey, TKey, bool>? less, bool max) where T : class =>
        TryExtreme(values, key, less, max, out var best) ? best : null;

    private static bool TryExtreme<T, TKey>(IEnumerable<T> values, Func<T, TKey> key, Func<TKey, TKey, bool>? less, bool max, out T best)
    {
        var comparer = Ops.ComparerFor(less);
        best = default!;
        TKey bestKey = default!;
        var found = false;
        foreach (var value in values)
        {
            var valueKey = key(value);
            // Only a strictly better value replaces the best, so the first of equal ones wins, as in Python.
            if (!found || (max ? comparer.Compare(valueKey, bestKey) > 0 : comparer.Compare(valueKey, bestKey) < 0))
                (best, bestKey, found) = (value, valueKey, true);
        }
        return found;
    }

    // ---- sum, any, all ----

    public static long Sum(IEnumerable<long> values, long start)
    {
        foreach (var value in values)
            start = checked(start + value);
        return start;
    }

    // Compensated summation (Neumaier's), as in Python 3.12 (SPEC §6).
    public static double Sum(IEnumerable<double> values, double start)
    {
        var total = start;
        var compensation = 0.0;
        foreach (var value in values)
        {
            var next = total + value;
            compensation += Math.Abs(total) >= Math.Abs(value) ? total - next + value : value - next + total;
            total = next;
        }
        return compensation != 0 && double.IsFinite(compensation) ? total + compensation : total;
    }

    public static double Sum(IEnumerable<long> values, double start)
    {
        foreach (var value in values)
            start += value;
        return start;
    }

    public static bool Any(IEnumerable<bool> values)
    {
        foreach (var value in values)
        {
            if (value)
                return true;
        }
        return false;
    }

    public static bool All(IEnumerable<bool> values)
    {
        foreach (var value in values)
        {
            if (!value)
                return false;
        }
        return true;
    }

    // ---- abs, round, divmod ----

    public static long Abs(long value) => value == long.MinValue ? throw new OverflowError("int too large to convert") : Math.Abs(value);

    /// <summary><c>round(x)</c>: the nearest int, ties to even, as Python's.</summary>
    public static long Round(double value)
    {
        if (double.IsNaN(value))
            throw new ValueError("cannot convert float NaN to integer");
        if (double.IsInfinity(value))
            throw new OverflowError("cannot convert float infinity to integer");
        var rounded = Math.Round(value, MidpointRounding.ToEven);
        return rounded is >= -9223372036854775808.0 and < 9223372036854775808.0 ? (long)rounded : throw new OverflowError("int too large to convert");
    }

    /// <summary>
    /// <c>round(x, n)</c>: <paramref name="value"/>'s exact binary value rounded to <paramref name="digits"/>
    /// decimal digits, ties to even, then read back as the nearest float, as Python does.
    /// </summary>
    public static double Round(double value, long digits)
    {
        if (!double.IsFinite(value) || value == 0 || digits > 400)
            return value;
        if (digits < -400)
            return 0.0 * value;
        // value = mantissa * 2^exponent exactly.
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent = 1;
        else
            mantissa |= 1L << 52;
        exponent -= 1075;

        BigInteger numerator = mantissa, denominator = BigInteger.One;
        if (exponent >= 0)
            numerator <<= exponent;
        else
            denominator <<= -exponent;
        if (digits >= 0)
            numerator *= BigInteger.Pow(10, (int)digits);
        else
            denominator *= BigInteger.Pow(10, (int)-digits);
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || twice == denominator && !quotient.IsEven)
            quotient += 1;
        var text = $"{(value < 0 ? "-" : "")}{quotient}e{-digits}";
        var result = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsInfinity(result))
            throw new OverflowError("rounded value too large to represent");
        return result == 0 ? 0.0 * value : result;
    }

    /// <summary><c>round(n, d)</c> on an int: unchanged for <c>d ≥ 0</c>, else to a multiple of 10^-d, ties to even.</summary>
    public static long Round(long value, long digits)
    {
        if (digits >= 0)
            return value;
        if (digits < -18)
            return 0;
        var unit = (long)Math.Pow(10, -digits);
        var quotient = Math.DivRem(value, unit, out var remainder);
        if (remainder < 0)
        {
            quotient -= 1;
            remainder += unit;
        }
        if (remainder * 2 > unit || remainder * 2 == unit && quotient % 2 != 0)
            quotient += 1;
        return checked(quotient * unit);
    }

    public static (long, long) DivMod(long left, long right) => (Ops.FloorDiv(left, right), Ops.Mod(left, right));

    /// <summary><c>divmod</c> on floats, as CPython computes it.</summary>
    public static (double, double) DivMod(double left, double right)
    {
        if (right == 0)
            throw new ZeroDivisionError("float divmod()");
        var mod = left % right;
        var div = (left - mod) / right;
        if (mod != 0)
        {
            if (right < 0 != mod < 0)
            {
                mod += right;
                div -= 1.0;
            }
        }
        else
        {
            mod = right < 0 ? -0.0 : 0.0;
        }
        double floor;
        if (div != 0)
        {
            floor = Math.Floor(div);
            if (div - floor > 0.5)
                floor += 1.0;
        }
        else
        {
            floor = 0.0 * (left / right);
        }
        return (floor, mod);
    }
}
