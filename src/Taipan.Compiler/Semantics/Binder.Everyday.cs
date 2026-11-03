using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// The everyday built-ins of M35 (SPEC §6): abs, min, max, sum, any, all, round, divmod and set.
public sealed partial class Binder
{
    private BoundExpr BindEverydayBuiltin(CallExpr call, FunctionSymbol function, BuiltinFunction builtin, TaipanType? expected)
    {
        var nameSpan = call.Callee.Span;
        var keywords = builtin is BuiltinFunction.Min or BuiltinFunction.Max ? ["key", "default"] : Array.Empty<string>();
        var named = new Dictionary<string, Argument>();
        foreach (var argument in call.Arguments.Where(a => a.Name is not null))
        {
            var keyword = argument.Name!;
            if (!keywords.Contains(keyword.Text))
            {
                _diagnostics.ReportNoKeywordArgument(keyword.Span, function.Name, keyword.Text);
                BindIgnored(argument.Value);
            }
            else if (!named.TryAdd(keyword.Text, argument))
            {
                _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                BindIgnored(argument.Value);
            }
        }
        var positional = call.Arguments.Where(a => a.Name is null).Select(a => a.Value).ToList();
        var (fewest, most) = builtin switch
        {
            BuiltinFunction.Abs or BuiltinFunction.Any or BuiltinFunction.All => (1, 1),
            BuiltinFunction.Sum or BuiltinFunction.Round => (1, 2),
            BuiltinFunction.DivMod => (2, 2),
            BuiltinFunction.Set => (0, 1),
            _ => (1, int.MaxValue),
        };
        if (positional.Count < fewest || positional.Count > most)
        {
            var count = fewest == most ? Arguments(fewest) : most == int.MaxValue ? $"{fewest} or more arguments" : $"{fewest} or {most} arguments";
            _diagnostics.ReportWrongArgumentCount(nameSpan, function.Name, count, positional.Count);
            foreach (var argument in positional)
                BindIgnored(argument);
            return new BoundErrorExpr(call.Span);
        }

        switch (builtin)
        {
            case BuiltinFunction.Abs:
            {
                var value = BindNumber(positional[0]);
                return new BoundCall(function, [value], value.Type, call.Span);
            }
            case BuiltinFunction.Min or BuiltinFunction.Max:
                return BindMinMax(call, function, positional, named, builtin == BuiltinFunction.Max);
            case BuiltinFunction.Sum:
            {
                var (values, element) = BindIterable(BindValue(positional[0]), positional[0].Span);
                if (!element.ContainsError && !element.IsNumeric)
                    _diagnostics.ReportSumOfNonNumbers(positional[0].Span, element);
                var start = positional.Count > 1 ? BindNumber(positional[1]) : new BoundLiteral(0L, TaipanType.Int, call.Span);
                var type = element.ContainsError || start.Type.ContainsError ? TaipanType.Error
                    : element == TaipanType.Int && start.Type == TaipanType.Int ? TaipanType.Int : TaipanType.Float;
                return new BoundCall(function, [values, start], type, call.Span);
            }
            case BuiltinFunction.Any or BuiltinFunction.All:
            {
                var (values, element) = BindIterable(BindValue(positional[0]), positional[0].Span);
                if (!element.ContainsError && element != TaipanType.Bool)
                    _diagnostics.ReportAnyAllNotBool(positional[0].Span, function.Name, element);
                return new BoundCall(function, [values], TaipanType.Bool, call.Span);
            }
            case BuiltinFunction.Round:
            {
                var value = BindNumber(positional[0]);
                var digits = positional.Count > 1 ? BindExpected(positional[1], TaipanType.Int) : null;
                var type = value.Type.ContainsError ? TaipanType.Error : digits is not null ? value.Type : TaipanType.Int;
                return new BoundCall(function, digits is null ? [value] : [value, digits], type, call.Span);
            }
            case BuiltinFunction.DivMod:
            {
                var (left, right) = (BindNumber(positional[0]), BindNumber(positional[1]));
                var number = left.Type.ContainsError || right.Type.ContainsError ? TaipanType.Error
                    : left.Type == TaipanType.Int && right.Type == TaipanType.Int ? TaipanType.Int : TaipanType.Float;
                return new BoundCall(function, [left, right], number.ContainsError ? number : new TupleOfType([number, number]), call.Span);
            }
            default:
            {
                // set() needs an expected type, as [] does; set(xs) takes an iterable's values (SPEC §3.15).
                if (positional.Count == 0)
                {
                    if (TypeChecker.Unwrap(expected ?? TaipanType.Error) is SetOfType expectedSet)
                        return new BoundCall(function, [], expectedSet, call.Span);
                    if (expected is not { IsError: true })
                        _diagnostics.ReportCannotInferEmpty(call.Span, "set");
                    return new BoundErrorExpr(call.Span);
                }
                var (values, element) = BindIterable(BindValue(positional[0]), positional[0].Span);
                if (element.ContainsError)
                    return new BoundErrorExpr(call.Span);
                CheckSetElement(positional[0].Span, element);
                return new BoundCall(function, [values], new SetOfType(element), call.Span);
            }
        }
    }

    // An int or float argument; anything else is TP0200.
    private BoundExpr BindNumber(Expr expr)
    {
        var value = BindValue(expr);
        if (!value.Type.ContainsError && !value.Type.IsNumeric && !ReportIfOptional(value, expr.Span))
            _diagnostics.ReportTypeMismatch(expr.Span, "int or float", value.Type);
        return value.Type.IsNumeric ? value : new BoundErrorExpr(expr.Span);
    }

    /// <summary>
    /// <c>min</c> and <c>max</c> (SPEC §6): two or more values, or one iterable, ordered as
    /// <c>sorted</c> orders them, by <c>key=</c> when it's given, with a <c>default=</c> for an empty iterable.
    /// </summary>
    private BoundExpr BindMinMax(CallExpr call, FunctionSymbol function, List<Expr> positional, Dictionary<string, Argument> named, bool max)
    {
        BoundExpr values;
        TaipanType element;
        if (positional.Count >= 2)
        {
            if (named.TryGetValue("default", out var misplaced))
                _diagnostics.ReportNoKeywordArgument(misplaced.Name!.Span, function.Name, "default");
            var items = positional.Select(p => BindValue(p)).ToImmutableArray();
            element = InferElementType(items);
            values = new BoundListLiteral(items, new ListOfType(element), call.Span);
        }
        else
        {
            (values, element) = BindIterable(BindValue(positional[0]), positional[0].Span);
        }
        if (element.ContainsError)
            return new BoundErrorExpr(call.Span);

        BoundExpr? key = null;
        if (named.TryGetValue("key", out var keyArgument))
        {
            var (bound, keyType) = BindCallback(keyArgument.Value, [element], required: null, hint: null, resultName: "K");
            if (!keyType.ContainsError && !IsSortable(keyType))
                _diagnostics.ReportCannotSortBy(keyArgument.Value.Span, keyType);
            key = bound;
        }
        else if (!IsSortable(element))
        {
            _diagnostics.ReportCannotOrder(positional[0].Span, function.Name, element);
        }

        var type = element;
        BoundExpr? fallback = null;
        if (positional.Count == 1 && named.TryGetValue("default", out var defaultArgument))
        {
            if (defaultArgument.Value is NoneLiteral)
            {
                fallback = new BoundNone(defaultArgument.Value.Span);
                type = element is OptionalOf ? element : new OptionalOf(element);
            }
            else
            {
                fallback = BindExpected(defaultArgument.Value, element);
            }
        }
        return new BoundMinMax(max, values, key, fallback, element, type, call.Span);
    }

    /// <summary>A method M35 adds to a list, dict, str or set (SPEC §6, §3.15): its runtime class, and its overloads by argument count.</summary>
    private sealed record RuntimeMethod(string Kind, ImmutableArray<(ImmutableArray<TaipanType> Parameters, TaipanType Result)> Overloads);

    private static RuntimeMethod? RuntimeMethodOf(TaipanType type, string name)
    {
        var (str, integer, boolean, nothing) = (TaipanType.Str, TaipanType.Int, TaipanType.Bool, TaipanType.Nothing);
        (ImmutableArray<TaipanType>, TaipanType) Of(TaipanType result, params TaipanType[] parameters) => ([.. parameters], result);
        RuntimeMethod Method(string kind, params (ImmutableArray<TaipanType>, TaipanType)[] overloads) => new(kind, [.. overloads]);
        switch (type)
        {
            case ListOfType list:
            {
                var t = list.Element;
                return name switch
                {
                    "extend" => Method("List", Of(nothing, new IterableOfType(t))),
                    "remove" => Method("List", Of(nothing, t)),
                    "index" or "count" => Method("List", Of(integer, t)),
                    "reverse" or "clear" => Method("List", Of(nothing)),
                    "copy" => Method("List", Of(list)),
                    "pop" => Method("List", Of(t, integer)),
                    _ => null,
                };
            }
            case DictOfType dict:
                return name switch
                {
                    "pop" => Method("Dict", Of(dict.Value, dict.Key), Of(dict.Value, dict.Key, dict.Value)),
                    "get" or "setdefault" => Method("Dict", Of(dict.Value, dict.Key, dict.Value)),
                    "update" => Method("Dict", Of(nothing, dict)),
                    "clear" => Method("Dict", Of(nothing)),
                    "copy" => Method("Dict", Of(dict)),
                    _ => null,
                };
            case SetOfType set:
            {
                var t = set.Element;
                var values = new IterableOfType(t);
                return name switch
                {
                    "add" or "remove" or "discard" => Method("Set", Of(nothing, t)),
                    "pop" => Method("Set", Of(t)),
                    "clear" => Method("Set", Of(nothing)),
                    "copy" => Method("Set", Of(set)),
                    "update" => Method("Set", Of(nothing, values)),
                    "union" or "intersection" or "difference" or "symmetric_difference" => Method("Set", Of(set, values)),
                    "issubset" or "issuperset" or "isdisjoint" => Method("Set", Of(boolean, values)),
                    _ => null,
                };
            }
            case PrimitiveType { Name: "str" }:
            {
                var separator = new OptionalOf(str);
                var parts = new ListOfType(str);
                var three = new TupleOfType([str, str, str]);
                return name switch
                {
                    "join" => Method("Str", Of(str, new IterableOfType(str))),
                    "find" or "rfind" or "index" or "count" => Method("Str", Of(integer, str)),
                    "split" or "rsplit" => Method("Str", Of(parts), Of(parts, separator), Of(parts, separator, integer)),
                    "splitlines" => Method("Str", Of(parts)),
                    "strip" => Method("Str", Of(str, str)),
                    "lstrip" or "rstrip" => Method("Str", Of(str), Of(str, str)),
                    "partition" or "rpartition" => Method("Str", Of(three, str)),
                    "removeprefix" or "removesuffix" => Method("Str", Of(str, str)),
                    "title" or "capitalize" or "swapcase" => Method("Str", Of(str)),
                    "zfill" => Method("Str", Of(str, integer)),
                    "ljust" or "rjust" or "center" => Method("Str", Of(str, integer), Of(str, integer, str)),
                    "isdigit" or "isalpha" or "isalnum" or "isspace" or "isupper" or "islower" => Method("Str", Of(boolean)),
                    _ => null,
                };
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// A call of a method M35 adds (SPEC §6, §3.15), or null to bind it as an older one: an overload
    /// with that many arguments, positionally, but for <c>split</c>'s and <c>rsplit</c>'s <c>sep</c> and <c>maxsplit</c>.
    /// </summary>
    private BoundExpr? BindRuntimeMethod(CallExpr call, BoundExpr target, Identifier name, bool hasOlder)
    {
        if (RuntimeMethodOf(target.Type, name.Text) is not { } method)
            return null;
        var keywordNames = method.Kind == "Str" && name.Text is "split" or "rsplit" ? ["sep", "maxsplit"] : Array.Empty<string>();
        var slots = new List<Expr?>(call.Arguments.Where(a => a.Name is null).Select(a => (Expr?)a.Value));
        foreach (var argument in call.Arguments.Where(a => a.Name is not null))
        {
            var at = Array.IndexOf(keywordNames, argument.Name!.Text);
            if (at < 0)
            {
                _diagnostics.ReportKeywordArguments(argument.Span, name.Text);
                BindArguments(call);
                return new BoundErrorExpr(call.Span);
            }
            while (slots.Count <= at)
                slots.Add(null);
            if (slots[at] is not null)
            {
                _diagnostics.ReportRepeatedArgument(argument.Name.Span, argument.Name.Text);
                BindArguments(call);
                return new BoundErrorExpr(call.Span);
            }
            slots[at] = argument.Value;
        }
        if (method.Overloads.FirstOrDefault(o => o.Parameters.Length == slots.Count) is not { Parameters.IsDefault: false } overload)
        {
            if (hasOlder)
                return null;
            var counts = method.Overloads.Select(o => o.Parameters.Length).Order().ToList();
            var expected = counts.Count == 1 ? Arguments(counts[0]) : $"{string.Join(", ", counts[..^1])} or {Arguments(counts[^1])}";
            _diagnostics.ReportWrongArgumentCount(name.Span, name.Text, expected, slots.Count);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        // A keyword after a missing `sep` leaves it None, as in Python.
        var arguments = slots.Select((slot, i) => slot is null ? new BoundNone(name.Span) : BindExpected(slot, overload.Parameters[i])).ToImmutableArray();
        return new BoundRuntimeMethodCall(method.Kind, name.Text, target, arguments, overload.Result, call.Span);
    }
}
