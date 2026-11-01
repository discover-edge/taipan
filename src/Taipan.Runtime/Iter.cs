namespace Taipan.Runtime;

/// <summary>
/// What <c>for</c> iterates over (SPEC §5.2). Changing a list or dict during its iteration
/// panics (SPEC §7); .NET's enumerators detect it and this turns their error into the panic.
/// </summary>
public static class Iter
{
    public static IEnumerable<T> Over<T>(List<T> list) => Guard(list, "list");

    // Dicts with an iteration in progress, with nesting counts. .NET's enumerator catches added
    // and removed keys but not a changed value, which Ops.SetIndex checks against this.
    private static readonly Dictionary<object, int> IteratingDicts = new(ReferenceEqualityComparer.Instance);

    /// <summary>A dict iterates over its keys, in insertion order.</summary>
    public static IEnumerable<TKey> Over<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull
    {
        IteratingDicts[dict] = IteratingDicts.GetValueOrDefault(dict) + 1;
        try
        {
            foreach (var entry in Guard(dict, "dict"))
                yield return entry.Key;
        }
        finally
        {
            if (--IteratingDicts[dict] == 0)
                IteratingDicts.Remove(dict);
        }
    }

    internal static bool IsIterating(object dict) => IteratingDicts.ContainsKey(dict);

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
