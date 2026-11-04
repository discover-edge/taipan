using System.Collections.Immutable;
using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// Patterns II (SPEC §8, M38): or-patterns C# can't write with `or`, and mapping patterns.
public sealed partial class CSharpEmitter
{
    /// <summary>
    /// A pattern as the alternatives C# needs: an or-pattern that binds names, or needs a
    /// <c>when</c> inside an alternative, becomes one pattern per alternative, through all nesting.
    /// </summary>
    private static IEnumerable<BoundPattern> Expand(BoundPattern pattern) => pattern switch
    {
        BoundOrPattern choice when choice.Alternatives.Any(NeedsCase) => choice.Alternatives.SelectMany(Expand),
        BoundSomePattern some => Expand(some.Inner).Select(i => (BoundPattern)new BoundSomePattern(i)),
        BoundAsPattern named => Expand(named.Pattern).Select(i => (BoundPattern)(named with { Pattern = i })),
        BoundVariantPattern variant => Product(variant.Fields).Select(f => (BoundPattern)(variant with { Fields = f })),
        BoundClassPattern classPattern => Product([.. classPattern.Fields.Select(f => f.Pattern)])
            .Select(p => (BoundPattern)(classPattern with { Fields = [.. classPattern.Fields.Select((f, i) => (f.Field, p[i]))] })),
        BoundDotNetPattern dotnet => Product([.. dotnet.Members.Select(m => m.Pattern)])
            .Select(p => (BoundPattern)(dotnet with { Members = [.. dotnet.Members.Select((m, i) => (m.Member, m.MemberType, p[i]))] })),
        BoundTuplePattern tuple => Product(tuple.Elements).Select(e => (BoundPattern)(tuple with { Elements = e })),
        BoundListPattern list => Product([.. list.Before, .. list.After])
            .Select(e => (BoundPattern)(list with { Before = e[..list.Before.Length], After = e[list.Before.Length..] })),
        BoundMappingPattern mapping => Product([.. mapping.Entries.Select(e => e.Value)])
            .Select(v => (BoundPattern)(mapping with { Entries = [.. mapping.Entries.Select((e, i) => (e.Key, v[i]))] })),
        _ => [pattern],
    };

    // Every choice of one expansion for each sub-pattern, in order.
    private static IEnumerable<ImmutableArray<BoundPattern>> Product(ImmutableArray<BoundPattern> parts)
    {
        IEnumerable<ImmutableArray<BoundPattern>> result = [[]];
        foreach (var part in parts)
        {
            var choices = Expand(part).ToList();
            result = result.SelectMany(prefix => choices.Select(choice => prefix.Add(choice)));
        }
        return result;
    }

    // Whether a pattern declares a variable or needs a `when`, which C# allows under `and` but not `or`.
    private static bool NeedsCase(BoundPattern pattern) => pattern switch
    {
        BoundBindingPattern or BoundAsPattern or BoundMappingPattern => true,
        BoundListPattern list => list.Rest is not null || list.Before.Concat(list.After).Any(NeedsCase),
        BoundTuplePattern tuple => tuple.Rest is not null || tuple.Elements.Any(NeedsCase),
        BoundSomePattern some => NeedsCase(some.Inner),
        BoundOrPattern choice => choice.Alternatives.Any(NeedsCase),
        BoundVariantPattern variant => variant.Fields.Any(NeedsCase),
        BoundClassPattern classPattern => classPattern.Fields.Any(f => NeedsCase(f.Pattern)),
        // Its members are tested in a `when` (SPEC §13.17).
        BoundDotNetPattern dotnet => !dotnet.Members.IsEmpty,
        _ => false,
    };

    /// <summary>
    /// <c>{"k": p, **rest}</c> (SPEC §8): a non-null pattern naming the dict, whose <c>when</c> tests
    /// each key with <c>TryGetValue</c> and its value with <c>is</c>, then gathers the rest.
    /// </summary>
    private string MappingPattern(BoundMappingPattern mapping, List<string> guards)
    {
        var dict = $"@{_locals.Fresh("dict")}";
        foreach (var (key, value) in mapping.Entries)
        {
            var held = $"@{_locals.Fresh("value")}";
            var inner = new List<string>();
            var sub = Pattern(value, inner);
            guards.Add($"{dict}.TryGetValue({Literal(key)}, out var {held}) && {held} is {sub}");
            guards.AddRange(inner);
        }
        if (mapping.Rest is { } rest)
        {
            var keys = string.Join(", ", mapping.Entries.Select(e => Literal(e.Key)));
            guards.Add($"{Runtime}.Ops.MappingRest({dict}, new {TypeName(mapping.Dict.Key)}[] {{ {keys} }}, out var @{_locals[rest]})");
        }
        return $"{{ }} {dict}";
    }

    /// <summary>An object with <c>__call__</c> as a function (SPEC §4.9): evaluated once, then called by a lambda.</summary>
    private string CallableObject(BoundCallableObject callable)
    {
        var function = TypeName(callable.Function);
        var target = $"@{_locals.Fresh("callable")}";
        var parameters = callable.Function.Parameters.Select(_ => $"@{_locals.Fresh("argument")}").ToList();
        var name = $"{((ClassType)Concrete(callable.Value.Type)).Symbol.Name}.__call__";
        return $"((global::System.Func<{TypeName(Concrete(callable.Value.Type))}, {function}>)({target} => new {function}(\"{name}\", ({string.Join(", ", parameters)}) => {target}.@__call__({string.Join(", ", parameters)}))))({Expr(callable.Value)})";
    }
}
