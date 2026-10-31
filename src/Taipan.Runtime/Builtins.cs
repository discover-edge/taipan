namespace Taipan.Runtime;

/// <summary>Built-in functions (SPEC §6).</summary>
public static class Builtins
{
    /// <summary>Writes each argument's display form, space-separated, then a newline.</summary>
    public static void Print(params object?[] values) =>
        Io.Out.WriteLine(string.Join(" ", values.Select(Display.Format)));

    public static long Len<T>(List<T> list) => list.Count;

    public static long Len<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull => dict.Count;

    /// <summary><c>len(s)</c>: the number of Unicode code points.</summary>
    public static long Len(string value)
    {
        long count = 0;
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }
}
