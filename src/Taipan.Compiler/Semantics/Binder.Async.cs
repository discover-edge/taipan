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

    /// <summary><c>await e</c>: in an async function's body or at the main file's top level, of an <c>Awaitable[T]</c>.</summary>
    private BoundExpr BindAwait(AwaitExpr awaitExpr)
    {
        var keyword = new Text.TextSpan(awaitExpr.Span.Start, "await".Length);
        var inAsync = _lambdaDepth == 0 && (_function is { IsAsync: true } || _function is null && _inTopLevel);
        if (_comprehensionDepth > 0)
            _diagnostics.ReportAwaitInComprehension(keyword);
        else if (!inAsync)
            _diagnostics.ReportAwaitOutsideAsync(keyword);
        else if (_function is null)
            _topLevelAwaits = true;
        var operand = BindValue(awaitExpr.Operand);
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
}
