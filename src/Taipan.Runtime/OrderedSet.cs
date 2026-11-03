using System.Collections;

namespace Taipan.Runtime;

/// <summary>A set, seen without its element type, for display and equality (SPEC §3.15).</summary>
public interface ISet
{
    int Count { get; }

    IEnumerable<object?> Values { get; }

    bool ContainsValue(object? value);
}

/// <summary>
/// Taipan's <c>set[T]</c> (SPEC §3.15, M35): distinct values in the order they were first added.
/// Elements are found as dict keys are, by their C# equality and hash code.
/// </summary>
public sealed class OrderedSet<T> : IEnumerable<T>, ISet where T : notnull
{
    private readonly OrderedDictionary<T, bool> _values = [];

    public OrderedSet()
    {
    }

    public OrderedSet(IEnumerable<T> values)
    {
        foreach (var value in values)
            _values.TryAdd(value, true);
    }

    public int Count => _values.Count;

    IEnumerable<object?> ISet.Values => _values.Keys.Cast<object?>();

    bool ISet.ContainsValue(object? value) => value is T typed && _values.ContainsKey(typed);

    public bool Contains(T value) => _values.ContainsKey(value);

    public void Add(T value) => _values.TryAdd(value, true);

    public void Remove(T value)
    {
        if (!_values.Remove(value))
            throw new KeyError(Display.Repr(value));
    }

    public void Discard(T value) => _values.Remove(value);

    /// <summary>Removes and gives the first value, in insertion order.</summary>
    public T Pop()
    {
        if (_values.Count == 0)
            throw new KeyError("'pop from an empty set'");
        var first = _values.GetAt(0).Key;
        _values.RemoveAt(0);
        return first;
    }

    public void Clear() => _values.Clear();

    public OrderedSet<T> Copy() => new(_values.Keys);

    public void Update(IEnumerable<T> values)
    {
        foreach (var value in values)
            _values.TryAdd(value, true);
    }

    public OrderedSet<T> Union(IEnumerable<T> other)
    {
        var result = Copy();
        result.Update(other);
        return result;
    }

    public OrderedSet<T> Intersection(IEnumerable<T> other)
    {
        var keep = other as OrderedSet<T> ?? new OrderedSet<T>(other);
        return new(_values.Keys.Where(keep.Contains));
    }

    public OrderedSet<T> Difference(IEnumerable<T> other)
    {
        var drop = other as OrderedSet<T> ?? new OrderedSet<T>(other);
        return new(_values.Keys.Where(v => !drop.Contains(v)));
    }

    public OrderedSet<T> SymmetricDifference(IEnumerable<T> other)
    {
        var them = other as OrderedSet<T> ?? new OrderedSet<T>(other);
        return new(_values.Keys.Where(v => !them.Contains(v)).Concat(them.Where(v => !Contains(v))));
    }

    public bool IsSubset(IEnumerable<T> other)
    {
        var them = other as OrderedSet<T> ?? new OrderedSet<T>(other);
        return _values.Keys.All(them.Contains);
    }

    public bool IsSuperset(IEnumerable<T> other) => other.All(Contains);

    public bool IsDisjoint(IEnumerable<T> other) => !other.Any(Contains);

    /// <summary><c>a &lt; b</c>: a proper subset.</summary>
    public bool IsProperSubset(OrderedSet<T> other) => Count < other.Count && IsSubset(other);

    public IEnumerator<T> GetEnumerator() => _values.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Display uses Taipan's form, so a set inside a .NET string still reads as one.</summary>
    public override string ToString() => Display.Format(this);
}
