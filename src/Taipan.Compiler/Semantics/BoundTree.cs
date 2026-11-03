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
        BoundPropertySet assign => [assign.Target, assign.Value],
        BoundClassAttributeAssign assign => [assign.Value],
        BoundDotNetAssign { Target: { } target } assign => [target, assign.Value],
        BoundDotNetAssign assign => [assign.Value],
        BoundDotNetIndexAssign assign => [assign.Index.Target, .. assign.Index.Arguments.Select(a => a.Value), assign.Value],
        BoundIndexAssign assign => [assign.Target, assign.Index, assign.Value],
        BoundPySetAttr assign => [assign.Target, assign.Value],
        BoundPySetItem assign => [assign.Target, assign.Index, assign.Value],
        BoundSliceAssign assign => [assign.Slice, assign.Value],
        BoundDelete delete => delete.Targets,
        BoundReturn { Value: { } value } => [value],
        BoundRaise { Value: { } value } => [value],
        BoundYield yield => [yield.Value],
        BoundTry tryStmt => tryStmt.Handlers.SelectMany(h => h.PythonClasses),
        BoundExprStmt exprStmt => [exprStmt.Expr],
        BoundIf ifStmt => ifStmt.Clauses.Select(c => c.Condition),
        BoundWhile whileStmt => [whileStmt.Condition],
        BoundForRange { Start: { } start } forStmt => [start, forStmt.Stop],
        BoundForRange forStmt => [forStmt.Stop],
        BoundForEach forEach => [forEach.Iterable],
        BoundMatch match => [match.Scrutinee],
        BoundUnpack unpack => [unpack.Value, .. unpack.Stores.SelectMany(Expressions)],
        // A nested def's defaults are emitted at its calls, in the same method (SPEC §8).
        BoundLocalFunction function => function.Function.Defaults.OfType<BoundExpr>(),
        _ => [],
    };

    /// <summary>
    /// The block arms of the match expressions in an expression (SPEC §5.1.1, M29): nested blocks
    /// inside it, not counting those inside lambdas or inside the blocks themselves.
    /// </summary>
    public static IEnumerable<BoundBlock> ArmBlocks(BoundExpr expr) => expr switch
    {
        BoundLambda => [],
        BoundMatchExpr match => match.Arms.Select(a => a.Body).OfType<BoundBlock>().Concat(Children(match).SelectMany(ArmBlocks)),
        _ => Children(expr).SelectMany(ArmBlocks),
    };

    /// <summary>The block arms in a statement's own expressions.</summary>
    public static IEnumerable<BoundBlock> ArmBlocks(BoundStmt stmt) => Expressions(stmt).SelectMany(ArmBlocks);

    /// <summary>An expression's direct sub-expressions, including a lambda's body.</summary>
    public static IEnumerable<BoundExpr> Children(BoundExpr expr) => expr switch
    {
        BoundUnary unary => [unary.Operand],
        BoundAwait awaited => [awaited.Operand],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundCall call => call.Arguments,
        BoundListLiteral list => list.Elements,
        BoundDictLiteral dict => dict.Entries.SelectMany(e => (BoundExpr[])[e.Key, e.Value]),
        BoundConstruct construct => construct.Arguments.Select(a => a.Value),
        BoundConstructVariant construct => construct.Arguments.Select(a => a.Value),
        BoundFieldAccess access => [access.Target],
        BoundPropertyGet get => [get.Target],
        BoundClassAttribute { Instance: { } instance } => [instance],
        BoundClassMethodCall call => [.. ((BoundExpr?[])[call.Instance]).OfType<BoundExpr>(), .. call.Arguments],
        BoundClsNew created => Children(created.Construction),
        BoundIndex index => [index.Target, index.Index],
        BoundMethodCall call => [call.Target, .. call.Arguments],
        BoundBuiltinMethodCall call => [call.Target, .. call.Arguments],
        BoundFString fstring => fstring.Parts,
        BoundFormat format => [format.Value],
        BoundPyGetAttr get => [get.Target],
        BoundPyGetItem get => [get.Target, get.Index],
        BoundPyCall call => [call.Callee, .. call.Arguments, .. call.Keywords.Select(k => k.Value)],
        BoundToPy convert => [convert.Value],
        BoundCast cast => [cast.Value],
        BoundIsSome isSome => [isSome.Operand],
        BoundIsNone isNone => [isNone.Operand],
        BoundNew construct => construct.Arguments,
        BoundSuperCall call => call.Arguments,
        BoundIsInstance isInstance => [isInstance.Operand],
        BoundNarrowed narrowed => [narrowed.Value],
        BoundClassCast cast => [cast.Value],
        BoundConvertFunction convert => [convert.Value],
        BoundLambda lambda => [lambda.Body],
        BoundInvoke invoke => [invoke.Callee, .. invoke.Arguments],
        BoundSort sort => [sort.List, .. sort.Keywords.Select(k => k.Value)],
        BoundTupleLiteral tuple => tuple.Elements,
        BoundTupleElement element => [element.Tuple],
        BoundToIterable converted => [converted.Value],
        BoundToList converted => [converted.Values],
        BoundCollectedArguments collected => collected.Items.Select(i => i.Value),
        BoundCollectedKeywords collected => collected.Items.Select(i => i.Value),
        BoundListElement element => [element.List],
        BoundListRest rest => [rest.List],
        BoundTupleRest rest => [rest.Tuple],
        BoundSlice slice => ((BoundExpr?[])[slice.Target, slice.Start, slice.Stop, slice.Step]).OfType<BoundExpr>(),
        // A block arm's statements are a nested block, reached through ArmBlocks (M29).
        BoundMatchExpr match => [match.Scrutinee, .. match.Arms.Select(a => a.Value ?? a.Raise?.Value).OfType<BoundExpr>()],
        BoundDotNetCall call => [.. ((BoundExpr?[])[call.Target]).OfType<BoundExpr>(), .. call.Arguments.Select(a => a.Value)],
        BoundDotNetIndex index => [index.Target, .. index.Arguments.Select(a => a.Value)],
        BoundDotNetOperator op => [.. op.Arguments.Select(a => a.Value)],
        BoundDotNetNew created => created.Arguments.Select(a => a.Value),
        BoundDotNetMember { Target: { } target } => [target],
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
        BoundTry tryStmt =>
        [
            .. tryStmt.Body.Statements,
            .. tryStmt.Handlers.SelectMany(h => h.Body.Statements),
            .. tryStmt.Else?.Statements ?? [],
            .. tryStmt.Finally?.Statements ?? [],
        ],
        BoundLocalFunction function => function.Body.Statements,
        _ => [],
    };

    /// <summary>Whether a statement makes a closure anywhere inside it: a lambda or a nested <c>def</c>.</summary>
    public static bool MakesClosures(BoundStmt stmt) =>
        stmt is BoundLocalFunction || Lambdas(stmt).Any() || Statements(stmt).Any(MakesClosures);
}
