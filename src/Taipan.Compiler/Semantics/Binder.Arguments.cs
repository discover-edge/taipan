using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Matching a call's arguments to a function's parameters, as Python binds them (SPEC §4.2): by
// position and keyword, with defaults (M14), positional-only and keyword-only parameters, *args
// and **kwargs, and *e and **e at the call (M30).
public sealed partial class Binder
{
    /// <summary>What an argument does in a call.</summary>
    private enum ArgumentRole
    {
        /// <summary>Fills its parameter.</summary>
        Parameter,

        /// <summary>One element of <c>*args</c>.</summary>
        Extra,

        /// <summary><c>*xs</c> with a list: every element goes to <c>*args</c>.</summary>
        ExtraList,

        /// <summary>One entry of <c>**kwargs</c>, under <see cref="PlannedArgument.Key"/>.</summary>
        Keyword,

        /// <summary><c>**d</c>: every entry goes to <c>**kwargs</c>.</summary>
        KeywordDict,

        /// <summary>An argument that doesn't fit; it's bound only to report its own errors.</summary>
        Unmatched,
    }

    /// <summary>One argument, in source order. A tuple unpacked with <c>*</c> gives one per element.</summary>
    private sealed record PlannedArgument(Expr Value, ArgumentRole Role, int Parameter = -1, string? Key = null)
    {
        /// <summary>For an element of a tuple unpacked with <c>*</c>, which tuple.</summary>
        public int Tuple { get; init; } = -1;
    }

    /// <summary>
    /// Matches a call's arguments to <paramref name="function"/>'s parameters and binds them (SPEC
    /// §4.2): each against its parameter's type, through <paramref name="substitute"/>, inferring
    /// <paramref name="typeParameters"/> first. Arguments for <c>*args</c> and <c>**kwargs</c> are
    /// collected into one argument each. Errors are reported; the result says if there were any.
    /// </summary>
    private (ImmutableArray<BoundExpr> Arguments, ArgumentShape? Shape, Dictionary<TypeParameterSymbol, TaipanType> Map, bool Valid) BindCallArguments(
        CallExpr call, FunctionSymbol function, string name, TextSpan nameSpan, ImmutableArray<TypeParameterSymbol> typeParameters,
        Func<TaipanType, TaipanType> substitute, TaipanType resultType, TaipanType? expected)
    {
        var (entries, valid, tuples) = MatchArguments(call, function, name, nameSpan);
        TaipanType TypeOf(PlannedArgument entry)
        {
            var varArgs = function.IndexOf(ParamKind.VarArgs);
            var varKeywords = function.IndexOf(ParamKind.VarKeywords);
            return entry.Role switch
            {
                ArgumentRole.Parameter => substitute(function.Parameters[entry.Parameter].Type),
                ArgumentRole.Extra => ((ListOfType)substitute(function.Parameters[varArgs].Type)).Element,
                ArgumentRole.ExtraList => substitute(function.Parameters[varArgs].Type),
                ArgumentRole.Keyword => ((DictOfType)substitute(function.Parameters[varKeywords].Type)).Value,
                ArgumentRole.KeywordDict => substitute(function.Parameters[varKeywords].Type),
                _ => TaipanType.Error,
            };
        }
        if (!valid)
        {
            var orphans = entries.Select(e => e.Role == ArgumentRole.Unmatched ? BindValue(e.Value) : BindExpected(e.Value, TypeOf(e))).ToImmutableArray();
            return (orphans, null, [], false);
        }
        var (bound, map) = InferAndBind(name, call.Callee.Span, typeParameters, [.. entries.Select(TypeOf)], resultType, [.. entries.Select(e => e.Value)], expected);
        var (arguments, shape) = Collect(function, entries, bound, map, substitute, call, tuples);
        return (arguments, shape, map, true);
    }

    /// <summary>
    /// Each argument's role and parameter (SPEC §4.2): positional ones fill the positional
    /// parameters and then <c>*args</c>; keywords fill their parameters or <c>**kwargs</c>. A tuple
    /// unpacked with <c>*</c> is bound here, since its length decides the parameters it fills.
    /// </summary>
    private (List<PlannedArgument> Entries, bool Valid, List<BoundExpr> Tuples) MatchArguments(CallExpr call, FunctionSymbol function, string name, TextSpan nameSpan)
    {
        var parameters = function.Parameters;
        var kinds = function.Kinds;
        var positional = Enumerable.Range(0, parameters.Length).Where(i => kinds[i] is ParamKind.Normal or ParamKind.PositionalOnly).ToList();
        var (varArgs, varKeywords) = (function.IndexOf(ParamKind.VarArgs), function.IndexOf(ParamKind.VarKeywords));
        var entries = new List<PlannedArgument>();
        var tuples = new List<BoundExpr>();
        var filled = new HashSet<int>();
        var keys = new HashSet<string>();
        var (valid, unknown, keywordDict, listSpread, onlyPositional) = (true, false, (Argument?)null, false, true);
        var (next, given) = (0, 0);

        void Positional(Expr value, int tuple = -1)
        {
            given++;
            if (!listSpread && next < positional.Count)
            {
                filled.Add(positional[next]);
                entries.Add(new PlannedArgument(value, ArgumentRole.Parameter, positional[next++]) { Tuple = tuple });
            }
            else if (varArgs >= 0)
            {
                entries.Add(new PlannedArgument(value, ArgumentRole.Extra) { Tuple = tuple });
            }
            else
            {
                entries.Add(new PlannedArgument(value, ArgumentRole.Unmatched) { Tuple = tuple });
                valid = false;
            }
        }

        foreach (var argument in call.Arguments)
        {
            if (argument.Unpack == ArgumentUnpack.Positional)
            {
                onlyPositional = false;
                var unpacked = BindValue(argument.Value);
                if (unpacked.Type is TupleOfType tuple)
                {
                    tuples.Add(unpacked);
                    for (var k = 0; k < tuple.Elements.Length; k++)
                        Positional(new PreboundExpr(new BoundTupleElement(unpacked, k, tuple.Elements[k], argument.Span), argument.Span), tuples.Count - 1);
                    continue;
                }
                var entry = new PlannedArgument(new PreboundExpr(unpacked, argument.Value.Span), ArgumentRole.ExtraList);
                if (unpacked.Type is not ListOfType)
                {
                    if (!unpacked.Type.ContainsError)
                        _diagnostics.ReportCannotUnpackArguments(argument.Value.Span, unpacked.Type);
                    (valid, entry) = (false, entry with { Role = ArgumentRole.Unmatched });
                }
                else if (varArgs < 0)
                {
                    _diagnostics.ReportNoVarArgs(argument.Span, name, _source.ToString(argument.Value.Span), keywords: false);
                    (valid, unknown, entry) = (false, true, entry with { Role = ArgumentRole.Unmatched });
                }
                else if (!listSpread && next < positional.Count)
                {
                    _diagnostics.ReportListOnlyFillsArgs(argument.Span, _source.ToString(argument.Value.Span), parameters[varArgs].Name);
                    (valid, unknown, entry) = (false, true, entry with { Role = ArgumentRole.Unmatched });
                }
                listSpread = true;
                entries.Add(entry);
            }
            else if (argument.Unpack == ArgumentUnpack.Keyword)
            {
                onlyPositional = false;
                keywordDict = argument;
                var unpacked = BindValue(argument.Value);
                var entry = new PlannedArgument(new PreboundExpr(unpacked, argument.Value.Span), ArgumentRole.KeywordDict);
                if (unpacked.Type is not DictOfType { Key: PrimitiveType { Name: "str" } })
                {
                    if (!unpacked.Type.ContainsError)
                        _diagnostics.ReportCannotUnpackArguments(argument.Value.Span, unpacked.Type);
                    (valid, entry) = (false, entry with { Role = ArgumentRole.Unmatched });
                }
                else if (varKeywords < 0)
                {
                    _diagnostics.ReportNoVarArgs(argument.Span, name, _source.ToString(argument.Value.Span), keywords: true);
                    (valid, unknown, entry) = (false, true, entry with { Role = ArgumentRole.Unmatched });
                }
                entries.Add(entry);
            }
            else if (argument.Name is null)
            {
                Positional(argument.Value);
            }
            else
            {
                onlyPositional = false;
                var keyword = argument.Name;
                var index = Enumerable.Range(0, parameters.Length).FirstOrDefault(i => parameters[i].Name == keyword.Text && kinds[i] is not (ParamKind.VarArgs or ParamKind.VarKeywords), -1);
                if (index >= 0 && kinds[index] != ParamKind.PositionalOnly)
                {
                    // A keyword names its parameter (SPEC §16.6): for an inherited constructor, its base's.
                    var named = function;
                    while (named.DefaultsFrom is { Name: "__init__" } from && named.Name == "__init__")
                        named = from;
                    Index(keyword.Span, named.Parameters[index]);
                    if (!filled.Add(index))
                    {
                        _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                        valid = false;
                    }
                    entries.Add(new PlannedArgument(argument.Value, ArgumentRole.Parameter, index));
                }
                else if (varKeywords >= 0)
                {
                    // A positional-only parameter's name is free for **kwargs, as in Python.
                    if (!keys.Add(keyword.Text))
                    {
                        _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                        valid = false;
                    }
                    entries.Add(new PlannedArgument(argument.Value, ArgumentRole.Keyword, Key: keyword.Text));
                }
                else
                {
                    if (index >= 0)
                        _diagnostics.ReportPositionalOnlyByKeyword(keyword.Span, keyword.Text);
                    else
                        _diagnostics.ReportNoKeywordArgument(keyword.Span, name, keyword.Text);
                    // Either way, the parameter it meant isn't also reported missing.
                    (valid, unknown) = (false, true);
                    entries.Add(new PlannedArgument(argument.Value, ArgumentRole.Unmatched));
                }
            }
        }

        // Too many positional arguments, or too few in a call that has only those, is a count (M14).
        var required = positional.Count(i => function.DefaultFor(i) is null);
        if (given > positional.Count && varArgs < 0 || onlyPositional && given < required)
        {
            var noun = kinds.Contains(ParamKind.KeywordOnly) ? "positional argument" : "argument";
            var expectedCount = required == positional.Count
                ? $"{positional.Count} {noun}{(positional.Count == 1 ? "" : "s")}"
                : $"{required} to {positional.Count} {noun}s";
            _diagnostics.ReportWrongArgumentCount(nameSpan, name, expectedCount, given);
            return (entries, false, tuples);
        }
        // After an unknown keyword, a missing argument is probably the one it misspells; after an
        // argument that couldn't be unpacked, it's probably one that argument meant to fill.
        if (!unknown)
        {
            foreach (var missing in Enumerable.Range(0, parameters.Length).Where(i =>
                kinds[i] is not (ParamKind.VarArgs or ParamKind.VarKeywords) && function.DefaultFor(i) is null && !filled.Contains(i)))
            {
                if (keywordDict is not null && kinds[missing] != ParamKind.PositionalOnly)
                    _diagnostics.ReportKeywordsCantFill(keywordDict.Span, _source.ToString(keywordDict.Value.Span), parameters[missing].Name);
                else if (kinds[missing] == ParamKind.KeywordOnly)
                    _diagnostics.ReportMissingKeywordOnly(nameSpan, parameters[missing].Name);
                else
                    _diagnostics.ReportMissingArgument(nameSpan, parameters[missing].Name);
                valid = false;
            }
        }
        return (entries, valid, tuples);
    }

    /// <summary>
    /// The bound arguments as the call passes them: each that fills a parameter, then the
    /// arguments for <c>*args</c> and <c>**kwargs</c> collected into one each, where the first of
    /// them was, or after the others when there are none. When that changes the order arguments
    /// are evaluated in, or a tuple is unpacked, the call evaluates them into temporaries first.
    /// </summary>
    private (ImmutableArray<BoundExpr> Arguments, ArgumentShape? Shape) Collect(
        FunctionSymbol function, List<PlannedArgument> entries, ImmutableArray<BoundExpr> bound, Dictionary<TypeParameterSymbol, TaipanType> map,
        Func<TaipanType, TaipanType> substitute, CallExpr call, List<BoundExpr> tuples)
    {
        var (varArgs, varKeywords) = (function.IndexOf(ParamKind.VarArgs), function.IndexOf(ParamKind.VarKeywords));
        var arguments = new List<BoundExpr?>();
        var parameters = new List<int>();
        var extras = new List<(BoundExpr Value, bool Spread)>();
        var keywords = new List<(string? Key, BoundExpr Value)>();
        var (extraAt, keywordAt) = (-1, -1);
        var reordered = false;
        for (var i = 0; i < entries.Count; i++)
        {
            switch (entries[i].Role)
            {
                case ArgumentRole.Parameter:
                    // A keyword after **kwargs's first entry is evaluated after it, but passed before.
                    reordered |= keywordAt >= 0;
                    arguments.Add(bound[i]);
                    parameters.Add(entries[i].Parameter);
                    break;
                case ArgumentRole.Extra or ArgumentRole.ExtraList:
                    if (extraAt < 0)
                    {
                        extraAt = arguments.Count;
                        arguments.Add(null);
                        parameters.Add(varArgs);
                    }
                    extras.Add((bound[i], entries[i].Role == ArgumentRole.ExtraList));
                    break;
                default:
                    if (keywordAt < 0)
                    {
                        keywordAt = arguments.Count;
                        arguments.Add(null);
                        parameters.Add(varKeywords);
                    }
                    keywords.Add((entries[i].Key, bound[i]));
                    break;
            }
        }
        var span = call.Span;
        if (varArgs >= 0)
        {
            if (extraAt < 0)
            {
                extraAt = arguments.Count;
                arguments.Add(null);
                parameters.Add(varArgs);
            }
            arguments[extraAt] = new BoundCollectedArguments([.. extras], (ListOfType)TypeChecker.Substitute(substitute(function.Parameters[varArgs].Type), map), span);
        }
        if (varKeywords >= 0)
        {
            if (keywordAt < 0)
            {
                keywordAt = arguments.Count;
                arguments.Add(null);
                parameters.Add(varKeywords);
            }
            // A **d key naming a parameter is a run-time TypeError (SPEC §4.2).
            var reserved = Enumerable.Range(0, function.Parameters.Length)
                .Where(i => function.Kinds[i] is ParamKind.Normal or ParamKind.KeywordOnly).Select(i => function.Parameters[i].Name);
            arguments[keywordAt] = new BoundCollectedKeywords([.. keywords], [.. reserved], function.Name,
                (DictOfType)TypeChecker.Substitute(substitute(function.Parameters[varKeywords].Type), map), span);
        }
        var shape = new ArgumentShape([.. parameters], function.Parameters.Length)
        {
            // Unpacked tuples are evaluated once, with every argument, in source order.
            Sources = reordered || tuples.Count > 0 ? [.. SourceOrder(entries, bound, tuples)] : [],
        };
        return ([.. arguments.Select(a => a!)], Simplify(shape));
    }

    // Each argument as written, once: an unpacked tuple stands for all its elements.
    private static IEnumerable<BoundExpr> SourceOrder(List<PlannedArgument> entries, ImmutableArray<BoundExpr> bound, List<BoundExpr> tuples)
    {
        var seen = new HashSet<BoundExpr>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < entries.Count; i++)
        {
            var value = entries[i].Tuple >= 0 ? tuples[entries[i].Tuple] : bound[i];
            if (seen.Add(value))
                yield return value;
        }
    }
}
