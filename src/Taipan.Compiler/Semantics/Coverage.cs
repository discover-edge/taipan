using System.Collections.Immutable;

namespace Taipan.Compiler.Semantics;

/// <summary>
/// What a match's arms cover, for reachability (TP0300) and exhaustiveness (TP0204), through
/// nested patterns (SPEC §5.1, M29). Patterns are seen as constructors applied to sub-patterns, or
/// wildcards; an arm is reachable if it's useful against the arms before it, and the cases no arm
/// covers are found the same way, as Maranget's algorithm does.
/// </summary>
public sealed class Coverage(TaipanType type)
{
    private abstract record Space;

    private sealed record Any : Space
    {
        public static readonly Any Instance = new();
    }

    /// <summary>A constructor; as a missing case, <see cref="Whole"/> when no arm uses it, so it's shown bare.</summary>
    private sealed record Ctor(object Key, ImmutableArray<Space> Args, bool Whole = false) : Space;

    /// <summary>An or-pattern (M38): rows with one are seen as one row per alternative.</summary>
    private sealed record Or(ImmutableArray<Space> Alternatives) : Space;

    // An optional's two constructors (M38): None, and a value, whose one argument is of the inner type.
    private sealed record NoneKey
    {
        public static readonly NoneKey Instance = new();
    }

    private sealed record SomeKey
    {
        public static readonly SomeKey Instance = new();
    }

    /// <summary>A mapping pattern with keys (M38), by its keys; its arguments are their values' patterns.</summary>
    private sealed record MappingKey(string Keys, int Count);

    // The keys of constructors that aren't symbols or literals.
    private sealed record TupleKey
    {
        public static readonly TupleKey Instance = new();
    }

    /// <summary>A list of exactly <see cref="Length"/> elements, or with <see cref="AtLeast"/>, of that many or more.</summary>
    private sealed record ListLength(int Length, bool AtLeast);

    /// <summary>A list pattern with a star: <see cref="Before"/> elements, any number, then <see cref="After"/>.</summary>
    private sealed record ListStar(int Before, int After);

    // Enough missing cases to say what's wrong, without listing every combination.
    private const int MaxWitnesses = 12;

    private readonly List<Space[]> _rows = [];
    private readonly TaipanType[] _types = [type];

    /// <summary>
    /// Records an arm; false if the arms before it already cover everything it matches (unreachable).
    /// A guarded arm covers nothing (M38), so it's checked but not recorded. <paramref name="dead"/>
    /// lists the alternatives of a top-level or-pattern that can't match anything earlier ones don't.
    /// </summary>
    public bool Add(BoundPattern pattern, bool guarded, out List<int> dead)
    {
        var row = new[] { SpaceOf(pattern) };
        var useful = Useful(_rows, row, _types);
        dead = [];
        if (useful && Unnamed(pattern) is BoundOrPattern choice)
        {
            var before = new List<Space[]>(_rows);
            for (var i = 0; i < choice.Alternatives.Length; i++)
            {
                var alternative = new[] { SpaceOf(choice.Alternatives[i]) };
                if (!Useful(before, alternative, _types))
                    dead.Add(i);
                before.Add(alternative);
            }
        }
        if (!guarded)
            _rows.Add(row);
        return useful;
    }

    /// <summary>Whether the arms so far cover None, at the top of an optional scrutinee (M38).</summary>
    public bool CoversNone() => !Useful(_rows, [new Ctor(NoneKey.Instance, [])], _types);

    private static BoundPattern Unnamed(BoundPattern pattern) => pattern is BoundAsPattern named ? Unnamed(named.Pattern) : pattern;

    /// <summary>The cases no arm covers, as patterns: <c>Down</c>, <c>Degraded(0, _)</c>, <c>[_, *_]</c>.</summary>
    public List<string> Missing() =>
        [.. Witnesses(_rows, _types).Select(w => Show(w[0], _types[0])).Distinct()];

    private static Space SpaceOf(BoundPattern pattern) => pattern switch
    {
        BoundLiteralPattern literal => new Ctor(literal.Value, []),
        BoundVariantPattern { Fields.IsEmpty: true } variant => new Ctor(variant.Variant, [.. variant.Variant.Fields.Select(_ => (Space)Any.Instance)]),
        BoundVariantPattern variant => new Ctor(variant.Variant, [.. variant.Fields.Select(SpaceOf)]),
        BoundClassPattern classPattern => new Ctor(classPattern.Class.Symbol, [.. classPattern.Class.Symbol.AllFields.Select(f =>
            classPattern.Fields.FirstOrDefault(p => p.Field == f).Pattern is { } sub ? SpaceOf(sub) : Any.Instance)]),
        BoundTuplePattern tuple => new Ctor(TupleKey.Instance, [.. tuple.Elements.Select(SpaceOf)]),
        BoundNonePattern => new Ctor(NoneKey.Instance, []),
        BoundSomePattern some => new Ctor(SomeKey.Instance, [SpaceOf(some.Inner)]),
        BoundOrPattern choice => new Or([.. choice.Alternatives.Select(SpaceOf)]),
        BoundAsPattern named => SpaceOf(named.Pattern),
        // One without keys matches every dict, as in Python.
        BoundMappingPattern { Entries.IsEmpty: true } => Any.Instance,
        BoundMappingPattern mapping => new Ctor(new MappingKey(string.Join("\0", mapping.Entries.Select(e => $"{e.Key.GetType().Name}:{e.Key}")), mapping.Entries.Length),
            [.. mapping.Entries.Select(e => SpaceOf(e.Value))]),
        BoundListPattern { HasStar: false } list => new Ctor(new ListLength(list.Before.Length, AtLeast: false), [.. list.Before.Select(SpaceOf)]),
        BoundListPattern list => new Ctor(new ListStar(list.Before.Length, list.After.Length), [.. list.Before.Concat(list.After).Select(SpaceOf)]),
        _ => Any.Instance,
    };

    // ---- Usefulness ----

    private static bool Useful(List<Space[]> rows, Space[] q, TaipanType[] types)
    {
        if (q.Length == 0)
            return rows.Count == 0;
        rows = Expand(rows);
        if (q[0] is Or alternatives)
            return alternatives.Alternatives.Any(a => Useful(rows, [a, .. q[1..]], types));
        var type = types[0];
        if (q[0] is Ctor { Key: ListStar star } starred)
        {
            // A star covers every length from its minimum; one past the longest pattern stands for the rest.
            var longest = Math.Max(Longest(rows), star.Before + star.After);
            for (var length = star.Before + star.After; length <= longest + 1; length++)
            {
                var key = new ListLength(length, AtLeast: length == longest + 1);
                if (Useful(Specialize(rows, key, type), [.. Fill(starred.Args, star, length), .. q[1..]], [.. ArgTypes(key, type), .. types[1..]]))
                    return true;
            }
            return false;
        }
        if (q[0] is Ctor ctor)
            return Useful(Specialize(rows, ctor.Key, type), [.. ctor.Args, .. q[1..]], [.. ArgTypes(ctor.Key, type), .. types[1..]]);
        if (Signature(rows, type) is { } signature)
        {
            return signature.Any(key =>
            {
                var argTypes = ArgTypes(key, type);
                return Useful(Specialize(rows, key, type), [.. argTypes.Select(_ => (Space)Any.Instance), .. q[1..]], [.. argTypes, .. types[1..]]);
            });
        }
        return Useful(Default(rows), q[1..], types[1..]);
    }

    /// <summary>The value vectors no row covers, each as patterns.</summary>
    private static List<Space[]> Witnesses(List<Space[]> rows, TaipanType[] types)
    {
        if (types.Length == 0)
            return rows.Count == 0 ? [[]] : [];
        rows = Expand(rows);
        var type = types[0];
        var found = new List<Space[]>();
        if (Signature(rows, type) is { } signature)
        {
            foreach (var key in signature)
            {
                var argTypes = ArgTypes(key, type);
                var specialized = Specialize(rows, key, type);
                foreach (var witness in Witnesses(specialized, [.. argTypes, .. types[1..]]))
                {
                    found.Add([new Ctor(key, [.. witness[..argTypes.Length]], Whole: specialized.Count == 0), .. witness[argTypes.Length..]]);
                    if (found.Count >= MaxWitnesses)
                        return found;
                }
            }
            return found;
        }
        var rest = Witnesses(Default(rows), types[1..]);
        if (rest.Count == 0)
            return [];
        foreach (var head in MissingHeads(rows, type))
        {
            foreach (var witness in rest)
            {
                found.Add([head, .. witness]);
                if (found.Count >= MaxWitnesses)
                    return found;
            }
        }
        return found;
    }

    /// <summary>
    /// The constructors of a type, when the rows' first column uses enough of them to cover it: all
    /// of an enum's variants or both bools, any tuple or list pattern, or the class's own pattern.
    /// Null when values could be left out by every constructor the rows use.
    /// </summary>
    private static List<object>? Signature(List<Space[]> rows, TaipanType type)
    {
        var keys = rows.Select(r => r[0]).OfType<Ctor>().Select(c => c.Key).ToList();
        if (keys.Count == 0)
            return null;
        switch (type)
        {
            // A finite type is split by constructor once any is used, so every missing case is listed.
            case EnumType enumType:
                return [.. enumType.Symbol.Variants];
            case PrimitiveType { Name: "bool" }:
                return [true, false];
            case TupleOfType:
                return [TupleKey.Instance];
            case ClassType classType when keys.Contains(classType.Symbol):
                return [classType.Symbol];
            case ListOfType:
                var longest = Longest(rows);
                return [.. Enumerable.Range(0, longest + 1).Select(n => (object)new ListLength(n, AtLeast: false)), new ListLength(longest + 1, AtLeast: true)];
            case OptionalOf:
                return [NoneKey.Instance, SomeKey.Instance];
            default:
                return null;
        }
    }

    /// <summary>The first column's missing cases when its signature isn't covered: each unused variant or bool, or else any value.</summary>
    private static IEnumerable<Space> MissingHeads(List<Space[]> rows, TaipanType type)
    {
        var keys = rows.Select(r => r[0]).OfType<Ctor>().Select(c => c.Key).ToHashSet();
        return type switch
        {
            EnumType enumType => enumType.Symbol.Variants.Where(v => !keys.Contains(v))
                .Select(v => new Ctor(v, [.. v.Fields.Select(_ => (Space)Any.Instance)], Whole: true)),
            PrimitiveType { Name: "bool" } => new object[] { true, false }.Where(b => !keys.Contains(b)).Select(b => new Ctor(b, [])),
            OptionalOf => keys.Contains(NoneKey.Instance) ? [new Ctor(SomeKey.Instance, [Any.Instance])] : [new Ctor(NoneKey.Instance, [])],
            _ => [Any.Instance],
        };
    }

    // The longest list a row's first column names exactly, or needs at least.
    private static int Longest(List<Space[]> rows) => rows.Select(r => r[0]).OfType<Ctor>().Select(c => c.Key switch
    {
        ListLength length => length.Length,
        ListStar star => star.Before + star.After,
        _ => 0,
    }).DefaultIfEmpty(0).Max();

    /// <summary>The rows that match a constructor, with its arguments in place of their first column.</summary>
    private static List<Space[]> Specialize(List<Space[]> rows, object key, TaipanType type)
    {
        var arity = ArgTypes(key, type).Length;
        var result = new List<Space[]>();
        foreach (var row in rows)
        {
            if (Project(row[0], key, arity) is { } args)
                result.Add([.. args, .. row[1..]]);
        }
        return result;
    }

    /// <summary>A pattern's sub-patterns for the constructor <paramref name="key"/>, or null if it can't match one.</summary>
    private static Space[]? Project(Space head, object key, int arity)
    {
        if (head is not Ctor ctor)
            return [.. Enumerable.Repeat((Space)Any.Instance, arity)];
        switch (ctor.Key, key)
        {
            case (ListStar star, ListLength length) when star.Before + star.After <= length.Length:
                return Fill(ctor.Args, star, length.Length);
            case (ListLength row, ListLength length):
                return row.Length == length.Length && !length.AtLeast ? [.. ctor.Args] : null;
            // A pattern of a base class matches every instance of a class derived from it.
            case (ClassSymbol rowClass, ClassSymbol keyClass) when keyClass.DerivesFrom(rowClass):
                return [.. keyClass.AllFields.Select(f => rowClass.AllFields.IndexOf(f) is var i and >= 0 ? ctor.Args[i] : Any.Instance)];
            case (ClassSymbol, ClassSymbol):
                return null;
            default:
                return Equals(ctor.Key, key) ? [.. ctor.Args] : null;
        }
    }

    // A star pattern's sub-patterns for a list of `length` elements: wildcards for the middle.
    private static Space[] Fill(ImmutableArray<Space> args, ListStar star, int length) =>
        [.. args[..star.Before], .. Enumerable.Repeat((Space)Any.Instance, length - star.Before - star.After), .. args[star.Before..]];

    /// <summary>The rows with an or-pattern first, as one row per alternative (M38).</summary>
    private static List<Space[]> Expand(List<Space[]> rows)
    {
        if (!rows.Any(r => r[0] is Or))
            return rows;
        var result = new List<Space[]>();
        void Add(Space head, Space[] row)
        {
            if (head is Or choice)
            {
                foreach (var alternative in choice.Alternatives)
                    Add(alternative, row);
            }
            else
            {
                result.Add([head, .. row[1..]]);
            }
        }
        foreach (var row in rows)
            Add(row[0], row);
        return result;
    }

    /// <summary>The rows whose first column is a wildcard, without it.</summary>
    private static List<Space[]> Default(List<Space[]> rows) => [.. rows.Where(r => r[0] is Any).Select(r => r[1..])];

    /// <summary>The types of a constructor's arguments, at a position of <paramref name="type"/>.</summary>
    private static TaipanType[] ArgTypes(object key, TaipanType type) => (key, type) switch
    {
        (VariantSymbol variant, EnumType enumType) => [.. variant.Fields.Select(f => TypeChecker.Substitute(f.Type, TypeChecker.MapOf(enumType)))],
        (ClassSymbol symbol, ClassType classType) => [.. symbol.AllFields.Select(f =>
            TypeChecker.Substitute(f.Type, TypeChecker.MapOf(symbol == classType.Symbol ? classType : symbol.Type)))],
        (TupleKey, TupleOfType tuple) => [.. tuple.Elements],
        (ListLength length, ListOfType list) => [.. Enumerable.Repeat(list.Element, length.Length)],
        (SomeKey, OptionalOf optional) => [optional.Inner],
        (MappingKey mapping, DictOfType dict) => [.. Enumerable.Repeat(dict.Value, mapping.Count)],
        _ => [],
    };

    // ---- Showing missing cases ----

    private static string Show(Space space, TaipanType type)
    {
        if (space is not Ctor ctor)
            return "_";
        var argTypes = ArgTypes(ctor.Key, type);
        var args = ctor.Args.Select((a, i) => Show(a, argTypes[i])).ToList();
        return ctor.Key switch
        {
            VariantSymbol variant when ctor.Whole || args.Count == 0 => variant.Name,
            VariantSymbol variant => $"{variant.Name}({string.Join(", ", args)})",
            bool b => b ? "True" : "False",
            NoneKey => "None",
            SomeKey => args[0],
            ClassSymbol symbol => $"{symbol.Name}({string.Join(", ", symbol.AllFields.Select((f, i) => (f, i)).Where(p => args[p.i] != "_").Select(p => $"{p.f.Name}={args[p.i]}"))})",
            TupleKey => $"({string.Join(", ", args)})",
            ListLength { AtLeast: true } => $"[{string.Join("", args.Select(a => a + ", "))}*_]",
            ListLength => $"[{string.Join(", ", args)}]",
            _ => "_",
        };
    }
}
