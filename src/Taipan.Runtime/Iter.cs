namespace Taipan.Runtime;

/// <summary>
/// What <c>for</c> iterates over (SPEC §5.2). Changing a collection's size during its iteration
/// panics (SPEC §7). A list checks its size itself, since .NET's enumerator also rejects writing to
/// an element. For a dict or set, .NET's enumerators detect it and this turns their error into the panic.
/// </summary>
public static class Iter
{
    public static IEnumerable<T> Over<T>(List<T> list)
    {
        var count = list.Count;
        for (var i = 0; ; i++)
        {
            if (list.Count != count)
                throw new RuntimeError("list mutated during iteration");
            if (i == count)
                yield break;
            yield return list[i];
        }
    }

    /// <summary>A range can't change, so it needs no guard (SPEC §6).</summary>
    public static IEnumerable<long> Over(RangeObject range) => range;

    /// <summary>A tuple[T, ...] can't change, so it needs no guard (SPEC §3.19).</summary>
    public static IEnumerable<T> Over<T>(VarTuple<T> tuple) => tuple;

    /// <summary>A set iterates over its values, in insertion order (SPEC §3.15).</summary>
    public static IEnumerable<T> Over<T>(OrderedSet<T> set) where T : notnull => Guard(set, "set");

    /// <summary>A dict iterates over its keys, in insertion order.</summary>
    public static IEnumerable<TKey> Over<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull
    {
        foreach (var entry in Guard(dict, "dict"))
            yield return entry.Key;
    }

    /// <summary>A str iterates over its characters (code points), each as a str.</summary>
    public static IEnumerable<string> Over(string value)
    {
        foreach (var rune in value.EnumerateRunes())
            yield return rune.ToString();
    }

    private static IEnumerable<T> Guard<T>(IEnumerable<T> source, string kind)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
            }
            catch (InvalidOperationException)
            {
                throw new RuntimeError($"{kind} mutated during iteration");
            }
            yield return enumerator.Current;
        }
    }
}
