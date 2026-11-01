using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Taipan.Runtime;

/// <summary>Operators whose semantics differ from C#'s (SPEC §5.3).</summary>
public static class Ops
{
    /// <summary><c>/</c>: always true division; dividing by zero panics, for floats too.</summary>
    /// <summary>
    /// <c>x as C</c> between classes (SPEC §4.3): <paramref name="value"/> itself if it's a
    /// <typeparamref name="T"/>, else a panic naming its runtime class.
    /// </summary>
    public static T Cast<T>(object? value, string target) where T : class =>
        value as T ?? throw Panic.Create($"cannot convert {RuntimeClass(value)} to {target}");

    private static string RuntimeClass(object? value) => value switch
    {
        null => "None",
        IObject o => o.TypeName,
        IStruct s => s.TypeName,
        _ => value.GetType().Name,
    };

    /// <summary>Evaluates <paramref name="value"/> once for an expression that uses it twice (SPEC §8).</summary>
    public static TResult Let<T, TResult>(T value, Func<T, TResult> body) => body(value);

    /// <inheritdoc cref="Let"/>
    public static void Do<T>(T value, Action<T> body) => body(value);

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

    // ---- Tuples (SPEC §3.8) ----

    private enum Ordering { Less, LessEqual, Greater, GreaterEqual }

    // Python's tuple comparison: the first pair of elements that differ decides, compared with the
    // operator itself; if there's none, the tuples are equal.
    public static bool Less(object left, object right) => Compare(left, right, Ordering.Less);

    public static bool LessEqual(object left, object right) => Compare(left, right, Ordering.LessEqual);

    public static bool Greater(object left, object right) => Compare(left, right, Ordering.Greater);

    public static bool GreaterEqual(object left, object right) => Compare(left, right, Ordering.GreaterEqual);

    private static bool Compare(object left, object right, Ordering ordering)
    {
        if (left is ITuple l && right is ITuple r)
        {
            for (var i = 0; i < l.Length; i++)
            {
                if (!Equality.Equal(l[i], r[i]))
                    return Compare(l[i]!, r[i]!, ordering);
            }
            return ordering is Ordering.LessEqual or Ordering.GreaterEqual;
        }
        if (left is string ls && right is string rs)
            return Apply(string.CompareOrdinal(ls, rs), 0, ordering);
        if (left is long ll && right is long rl)
            return Apply(ll, rl, ordering);
        return Apply(System.Convert.ToDouble(left), System.Convert.ToDouble(right), ordering);
    }

    private static bool Apply<T>(T left, T right, Ordering ordering) where T : System.Numerics.IComparisonOperators<T, T, bool> => ordering switch
    {
        Ordering.Less => left < right,
        Ordering.LessEqual => left <= right,
        Ordering.Greater => left > right,
        _ => left >= right,
    };

    /// <summary>The total order <c>sorted</c> uses: strings by ordinal, NaN first, tuples element by element.</summary>
    private static int SortCompare(object? left, object? right) => (left, right) switch
    {
        (ITuple l, ITuple r) => Enumerable.Range(0, l.Length).Select(i => SortCompare(l[i], r[i])).FirstOrDefault(c => c != 0),
        (string l, string r) => string.CompareOrdinal(l, r),
        (long l, long r) => l.CompareTo(r),
        _ => System.Convert.ToDouble(left).CompareTo(System.Convert.ToDouble(right)),
    };

    // ---- Slices (SPEC §5.4) ----

    /// <summary><c>xs[start:stop:step]</c>: a new list.</summary>
    public static List<T> Slice<T>(List<T> list, long? start, long? stop, long? step) =>
        [.. SliceIndices(list.Count, start, stop, step).Select(i => list[i])];

    /// <summary><c>s[start:stop:step]</c>, by code point.</summary>
    public static string Slice(string value, long? start, long? stop, long? step)
    {
        var runes = value.EnumerateRunes().ToList();
        return string.Concat(SliceIndices(runes.Count, start, stop, step).Select(i => runes[i].ToString()));
    }

    // Python's slice.indices: bounds are clamped, and omitted ones default to the ends in the
    // step's direction.
    private static IEnumerable<int> SliceIndices(int length, long? start, long? stop, long? step)
    {
        var by = step ?? 1;
        if (by == 0)
            throw Panic.Create("slice step cannot be zero");
        long Adjust(long? bound, long whenOmitted)
        {
            if (bound is not { } b)
                return whenOmitted;
            if (b < 0)
                return Math.Max(b + length, by < 0 ? -1 : 0);
            return Math.Min(b, by < 0 ? length - 1 : length);
        }
        var first = Adjust(start, by < 0 ? length - 1 : 0);
        var end = Adjust(stop, by < 0 ? -1 : length);
        // The count, computed without stepping past the ends, so a huge step can't overflow.
        var count = by > 0
            ? (first < end ? (end - first - 1) / by + 1 : 0)
            : (end < first ? (by == long.MinValue ? 1 : (first - end - 1) / -by + 1) : 0);
        for (long k = 0; k < count; k++)
            yield return (int)(first + k * by);
    }

    // ---- Lists and dicts (SPEC §6) ----

    /// <summary><c>enumerate(xs, start)</c>: (index, element) pairs, in a new list.</summary>
    public static List<(long, T)> Enumerate<T>(List<T> list, long start = 0)
    {
        var result = new List<(long, T)>();
        foreach (var item in Iter.Over(list))
            result.Add((start + result.Count, item));
        return result;
    }

    /// <summary><c>enumerate(s, start)</c> over a str's code points.</summary>
    public static List<(long, string)> Enumerate(string value, long start = 0)
    {
        var result = new List<(long, string)>();
        foreach (var item in Iter.Over(value))
            result.Add((start + result.Count, item));
        return result;
    }

    /// <summary><c>zip(xs, ys)</c>: pairs up to the shorter list's length.</summary>
    public static List<(T1, T2)> Zip<T1, T2>(List<T1> first, List<T2> second)
    {
        var result = new List<(T1, T2)>();
        using var other = Iter.Over(second).GetEnumerator();
        foreach (var item in Iter.Over(first))
        {
            if (!other.MoveNext())
                break;
            result.Add((item, other.Current));
        }
        return result;
    }

    public static List<(TKey, TValue)> Items<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull =>
        [.. dict.Select(entry => (entry.Key, entry.Value))];

    /// <summary><c>sorted(xs, reverse=…)</c>: a new list, in a stable order.</summary>
    public static List<T> Sorted<T>(List<T> list, bool reverse = false) => Order(list, item => item, reverse);

    /// <summary><c>sorted(xs, key=…, reverse=…)</c>.</summary>
    public static List<T> SortedBy<T, TKey>(List<T> list, Function<Func<T, TKey>> key, bool reverse = false) =>
        Order(list, key.Invoke, reverse);

    /// <summary><c>xs.sort(reverse=…)</c>: sorts in place, as <see cref="Sorted"/>.</summary>
    public static void Sort<T>(List<T> list, bool reverse = false) => Replace(list, Sorted(list, reverse));

    /// <summary><c>xs.sort(key=…, reverse=…)</c>.</summary>
    public static void SortBy<T, TKey>(List<T> list, Function<Func<T, TKey>> key, bool reverse = false) =>
        Replace(list, SortedBy(list, key, reverse));

    // Keys are computed once per element, in list order, while the list counts as being iterated.
    // OrderBy and OrderByDescending are stable, so equal keys keep their order either way. Strings
    // compare by ordinal, and a NaN float sorts first, as .NET compares doubles.
    private static List<T> Order<T, TKey>(List<T> list, Func<T, TKey> key, bool reverse)
    {
        var items = new List<(T Item, TKey Key)>();
        foreach (var item in Iter.Over(list))
            items.Add((item, key(item)));
        var comparer = typeof(TKey) == typeof(string) ? (IComparer<TKey>)StringComparer.Ordinal
            : typeof(ITuple).IsAssignableFrom(typeof(TKey)) ? Comparer<TKey>.Create((l, r) => SortCompare(l, r))
            : Comparer<TKey>.Default;
        var ordered = reverse ? items.OrderByDescending(p => p.Key, comparer) : items.OrderBy(p => p.Key, comparer);
        return [.. ordered.Select(p => p.Item)];
    }

    private static void Replace<T>(List<T> list, List<T> items)
    {
        list.Clear();
        list.AddRange(items);
    }

    /// <summary><c>map(f, xs)</c>: a new list (SPEC §6).</summary>
    public static List<TResult> Map<T, TResult>(Function<Func<T, TResult>> function, List<T> list)
    {
        var result = new List<TResult>();
        foreach (var item in Iter.Over(list))
            result.Add(function.Invoke(item));
        return result;
    }

    /// <summary><c>filter(f, xs)</c>: a new list (SPEC §6).</summary>
    public static List<T> Filter<T>(Function<Func<T, bool>> predicate, List<T> list)
    {
        var result = new List<T>();
        foreach (var item in Iter.Over(list))
        {
            if (predicate.Invoke(item))
                result.Add(item);
        }
        return result;
    }

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

    /// <summary><c>d.get(k)</c> when the values are already optional: a missing key gives none.</summary>
    public static TValue? GetOrDefault<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key) where TKey : notnull =>
        dict.TryGetValue(key, out var value) ? value : default;

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
