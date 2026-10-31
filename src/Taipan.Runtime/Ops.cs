using System.Globalization;
using System.Text.RegularExpressions;

namespace Taipan.Runtime;

/// <summary>Operators whose semantics differ from C#'s (SPEC §5.3).</summary>
public static class Ops
{
    /// <summary><c>/</c>: always true division; dividing by zero panics, for floats too.</summary>
    public static double Div(double left, double right)
    {
        if (right == 0.0)
            throw Panic.Create("division by zero");
        return left / right;
    }

    /// <summary><c>//</c>: floor division, as in Python.</summary>
    public static long FloorDiv(long left, long right)
    {
        if (right == 0)
            throw Panic.Create("division by zero");
        if (left == long.MinValue && right == -1)
            throw new OverflowException();
        var quotient = left / right;
        if (left % right != 0 && (left < 0) != (right < 0))
            quotient--;
        return quotient;
    }

    /// <summary><c>%</c>: the result takes the divisor's sign, as in Python.</summary>
    public static long Mod(long left, long right)
    {
        if (right == 0)
            throw Panic.Create("division by zero");
        if (right == -1)
            return 0;
        var remainder = left % right;
        if (remainder != 0 && (remainder < 0) != (right < 0))
            remainder += right;
        return remainder;
    }

    // Checked int arithmetic for operands the C# compiler would fold at compile time, where an
    // overflow would be a C# error instead of a Taipan panic. Other arithmetic is emitted as C#
    // operators in a checked compilation.

    public static long Add(long left, long right) => checked(left + right);

    public static long Subtract(long left, long right) => checked(left - right);

    public static long Multiply(long left, long right) => checked(left * right);

    public static long Negate(long value) => checked(-value);

    // ---- Membership (SPEC §5.3); operands are evaluated in source order ----

    public static bool In(string item, string container) => container.Contains(item, StringComparison.Ordinal);

    public static bool In<T>(T item, List<T> list) => Contains(list, item);

    public static bool In<TKey, TValue>(TKey key, OrderedDictionary<TKey, TValue> dict) where TKey : notnull =>
        dict.ContainsKey(key);

    /// <summary><c>list.contains(x)</c>, with structural equality.</summary>
    public static bool Contains<T>(List<T> list, T item)
    {
        foreach (var element in list)
        {
            if (Equality.Equal(element, item))
                return true;
        }
        return false;
    }

    // ---- Indexing (SPEC §5.4) ----

    public static T Index<T>(List<T> list, long index) => list[(int)Normalize(index, list.Count)];

    public static TValue Index<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key) where TKey : notnull =>
        dict.TryGetValue(key, out var value) ? value : throw Panic.Create($"missing key {Display.Repr(key)}");

    /// <summary><c>s[i]</c>: the code point at <paramref name="index"/>, as a str.</summary>
    public static string Index(string value, long index)
    {
        var runes = value.EnumerateRunes().ToList();
        return runes[(int)Normalize(index, runes.Count)].ToString();
    }

    public static void SetIndex<T>(List<T> list, long index, T value) => list[(int)Normalize(index, list.Count)] = value;

    /// <summary><c>d[k] = v</c> inserts or updates; an update keeps the key's position.</summary>
    public static void SetIndex<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key, TValue value) where TKey : notnull
    {
        if (Iter.IsIterating(dict))
            throw Panic.Create("dict mutated during iteration");
        dict[key] = value;
    }

    // A negative index counts from the end.
    private static long Normalize(long index, int count)
    {
        var normalized = index < 0 ? index + count : index;
        if (normalized < 0 || normalized >= count)
            throw Panic.Create($"index {index} out of range for length {count}");
        return normalized;
    }

    // ---- Lists and dicts (SPEC §6) ----

    public static List<T> Concat<T>(List<T> left, List<T> right) => [.. left, .. right];

    public static T Pop<T>(List<T> list)
    {
        if (list.Count == 0)
            throw Panic.Create("pop from empty list");
        var last = list[^1];
        list.RemoveAt(list.Count - 1);
        return last;
    }

    /// <summary>Python's <c>list.insert</c>: negative indices count from the end; out-of-range ones clamp.</summary>
    public static void Insert<T>(List<T> list, long index, T value)
    {
        var position = index < 0 ? Math.Max(0, index + list.Count) : Math.Min(index, list.Count);
        list.Insert((int)position, value);
    }

    /// <summary><c>d.get(k)</c> for value-type values (int, float, bool): <c>none</c> if missing.</summary>
    public static TValue? Get<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key)
        where TKey : notnull where TValue : struct =>
        dict.TryGetValue(key, out var value) ? value : null;

    /// <summary><c>d.get(k)</c> for reference-type values: <c>none</c> if missing.</summary>
    public static TValue? GetOrNull<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key)
        where TKey : notnull where TValue : class =>
        dict.TryGetValue(key, out var value) ? value : null;

    public static List<TKey> Keys<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull => [.. dict.Keys];

    public static List<TValue> Values<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull => [.. dict.Values];

    // ---- Strings (SPEC §6) ----

    private static readonly Regex IntText = new(@"^[+-]?[0-9](_?[0-9])*$", RegexOptions.CultureInvariant);

    private static readonly Regex FloatText = new(
        @"^[+-]?(([0-9](_?[0-9])*(\.([0-9](_?[0-9])*)?)?|\.[0-9](_?[0-9])*)([eE][+-]?[0-9](_?[0-9])*)?|inf|infinity|nan)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary><c>int(s)</c>: <c>none</c> unless <paramref name="text"/> is a decimal int in range.</summary>
    public static long? ParseInt(string text)
    {
        var trimmed = text.Trim();
        if (!IntText.IsMatch(trimmed))
            return null;
        return long.TryParse(trimmed.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary><c>float(s)</c>: <c>none</c> unless <paramref name="text"/> is a decimal float, inf or nan.</summary>
    public static double? ParseFloat(string text)
    {
        var trimmed = text.Trim();
        if (!FloatText.IsMatch(trimmed))
            return null;
        var clean = trimmed.Replace("_", "").ToLowerInvariant();
        var negative = clean.StartsWith('-');
        return clean.TrimStart('+', '-') switch
        {
            "inf" or "infinity" => negative ? double.NegativeInfinity : double.PositiveInfinity,
            "nan" => double.NaN,
            _ => double.Parse(clean, NumberStyles.Float, CultureInfo.InvariantCulture),
        };
    }

    public static List<string> Split(string value, string separator)
    {
        if (separator.Length == 0)
            throw Panic.Create("empty separator");
        return [.. value.Split(separator)];
    }

    public static string Replace(string value, string oldValue, string newValue)
    {
        if (oldValue.Length == 0)
            throw Panic.Create("empty separator");
        return value.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    /// <summary><c>int(x)</c> for a float: truncates toward zero (SPEC §6).</summary>
    public static long ToInt(double value)
    {
        // 2^63 is exactly representable; every double below it and at or above -2^63 fits.
        if (double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0)
            throw Panic.Create($"cannot convert {Display.Format(value)} to int");
        return (long)value;
    }
}
