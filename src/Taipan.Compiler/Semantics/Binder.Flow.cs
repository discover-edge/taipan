using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Narrowing that follows the program's flow (SPEC §3.9, M27): facts about names and field paths,
// carried from statement to statement, joined where branches meet, and ended by what could change them.
public sealed partial class Binder
{
    /// <summary>
    /// A field path, <c>p.x</c> or <c>self.config.port</c> (SPEC §3.9), or one rooted at a class
    /// attribute, <c>C.cache</c> with no fields (§4.8, M28). Since M40 its steps may be elements,
    /// <c>xs[0]</c>, and its root a var (§3.18). It can be narrowed when its root is a name and
    /// each element's key is fixed.
    /// </summary>
    private sealed record FieldPath(Symbol Root, ImmutableArray<object> Steps)
    {
        public bool Equals(FieldPath? other) => other is not null && other.Root == Root && other.Steps.SequenceEqual(Steps);

        public override int GetHashCode() => Steps.Aggregate(Root.GetHashCode(), (hash, step) => HashCode.Combine(hash, step));

        public IEnumerable<FieldSymbol> Fields => Steps.OfType<FieldSymbol>();

        public bool CanNarrow =>
            Root is VariableSymbol { Kind: VariableKind.Let or VariableKind.Parameter or VariableKind.LoopVariable or VariableKind.Var } or ClassAttributeSymbol
            && Steps.All(s => s is not ElementStep { Key: null });
    }

    /// <summary>
    /// An element of a list, dict or tuple of type <see cref="Container"/> in a path (SPEC §3.18):
    /// its key, a literal or a name that can't change, or null for any other key.
    /// </summary>
    private sealed record ElementStep(object? Key, TaipanType Container);

    // Each ending since the binder started, in order, so a nested region's endings can be applied
    // to the state it returns to (SPEC §3.9: ending is ordered as the code runs).
    private readonly List<(object Key, string Why)> _endings = [];

    // Vars that a nested function assigns, which are never narrowed, and the function each local was declared in.
    private readonly HashSet<VariableSymbol> _nestedAssigned = [];
    private readonly Dictionary<VariableSymbol, FunctionSymbol?> _declaredIn = [];

    private static bool CanEnd(object key) => key is VariableSymbol { Kind: VariableKind.Var } or FieldPath;

    /// <summary>Ends what's known about <paramref name="key"/>, saying why for later errors.</summary>
    private void End(object key, string why)
    {
        _narrowing = _narrowing with { Types = _narrowing.Types.Remove(key), Why = _narrowing.Why.SetItem(key, why) };
        _endings.Add((key, why));
    }

    private void EndPaths(Func<FieldPath, bool> which, string why)
    {
        foreach (var path in _narrowing.Types.Keys.OfType<FieldPath>().Where(which).ToList())
            End(path, why);
    }

    // The state a region returns to: what held before it, less what the region ended.
    private Narrowing Replay(Narrowing before, int mark) =>
        _endings.Skip(mark).Aggregate(before, (state, ending) =>
            state with { Types = state.Types.Remove(ending.Key), Why = state.Why.SetItem(ending.Key, ending.Why) });

    /// <summary>What holds where several branches meet: the facts they all have, with the same type.</summary>
    private static Narrowing Join(IReadOnlyList<Narrowing> states)
    {
        var first = states[0];
        var types = first.Types.Where(pair => states.All(s => s.Types.TryGetValue(pair.Key, out var type) && type == pair.Value));
        var why = states.Aggregate(ImmutableDictionary<object, string>.Empty, (all, s) => all.SetItems(s.Why));
        return new Narrowing(types.ToImmutableDictionary(), why.RemoveRange(types.Select(pair => pair.Key)));
    }

    /// <summary>
    /// Whether a statement can't finish normally, so the code after it doesn't follow from its end:
    /// it returns, raises, breaks or continues on every path (SPEC §4.5).
    /// </summary>
    private static bool Exits(BoundStmt stmt) => stmt switch
    {
        BoundBreak or BoundContinue => true,
        BoundBlock block => block.Statements.Any(Exits),
        BoundWith with => !with.Suppresses && Exits(with.Body),
        BoundIf { Else: { } elseBlock } ifStmt => ifStmt.Clauses.All(c => Exits(c.Body)) && Exits(elseBlock),
        BoundMatch match => match.Arms.Where(a => a.IsReachable).All(a => Exits(a.Body)),
        BoundTry tryStmt => (tryStmt.Finally is { } finallyBlock && Exits(finallyBlock))
            || (tryStmt.Handlers.All(h => Exits(h.Body)) && (Exits(tryStmt.Body) || (tryStmt.Else is { } elseBlock && Exits(elseBlock)))),
        _ => TypeChecker.Terminates(stmt),
    };

    /// <summary>Whether a loop body has a <c>break</c> for that loop: not inside a nested loop or function.</summary>
    private static bool Breaks(BoundStmt stmt) => stmt switch
    {
        BoundBreak => true,
        BoundWhile or BoundForEach or BoundForRange or BoundLocalFunction => false,
        _ => BoundTree.Statements(stmt).Any(Breaks),
    };

    // ---- Calls and field assignments end paths ----

    /// <summary>
    /// Whether evaluating this node itself could run Taipan or foreign code, and so change a field
    /// (SPEC §3.9): calls, constructions, and operations on py and .NET values. Built-ins don't,
    /// unless given a function.
    /// </summary>
    private static bool RunsCode(BoundExpr expr) => expr switch
    {
        BoundCall { Function.Builtin: null } => true,
        BoundCall { Function.Builtin: BuiltinFunction.Map or BuiltinFunction.Filter or BuiltinFunction.Sorted } call
            => call.Arguments.Any(a => a.Type is FunctionType or OptionalOf { Inner: FunctionType }),
        BoundSort sort => sort.Keywords.Any(k => k.Name == "key"),
        BoundMethodCall or BoundNew or BoundConstruct or BoundInvoke or BoundSuperCall => true,
        BoundPropertyGet or BoundClassMethodCall or BoundClsNew => true,
        _ when RunsSpecialMethod(expr) => true,
        BoundPyCall or BoundPyGetAttr or BoundPyGetItem or BoundDotNetCall or BoundDotNetNew or BoundDotNetMember => true,
        _ => false,
    };

    /// <summary>Whether an expression runs code anywhere in it, outside lambdas, which don't run where they're written.</summary>
    private static bool ContainsCode(BoundExpr expr) =>
        RunsCode(expr) || (expr is not BoundLambda && BoundTree.Children(expr).Any(ContainsCode));

    private string Line(TextSpan span) => _source.GetLinePosition(span.Start).Line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>After an expression that runs code is bound, no field path stays narrowed.</summary>
    private BoundExpr EndPathsAfter(BoundExpr bound)
    {
        if (RunsCode(bound) && _narrowing.Types.Keys.Any(k => k is FieldPath))
            EndPaths(_ => true, CallWhy(bound.Span));
        EndElementsAfter(bound, bound.Span);
        return bound;
    }

    private string CallWhy(TextSpan span) => $"the call on line {Line(span)} could have changed it; copy it to a 'let' first";

    /// <summary>An assignment to a class attribute ends the paths rooted at it (SPEC §4.8).</summary>
    private void EndPathsThrough(ClassAttributeSymbol attribute, TextSpan span) =>
        EndPaths(path => path.Root == attribute,
            $"the assignment to '{attribute.Owner.Name}.{attribute.Name}' on line {Line(span)} could have changed it; copy it to a 'let' first");

    /// <summary>A class attribute's read, narrowed if it is (SPEC §4.8).</summary>
    private BoundExpr NarrowedAttribute(BoundClassAttribute attribute) =>
        KeyOf(attribute) is FieldPath path && _narrowing.Types.TryGetValue(path, out var type)
            ? new BoundNarrowed(attribute, type, attribute.Span)
            : attribute;

    /// <summary>An assignment to a field ends every path through a field of that name, on any object.</summary>
    private void EndPathsThrough(FieldSymbol field, TextSpan span) =>
        EndPaths(path => path.Fields.Any(f => f.Name == field.Name),
            $"the assignment to '.{field.Name}' on line {Line(span)} could have changed it; copy it to a 'let' first");

    // ---- Vars ----

    /// <summary>
    /// After an assignment to a var, what's known about it ends; a value of a narrower type narrows
    /// it to that type (SPEC §3.9). A nested function's assignment marks the var as never narrowed.
    /// </summary>
    private void AfterAssigning(VariableSymbol variable, BoundExpr value, TextSpan span)
    {
        if (variable.Kind != VariableKind.Var)
            return;
        if (_declaredIn.TryGetValue(variable, out var owner) && owner != _function)
            _nestedAssigned.Add(variable);
        // Saying why only matters when the assignment ends a narrowing.
        EndPaths(path => path.Root == variable, RootWhy(variable, span));
        if (_narrowing.Types.ContainsKey(variable))
        {
            End(variable, variable.Type is OptionalOf
                ? $"it can be None again after the assignment on line {Line(span)}"
                : $"it was assigned again on line {Line(span)}");
        }
        if (!_nestedAssigned.Contains(variable) && Narrower(variable.Type, value.Type) is { } narrower)
            _narrowing = _narrowing with { Types = _narrowing.Types.SetItem(variable, narrower), Why = _narrowing.Why.Remove(variable) };
    }

    private string RootWhy(VariableSymbol variable, TextSpan span) =>
        $"the assignment to '{variable.Name}' on line {Line(span)} could have changed it";

    // ---- Elements (SPEC §3.18) ----

    /// <summary>The list or dict type a statement or expression itself changes, if it changes one.</summary>
    private static TaipanType? Changes(object node) => node switch
    {
        BoundIndexAssign assign when assign.Target.Type is ListOfType or DictOfType => assign.Target.Type,
        BoundSliceAssign slice => slice.Slice.Target.Type,
        BoundDelete delete => delete.Targets.OfType<BoundIndex>().Select(t => t.Target.Type).FirstOrDefault(),
        BoundBuiltinMethodCall { Method: BuiltinMethod.ListAppend or BuiltinMethod.ListPop or BuiltinMethod.ListInsert or BuiltinMethod.ListSort or BuiltinMethod.DictRemove } call => call.Target.Type,
        BoundRuntimeMethodCall { Kind: "List", Name: "extend" or "remove" or "reverse" or "clear" or "pop" } call => call.Target.Type,
        BoundRuntimeMethodCall { Kind: "Dict", Name: "pop" or "setdefault" or "update" or "clear" } call => call.Target.Type,
        BoundSort { InPlace: true } sort => sort.List.Type,
        _ => null,
    };

    private string ChangeWhy(TaipanType container, TextSpan span) =>
        $"the change to a {container} on line {Line(span)} could have changed it; copy it to a 'let' first";

    /// <summary>After a change to a list or dict, no element of one of its type stays narrowed, through any name.</summary>
    private void EndElementsAfter(object node, TextSpan span)
    {
        if (Changes(node) is { } changed)
        {
            var container = TypeChecker.Unwrap(changed);
            EndPaths(path => path.Steps.Any(s => s is ElementStep e && e.Container == container), ChangeWhy(container, span));
        }
    }

    // The key of an element that can be narrowed: an int or str literal, or a name that can't change.
    private static object? ElementKey(BoundExpr index) => index switch
    {
        BoundLiteral { Value: long or string } literal => literal.Value,
        BoundUnary { Op: UnaryOp.Negate, Operand: BoundLiteral { Value: long value } } => -value,
        BoundVariable { Symbol: { Kind: VariableKind.Let or VariableKind.Parameter or VariableKind.LoopVariable } symbol } => symbol,
        _ => null,
    };

    private static FieldPath? ElementPath(BoundExpr target, object? key) =>
        (KeyOf(target), new ElementStep(key, TypeChecker.Unwrap(target.Type))) switch
        {
            (VariableSymbol root, var step) => new FieldPath(root, [step]),
            (FieldPath path, var step) => path with { Steps = path.Steps.Add(step) },
            _ => null,
        };

    /// <summary>An element read, narrowed if its path is (SPEC §3.18).</summary>
    private BoundExpr NarrowedElement(BoundExpr element) =>
        KeyOf(element) is FieldPath path && _narrowing.Types.TryGetValue(path, out var type)
            ? new BoundNarrowed(element, type, element.Span)
            : element;

    /// <summary>A type narrower than <paramref name="declared"/> that a value of <paramref name="value"/>'s type has: no longer optional, or a subclass.</summary>
    private static TaipanType? Narrower(TaipanType declared, TaipanType value)
    {
        if (value.ContainsError || value is NoneType or OptionalOf)
            return null;
        if (declared is OptionalOf optional)
            return Narrower(optional.Inner, value) ?? optional.Inner;
        return declared is ClassType declaredClass && value is ClassType valueClass
            && valueClass.Symbol != declaredClass.Symbol && valueClass.Symbol.DerivesFrom(declaredClass.Symbol)
            ? valueClass
            : null;
    }

    // ---- Regions ----

    /// <summary>
    /// Binds a lambda's or nested function's body (SPEC §3.9): it sees narrowed lets, parameters and
    /// bindings, but not vars or paths, since it runs later; and what it ends doesn't apply here.
    /// </summary>
    private T InDeferredRegion<T>(Func<T> bind)
    {
        var before = _narrowing;
        var mark = _endings.Count;
        var deferred = before.Types.Keys.Where(CanEnd).ToList();
        _narrowing = before with
        {
            Types = before.Types.RemoveRange(deferred),
            Why = before.Why.SetItems(deferred.Select(key => KeyValuePair.Create(key, "a lambda or nested function runs later, when it may have changed; copy it to a 'let' first"))),
        };
        var bound = bind();
        _endings.RemoveRange(mark, _endings.Count - mark);
        _narrowing = before;
        // A var this function assigns is never narrowed from here on.
        foreach (var variable in _nestedAssigned.Where(v => before.Types.ContainsKey(v)).ToList())
            End(variable, "a nested function assigns it, so it isn't narrowed");
        return bound;
    }

    /// <summary>
    /// Binds code that may run more than once, or be left at any point (a loop, a <c>try</c>): it
    /// starts from what held before less whatever it ends (SPEC §3.9). When it ends something that
    /// held, it's bound again from the reduced state, with the first binding's diagnostics dropped.
    /// </summary>
    private T Repeatable<T>(Func<T> bind, Func<T, IEnumerable<BoundStmt>> parts)
    {
        // Each binding starts from _narrowing as set here; bind leaves the state after it there.
        var entry = _narrowing;
        if (!entry.Types.Keys.Any(CanEnd))
            return bind();

        var (diagnostics, mark) = (_diagnostics, _endings.Count);
        var scratch = new DiagnosticBag { Source = _source, FileOrder = diagnostics.FileOrder };
        _diagnostics = scratch;
        var first = bind();
        _diagnostics = diagnostics;

        var ended = Ends(parts(first), entry);
        if (ended.Count == 0)
        {
            // Nothing that held is ended, so the first binding stands, diagnostics and all.
            _diagnostics.AddFrom(scratch);
            return first;
        }
        _endings.RemoveRange(mark, _endings.Count - mark);
        _narrowing = entry;
        foreach (var (key, why) in ended)
            End(key, why);
        return bind();
    }

    /// <summary>The facts in <paramref name="state"/> that statements could end, with why, outside nested functions and lambdas.</summary>
    private List<(object Key, string Why)> Ends(IEnumerable<BoundStmt> statements, Narrowing state)
    {
        var ended = new Dictionary<object, string>();
        void Visit(BoundStmt stmt)
        {
            switch (stmt)
            {
                case BoundAssign assign:
                    if (state.Types.ContainsKey(assign.Target))
                    {
                        ended.TryAdd(assign.Target, assign.Target.Type is OptionalOf
                            ? $"it can be None again after the assignment on line {Line(assign.Span)}"
                            : $"it was assigned again on line {Line(assign.Span)}");
                    }
                    foreach (var path in state.Types.Keys.OfType<FieldPath>().Where(p => p.Root == assign.Target))
                        ended.TryAdd(path, RootWhy(assign.Target, assign.Span));
                    break;
                case BoundPropertySet propertySet:
                    foreach (var path in state.Types.Keys.OfType<FieldPath>())
                        ended.TryAdd(path, CallWhy(propertySet.Span));
                    break;
                case BoundClassAttributeAssign attributeAssign:
                    foreach (var path in state.Types.Keys.OfType<FieldPath>().Where(p => p.Root == attributeAssign.Attribute))
                        ended.TryAdd(path, $"the assignment to '{attributeAssign.Attribute.Owner.Name}.{attributeAssign.Attribute.Name}' on line {Line(attributeAssign.Span)} could have changed it; copy it to a 'let' first");
                    break;
                case BoundFieldAssign fieldAssign:
                    foreach (var path in state.Types.Keys.OfType<FieldPath>().Where(p => p.Fields.Any(f => f.Name == fieldAssign.Field.Name)))
                        ended.TryAdd(path, $"the assignment to '.{fieldAssign.Field.Name}' on line {Line(fieldAssign.Span)} could have changed it; copy it to a 'let' first");
                    break;
                case BoundLocalFunction:
                    return;
            }
            foreach (var expr in BoundTree.Expressions(stmt))
            {
                if (FirstCode(expr) is { } call)
                {
                    foreach (var path in state.Types.Keys.OfType<FieldPath>())
                        ended.TryAdd(path, $"the call on line {Line(call.Span)} could have changed it; copy it to a 'let' first");
                }
                foreach (var change in Descendants(expr).Where(e => Changes(e) is not null))
                    EndChanged(change, change.Span);
            }
            if (Changes(stmt) is not null)
                EndChanged(stmt, stmt.Span);
            if (stmt is BoundUnpack unpack)
            {
                foreach (var store in unpack.Stores)
                    Visit(store);
            }
            foreach (var arm in BoundTree.ArmBlocks(stmt))
                Visit(arm);
            foreach (var inner in BoundTree.Statements(stmt))
                Visit(inner);
        }
        void EndChanged(object node, TextSpan span)
        {
            var container = TypeChecker.Unwrap(Changes(node)!);
            foreach (var path in state.Types.Keys.OfType<FieldPath>().Where(p => p.Steps.Any(s => s is ElementStep e && e.Container == container)))
                ended.TryAdd(path, ChangeWhy(container, span));
        }
        foreach (var stmt in statements)
            Visit(stmt);
        // A var a nested function assigns is never narrowed.
        foreach (var variable in _nestedAssigned.Where(state.Types.ContainsKey))
            ended.TryAdd(variable, "a nested function assigns it, so it isn't narrowed");
        return [.. ended.Select(pair => (pair.Key, pair.Value))];
    }

    private static IEnumerable<BoundExpr> Descendants(BoundExpr expr) =>
        expr is BoundLambda ? [] : BoundTree.Children(expr).SelectMany(Descendants).Prepend(expr);

    private static BoundExpr? FirstCode(BoundExpr expr) =>
        RunsCode(expr) ? expr : expr is BoundLambda ? null : BoundTree.Children(expr).Select(FirstCode).FirstOrDefault(e => e is not null);

    // ---- Paths ----

    /// <summary>What a narrowing is kept under: a name's symbol, or a field path (any root, for errors).</summary>
    private static object? KeyOf(BoundExpr expr) => expr switch
    {
        BoundVariable variable => variable.Symbol,
        BoundNarrowed narrowed => KeyOf(narrowed.Value),
        BoundClassAttribute attribute => new FieldPath(attribute.Attribute, []),
        BoundFieldAccess access => KeyOf(access.Target) switch
        {
            VariableSymbol root => new FieldPath(root, [access.Field]),
            FieldPath path => path with { Steps = path.Steps.Add(access.Field) },
            _ => null,
        },
        BoundIndex { Target.Type: ListOfType or DictOfType } index => ElementPath(index.Target, ElementKey(index.Index)),
        BoundTupleElement element => ElementPath(element.Tuple, (long)element.Index),
        _ => null,
    };

    /// <summary>
    /// A name or path as its declared optional type, for the optional tests (<c>is None</c>,
    /// <c>is some</c>, <c>??</c>), which a narrowing that rules out None doesn't make invalid (SPEC §3.9).
    /// </summary>
    private static BoundExpr Declared(BoundExpr value) =>
        value is BoundNarrowed { Value: { Type: OptionalOf } declared } ? declared : value;

    /// <summary>A field read, narrowed if its path is (SPEC §3.9).</summary>
    private BoundExpr NarrowedField(BoundFieldAccess access) =>
        KeyOf(access) is FieldPath path && _narrowing.Types.TryGetValue(path, out var type)
            ? new BoundNarrowed(access, type, access.Span)
            : access;

    /// <summary>The help for a name or path that isn't narrowed here, saying why; null if nothing would have narrowed it.</summary>
    private string? WhyNotNarrowed(BoundExpr value) => value switch
    {
        // A property is code, and isn't narrowed (SPEC §4.8).
        BoundPropertyGet get => $"'{get.Property.Name}' is a property, so it may change; copy it to a 'let' first",
        _ => KeyOf(value) is { } key && _narrowing.Why.TryGetValue(key, out var why) ? why : null,
    };

    /// <summary>
    /// Whether an operation runs a special method that a class, or a class derived from it, defines
    /// (SPEC §3.9, §4.8): an operator on a class value, or display, equality, hashing and dict
    /// lookups on values that hold one.
    /// </summary>
    private static bool RunsSpecialMethod(BoundExpr expr) => expr switch
    {
        BoundUnary unary => Holds(unary.Operand.Type, "__neg__"),
        BoundBinary { Op: BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.In or BinaryOp.NotIn } binary =>
            Holds(binary.Left.Type, "__eq__", "__hash__") || Holds(binary.Right.Type, "__eq__", "__hash__"),
        BoundBinary binary when SpecialMethods.Forward(binary.Op) is { } forward =>
            Holds(binary.Left.Type, forward, SpecialMethods.Reflected(binary.Op)!) || Holds(binary.Right.Type, forward, SpecialMethods.Reflected(binary.Op)!),
        BoundCall { Function.Builtin: BuiltinFunction.Print or BuiltinFunction.Str or BuiltinFunction.Repr } call =>
            call.Arguments.Any(a => Holds(a.Type, "__str__", "__repr__")),
        BoundCall { Function.Builtin: BuiltinFunction.Hash } call => call.Arguments.Any(a => Holds(a.Type, "__hash__")),
        BoundFormat format => Holds(format.Value.Type, "__str__", "__repr__"),
        BoundIndex { Target.Type: DictOfType dict } => Holds(dict.Key, "__eq__", "__hash__"),
        BoundBuiltinMethodCall call => Holds(call.Target.Type, "__eq__", "__hash__"),
        _ => false,
    };

    /// <summary>Whether a value of this type could hold a class value that has one of the methods, its own or a derived class's.</summary>
    private static bool Holds(TaipanType type, params string[] methods) => type switch
    {
        ClassType classType => classType.Symbol.MayDefine(methods),
        // A bounded type parameter's type argument may be a class (SPEC §3.11).
        TypeParameterType { Symbol.Bound: not null } => true,
        OptionalOf optional => Holds(optional.Inner, methods),
        ListOfType list => Holds(list.Element, methods),
        DictOfType dict => Holds(dict.Key, methods) || Holds(dict.Value, methods),
        TupleOfType tuple => tuple.Elements.Any(e => Holds(e, methods)),
        VarTupleOfType varTuple => Holds(varTuple.Element, methods),
        _ => false,
    };
}
