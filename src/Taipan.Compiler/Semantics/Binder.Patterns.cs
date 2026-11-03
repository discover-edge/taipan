using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Patterns II (SPEC §5.1, M38): or-patterns, as patterns, None, optional positions, mapping
// patterns and stars in tuple patterns.
public sealed partial class Binder
{
    private bool IsBareBinding(Pattern pattern, TaipanType type) =>
        pattern is VariantPattern { Module: null, Enum: null, Bindings: null } bare && IsBinding(bare.Variant.Text, type);

    /// <summary>
    /// An arm's pattern after earlier arms cover None (SPEC §5.1): a bare binding, or <c>_ as x</c>,
    /// has the optional's inner type there.
    /// </summary>
    private BoundPattern BindUnwrapped(Pattern pattern, OptionalOf type, PatternBindings bindings) => pattern switch
    {
        VariantPattern bare when IsBareBinding(bare, type) => BindName(bare.Variant, type.Inner, bindings, typed: true),
        AsPattern { Pattern: WildcardPattern } named => BindName(named.Name, type.Inner, bindings, typed: true, variantLike: false),
        _ => BindPattern(pattern, type, bindings),
    };

    /// <summary>A top-level or-pattern's alternatives, through an <c>as</c> and groups, for the ones that can't match (TP0300).</summary>
    private static ImmutableArray<Pattern>? Alternatives(Pattern pattern) => pattern switch
    {
        AsPattern named => Alternatives(named.Pattern),
        OrPattern choice => choice.Alternatives,
        _ => null,
    };

    /// <summary>
    /// The type a pattern narrows the value at its position to, or null if it doesn't: a class
    /// pattern's class, the inner type for a pattern that can't match None, and an or-pattern's
    /// alternatives' common type (SPEC §5.1).
    /// </summary>
    private static TaipanType? Narrows(BoundPattern pattern, TaipanType type) => pattern switch
    {
        BoundAsPattern named => Narrows(named.Pattern, type),
        BoundSomePattern some when type is OptionalOf optional => Narrows(some.Inner, optional.Inner) ?? optional.Inner,
        BoundClassPattern classPattern when type is ClassType classType && classPattern.Class.Symbol.DerivesFrom(classType.Symbol) => classPattern.Class,
        BoundOrPattern choice => choice.Alternatives.Select(a => Narrows(a, type)).ToList() is var each && each.All(t => t is not null)
            ? each.Skip(1).Aggregate(each[0], (a, b) => a == b ? a : Join(a!, b!) ?? (TypeChecker.Unwrap(type) is var inner && a == inner && b == inner ? inner : null))
            : null,
        _ => null,
    };

    /// <summary>
    /// <c>A | B</c> (SPEC §5.1): each alternative must bind the same names, each of one type, except
    /// that classes join to their common base. The alternatives then share one symbol per name.
    /// </summary>
    private BoundPattern BindOrPattern(OrPattern pattern, TaipanType type, PatternBindings bindings)
    {
        var alternatives = pattern.Alternatives.Select(a =>
        {
            var own = new PatternBindings();
            var bound = BindPattern(a, type, own);
            bindings.Valid &= own.Valid;
            return (Syntax: a, Pattern: bound, Bindings: own);
        }).ToList();

        var first = alternatives[0].Bindings.Names;
        var valid = true;
        foreach (var alternative in alternatives.Skip(1))
        {
            var names = alternative.Bindings.Names.Select(n => n.Item1.Text).ToHashSet();
            var firstNames = first.Select(n => n.Item1.Text).ToHashSet();
            if (firstNames.SetEquals(names))
                continue;
            var missing = firstNames.Except(names).Concat(names.Except(firstNames)).First();
            _diagnostics.ReportAlternativeNames(alternative.Syntax.Span, missing);
            valid = false;
        }
        if (!valid)
        {
            bindings.Valid = false;
            foreach (var (identifier, symbol) in alternatives.SelectMany(a => a.Bindings.Names).DistinctBy(n => n.Item1.Text))
                BindName(identifier, TaipanType.Error, bindings, variantLike: false);
            return new BoundWildcardPattern();
        }

        // One symbol per name, of the type each alternative gives it, or the classes' common base.
        var shared = new Dictionary<VariableSymbol, (VariableSymbol Symbol, bool Typed)>();
        foreach (var (identifier, firstSymbol) in first)
        {
            var symbols = alternatives.Select(a => a.Bindings.Names.First(n => n.Item1.Text == identifier.Text).Item2).ToList();
            var joined = firstSymbol.Type;
            foreach (var symbol in symbols.Skip(1))
            {
                if (symbol.Type == joined || symbol.Type.ContainsError || joined.ContainsError)
                    continue;
                if (Join(joined, symbol.Type) is { } common)
                {
                    joined = common;
                    continue;
                }
                _diagnostics.ReportAlternativeTypes(identifier.Span, identifier.Text, joined, symbol.Type);
                bindings.Valid = false;
                joined = TaipanType.Error;
                break;
            }
            var bound = BindName(identifier, joined, bindings, variantLike: false).Symbol;
            foreach (var symbol in symbols)
                shared[symbol] = (bound, symbol.Type != joined && !joined.ContainsError);
        }
        return new BoundOrPattern([.. alternatives.Select(a => Share(a.Pattern, shared))]);
    }

    // A pattern with its bindings replaced by the symbols an or-pattern's alternatives share.
    private static BoundPattern Share(BoundPattern pattern, Dictionary<VariableSymbol, (VariableSymbol Symbol, bool Typed)> shared)
    {
        BoundPattern Sub(BoundPattern p) => Share(p, shared);
        VariableSymbol? Symbol(VariableSymbol? s) => s is not null && shared.TryGetValue(s, out var found) ? found.Symbol : s;
        return pattern switch
        {
            BoundBindingPattern binding when shared.TryGetValue(binding.Symbol, out var found) =>
                new BoundBindingPattern(found.Symbol) { Typed = binding.Typed || found.Typed },
            BoundAsPattern named when shared.TryGetValue(named.Symbol, out var found) =>
                new BoundAsPattern(Sub(named.Pattern), found.Symbol, named.Typed || found.Typed),
            BoundAsPattern named => named with { Pattern = Sub(named.Pattern) },
            BoundSomePattern some => new BoundSomePattern(Sub(some.Inner)),
            BoundOrPattern choice => new BoundOrPattern([.. choice.Alternatives.Select(Sub)]),
            BoundVariantPattern variant => variant with { Fields = [.. variant.Fields.Select(Sub)] },
            BoundClassPattern classPattern => classPattern with { Fields = [.. classPattern.Fields.Select(f => (f.Field, Sub(f.Pattern)))] },
            BoundTuplePattern tuple => tuple with { Elements = [.. tuple.Elements.Select(Sub)], Rest = Symbol(tuple.Rest) },
            BoundListPattern list => list with { Before = [.. list.Before.Select(Sub)], After = [.. list.After.Select(Sub)], Rest = Symbol(list.Rest) },
            BoundMappingPattern mapping => mapping with { Entries = [.. mapping.Entries.Select(e => (e.Key, Sub(e.Value)))], Rest = Symbol(mapping.Rest) },
            _ => pattern,
        };
    }

    /// <summary><c>p as name</c> (SPEC §5.1): <c>name</c> has the type <c>p</c> narrows its value to.</summary>
    private BoundPattern BindAsPattern(AsPattern pattern, TaipanType type, PatternBindings bindings)
    {
        var inner = BindPattern(pattern.Pattern, type, bindings);
        var narrowed = Narrows(inner, type) ?? type;
        var symbol = BindName(pattern.Name, narrowed, bindings, variantLike: false).Symbol;
        return new BoundAsPattern(inner, symbol, Typed: narrowed != type && narrowed is not OptionalOf);
    }

    /// <summary>
    /// <c>{"k": p, **rest}</c> on a <c>dict[K, V]</c> (SPEC §5.1): literal keys of an int, str or
    /// bool <c>K</c>, each value matched at <c>V</c>; <c>rest</c> is a <c>dict[K, V]</c> of the other keys.
    /// </summary>
    private BoundPattern BindMappingPattern(MappingPattern pattern, TaipanType type, PatternBindings bindings)
    {
        BoundPattern Invalid()
        {
            bindings.Valid = false;
            foreach (var entry in pattern.Entries)
                BindPattern(entry.Value, TaipanType.Error, bindings);
            if (pattern.Rest is { } restName)
                BindName(restName, TaipanType.Error, bindings, variantLike: false);
            return new BoundWildcardPattern();
        }

        if (type is not DictOfType dict)
        {
            if (!type.ContainsError)
                _diagnostics.ReportCannotMatchPattern(pattern.Span, type, "mapping");
            return Invalid();
        }
        if (!pattern.Entries.IsEmpty && dict.Key != TaipanType.Int && dict.Key != TaipanType.Str && dict.Key != TaipanType.Bool)
        {
            if (!dict.Key.ContainsError)
                _diagnostics.ReportMappingKeyType(pattern.Entries[0].Key.Span, dict.Key);
            return Invalid();
        }
        var entries = ImmutableArray.CreateBuilder<(object, BoundPattern)>();
        var seen = new HashSet<object>();
        foreach (var entry in pattern.Entries)
        {
            var key = BindPattern(entry.Key, dict.Key, bindings);
            var value = BindPattern(entry.Value, dict.Value, bindings);
            if (key is not BoundLiteralPattern { Value: var literal })
                continue;
            if (!seen.Add(literal))
            {
                _diagnostics.ReportDuplicateMappingKey(entry.Key.Span, _source.ToString(entry.Key.Span));
                bindings.Valid = false;
            }
            entries.Add((literal, value));
        }
        var rest = pattern.Rest is { } name ? BindName(name, dict, bindings, variantLike: false).Symbol : null;
        return new BoundMappingPattern(dict, entries.ToImmutable(), rest);
    }

    /// <summary>
    /// A tuple pattern with <c>*</c> (SPEC §5.1): the star stands for the elements the other
    /// sub-patterns leave, as wildcards; <c>*rest</c> binds them as a list. Null after reporting.
    /// </summary>
    private BoundTuplePattern? BindStarredTuplePattern(TuplePattern pattern, TupleOfType tuple, int star, PatternBindings bindings)
    {
        var others = pattern.Elements.Length - 1;
        if (others > tuple.Elements.Length)
        {
            _diagnostics.ReportStarredTuplePatternCount(pattern.Span, tuple, tuple.Elements.Length, others);
            return null;
        }
        var count = tuple.Elements.Length - others;
        var after = pattern.Elements.Length - star - 1;
        var elements = ImmutableArray.CreateBuilder<BoundPattern>();
        for (var i = 0; i < star; i++)
            elements.Add(BindPattern(pattern.Elements[i], tuple.Elements[i], bindings));
        for (var i = 0; i < count; i++)
            elements.Add(new BoundWildcardPattern());
        for (var i = 0; i < after; i++)
            elements.Add(BindPattern(pattern.Elements[star + 1 + i], tuple.Elements[star + count + i], bindings));

        VariableSymbol? rest = null;
        if (pattern.Elements[star] is StarPattern { Name: { Text: not "_" } name } starPattern)
        {
            var element = StarredElement(tuple.Elements[star..(star + count)], name.Text, starPattern.Span);
            if (element is null)
                bindings.Valid = false;
            rest = BindName(name, element is null ? TaipanType.Error : new ListOfType(element), bindings, variantLike: false).Symbol;
        }
        return new BoundTuplePattern(tuple, elements.ToImmutable()) { StarAt = star, StarCount = count, Rest = rest };
    }
}
