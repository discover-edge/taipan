using System.Globalization;
using System.Text;

namespace Taipan.Runtime;

// The methods of str, list, dict and set that M35 adds (SPEC §6, §3.15), named as in Python.
// Indices and widths count code points, as len does.

/// <summary><c>str</c> methods (SPEC §6, M35).</summary>
public static class StrMethods
{
    public static string join(string separator, IEnumerable<string> values) => string.Join(separator, values);

    // A UTF-16 index as a code-point index.
    private static long CodePoints(string value, int utf16) => utf16 < 0 ? -1 : value.AsSpan(0, utf16).ToString().EnumerateRunes().LongCount();

    public static long find(string value, string sub) => CodePoints(value, value.IndexOf(sub, StringComparison.Ordinal));

    public static long rfind(string value, string sub) =>
        CodePoints(value, sub.Length == 0 ? value.Length : value.LastIndexOf(sub, StringComparison.Ordinal));

    public static long index(string value, string sub) =>
        find(value, sub) is var found and >= 0 ? found : throw new ValueError("substring not found");

    public static long count(string value, string sub)
    {
        if (sub.Length == 0)
            return Builtins.Len(value) + 1;
        long count = 0;
        for (var at = value.IndexOf(sub, StringComparison.Ordinal); at >= 0; at = value.IndexOf(sub, at + sub.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is '\x1c' or '\x1d' or '\x1e' or '\x1f';

    public static List<string> split(string value) => split(value, null, -1);

    public static List<string> split(string value, string? separator) => split(value, separator, -1);

    /// <summary>Python's <c>str.split</c>: on runs of whitespace without a separator, else on each separator.</summary>
    public static List<string> split(string value, string? separator, long maxsplit)
    {
        var parts = new List<string>();
        if (separator is null)
        {
            var i = 0;
            while (true)
            {
                while (i < value.Length && IsSpace(value[i]))
                    i++;
                if (i == value.Length)
                    return parts;
                if (maxsplit >= 0 && parts.Count == maxsplit)
                {
                    parts.Add(value[i..].TrimEnd());
                    return parts;
                }
                var start = i;
                while (i < value.Length && !IsSpace(value[i]))
                    i++;
                parts.Add(value[start..i]);
            }
        }
        if (separator.Length == 0)
            throw new ValueError("empty separator");
        var from = 0;
        while (maxsplit < 0 || parts.Count < maxsplit)
        {
            var at = value.IndexOf(separator, from, StringComparison.Ordinal);
            if (at < 0)
                break;
            parts.Add(value[from..at]);
            from = at + separator.Length;
        }
        parts.Add(value[from..]);
        return parts;
    }

    public static List<string> rsplit(string value) => rsplit(value, null, -1);

    public static List<string> rsplit(string value, string? separator) => rsplit(value, separator, -1);

    /// <summary>Python's <c>str.rsplit</c>: as <c>split</c>, splitting from the right when <paramref name="maxsplit"/> limits it.</summary>
    public static List<string> rsplit(string value, string? separator, long maxsplit)
    {
        if (maxsplit < 0)
            return split(value, separator, -1);
        var parts = new List<string>();
        if (separator is null)
        {
            var i = value.Length;
            while (true)
            {
                while (i > 0 && IsSpace(value[i - 1]))
                    i--;
                if (i == 0)
                    break;
                if (parts.Count == maxsplit)
                {
                    parts.Add(value[..i].TrimStart());
                    break;
                }
                var end = i;
                while (i > 0 && !IsSpace(value[i - 1]))
                    i--;
                parts.Add(value[i..end]);
            }
            parts.Reverse();
            return parts;
        }
        if (separator.Length == 0)
            throw new ValueError("empty separator");
        var to = value.Length;
        while (parts.Count < maxsplit)
        {
            var at = to == 0 ? -1 : value.LastIndexOf(separator, to - 1, StringComparison.Ordinal);
            if (at < 0 || at + separator.Length > to)
                break;
            parts.Add(value[(at + separator.Length)..to]);
            to = at;
        }
        parts.Add(value[..to]);
        parts.Reverse();
        return parts;
    }

    /// <summary>Lines without their endings, split at Python's line boundaries.</summary>
    public static List<string> splitlines(string value)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029'))
                continue;
            lines.Add(value[start..i]);
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < value.Length)
            lines.Add(value[start..]);
        return lines;
    }

    public static string strip(string value, string chars) => value.Trim(chars.ToCharArray());

    public static string lstrip(string value) => value.TrimStart();

    public static string lstrip(string value, string chars) => value.TrimStart(chars.ToCharArray());

    public static string rstrip(string value) => value.TrimEnd();

    public static string rstrip(string value, string chars) => value.TrimEnd(chars.ToCharArray());

    public static (string, string, string) partition(string value, string separator)
    {
        if (separator.Length == 0)
            throw new ValueError("empty separator");
        var at = value.IndexOf(separator, StringComparison.Ordinal);
        return at < 0 ? (value, "", "") : (value[..at], separator, value[(at + separator.Length)..]);
    }

    public static (string, string, string) rpartition(string value, string separator)
    {
        if (separator.Length == 0)
            throw new ValueError("empty separator");
        var at = value.LastIndexOf(separator, StringComparison.Ordinal);
        return at < 0 ? ("", "", value) : (value[..at], separator, value[(at + separator.Length)..]);
    }

    public static string removeprefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;

    public static string removesuffix(string value, string suffix) =>
        suffix.Length > 0 && value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;

    private static readonly TextInfo Text = CultureInfo.InvariantCulture.TextInfo;

    // Whether a character has case, as Python's "cased" characters do.
    private static bool IsCased(char c) => char.IsUpper(c) || char.IsLower(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.TitlecaseLetter;

    /// <summary>Each word's first cased character upper, the rest lower; a word is a run of cased characters.</summary>
    public static string title(string value)
    {
        var result = new StringBuilder(value.Length);
        var previousCased = false;
        foreach (var c in value)
        {
            result.Append(previousCased ? Text.ToLower(c) : Text.ToUpper(c));
            previousCased = IsCased(c);
        }
        return result.ToString();
    }

    public static string capitalize(string value) =>
        value.Length == 0 ? value : Text.ToUpper(value[0]) + Text.ToLower(value[1..]);

    public static string swapcase(string value) =>
        string.Concat(value.Select(c => char.IsUpper(c) ? Text.ToLower(c) : char.IsLower(c) ? Text.ToUpper(c) : c));

    public static string zfill(string value, long width)
    {
        var length = Builtins.Len(value);
        if (width <= length)
            return value;
        var zeros = new string('0', (int)(width - length));
        return value.Length > 0 && value[0] is '+' or '-' ? value[0] + zeros + value[1..] : zeros + value;
    }

    private static string Fill(string fill) =>
        Builtins.Len(fill) == 1 ? fill : throw new TypeError("The fill character must be exactly one character long");

    public static string ljust(string value, long width) => ljust(value, width, " ");

    public static string ljust(string value, long width, string fill)
    {
        var pad = width - Builtins.Len(value);
        return pad <= 0 ? value : value + string.Concat(Enumerable.Repeat(Fill(fill), (int)pad));
    }

    public static string rjust(string value, long width) => rjust(value, width, " ");

    public static string rjust(string value, long width, string fill)
    {
        var pad = width - Builtins.Len(value);
        return pad <= 0 ? value : string.Concat(Enumerable.Repeat(Fill(fill), (int)pad)) + value;
    }

    public static string center(string value, long width) => center(value, width, " ");

    /// <summary>Python's <c>str.center</c>, whose odd padding goes left when the width is odd.</summary>
    public static string center(string value, long width, string fill)
    {
        var margin = width - Builtins.Len(value);
        if (margin <= 0)
            return value;
        var left = margin / 2 + (margin & width & 1);
        var each = Fill(fill);
        return string.Concat(Enumerable.Repeat(each, (int)left)) + value + string.Concat(Enumerable.Repeat(each, (int)(margin - left)));
    }

    public static bool isdigit(string value) => value.Length > 0 && value.All(char.IsDigit);

    public static bool isalpha(string value) => value.Length > 0 && value.All(char.IsLetter);

    public static bool isalnum(string value) => value.Length > 0 && value.All(char.IsLetterOrDigit);

    public static bool isspace(string value) => value.Length > 0 && value.All(IsSpace);

    public static bool isupper(string value) => value.Any(IsCased) && !value.Any(char.IsLower);

    public static bool islower(string value) => value.Any(IsCased) && !value.Any(char.IsUpper);
}

/// <summary><c>list</c> methods (SPEC §6, M35).</summary>
public static class ListMethods
{
    public static void extend<T>(List<T> list, IEnumerable<T> values) => list.AddRange(values is List<T> same && same == list ? [.. same] : values);

    private static int IndexOf<T>(List<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (Equality.ElementEqual(list[i], value))
                return i;
        }
        return -1;
    }

    public static void remove<T>(List<T> list, T value)
    {
        var at = IndexOf(list, value);
        if (at < 0)
            throw new ValueError("list.remove(x): x not in list");
        list.RemoveAt(at);
    }

    public static long index<T>(List<T> list, T value) =>
        IndexOf(list, value) is var at and >= 0 ? at : throw new ValueError($"{Display.Repr(value)} is not in list");

    public static long count<T>(List<T> list, T value) => list.LongCount(item => Equality.ElementEqual(item, value));

    public static void reverse<T>(List<T> list) => list.Reverse();

    public static void clear<T>(List<T> list) => list.Clear();

    public static List<T> copy<T>(List<T> list) => [.. list];

    public static T pop<T>(List<T> list, long index)
    {
        var at = index < 0 ? index + list.Count : index;
        if (at < 0 || at >= list.Count)
            throw new IndexError(list.Count == 0 ? "pop from empty list" : "pop index out of range");
        var value = list[(int)at];
        list.RemoveAt((int)at);
        return value;
    }
}

/// <summary><c>dict</c> methods (SPEC §6, M35).</summary>
public static class DictMethods
{
    public static TValue pop<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key) where TKey : notnull =>
        dict.Remove(key, out var value) ? value : throw new KeyError(Display.Repr(key));

    public static TValue pop<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key, TValue fallback) where TKey : notnull =>
        dict.Remove(key, out var value) ? value : fallback;

    public static TValue get<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key, TValue fallback) where TKey : notnull =>
        dict.TryGetValue(key, out var value) ? value : fallback;

    public static TValue setdefault<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, TKey key, TValue fallback) where TKey : notnull
    {
        if (dict.TryGetValue(key, out var value))
            return value;
        dict[key] = fallback;
        return fallback;
    }

    public static void update<TKey, TValue>(OrderedDictionary<TKey, TValue> dict, OrderedDictionary<TKey, TValue> other) where TKey : notnull
    {
        foreach (var (key, value) in other.ToList())
            dict[key] = value;
    }

    public static void clear<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull => dict.Clear();

    public static OrderedDictionary<TKey, TValue> copy<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull => new(dict);
}

/// <summary><c>set</c> methods (SPEC §3.15, M35).</summary>
public static class SetMethods
{
    public static void add<T>(OrderedSet<T> set, T value) where T : notnull => set.Add(value);

    public static void remove<T>(OrderedSet<T> set, T value) where T : notnull => set.Remove(value);

    public static void discard<T>(OrderedSet<T> set, T value) where T : notnull => set.Discard(value);

    public static T pop<T>(OrderedSet<T> set) where T : notnull => set.Pop();

    public static void clear<T>(OrderedSet<T> set) where T : notnull => set.Clear();

    public static OrderedSet<T> copy<T>(OrderedSet<T> set) where T : notnull => set.Copy();

    public static void update<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.Update(values.ToList());

    public static OrderedSet<T> union<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.Union(values);

    public static OrderedSet<T> intersection<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.Intersection(values);

    public static OrderedSet<T> difference<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.Difference(values);

    public static OrderedSet<T> symmetric_difference<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.SymmetricDifference(values);

    public static bool issubset<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.IsSubset(values);

    public static bool issuperset<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.IsSuperset(values);

    public static bool isdisjoint<T>(OrderedSet<T> set, IEnumerable<T> values) where T : notnull => set.IsDisjoint(values);
}
