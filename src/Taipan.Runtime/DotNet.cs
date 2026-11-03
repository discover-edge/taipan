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

    /// <summary>A Taipan dict as a new .NET <c>Dictionary</c> (SPEC §13.3, M33).</summary>
    public static Dictionary<TKey, TValue>? ToDictionary<TKey, TValue>(OrderedDictionary<TKey, TValue>? source) where TKey : notnull =>
        source is null ? null : new(source);

    public static Dictionary<TResultKey, TResultValue>? ToDictionary<TKey, TValue, TResultKey, TResultValue>(
        OrderedDictionary<TKey, TValue>? source, Func<TKey, TResultKey> key, Func<TValue, TResultValue> value)
        where TKey : notnull where TResultKey : notnull =>
        source?.ToDictionary(p => key(p.Key), p => value(p.Value));

    /// <summary>A .NET dictionary copied into a new Taipan dict, in the order it enumerates.</summary>
    public static OrderedDictionary<TKey, TValue>? ToDict<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? source) where TKey : notnull =>
        source is null ? null : new(source);

    public static OrderedDictionary<TResultKey, TResultValue>? ToDict<TKey, TValue, TResultKey, TResultValue>(
        IEnumerable<KeyValuePair<TKey, TValue>>? source, Func<TKey, TResultKey> key, Func<TValue, TResultValue> value)
        where TResultKey : notnull
    {
        if (source is null)
            return null;
        var dict = new OrderedDictionary<TResultKey, TResultValue>();
        foreach (var (k, v) in source)
            dict[key(k)] = value(v);
        return dict;
    }

    /// <summary>A task whose result converts when it's done (SPEC §13.11, M33).</summary>
    public static async Task<TResult> MapTask<TSource, TResult>(Task<TSource> task, Func<TSource, TResult> convert) => convert(await task);

    /// <summary><paramref name="make"/> applied to a value, so it's evaluated once: a delegate built around a function (SPEC §13.9).</summary>
    public static TResult With<T, TResult>(T value, Func<T, TResult> make) => make(value);
}
