using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// Async functions and await (SPEC §3.14, M33).
public sealed partial class Binder
{
    // While the main file's top-level statements are bound, where `await` may appear.
    private bool _inTopLevel;

    // Lambdas being bound, which can't await.
    private int _lambdaDepth;

    // Whether a top-level statement awaits, so the program's entry point is async.
    private bool _topLevelAwaits;

    /// <summary>An async function's call gives an <c>Awaitable</c> of its declared result.</summary>
    private static TaipanType AsyncResult(bool isAsync, TaipanType declared) =>
        isAsync && !declared.ContainsError ? new AwaitableOfType(declared) : declared;

    /// <summary>
    /// Whether code may await here (SPEC §3.14): in an async function's body, or at the main file's
    /// top level, which then runs as an async entry point. <c>await</c>, and since M42 <c>async for</c>
    /// and <c>async with</c> (SPEC §3.20), are TP0215 elsewhere.
    /// </summary>
    private bool CheckAsyncContext(Text.TextSpan keyword, string what)
    {
        var inAsync = _lambdaDepth == 0 && (_function is { IsAsync: true } || _function is null && _inTopLevel);
        if (_comprehensionDepth > 0 && what == "await")
            _diagnostics.ReportAwaitInComprehension(keyword);
        else if (!inAsync && what == "await")
            _diagnostics.ReportAwaitOutsideAsync(keyword);
        else if (!inAsync)
            _diagnostics.ReportAsyncOutsideAsync(keyword, what);
        else if (_function is null)
            _topLevelAwaits = true;
        return inAsync && _comprehensionDepth == 0;
    }

    /// <summary><c>await e</c>: in an async function's body or at the main file's top level, of an <c>Awaitable[T]</c>.</summary>
    private BoundExpr BindAwait(AwaitExpr awaitExpr, TaipanType? expected)
    {
        CheckAsyncContext(new Text.TextSpan(awaitExpr.Span.Start, "await".Length), "await");
        // An expected result tells the awaitable its type, as for an empty gather (SPEC §3.20).
        var operand = BindValue(awaitExpr.Operand, expected is null or NothingType ? null : new AwaitableOfType(expected));
        if (operand.Type.ContainsError)
            return new BoundErrorExpr(awaitExpr.Span);
        if (TypeChecker.Unwrap(operand.Type) is not AwaitableOfType awaitable || operand.Type is OptionalOf)
        {
            if (!ReportIfOptional(operand, awaitExpr.Operand.Span))
                _diagnostics.ReportCannotAwait(awaitExpr.Operand.Span, operand.Type);
            return new BoundErrorExpr(awaitExpr.Span);
        }
        return new BoundAwait(operand, awaitable.Result, awaitExpr.Span);
    }

    /// <summary>An expression statement whose awaitable is dropped is TP0235: its exceptions would be lost (SPEC §3.14).</summary>
    private BoundStmt BindExprStmt(ExprStmt exprStmt)
    {
        var value = BindExpr(exprStmt.Expr);
        if (value.Type is AwaitableOfType)
            _diagnostics.ReportAwaitableDiscarded(exprStmt.Expr.Span);
        return new BoundExprStmt(value, exprStmt.Span);
    }

    /// <summary>
    /// <c>asyncio</c>'s functions (SPEC §3.20, M42): <c>gather</c>, <c>sleep</c>, <c>wait_for</c>,
    /// <c>timeout</c> and <c>run</c>, each taking positional arguments.
    /// </summary>
    private BoundExpr BindAsyncioCall(CallExpr call, FunctionSymbol function, TaipanType? expected)
    {
        var builtin = function.Builtin!.Value;
        if (builtin == BuiltinFunction.Gather)
            return BindGather(call, expected);
        var (min, max) = builtin switch
        {
            BuiltinFunction.WaitFor => (2, 2),
            _ => (1, 1),
        };
        if (call.Arguments.Length < min || call.Arguments.Length > max)
        {
            BindArguments(call);
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, Arguments(min), call.Arguments.Length);
            return new BoundErrorExpr(call.Span);
        }
        var first = call.Arguments[0].Value;
        switch (builtin)
        {
            case BuiltinFunction.Sleep:
                return new BoundCall(function, [BindExpected(first, TaipanType.Float)], new AwaitableOfType(TaipanType.Nothing), call.Span);
            case BuiltinFunction.Timeout:
                return new BoundCall(function, [BindExpected(first, new OptionalOf(TaipanType.Float))], TaipanType.Timeout, call.Span);
            case BuiltinFunction.WaitFor:
            {
                var awaited = BindAwaitable(first, expected is AwaitableOfType hint ? hint : null);
                var timeout = BindExpected(call.Arguments[1].Value, new OptionalOf(TaipanType.Float));
                return awaited is null ? new BoundErrorExpr(call.Span) : new BoundCall(function, [awaited, timeout], awaited.Type, call.Span);
            }
            default:
            {
                var awaited = BindAwaitable(first, expected is null or NothingType ? null : new AwaitableOfType(expected));
                return awaited is null ? new BoundErrorExpr(call.Span) : new BoundCall(function, [awaited], ((AwaitableOfType)awaited.Type).Result, call.Span);
            }
        }
    }

    /// <summary>An argument that must be an <c>Awaitable[T]</c>, or null once reported.</summary>
    private BoundExpr? BindAwaitable(Expr syntax, TaipanType? expected)
    {
        var value = BindValue(syntax, expected);
        if (value.Type.ContainsError)
            return null;
        if (value.Type is AwaitableOfType)
            return value;
        if (!ReportIfOptional(value, syntax.Span))
            _diagnostics.ReportCannotAwait(syntax.Span, value.Type);
        return null;
    }

    /// <summary>
    /// <c>asyncio.gather(*aws)</c> (SPEC §3.20): its results' types join as list literal elements do,
    /// and with no arguments it needs an expected type, as <c>[]</c> does. <c>*xs</c> spreads a list of awaitables.
    /// </summary>
    private BoundExpr BindGather(CallExpr call, TaipanType? expected)
    {
        var expectedElement = expected is AwaitableOfType { Result: ListOfType list } ? list.Element : null;
        var hint = expectedElement is null ? null : new AwaitableOfType(expectedElement is NoneType ? TaipanType.Nothing : expectedElement);
        var items = ImmutableArray.CreateBuilder<(BoundExpr Value, bool Spread)>();
        var results = ImmutableArray.CreateBuilder<BoundExpr>();
        var failed = false;
        foreach (var argument in call.Arguments)
        {
            var spread = argument.Unpack == ArgumentUnpack.Positional;
            var value = BindValue(argument.Value, spread && hint is not null ? new ListOfType(hint) : hint);
            var awaitable = spread ? (value.Type as ListOfType)?.Element : value.Type;
            if (value.Type.ContainsError || awaitable is { ContainsError: true })
            {
                failed = true;
                continue;
            }
            if (awaitable is not AwaitableOfType { Result: var result })
            {
                if (!ReportIfOptional(value, argument.Value.Span))
                    _diagnostics.ReportCannotAwait(argument.Value.Span, value.Type);
                failed = true;
                continue;
            }
            items.Add((value, spread));
            results.Add(new BoundAwait(value, result is NothingType ? TaipanType.None : result, argument.Value.Span));
        }
        if (failed)
            return new BoundErrorExpr(call.Span);
        TaipanType element;
        if (results.Count == 0)
        {
            if (expectedElement is null)
            {
                _diagnostics.ReportCannotInferEmpty(call.Span, "list");
                return new BoundErrorExpr(call.Span);
            }
            element = expectedElement;
        }
        // Awaitables of None give a list of None, as Python's do.
        else if (results.All(r => r.Type is NoneType))
        {
            element = TaipanType.None;
        }
        else
        {
            element = InferElementType(results.ToImmutable());
            if (element.ContainsError)
                return new BoundErrorExpr(call.Span);
        }
        return new BoundGather(items.ToImmutable(), element, call.Span);
    }
}
