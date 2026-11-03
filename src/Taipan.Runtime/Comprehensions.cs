namespace Taipan.Runtime;

/// <summary>
/// What comprehensions and generator expressions compile to (SPEC §3.16, M36): LINQ's lazy
/// operators, here so a program needs no reference beyond the runtime.
/// </summary>
public static class Comprehensions
{
    public static IEnumerable<T> Where<T>(IEnumerable<T> values, Func<T, bool> condition) => values.Where(condition);

    public static IEnumerable<TResult> SelectMany<T, TResult>(IEnumerable<T> values, Func<T, IEnumerable<TResult>> inner) => values.SelectMany(inner);

    public static IEnumerable<TResult> Select<T, TResult>(IEnumerable<T> values, Func<T, TResult> element) => values.Select(element);
}
