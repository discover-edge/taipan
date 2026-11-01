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
        (IObject l, IObject r) => ReferenceEquals(l, r),
        (ITuple l, ITuple r) => l.Length == r.Length && Enumerable.Range(0, l.Length).All(i => Equal(l[i], r[i])),
        (IStruct l, IStruct r) => l.GetType() == r.GetType() && FieldsEqual(l, r),
        (IDictionary l, IDictionary r) => DictsEqual(l, r),
        (IList l, IList r) => ListsEqual(l, r),
        _ => false,
    };

    private static bool FieldsEqual(IStruct left, IStruct right)
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
            if (!Equal(left[i], right[i]))
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
