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

    /// <summary>A <c>ref</c> argument's box, which the call changes and the result reads back (SPEC §13.14, M43).</summary>
    public static System.Runtime.CompilerServices.StrongBox<T> Box<T>(T value, out System.Runtime.CompilerServices.StrongBox<T> box) =>
        box = new System.Runtime.CompilerServices.StrongBox<T>(value);

    // The delegate each function value was given to an event as, by delegate type (SPEC §13.18, M43).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<Type, Delegate>> s_handlers = new();

    /// <summary>The delegate for a function value as an event handler: the same one each time, so <c>-=</c> removes what <c>+=</c> added.</summary>
    public static TDelegate Handler<TFunction, TDelegate>(TFunction function, Func<TFunction, TDelegate> make)
        where TFunction : class where TDelegate : Delegate
    {
        var made = s_handlers.GetOrCreateValue(function);
        lock (made)
        {
            if (!made.TryGetValue(typeof(TDelegate), out var handler))
                made[typeof(TDelegate)] = handler = make(function);
            return (TDelegate)handler;
        }
    }

    /// <summary>A .NET enumerable's elements converted to Taipan values, as a loop goes over them (SPEC §13.13, M43).</summary>
    public static IEnumerable<TResult> Elements<TSource, TResult>(IEnumerable<TSource> source, Func<TSource, TResult> convert)
    {
        foreach (var item in source)
            yield return convert(item);
    }
}
