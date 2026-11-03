using System.Collections;

namespace Taipan.Runtime;

/// <summary>
/// Taipan's <c>Iterator[T]</c> (SPEC §3.13, M31): a single-pass stream, which a loop goes over once.
/// It's covariant, as Python's <c>Iterator</c> is.
/// </summary>
public interface IIterator<out T> : IEnumerable<T>
{
    /// <summary>Moves to the next value; false once there are none.</summary>
    bool MoveNext();

    T Current { get; }
}

/// <summary>
/// An iterator over a .NET enumerator. A loop over it takes values from where it is and leaves it
/// there if it stops early, as Python's does, so its enumerator isn't disposed by a loop.
/// </summary>
public class Iterator<T>(IEnumerator<T> source, string name) : IIterator<T>
{
    private bool _done;

    public bool MoveNext()
    {
        if (_done)
            return false;
        if (Advance(source))
            return true;
        _done = true;
        source.Dispose();
        return false;
    }

    protected virtual bool Advance(IEnumerator<T> enumerator) => enumerator.MoveNext();

    public T Current => source.Current;

    public IEnumerator<T> GetEnumerator()
    {
        while (MoveNext())
            yield return Current;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"<{name}>";
}

/// <summary>A generator's iterator (SPEC §3.13): a <c>StopIteration</c> escaping its body is a <c>RuntimeError</c>, as in Python 3.7+.</summary>
public sealed class Generator<T>(IEnumerable<T> body, string function) : Iterator<T>(body.GetEnumerator(), $"generator object {function}")
{
    protected override bool Advance(IEnumerator<T> enumerator)
    {
        try
        {
            return enumerator.MoveNext();
        }
        catch (StopIteration)
        {
            throw new RuntimeError("generator raised StopIteration");
        }
    }
}

/// <summary>Iterators for <c>for</c>, the built-ins, and classes (SPEC §3.13).</summary>
public static class Iterators
{
    /// <summary>A class with <c>__next__</c> as an iterator: each value is a call, until <c>StopIteration</c>.</summary>
    public static IIterator<T> FromNext<TObject, T>(TObject value, Func<TObject, T> next) where TObject : notnull =>
        new ObjectIterator<T>(Calls(value, next).GetEnumerator(), value);

    // An iterator class's instance displays as its class does (SPEC §3.13).
    private sealed class ObjectIterator<T>(IEnumerator<T> source, object value) : Iterator<T>(source, "")
    {
        public override string ToString() => Display.Format(value);
    }

    private static IEnumerable<T> Calls<TObject, T>(TObject value, Func<TObject, T> next)
    {
        while (true)
        {
            T item;
            try
            {
                item = next(value);
            }
            catch (StopIteration)
            {
                yield break;
            }
            yield return item;
        }
    }

    /// <summary>A class with <c>__iter__</c> as an iterable: each loop calls <c>__iter__</c> once.</summary>
    public static IEnumerable<T> FromIter<TObject, T>(TObject value, Func<TObject, IIterator<T>> iter) => new Iterable<TObject, T>(value, iter);

    private sealed class Iterable<TObject, T>(TObject value, Func<TObject, IIterator<T>> iter) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => iter(value).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Display.Format(value);
    }

    /// <summary><c>iter(xs)</c>: an iterator over an iterable, or the iterator itself.</summary>
    public static IIterator<T> Iter<T>(IEnumerable<T> values, string name) =>
        values as IIterator<T> ?? new Iterator<T>(values.GetEnumerator(), name);

    /// <summary><c>next(it)</c>: the next value, or <c>StopIteration</c>.</summary>
    public static T Next<T>(IIterator<T> iterator) =>
        iterator.MoveNext() ? iterator.Current : throw new StopIteration("");

    /// <summary><c>next(it, default)</c>: the next value, or the default.</summary>
    public static T Next<T>(IIterator<T> iterator, T fallback) => iterator.MoveNext() ? iterator.Current : fallback;

    /// <summary><c>next(it, None)</c> on an iterator of values, or of references.</summary>
    public static T? NextValueOrNone<T>(IIterator<T> iterator) where T : struct => iterator.MoveNext() ? iterator.Current : null;

    public static T? NextOrNone<T>(IIterator<T> iterator) where T : class => iterator.MoveNext() ? iterator.Current : null;

    /// <summary><c>list(xs)</c>: every value, into a new list.</summary>
    public static List<T> ToList<T>(IEnumerable<T> values) => [.. values];

    /// <summary><c>map(f, xs)</c>: calls <c>f</c> on each value as it's asked for (SPEC §6).</summary>
    public static IIterator<TResult> Map<T, TResult>(Function<Func<T, TResult>> function, IEnumerable<T> values) =>
        new Iterator<TResult>(values.Select(function.Invoke).GetEnumerator(), "map object");

    /// <summary><c>filter(f, xs)</c>: the values <c>f</c> accepts, each tested as it's asked for.</summary>
    public static IIterator<T> Filter<T>(Function<Func<T, bool>> predicate, IEnumerable<T> values) =>
        new Iterator<T>(values.Where(predicate.Invoke).GetEnumerator(), "filter object");

    /// <summary><c>enumerate(xs, start)</c>: (index, value) tuples.</summary>
    public static IIterator<(long, T)> Enumerate<T>(IEnumerable<T> values, long start = 0) =>
        new Iterator<(long, T)>(values.Select((value, i) => (start + i, value)).GetEnumerator(), "enumerate object");

    /// <summary>
    /// <c>zip(a, b, …)</c>: a tuple of one value from each iterable, taken left to right, until the
    /// first that has none, which leaves the later ones untouched, as Python's does.
    /// </summary>
    public static IIterator<TRow> Zip<TRow>(Func<object?[], TRow> row, params IEnumerable[] sources) =>
        new Iterator<TRow>(Rows(row, sources).GetEnumerator(), "zip object");

    private static IEnumerable<TRow> Rows<TRow>(Func<object?[], TRow> row, IEnumerable[] sources)
    {
        var enumerators = sources.Select(s => s.GetEnumerator()).ToArray();
        while (true)
        {
            var values = new object?[enumerators.Length];
            for (var i = 0; i < enumerators.Length; i++)
            {
                if (!enumerators[i].MoveNext())
                    yield break;
                values[i] = enumerators[i].Current;
            }
            yield return row(values);
        }
    }

    /// <summary><c>x in it</c>: takes values until one is equal (SPEC §3.13).</summary>
    public static bool In<T>(T item, IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            if (Equality.ElementEqual(value, item))
                return true;
        }
        return false;
    }
}
