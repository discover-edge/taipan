namespace Taipan.Runtime;

/// <summary>Built-in functions (SPEC §6).</summary>
public static class Builtins
{
    /// <summary>Writes each argument's display form, space-separated, then a newline.</summary>
    public static void Print(params object?[] values) =>
        Io.Out.WriteLine(string.Join(" ", values.Select(Display.Format)));

    /// <summary><c>print</c> with any of <c>sep</c>, <c>end</c>, <c>file</c> and <c>flush</c> (SPEC §6, M37); a null file is <c>sys.stdout</c>.</summary>
    public static void Print(string sep, string end, TextFile? file, bool flush, params object?[] values) =>
        (file ?? Sys.stdout).Print(string.Join(sep, values.Select(Display.Format)) + end, flush);

    /// <summary><c>input(prompt)</c> (SPEC §6, M37): a line from <c>sys.stdin</c>, without its line ending.</summary>
    public static string Input(string prompt = "")
    {
        if (prompt.Length > 0)
            Sys.stdout.write(prompt);
        Sys.stdout.flush();
        var line = Sys.stdin.readline();
        if (line.Length == 0)
            throw new EOFError("EOF when reading a line");
        return line.EndsWith('\n') ? line[..^1] : line;
    }

    public static long Len<T>(List<T> list) => list.Count;

    public static long Len<T>(VarTuple<T> tuple) => tuple.Count;

    public static long Len<TKey, TValue>(OrderedDictionary<TKey, TValue> dict) where TKey : notnull => dict.Count;

    public static long Len<T>(OrderedSet<T> set) where T : notnull => set.Count;

    /// <summary><c>len(s)</c>: the number of Unicode code points.</summary>
    public static long Len(RangeObject range) => range.Length;

    public static long Len(string value)
    {
        long count = 0;
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }
}
