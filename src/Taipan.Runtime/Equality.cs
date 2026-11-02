using System.Runtime.CompilerServices;
using System.Collections;

namespace Taipan.Runtime;

/// <summary>
/// Structural equality for lists, dicts, dataclasses and enums (SPEC §5.3). Dict equality ignores
/// order. Plain class instances and functions compare by identity.
/// </summary>
public static class Equality
{
    public static bool Equal(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (long l, long r) => l == r,
        (double l, double r) => l == r,
        (long l, double r) => l == r,
        (double l, long r) => l == r,
        (string l, string r) => string.Equals(l, r, StringComparison.Ordinal),
        (bool l, bool r) => l == r,
        (IFunction l, IFunction r) => ReferenceEquals(l.Identity, r.Identity),
        (IClass l, IClass r) => ClassesEqual(l, r),
        (ITuple l, ITuple r) => l.Length == r.Length && Enumerable.Range(0, l.Length).All(i => Equal(l[i], r[i])),
        (IStruct l, IStruct r) => l.GetType() == r.GetType() && FieldsEqual(l, r),
        (IDictionary l, IDictionary r) => DictsEqual(l, r),
        (IList l, IList r) => ListsEqual(l, r),
        // A .NET value, which a library's generic code can be given (SPEC §13.3, §14.2).
        _ => left.Equals(right),
    };

    /// <summary>
    /// <c>a == b</c> on classes, by Python's protocol (SPEC §4.8): a subclass's <c>__eq__</c> first,
    /// then the left's, then the right's, each only where it applies, and identity last.
    /// </summary>
    private static bool ClassesEqual(IClass left, IClass right)
    {
        bool result;
        if (Subclassed(left, right) && right.TryEq(left, out result))
            return result;
        if (left.TryEq(right, out result) || right.TryEq(left, out result))
            return result;
        return ReferenceEquals(left, right);
    }

    /// <summary>Whether <paramref name="right"/>'s runtime class is a proper subclass of <paramref name="left"/>'s.</summary>
    public static bool Subclassed(object left, object right) => right.GetType() != left.GetType() && right.GetType().IsSubclassOf(left.GetType());

    /// <summary>Equality inside a container, where an element is equal to itself first, as in Python (SPEC §4.8).</summary>
    public static bool ElementEqual(object? element, object? item) => ReferenceEquals(element, item) && element is IClass || Equal(element, item);

    /// <summary>A hashable class's C# <c>Equals</c>, so dicts find keys by identity, then by <c>==</c> (SPEC §4.8).</summary>
    public static bool KeyEqual(object self, object? other) => other is not null && (ReferenceEquals(self, other) || Equal(self, other));

    /// <summary>A dataclass's generated <c>__eq__</c>: it applies to an object of the same runtime class (SPEC §4.8).</summary>
    public static bool StructEq(IStruct self, object other, out bool result)
    {
        result = other is IStruct that && self.GetType() == other.GetType() && FieldsEqual(self, that);
        return self.GetType() == other.GetType();
    }

    internal static bool FieldsEqual(IStruct left, IStruct right)
    {
        var (l, r) = (left.Fields, right.Fields);
        for (var i = 0; i < l.Count; i++)
        {
            if (!Equal(l[i].Value, r[i].Value))
                return false;
        }
        return true;
    }

    private static bool ListsEqual(IList left, IList right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!ElementEqual(left[i], right[i]))
                return false;
        }
        return true;
    }

    private static bool DictsEqual(IDictionary left, IDictionary right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var key in left.Keys)
        {
            if (!right.Contains(key) || !Equal(left[key], right[key]))
                return false;
        }
        return true;
    }
}
