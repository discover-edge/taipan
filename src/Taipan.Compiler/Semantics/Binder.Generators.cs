using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// Generators (SPEC §3.13, M31; §3.20, M42): functions whose bodies yield.
public sealed partial class Binder
{
    /// <summary>Whether a body yields, not counting nested functions, which a generator's isn't.</summary>
    private static bool Yields(Block body) => body.Statements.Any(Yields);

    private static bool YieldsIn(Block? body) => body is not null && Yields(body);

    private static bool Yields(Stmt stmt) => stmt switch
    {
        YieldStmt => true,
        LetStmt let => Yields(let.Value),
        VarStmt var => Yields(var.Value),
        UnpackStmt unpack => Yields(unpack.Value),
        TupleAssignStmt assign => Yields(assign.Value),
        AssignStmt assign => Yields(assign.Target) || Yields(assign.Value),
        ReturnStmt ret => Yields(ret.Value),
        RaiseStmt raise => Yields(raise.Value) || Yields(raise.Cause),
        ExprStmt exprStmt => Yields(exprStmt.Expr),
        IfStmt ifStmt => ifStmt.Clauses.Any(c => Yields(c.Condition) || Yields(c.Body)) || YieldsIn(ifStmt.Else),
        WhileStmt whileStmt => Yields(whileStmt.Condition) || Yields(whileStmt.Body),
        ForStmt forStmt => Yields(forStmt.Iterable) || Yields(forStmt.Body),
        MatchStmt match => Yields(match.Scrutinee) || match.Arms.Any(a => Yields(a.Guard) || Yields(a.Body)),
        WithStmt with => with.Items.Any(i => Yields(i.Manager)) || Yields(with.Body),
        TryStmt tryStmt => Yields(tryStmt.Body) || tryStmt.Handlers.Any(h => Yields(h.Body))
            || YieldsIn(tryStmt.Else) || YieldsIn(tryStmt.Finally),
        _ => false,
    };

    private static bool Yields(Condition condition) => condition switch
    {
        ExprCondition expr => Yields(expr.Expr),
        IsSomeCondition isSome => Yields(isSome.Expr),
        _ => false,
    };

    // A yield expression (M42) inside another; a lambda or comprehension is no part of the function.
    private static bool Yields(Expr? expr) => expr switch
    {
        null => false,
        YieldExpr => true,
        FStringExpr fstring => fstring.Segments.OfType<FStringHoleSegment>().Any(h => Yields(h.Expr)),
        ListExpr list => list.Elements.Any(Yields),
        SetExpr set => set.Elements.Any(Yields),
        TupleExpr tuple => tuple.Elements.Any(Yields),
        DictExpr dict => dict.Entries.Any(e => Yields(e.Key) || Yields(e.Value)),
        UnaryExpr unary => Yields(unary.Operand),
        AwaitExpr awaited => Yields(awaited.Operand),
        BinaryExpr binary => Yields(binary.Left) || Yields(binary.Right),
        CallExpr call => Yields(call.Callee) || call.Arguments.Any(a => Yields(a.Value)),
        MemberExpr member => Yields(member.Target),
        IndexExpr index => Yields(index.Target) || Yields(index.Index),
        SliceExpr slice => Yields(slice.Target) || Yields(slice.Start) || Yields(slice.Stop) || Yields(slice.Step),
        IsNoneExpr isNone => Yields(isNone.Operand),
        IsExpr identity => Yields(identity.Left) || Yields(identity.Right),
        CastExpr cast => Yields(cast.Value),
        StarredExpr starred => Yields(starred.Value),
        MatchExpr match => Yields(match.Scrutinee) || match.Arms.Any(a => Yields(a.Value) || a.Block is { } block && Yields(block)),
        _ => false,
    };

    /// <summary>
    /// A generator is declared to return <c>Iterator[T]</c>, <c>Iterable[T]</c> or <c>Generator[Y, S, R]</c>,
    /// and an async one <c>AsyncIterator[T]</c>, <c>AsyncIterable[T]</c> or <c>AsyncGenerator[Y, S]</c> (SPEC §3.13, §3.20).
    /// </summary>
    private void CheckGeneratorReturn(FunctionSymbol function, Identifier name)
    {
        if (!function.IsGenerator || function.ReturnType.ContainsError)
            return;
        if (function.IsAsync && function.ReturnType is not (AsyncIteratorOfType or AsyncIterableOfType or AsyncGeneratorOfType))
            _diagnostics.ReportAsyncGeneratorReturnType(name.Span, function.Name);
        else if (!function.IsAsync && function.ReturnType is not (IteratorOfType or IterableOfType or GeneratorOfType))
            _diagnostics.ReportGeneratorReturnType(name.Span, function.Name);
    }

    /// <summary>
    /// What a generator yields, receives and returns, from its declared type: an iterator receives
    /// and returns None (SPEC §3.20). <c>Returned</c> is null when it can't return a value.
    /// </summary>
    private static (TaipanType Yielded, TaipanType Sent, TaipanType? Returned) GeneratorShape(TaipanType declared) => declared switch
    {
        GeneratorOfType generator => (generator.Yielded, generator.Sent, generator.Returned),
        AsyncGeneratorOfType generator => (generator.Yielded, generator.Sent, null),
        _ when (TypeChecker.ElementOf(declared) ?? TypeChecker.AsyncElementOf(declared)) is { } element => (element, TaipanType.None, null),
        _ => (TaipanType.Error, TaipanType.Error, TaipanType.Error),
    };

    /// <summary>The declared type of the generator being bound, with its type arguments and Self.</summary>
    private TaipanType DeclaredGenerator()
    {
        var declared = TypeChecker.Substitute(_function!.ReturnType, _specialization);
        if (_function.Owner is { } owner)
            declared = TypeChecker.SubstituteSelf(declared, owner.Type);
        return declared;
    }

    private BoundStmt BindYield(YieldStmt yield) =>
        new BoundExprStmt(BindYieldExpr(yield.Value, yield.From, used: false, yield.Span), yield.Span);

    /// <summary>
    /// <c>yield e</c> gives a value of the generator's yield type and is a value of what it receives;
    /// <c>yield from e</c> gives each value of an iterable of it, and is a generator's return value
    /// (SPEC §3.13, §3.20).
    /// </summary>
    private BoundExpr BindYieldExpr(Expr? valueSyntax, bool from, bool used, Text.TextSpan span)
    {
        var keyword = new Text.TextSpan(span.Start, "yield".Length);
        if (_function is null || _lambdaDepth > 0 || _comprehensionDepth > 0)
        {
            _diagnostics.ReportYieldOutsideFunction(keyword);
            if (valueSyntax is not null)
                BindValue(valueSyntax);
            return new BoundErrorExpr(span);
        }
        if (_matchExpressionArm is var (function, _) && function == _function)
            _diagnostics.ReportLeavesMatchExpression(keyword, "yield");
        var declared = DeclaredGenerator();
        var (yielded, sent, _) = GeneratorShape(declared);
        if (valueSyntax is null)
        {
            if (!TypeChecker.IsAssignable(TaipanType.None, yielded))
                _diagnostics.ReportBareYieldValue(keyword, _function.Name, yielded);
            return new BoundYield(null, From: false, used, sent, span);
        }
        if (!from)
            return new BoundYield(BindExpected(valueSyntax, yielded), From: false, used, sent, span);
        if (_function.IsAsync)
        {
            _diagnostics.ReportYieldFromInAsync(keyword);
            BindValue(valueSyntax);
            return new BoundErrorExpr(span);
        }
        var value = BindValue(valueSyntax);
        // Delegating to a generator gives its return value (SPEC §3.20, PEP 380).
        if (value.Type is GeneratorOfType inner)
        {
            if (!(TypeChecker.IsAssignable(new IteratorOfType(inner.Yielded), new IteratorOfType(yielded)) && TypeChecker.IsAssignable(sent, inner.Sent)))
            {
                _diagnostics.ReportTypeMismatch(valueSyntax.Span, new GeneratorOfType(yielded, sent, inner.Returned), value.Type);
                return new BoundErrorExpr(span);
            }
            return new BoundYield(value, From: true, used, inner.Returned, span);
        }
        var iterable = new IterableOfType(yielded);
        CheckAssignable(value, iterable, valueSyntax.Span);
        if (used && !value.Type.ContainsError)
        {
            _diagnostics.ReportYieldFromNoValue(span, value.Type);
            return new BoundErrorExpr(span);
        }
        return new BoundYield(Convert(value, iterable), From: true, used, TaipanType.Nothing, span);
    }

    /// <summary>
    /// <c>return</c> in a generator (SPEC §3.20): a <c>Generator[Y, S, R]</c>'s returns an <c>R</c>;
    /// an iterator's or async generator's returns no value. Null if the function isn't a generator.
    /// </summary>
    private BoundReturn? BindGeneratorReturn(ReturnStmt ret)
    {
        if (_function is not { IsGenerator: true })
            return null;
        var declared = DeclaredGenerator();
        if (ret.Value is null)
        {
            if (GeneratorShape(declared).Returned is { } required && !TypeChecker.IsAssignable(TaipanType.None, required))
                _diagnostics.ReportMissingReturnValue(ret.Span, _function.Name, required);
            return new BoundReturn(null, ret.Span);
        }
        if (declared.ContainsError)
            return new BoundReturn(BindValue(ret.Value), ret.Span);
        if (_function.IsAsync)
        {
            _diagnostics.ReportAsyncGeneratorReturnValue(ret.Value.Span);
            return new BoundReturn(BindValue(ret.Value), ret.Span);
        }
        if (GeneratorShape(declared).Returned is not { } returned)
        {
            _diagnostics.ReportGeneratorReturnValue(ret.Value.Span, declared);
            return new BoundReturn(BindValue(ret.Value), ret.Span);
        }
        return new BoundReturn(BindExpected(ret.Value, returned), ret.Span);
    }

    /// <summary>A <c>Generator[Y, S, R]</c> whose body can end returns None, which <c>R</c> must accept (SPEC §3.20).</summary>
    private void CheckGeneratorEnd(FunctionSymbol function, Identifier name, BoundBlock body)
    {
        if (function.IsGenerator && !function.IsAsync && function.ReturnType is GeneratorOfType { Returned: var returned }
            && !TypeChecker.IsAssignable(TaipanType.None, returned) && !TypeChecker.Terminates(body))
            _diagnostics.ReportGeneratorCanEnd(name.Span, function.Name, returned);
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

    /// <summary>
    /// <c>list(xs)</c>, <c>iter(xs)</c> and <c>next(it)</c> or <c>next(it, default)</c> (SPEC §6, M31), and
    /// <c>anext(it)</c> or <c>anext(it, default)</c> (M42), which give an awaitable.
    /// </summary>
    private BoundExpr BindIteratorBuiltin(CallExpr call, FunctionSymbol function, TaipanType? expected)
    {
        var builtin = function.Builtin!.Value;
        var count = call.Arguments.Length;
        var takesDefault = builtin is BuiltinFunction.Next or BuiltinFunction.Anext;
        // list() alone needs an expected type, as [] does.
        if (builtin == BuiltinFunction.List && count == 0)
        {
            if (expected is ListOfType listType)
                return new BoundListLiteral([], listType, call.Span);
            _diagnostics.ReportCannotInferEmpty(call.Span, "list");
            return new BoundErrorExpr(call.Span);
        }
        if (count != 1 && !(takesDefault && count == 2))
        {
            BindArguments(call);
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, takesDefault ? "1 or 2 arguments" : "1 argument", count);
            return new BoundErrorExpr(call.Span);
        }
        // An expected Iterator[T], Iterable[T] or list[T] tells an empty list literal its type.
        var hint = !takesDefault && expected is not null && TypeChecker.ElementOf(TypeChecker.Unwrap(expected)) is { } wanted
            ? new ListOfType(wanted)
            : null;
        var value = BindValue(call.Arguments[0].Value, hint);
        var span = call.Arguments[0].Value.Span;
        if (takesDefault)
        {
            var isAsync = builtin == BuiltinFunction.Anext;
            if (value.Type.ContainsError || ReportIfOptional(value, span))
                return new BoundErrorExpr(call.Span);
            if ((isAsync ? TypeChecker.AsyncIteratorElementOf(value.Type) : TypeChecker.IteratorElementOf(value.Type)) is not { } produced)
            {
                _diagnostics.ReportTypeMismatch(span, isAsync ? "AsyncIterator" : "Iterator", value.Type);
                return new BoundErrorExpr(call.Span);
            }
            var iterator = Convert(value, isAsync ? new AsyncIteratorOfType(produced) : new IteratorOfType(produced));
            TaipanType Result(TaipanType element) => isAsync ? new AwaitableOfType(element) : element;
            if (count == 1)
                return new BoundCall(function, [iterator], Result(produced), call.Span);
            var fallback = BindValue(call.Arguments[1].Value, produced);
            if (fallback.Type is NoneType)
                return new BoundCall(function, [iterator, fallback], Result(produced is OptionalOf ? produced : new OptionalOf(produced)), call.Span);
            CheckAssignable(fallback, produced, call.Arguments[1].Value.Span);
            return new BoundCall(function, [iterator, Convert(fallback, produced)], Result(produced), call.Span);
        }
        var (values, element) = BindIterable(value, span);
        if (element.ContainsError)
            return new BoundErrorExpr(call.Span);
        return builtin == BuiltinFunction.List
            ? new BoundToList(values, new ListOfType(element), call.Span)
            : new BoundCall(function, [values], new IteratorOfType(element), call.Span);
    }
}
