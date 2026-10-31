namespace Taipan.Compiler.Semantics;

/// <summary>Walks the bound tree, for the emitter's passes that look for lambdas (SPEC §3.7).</summary>
public static class BoundTree
{
    /// <summary>The expressions a statement evaluates itself, not those of the statements inside it.</summary>
    public static IEnumerable<BoundExpr> Expressions(BoundStmt stmt) => stmt switch
    {
        BoundVariableDecl decl => [decl.Value],
        BoundAssign assign => [assign.Value],
        BoundFieldAssign assign => [assign.Target, assign.Value],
        BoundIndexAssign assign => [assign.Target, assign.Index, assign.Value],
        BoundPySetAttr assign => [assign.Target, assign.Value],
        BoundPySetItem assign => [assign.Target, assign.Index, assign.Value],
        BoundReturn { Value: { } value } => [value],
        BoundExprStmt exprStmt => [exprStmt.Expr],
        BoundIf ifStmt => ifStmt.Clauses.Select(c => c.Condition),
        BoundWhile whileStmt => [whileStmt.Condition],
        BoundForRange { Start: { } start } forStmt => [start, forStmt.Stop],
        BoundForRange forStmt => [forStmt.Stop],
        BoundForEach forEach => [forEach.Iterable],
        BoundMatch match => [match.Scrutinee],
        BoundUnpack unpack => [unpack.Value, .. unpack.Stores.SelectMany(Expressions)],
        _ => [],
    };

    /// <summary>An expression's direct sub-expressions, including a lambda's body.</summary>
    public static IEnumerable<BoundExpr> Children(BoundExpr expr) => expr switch
    {
        BoundUnary unary => [unary.Operand],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundCall call => call.Arguments,
        BoundListLiteral list => list.Elements,
        BoundDictLiteral dict => dict.Entries.SelectMany(e => (BoundExpr[])[e.Key, e.Value]),
        BoundConstruct construct => construct.Arguments.Select(a => a.Value),
        BoundConstructVariant construct => construct.Arguments.Select(a => a.Value),
        BoundFieldAccess access => [access.Target],
        BoundIndex index => [index.Target, index.Index],
        BoundMethodCall call => [call.Target, .. call.Arguments],
        BoundBuiltinMethodCall call => [call.Target, .. call.Arguments],
        BoundFString fstring => fstring.Parts,
        BoundPyGetAttr get => [get.Target],
        BoundPyGetItem get => [get.Target, get.Index],
        BoundPyCall call => [call.Callee, .. call.Arguments, .. call.Keywords.Select(k => k.Value)],
        BoundToPy convert => [convert.Value],
        BoundCast cast => [cast.Value],
        BoundIsSome isSome => [isSome.Operand],
        BoundIsNone isNone => [isNone.Operand],
        BoundLambda lambda => [lambda.Body],
        BoundInvoke invoke => [invoke.Callee, .. invoke.Arguments],
        BoundSort sort => [sort.List, .. sort.Keywords.Select(k => k.Value)],
        BoundTupleLiteral tuple => tuple.Elements,
        BoundTupleElement element => [element.Tuple],
        BoundSlice slice => ((BoundExpr?[])[slice.Target, slice.Start, slice.Stop, slice.Step]).OfType<BoundExpr>(),
        _ => [],
    };

    /// <summary>
    /// The lambdas in a statement's own expressions, each once, not counting lambdas inside them.
    /// (A compound assignment's value repeats its target, so the same lambda can be reached twice.)
    /// </summary>
    public static IEnumerable<BoundLambda> Lambdas(BoundStmt stmt)
    {
        var seen = new HashSet<BoundLambda>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<BoundExpr>(Expressions(stmt));
        while (pending.TryPop(out var expr))
        {
            if (expr is BoundLambda lambda)
            {
                if (seen.Add(lambda))
                    yield return lambda;
                continue;
            }
            foreach (var child in Children(expr))
                pending.Push(child);
        }
    }

    /// <summary>The statements directly inside a statement's blocks.</summary>
    public static IEnumerable<BoundStmt> Statements(BoundStmt stmt) => stmt switch
    {
        BoundBlock block => block.Statements,
        BoundIf ifStmt => [.. ifStmt.Clauses.SelectMany(c => c.Body.Statements), .. ifStmt.Else?.Statements ?? []],
        BoundWhile whileStmt => whileStmt.Body.Statements,
        BoundForRange forStmt => forStmt.Body.Statements,
        BoundForEach forEach => forEach.Body.Statements,
        BoundMatch match => match.Arms.SelectMany(a => a.Body.Statements),
        BoundLocalFunction function => function.Body.Statements,
        _ => [],
    };

    /// <summary>Whether a statement makes a closure anywhere inside it: a lambda or a nested <c>def</c>.</summary>
    public static bool MakesClosures(BoundStmt stmt) =>
        stmt is BoundLocalFunction || Lambdas(stmt).Any() || Statements(stmt).Any(MakesClosures);
}
