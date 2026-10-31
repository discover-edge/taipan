namespace Taipan.Runtime;

/// <summary>Built-in functions (SPEC §6).</summary>
public static class Builtins
{
    /// <summary>Writes each argument's display form, space-separated, then a newline.</summary>
    public static void Print(params object?[] values) =>
        Io.Out.WriteLine(string.Join(" ", values.Select(Display.Format)));

    /// <summary><c>len(s)</c>: the number of Unicode code points.</summary>
    public static long Len(string value)
    {
        long count = 0;
        foreach (var _ in value.EnumerateRunes())
            count++;
        return count;
    }
}
