using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Taipan.Runtime;

/// <summary>Operators whose semantics differ from C#'s (SPEC §5.3).</summary>
public static class Ops
{
    /// <summary>
    /// <c>a is b</c> (SPEC §3.18, M40): identity, where a function value converted to another type
    /// is still the function it was converted from.
    /// </summary>
    public static bool Is(object? a, object? b) =>
        a is IFunction f && b is IFunction g ? ReferenceEquals(f.Identity, g.Identity) : ReferenceEquals(a, b);

    /// <summary><c>/</c>: always true division; dividing by zero panics, for floats too.</summary>
    /// <summary>
    /// <c>x as C</c> between classes (SPEC §4.3): <paramref name="value"/> itself if it's a
    /// <typeparamref name="T"/>, else a panic naming its runtime class.
    /// </summary>
    public static T Cast<T>(object? value, string target) where T : class =>
        value as T ?? throw new TypeError($"cannot convert {RuntimeClass(value)} to {target}");

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
            throw new ZeroDivisionError("division by zero");
        return left / right;
    }

    /// <summary><c>range(…)</c> as a comprehension's iterable (SPEC §3.16, M36).</summary>
    public static IEnumerable<long> Range(long start, long stop, long step = 1)
    {
        RangeStep(step);
        for (var i = start; step > 0 ? i < stop : i > stop; i += step)
            yield return i;
    }

    /// <summary>A dict comprehension's pairs, each key's last value at its first position, as Python's (SPEC §3.16).</summary>
    public static OrderedDictionary<TKey, TValue> ToDict<TKey, TValue>(IEnumerable<(TKey, TValue)> pairs) where TKey : notnull
    {
        var dict = new OrderedDictionary<TKey, TValue>();
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    /// <summary><c>range</c>'s step, which can't be 0 (SPEC §6, M35).</summary>
    public static long RangeStep(long step) => step != 0 ? step : throw new ValueError("range() arg 3 must not be zero");

    /// <summary><c>a ** b</c> on ints (SPEC §5.3, M35): checked, and a negative exponent raises, since Python's result would be a float.</summary>
    public static long Pow(long value, long exponent)
    {
        if (exponent < 0)
            throw new ValueError("negative exponent; use a float base, as in 2.0 ** -1");
        long result = 1;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
                result = checked(result * value);
            exponent >>= 1;
            if (exponent > 0)
                value = checked(value * value);
        }
        return result;
    }

    /// <summary><c>a ** b</c> with a float, as Python's (SPEC §5.3, M35).</summary>
    public static double Pow(double value, double exponent)
    {
        if (value == 0.0 && exponent < 0)
            throw new ZeroDivisionError("0.0 cannot be raised to a negative power");
        if (value < 0 && double.IsFinite(exponent) && Math.Floor(exponent) != exponent)
            throw new ValueError("a negative number can't be raised to a fractional power");
        var result = Math.Pow(value, exponent);
        if (double.IsInfinity(result) && double.IsFinite(value) && double.IsFinite(exponent))
            throw new OverflowError("(34, 'Numerical result out of range')");
        return result;
    }

    /// <summary><c>//</c>: floor division, as in Python.</summary>
    public static long FloorDiv(long left, long right)
    {
        if (right == 0)
            throw new ZeroDivisionError("division by zero");
        if (left == long.MinValue && right == -1)
            throw new OverflowException();
        var quotient = left / right;
        if (left % right != 0 && (left < 0) != (right < 0))
            quotient--;
        return quotient;
    }

    /// <summary><c>%</c>: the result takes the divisor's sign, as in Python.</summary>
    /// <summary><c>a // b</c> with a float, as CPython computes it (SPEC §5.3, M50).</summary>
    public static double FloorDiv(double left, double right) =>
        right != 0 ? Numbers.DivMod(left, right).Item1 : throw new ZeroDivisionError("division by zero");

    /// <summary><c>a % b</c> with a float, as CPython computes it (SPEC §5.3, M50).</summary>
    public static double Mod(double left, double right) =>
        right != 0 ? Numbers.DivMod(left, right).Item2 : throw new ZeroDivisionError("division by zero");

    public static long Mod(long left, long right)
    {
        if (right == 0)
            throw new ZeroDivisionError("division by zero");
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
            if (Equality.ElementEqual(element, item))
                return true;
        }
        return false;
    }

    // ---- Indexing (SPEC §5.4) ----

    public static T Index<T>(List<T> list, long index) => list[(int)Normalize(index, list.Count)];

    public static T Index<T>(VarTuple<T> tuple, long index) => tuple.At(index);

    public static long Index(RangeObject range, long index) => range.At(index);

    public static VarTuple<T> Slice<T>(VarTuple<T> tuple, long? start, long? stop, long? step) => tuple.Slice(start, stop, step);

    public static bool In<T>(T item, VarTuple<T> tuple) => tuple.Contains(item);

    public static bool In(long item, RangeObject range) => range.Contains(item);

    public static TValue Index<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key) where TKey : notnull =>
        dict.TryGetValue(key, out var value) ? value : throw new KeyError($"missing key {Display.Repr(key)}");

    /// <summary><c>s[i]</c>: the code point at <paramref name="index"/>, as a str.</summary>
    public static string Index(string value, long index)
    {
        var runes = value.EnumerateRunes().ToList();
        return runes[(int)Normalize(index, runes.Count)].ToString();
    }

    public static void SetIndex<T>(List<T> list, long index, T value) => list[(int)Normalize(index, list.Count)] = value;

    /// <summary><c>d[k] = v</c> inserts or updates; an update keeps the key's position.</summary>
    public static void SetIndex<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key, TValue value) where TKey : notnull =>
        dict[key] = value;

    // ---- Slice assignment and del (SPEC §5.5, M26) ----

    /// <summary>
    /// <c>xs[a:b:c] = ys</c>: with no step (or 1) the slice is replaced and the list may change
    /// length; with another step, the value must have as many elements as the slice.
    /// </summary>
    public static void SetSlice<T>(List<T> list, long? start, long? stop, long? step, List<T> value)
    {
        var replacement = value.ToList();
        if ((step ?? 1) == 1)
        {
            // Python's start and stop for a step of 1, with stop no earlier than start.
            var indices = SliceBounds(list.Count, start, stop);
            list.RemoveRange(indices.First, indices.End - indices.First);
            list.InsertRange(indices.First, replacement);
            return;
        }
        var slots = SliceIndices(list.Count, start, stop, step).ToList();
        if (slots.Count != replacement.Count)
            throw new ValueError($"attempt to assign sequence of size {replacement.Count} to extended slice of size {slots.Count}");
        for (var i = 0; i < slots.Count; i++)
            list[slots[i]] = replacement[i];
    }

    /// <summary><c>del xs[i]</c>.</summary>
    public static void DelItem<T>(List<T> list, long index) => list.RemoveAt((int)Normalize(index, list.Count));

    /// <summary><c>del d[k]</c>, keeping the other entries in order.</summary>
    public static void DelItem<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key) where TKey : notnull
    {
        if (!dict.Remove(key))
            throw new KeyError($"missing key {Display.Repr(key)}");
    }

    /// <summary><c>del xs[a:b:c]</c>: out-of-range bounds clamp, so only a step of 0 raises.</summary>
    public static void DelSlice<T>(List<T> list, long? start, long? stop, long? step)
    {
        foreach (var index in SliceIndices(list.Count, start, stop, step).OrderDescending())
            list.RemoveAt(index);
    }

    // A step-1 slice's bounds, clamped, with the end no earlier than the start.
    private static (int First, int End) SliceBounds(int length, long? start, long? stop)
    {
        long Clamp(long? bound, long whenOmitted) => bound is not { } b ? whenOmitted : b < 0 ? Math.Max(b + length, 0) : Math.Min(b, length);
        var first = (int)Clamp(start, 0);
        var end = (int)Clamp(stop, length);
        return (first, Math.Max(first, end));
    }

    // A negative index counts from the end.
    private static long Normalize(long index, int count)
    {
        var normalized = index < 0 ? index + count : index;
        if (normalized < 0 || normalized >= count)
            throw new IndexError($"index {index} out of range for length {count}");
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
        // A class element compares by its methods (SPEC §4.8).
        if (left is IClass)
            return Compare(left, right, (int)ordering);
        // Element by element, then a shorter tuple first, for tuple[T, ...] (SPEC §3.19).
        if (left is ITuple l && right is ITuple r)
        {
            for (var i = 0; i < Math.Min(l.Length, r.Length); i++)
            {
                if (!Equality.Equal(l[i], r[i]))
                    return Compare(l[i]!, r[i]!, ordering);
            }
            return Apply(l.Length, r.Length, ordering);
        }
        if (left is string ls && right is string rs)
            return Apply(CompareStr(ls, rs), 0, ordering);
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
        (ITuple l, ITuple r) => Enumerable.Range(0, Math.Min(l.Length, r.Length)).Select(i => SortCompare(l[i], r[i])).FirstOrDefault(c => c != 0) is var c and not 0
            ? c : l.Length.CompareTo(r.Length),
        (string l, string r) => CompareStr(l, r),
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
            throw new ValueError("slice step cannot be zero");
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

    public static List<(TKey, TValue)> Items<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull =>
        [.. dict.Select(entry => (entry.Key, entry.Value))];

    /// <summary><c>sorted(xs, reverse=…)</c>: a new list, in a stable order.</summary>
    public static List<T> Sorted<T>(List<T> list, bool reverse = false, Func<T, T, bool>? less = null) => Order(list, item => item, reverse, less);

    /// <summary><c>sorted(xs, key=…, reverse=…)</c>.</summary>
    public static List<T> SortedBy<T, TKey>(List<T> list, Function<Func<T, TKey>> key, bool reverse = false, Func<TKey, TKey, bool>? less = null) =>
        Order(list, key.Invoke, reverse, less);

    /// <summary><c>xs.sort(reverse=…)</c>: sorts in place, as <see cref="Sorted"/>.</summary>
    public static void Sort<T>(List<T> list, bool reverse = false, Func<T, T, bool>? less = null) =>
        SortInPlace(list, items => Sorted(items, reverse, less));

    // The list is empty while it's sorted, as in Python: what a key or __lt__ adds to it is lost, and
    // reported once the sort is done (SPEC §6).
    private static void SortInPlace<T>(List<T> list, Func<List<T>, List<T>> sort)
    {
        var items = list.ToList();
        list.Clear();
        List<T> sorted;
        try
        {
            sorted = sort(items);
        }
        catch
        {
            Replace(list, items);
            throw;
        }
        var modified = list.Count != 0;
        Replace(list, sorted);
        if (modified)
            throw new ValueError("list modified during sort");
    }

    /// <summary><c>xs.sort(key=…, reverse=…)</c>.</summary>
    public static void SortBy<T, TKey>(List<T> list, Function<Func<T, TKey>> key, bool reverse = false, Func<TKey, TKey, bool>? less = null) =>
        SortInPlace(list, items => SortedBy(items, key, reverse, less));

    /// <summary><c>a op b</c> through the right operand's reflected method, after evaluating both (SPEC §3.11, §4.8).</summary>
    public static TResult Reversed<TLeft, TRight, TResult>(TLeft left, TRight right, Func<TRight, TLeft, TResult> method) => method(right, left);

    // ---- Unpacking (SPEC §3.8, M29) ----

    /// <summary>Checks a list's length against the names it's unpacked into, as Python 3.12 reports it.</summary>
    public static void CheckUnpack(int count, int expected, bool starred)
    {
        if (starred && count < expected)
            throw new ValueError($"not enough values to unpack (expected at least {expected}, got {count})");
        if (!starred && count < expected)
            throw new ValueError($"not enough values to unpack (expected {expected}, got {count})");
        if (!starred && count > expected)
            throw new ValueError($"too many values to unpack (expected {expected})");
    }

    /// <summary>A starred name's new list: the elements after the first <paramref name="start"/>, leaving <paramref name="after"/>.</summary>
    public static List<T> UnpackRest<T>(List<T> list, int start, int after) => list.GetRange(start, list.Count - start - after);

    /// <summary>A list pattern's starred name (SPEC §5.1): binds the rest, and always matches.</summary>
    public static bool Rest<T>(List<T> list, int before, int after, out List<T> rest)
    {
        rest = UnpackRest(list, before, after);
        return true;
    }

    // ---- Special methods (SPEC §4.8) ----

    /// <summary>
    /// <c>a op b</c> on class values by Python's protocol: if <paramref name="right"/>'s runtime class is a
    /// proper subclass of <paramref name="left"/>'s, its reflected method first; then the left's
    /// method, then the right's reflected one, each only where it applies.
    /// </summary>
    public static bool Compare(object left, object right, int op)
    {
        var reflected = op switch { IClass.Lt => IClass.Gt, IClass.Le => IClass.Ge, IClass.Gt => IClass.Lt, _ => IClass.Le };
        bool result;
        if (left is IClass && right is IClass r && Equality.Subclassed(left, right) && r.TryCompare(reflected, left, out result))
            return result;
        if (left is IClass l && l.TryCompare(op, right, out result))
            return result;
        if (right is IClass rr && rr.TryCompare(reflected, left, out result))
            return result;
        // Only order=True's methods can refuse, for different runtime classes (SPEC §4.9).
        throw NotSupported(op, left, right);
    }

    /// <summary><c>x as T</c> on an object (SPEC §3.20): its value, if it's a <c>T</c>.</summary>
    public static T Unbox<T>(object? value, string type) =>
        TryUnbox<T>(value, out var typed) ? typed : throw new TypeError($"cannot convert {TypeOf(value)} to {type}");

    /// <summary><c>x as T?</c> on an object: None if it isn't a <c>T</c>.</summary>
    public static T? UnboxValueOrNone<T>(object? value) where T : struct => TryUnbox<T>(value, out var typed) ? typed : null;

    public static T? UnboxOrNone<T>(object? value) where T : class => TryUnbox<T>(value, out var typed) ? typed : null;

    /// <summary>Whether <c>x as T</c> succeeds on an object (SPEC §13.17): a .NET integer widens to <c>int</c>, a <c>float</c> to <c>float</c>, and a <c>char</c> is a <c>str</c>.</summary>
    public static bool Is<T>(object? value) => TryUnbox<T>(value, out _);

    private static bool TryUnbox<T>(object? value, out T result)
    {
        object? converted = value switch
        {
            T => value,
            int or short or sbyte or byte or ushort or uint when typeof(T) == typeof(long) => System.Convert.ToInt64(value),
            ulong big when typeof(T) == typeof(long) => big <= long.MaxValue ? (long)big : throw new OverflowError("int too large to convert"),
            float single when typeof(T) == typeof(double) => (double)single,
            char c when typeof(T) == typeof(string) => c.ToString(),
            _ => null,
        };
        if (converted is T typed)
        {
            result = typed;
            return true;
        }
        result = default!;
        return false;
    }

    // A value's Taipan type, as a message names it.
    private static string TypeOf(object? value) => value switch
    {
        null => "None",
        long => "int",
        double => "float",
        bool => "bool",
        string => "str",
        IStruct s => s.TypeName,
        IObject o => o.TypeName,
        System.Collections.IList => "list",
        System.Collections.IDictionary => "dict",
        System.Runtime.CompilerServices.ITuple => "tuple",
        _ => value.GetType().Name,
    };

    private static TypeError NotSupported(int op, object left, object right)
    {
        var symbol = op switch { IClass.Lt => "<", IClass.Le => "<=", IClass.Gt => ">", _ => ">=" };
        static string Name(object value) => value switch { IStruct s => s.TypeName, IObject o => o.TypeName, _ => value.GetType().Name };
        return new TypeError($"'{symbol}' not supported between instances of '{Name(left)}' and '{Name(right)}'");
    }

    /// <summary>
    /// A comparison <c>order=True</c> generates (SPEC §4.9): the fields as a tuple, for objects of the
    /// same runtime class only, raising <c>TypeError</c> otherwise, as Python's do.
    /// </summary>
    public static bool OrderFields(IStruct left, IStruct right, int op)
    {
        if (left.GetType() != right.GetType())
            throw NotSupported(op, left, right);
        var (l, r) = (left.Fields, right.Fields);
        for (var i = 0; i < l.Count; i++)
        {
            if (!Equality.Equal(l[i].Value, r[i].Value))
                return Compare(l[i].Value!, r[i].Value!, (Ordering)op);
        }
        return op is IClass.Le or IClass.Ge;
    }

    /// <summary>A frozen dataclass's hash (SPEC §4.9): its fields', as a tuple's.</summary>
    public static long HashFields(IStruct value) =>
        value.Fields.Aggregate(0x345678L, (acc, field) => unchecked(acc * 1000003 ^ Hash(field.Value)));

    /// <summary>
    /// <c>a op b</c> through <paramref name="left"/>'s method, unless <paramref name="right"/>'s runtime
    /// class is a proper subclass of the left's with a different reflected method that applies,
    /// which Python calls instead (SPEC §4.8).
    /// </summary>
    public static TResult Arithmetic<TLeft, TRight, TResult>(TLeft left, TRight right, int reflected, Func<TLeft, TRight, TResult> method)
    {
        if (left is IClass l && right is IClass r && Equality.Subclassed(left, right)
            && r.ReflectedOwner(reflected) is { } owner && owner != l.ReflectedOwner(reflected)
            && r.TryReflected(reflected, left, out var result))
            return (TResult)result!;
        return method(left, right);
    }

    /// <summary>A class attribute reached through an instance, which is evaluated first (SPEC §4.8).</summary>
    public static T After<T>(object? instance, T value) => value;

    /// <summary>
    /// <c>hash(x)</c> (SPEC §6): Python's for int and bool, a class's <c>__hash__</c> reduced as Python
    /// reduces it, and otherwise .NET's, which for a str differs between runs, as Python's does.
    /// </summary>
    public static long Hash(object? value) => value switch
    {
        long l => HashInt(l),
        bool b => b ? 1 : 0,
        string s => s.GetHashCode(),
        IClass c => c.TryHash(out var h) ? (h == -1 ? -2 : h) : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c),
        System.Runtime.CompilerServices.ITuple t => Enumerable.Range(0, t.Length).Aggregate(0x345678L, (acc, i) => unchecked(acc * 1000003 ^ Hash(t[i]))),
        null => 0,
        _ => value.GetHashCode(),
    };

    // Python's int hash: the value modulo 2**61 - 1, keeping its sign, with -1 taken by -2.
    private static long HashInt(long value)
    {
        const long modulus = (1L << 61) - 1;
        var magnitude = (long)((ulong)(value < 0 ? -(value + 1) : value) % modulus);
        if (value < 0)
            magnitude = -((magnitude + 1) % modulus);
        return magnitude == -1 ? -2 : magnitude;
    }

    // Keys are computed once per element, in list order, while the list counts as being iterated.
    // OrderBy and OrderByDescending are stable, so equal keys keep their order either way. Strings
    // compare by ordinal, and a NaN float sorts first, as .NET compares doubles. Classes compare
    // through `less`, their __lt__, as Python's sort uses only '<'.
    private static List<T> Order<T, TKey>(List<T> list, Func<T, TKey> key, bool reverse, Func<TKey, TKey, bool>? less)
    {
        var items = new List<(T Item, TKey Key)>();
        foreach (var item in Iter.Over(list))
            items.Add((item, key(item)));
        var comparer = ComparerFor(less);
        var ordered = reverse ? items.OrderByDescending(p => p.Key, comparer) : items.OrderBy(p => p.Key, comparer);
        return [.. ordered.Select(p => p.Item)];
    }

    /// <summary>How sort keys compare: by a class's <c>__lt__</c>, strings by code point, tuples element by element.</summary>
    internal static IComparer<TKey> ComparerFor<TKey>(Func<TKey, TKey, bool>? less) =>
        less is not null ? Comparer<TKey>.Create((l, r) => less(l, r) ? -1 : less(r, l) ? 1 : 0)
            : typeof(TKey) == typeof(string) ? (IComparer<TKey>)Comparer<string>.Create(CompareStr)
            : typeof(ITuple).IsAssignableFrom(typeof(TKey)) ? Comparer<TKey>.Create((l, r) => SortCompare(l, r))
            : Comparer<TKey>.Default;

    private static void Replace<T>(List<T> list, List<T> items)
    {
        list.Clear();
        list.AddRange(items);
    }

    public static List<T> Concat<T>(List<T> left, List<T> right) => [.. left, .. right];

    // ---- Repetition (SPEC §5.3, M48) ----

    private const int MaxStringLength = 0x3FFFFFDF;

    public static string Repeat(string value, long count)
    {
        if (count <= 0 || value.Length == 0)
            return "";
        if (count > MaxStringLength / value.Length)
            throw new OverflowError("repeated sequence is too long");
        return new System.Text.StringBuilder(value.Length * (int)count).Insert(0, value, (int)count).ToString();
    }

    public static string Repeat(long count, string value) => Repeat(value, count);

    public static List<T> Repeat<T>(List<T> list, long count)
    {
        if (count <= 0 || list.Count == 0)
            return [];
        if (count > Array.MaxLength / list.Count)
            throw new OverflowError("repeated sequence is too long");
        var result = new List<T>(list.Count * (int)count);
        for (var i = 0; i < count; i++)
            result.AddRange(list);
        return result;
    }

    public static List<T> Repeat<T>(long count, List<T> list) => Repeat(list, count);

    /// <summary><c>xs += ys</c>: adds to the list itself, and gives it back to be assigned (SPEC §4.5).</summary>
    public static List<T> Extend<T>(List<T> list, List<T> items)
    {
        list.AddRange(items);
        return list;
    }

    public static T Pop<T>(List<T> list)
    {
        if (list.Count == 0)
            throw new IndexError("pop from empty list");
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
            throw new ValueError("empty separator");
        return [.. value.Split(separator)];
    }

    public static string Replace(string value, string oldValue, string newValue)
    {
        // An empty old value matches between all the code points and at both ends, as in Python.
        if (oldValue.Length == 0)
            return newValue + string.Concat(value.EnumerateRunes().Select(rune => rune + newValue));
        return value.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    /// <summary>Orders strings by code point, as in Python (SPEC §5.3). UTF-16 order differs only for surrogates, which come above the rest of the BMP.</summary>
    public static int CompareStr(string left, string right)
    {
        var shared = Math.Min(left.Length, right.Length);
        var at = left.AsSpan(0, shared).CommonPrefixLength(right.AsSpan(0, shared));
        if (at == shared)
            return left.Length.CompareTo(right.Length);
        int l = left[at], r = right[at];
        if (l >= 0xD800 && r >= 0xD800)
        {
            l = l >= 0xE000 ? l - 0x800 : l + 0x2000;
            r = r >= 0xE000 ? r - 0x800 : r + 0x2000;
        }
        return l - r;
    }

    /// <summary><c>int(x)</c> for a float: truncates toward zero (SPEC §6).</summary>
    public static long ToInt(double value)
    {
        // 2^63 is exactly representable; every double below it and at or above -2^63 fits.
        // NaN is a ValueError and anything too large an OverflowError, as in Python (SPEC §7).
        if (double.IsNaN(value))
            throw new ValueError($"cannot convert {Display.Format(value)} to int");
        if (value >= 9223372036854775808.0 || value < -9223372036854775808.0)
            throw new OverflowError($"cannot convert {Display.Format(value)} to int");
        return (long)value;
    }

    /// <summary>Gives <paramref name="value"/> a name in a pattern's <c>when</c>, as a tuple pattern's starred name (SPEC §8, M38). Always true.</summary>
    public static bool Let<T>(T value, out T result)
    {
        result = value;
        return true;
    }

    /// <summary>A mapping pattern's <c>**rest</c> (SPEC §5.1, M38): a new dict of the other keys, in order. Always true.</summary>
    public static bool MappingRest<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey[] keys, out OrderedDictionary<TKey, TValue> rest)
        where TKey : notnull
    {
        rest = new OrderedDictionary<TKey, TValue>();
        foreach (var (key, value) in dict)
        {
            if (!keys.Contains(key))
                rest[key] = value;
        }
        return true;
    }

    /// <summary><c>len(x)</c> through a class's <c>__len__</c> (SPEC §4.9): a negative length raises <c>ValueError</c>, as in Python.</summary>
    public static long CheckLength(long length) =>
        length >= 0 ? length : throw new ValueError("__len__() should return >= 0");

    /// <summary><c>x op= y</c> on a <c>let</c> (SPEC §4.9): the in-place method must give <c>x</c> back.</summary>
    public static void InPlace(object? target, object? result, string op, string method)
    {
        if (!ReferenceEquals(target, result))
            throw new TypeError($"'{op}' can't rebind a 'let': '{method}' returned another object");
    }
}
