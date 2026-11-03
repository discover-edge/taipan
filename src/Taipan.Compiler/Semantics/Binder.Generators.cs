using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// Generators (SPEC §3.13, M31): functions whose bodies yield.
public sealed partial class Binder
{
    // Inside a try with an except clause, or an except or finally block, where C# can't yield.
    private int _noYield;

    /// <summary>Whether a body yields, not counting nested functions, which a generator's isn't.</summary>
    private static bool Yields(Block body) => body.Statements.Any(Yields);

    private static bool Yields(Stmt stmt) => stmt switch
    {
        YieldStmt => true,
        IfStmt ifStmt => ifStmt.Clauses.Any(c => Yields(c.Body)) || ifStmt.Else is { } elseBlock && Yields(elseBlock),
        WhileStmt whileStmt => Yields(whileStmt.Body),
        ForStmt forStmt => Yields(forStmt.Body),
        MatchStmt match => match.Arms.Any(a => Yields(a.Body)),
        TryStmt tryStmt => Yields(tryStmt.Body) || tryStmt.Handlers.Any(h => Yields(h.Body))
            || tryStmt.Else is { } elseBlock && Yields(elseBlock) || tryStmt.Finally is { } finallyBlock && Yields(finallyBlock),
        _ => false,
    };

    /// <summary>A generator is declared to return <c>Iterator[T]</c> or <c>Iterable[T]</c> (SPEC §3.13).</summary>
    private void CheckGeneratorReturn(FunctionSymbol function, Identifier name)
    {
        if (function.IsGenerator && function.ReturnType is not (IteratorOfType or IterableOfType) && !function.ReturnType.ContainsError)
            _diagnostics.ReportGeneratorReturnType(name.Span, function.Name);
    }

    /// <summary>
    /// <c>yield e</c> gives a value of the generator's element type; <c>yield from e</c> each value
    /// of an iterable of it (SPEC §3.13).
    /// </summary>
    private BoundStmt BindYield(YieldStmt yield)
    {
        var keyword = new Text.TextSpan(yield.Span.Start, "yield".Length);
        if (_function is null)
        {
            _diagnostics.ReportYieldOutsideFunction(keyword);
            return new BoundExprStmt(BindValue(yield.Value), yield.Span);
        }
        if (_noYield > 0)
            _diagnostics.ReportYieldInTry(keyword);
        else if (_matchExpressionArm is var (function, _) && function == _function)
            _diagnostics.ReportLeavesMatchExpression(keyword, "yield");
        var element = TypeChecker.ElementOf(TypeChecker.Substitute(_function.ReturnType, _specialization)) ?? TaipanType.Error;
        var value = yield.From ? BindExpected(yield.Value, new IterableOfType(element)) : BindExpected(yield.Value, element);
        return new BoundYield(value, yield.From, yield.Span);
    }

    /// <summary>
    /// A value a built-in iterates (SPEC §3.13): as an <c>Iterable</c> of its element type, which
    /// wraps a list, dict, str or class. Anything else is reported.
    /// </summary>
    private (BoundExpr Value, TaipanType Element) BindIterable(BoundExpr value, Text.TextSpan span)
    {
        if (value.Type.ContainsError || ReportIfOptional(value, span))
            return (value, TaipanType.Error);
        if (TypeChecker.ElementOf(value.Type) is not { } element)
        {
            _diagnostics.ReportCannotIterate(span, value.Type);
            return (value, TaipanType.Error);
        }
        return (Convert(value, new IterableOfType(element)), element);
    }

    /// <summary><c>list(xs)</c>, <c>iter(xs)</c> and <c>next(it)</c> or <c>next(it, default)</c> (SPEC §6, M31).</summary>
    private BoundExpr BindIteratorBuiltin(CallExpr call, FunctionSymbol function, TaipanType? expected)
    {
        var builtin = function.Builtin!.Value;
        var count = call.Arguments.Length;
        // list() alone needs an expected type, as [] does.
        if (builtin == BuiltinFunction.List && count == 0)
        {
            if (expected is ListOfType listType)
                return new BoundListLiteral([], listType, call.Span);
            _diagnostics.ReportCannotInferEmpty(call.Span, "list");
            return new BoundErrorExpr(call.Span);
        }
        if (count != 1 && !(builtin == BuiltinFunction.Next && count == 2))
        {
            BindArguments(call);
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, builtin == BuiltinFunction.Next ? "1 or 2 arguments" : "1 argument", count);
            return new BoundErrorExpr(call.Span);
        }
        // An expected Iterator[T], Iterable[T] or list[T] tells an empty list literal its type.
        var hint = builtin != BuiltinFunction.Next && expected is not null && TypeChecker.ElementOf(TypeChecker.Unwrap(expected)) is { } wanted
            ? new ListOfType(wanted)
            : null;
        var value = BindValue(call.Arguments[0].Value, hint);
        var span = call.Arguments[0].Value.Span;
        if (builtin == BuiltinFunction.Next)
        {
            if (value.Type.ContainsError || ReportIfOptional(value, span))
                return new BoundErrorExpr(call.Span);
            if (TypeChecker.IteratorElementOf(value.Type) is not { } produced)
            {
                _diagnostics.ReportTypeMismatch(span, "Iterator", value.Type);
                return new BoundErrorExpr(call.Span);
            }
            var iterator = Convert(value, new IteratorOfType(produced));
            if (count == 1)
                return new BoundCall(function, [iterator], produced, call.Span);
            var fallback = BindValue(call.Arguments[1].Value, produced);
            if (fallback.Type is NoneType)
                return new BoundCall(function, [iterator, fallback], produced is OptionalOf ? produced : new OptionalOf(produced), call.Span);
            CheckAssignable(fallback, produced, call.Arguments[1].Value.Span);
            return new BoundCall(function, [iterator, Convert(fallback, produced)], produced, call.Span);
        }
        var (values, element) = BindIterable(value, span);
        if (element.ContainsError)
            return new BoundErrorExpr(call.Span);
        return builtin == BuiltinFunction.List
            ? new BoundToList(values, new ListOfType(element), call.Span)
            : new BoundCall(function, [values], new IteratorOfType(element), call.Span);
    }
}
