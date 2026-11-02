namespace Taipan.Runtime;

/// <summary>Conversions at the boundary between Taipan and .NET code (SPEC §13.3).</summary>
public static class DotNet
{
    /// <summary>A result whose type says it isn't null, checked, since .NET doesn't enforce annotations.</summary>
    public static T NotNull<T>(T? value, string member) where T : class =>
        value ?? throw new TypeError($"'{member}' returned null");

    /// <summary>A one-character str as a .NET char.</summary>
    public static char Char(string value) =>
        value.Length == 1 ? value[0] : throw new ValueError($"expected one character, found {Display.Repr(value)}");

    public static char? CharOrNull(string? value) => value is null ? null : Char(value);

    public static string? Text(char? value) => value?.ToString();

    /// <summary>A .NET sequence copied into a new list, as Taipan's lists are .NET's <c>List&lt;T&gt;</c>.</summary>
    public static List<T>? ToList<T>(IEnumerable<T>? source) => source is null ? null : [.. source];

    public static List<TResult>? ToList<TSource, TResult>(IEnumerable<TSource>? source, Func<TSource, TResult> convert) =>
        source is null ? null : [.. source.Select(convert)];

    public static T[]? ToArray<T>(List<T>? source) => source?.ToArray();

    public static TResult[]? ToArray<TSource, TResult>(List<TSource>? source, Func<TSource, TResult> convert) =>
        source is null ? null : [.. source.Select(convert)];
}
