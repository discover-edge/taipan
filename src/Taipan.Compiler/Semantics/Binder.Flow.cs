using System.Collections.Immutable;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Narrowing that follows the program's flow (SPEC §3.9, M27): facts about names and field paths,
// carried from statement to statement, joined where branches meet, and ended by what could change them.
public sealed partial class Binder
{
    /// <summary>
    /// A field path, <c>p.x</c> or <c>self.config.port</c> (SPEC §3.9). It can be narrowed when its
    /// root can't change.
    /// </summary>
    private sealed record FieldPath(VariableSymbol Root, ImmutableArray<FieldSymbol> Fields)
    {
        public bool Equals(FieldPath? other) => other is not null && other.Root == Root && other.Fields.SequenceEqual(Fields);

        public override int GetHashCode() => Fields.Aggregate(Root.GetHashCode(), (hash, field) => HashCode.Combine(hash, field));

        public bool CanNarrow => Root.Kind is VariableKind.Let or VariableKind.Parameter or VariableKind.LoopVariable;
    }

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
            EndPaths(_ => true, $"the call on line {Line(bound.Span)} could have changed it; copy it to a 'let' first");
        return bound;
    }

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
        if (_narrowing.Types.ContainsKey(variable))
        {
            End(variable, variable.Type is OptionalOf
                ? $"it can be None again after the assignment on line {Line(span)}"
                : $"it was assigned again on line {Line(span)}");
        }
        if (!_nestedAssigned.Contains(variable) && Narrower(variable.Type, value.Type) is { } narrower)
            _narrowing = _narrowing with { Types = _narrowing.Types.SetItem(variable, narrower), Why = _narrowing.Why.Remove(variable) };
    }

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
                case BoundAssign assign when state.Types.ContainsKey(assign.Target):
                    ended.TryAdd(assign.Target, assign.Target.Type is OptionalOf
                        ? $"it can be None again after the assignment on line {Line(assign.Span)}"
                        : $"it was assigned again on line {Line(assign.Span)}");
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
            }
            if (stmt is BoundUnpack unpack)
            {
                foreach (var store in unpack.Stores)
                    Visit(store);
            }
            foreach (var inner in BoundTree.Statements(stmt))
                Visit(inner);
        }
        foreach (var stmt in statements)
            Visit(stmt);
        // A var a nested function assigns is never narrowed.
        foreach (var variable in _nestedAssigned.Where(state.Types.ContainsKey))
            ended.TryAdd(variable, "a nested function assigns it, so it isn't narrowed");
        return [.. ended.Select(pair => (pair.Key, pair.Value))];
    }

    private static BoundExpr? FirstCode(BoundExpr expr) =>
        RunsCode(expr) ? expr : expr is BoundLambda ? null : BoundTree.Children(expr).Select(FirstCode).FirstOrDefault(e => e is not null);

    // ---- Paths ----

    /// <summary>What a narrowing is kept under: a name's symbol, or a field path (any root, for errors).</summary>
    private static object? KeyOf(BoundExpr expr) => expr switch
    {
        BoundVariable variable => variable.Symbol,
        BoundNarrowed narrowed => KeyOf(narrowed.Value),
        BoundFieldAccess access => KeyOf(access.Target) switch
        {
            VariableSymbol root => new FieldPath(root, [access.Field]),
            FieldPath path => path with { Fields = path.Fields.Add(access.Field) },
            _ => null,
        },
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
    private string? WhyNotNarrowed(BoundExpr value) =>
        KeyOf(value) is { } key && _narrowing.Why.TryGetValue(key, out var why) ? why : null;
}
