using System.Collections;
using System.Runtime.CompilerServices;

namespace Taipan.Runtime;

/// <summary>
/// <c>tuple[T, ...]</c> (SPEC §3.19, M41): any number of <c>T</c>s, immutable. As an
/// <see cref="ITuple"/> it compares, orders, hashes, displays and converts to Python as fixed
/// tuples do.
/// </summary>
public sealed class VarTuple<T> : IReadOnlyList<T>, ITuple
{
    private readonly T[] _items;

    public VarTuple(IEnumerable<T> items) => _items = [.. items];

    public static VarTuple<T> Empty { get; } = new([]);

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    int ITuple.Length => _items.Length;

    object? ITuple.this[int index] => _items[index];

    /// <summary><c>t[i]</c>, from the end when negative, raising <c>IndexError</c> out of range (SPEC §7).</summary>
    public T At(long index)
    {
        var i = index < 0 ? index + _items.Length : index;
        if (i < 0 || i >= _items.Length)
            throw new IndexError($"index {index} out of range for length {_items.Length}");
        return _items[i];
    }

    /// <summary><c>t[start:stop:step]</c>, as a list's slice (SPEC §5.4).</summary>
    public VarTuple<T> Slice(long? start, long? stop, long? step) => new(Ops.Slice([.. _items], start, stop, step));

    public bool Contains(T value) => _items.Any(item => Equality.Equal(item, value));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public override bool Equals(object? obj) => obj is VarTuple<T> other && Equality.Equal(this, other);

    public override int GetHashCode() => (int)Ops.Hash(this);

    public override string ToString() => Display.Format(this);
}
