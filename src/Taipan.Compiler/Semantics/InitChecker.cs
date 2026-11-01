using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

/// <summary>
/// Strict initialization (SPEC §4.3, TP0226): checks a bound <c>__init__</c> body so that an
/// object is never seen with a field unset. It follows which of the class's own fields are set on
/// every path. Inherited fields belong to <c>super().__init__</c>, which a subclass calls once,
/// directly in the body, after setting its own fields. Until the object is complete, <c>self</c>
/// may only be used to set a field or read one already set.
/// </summary>
public sealed class InitChecker
{
    private readonly ClassSymbol _class;
    private readonly VariableSymbol _self;
    private readonly DiagnosticBag _diagnostics;
    private bool _sawSuperInit;

    /// <summary>What holds at a point in the body, on every path to it.</summary>
    private sealed record State(HashSet<FieldSymbol> Assigned, bool SuperCalled, bool Terminated)
    {
        public State Copy() => this with { Assigned = [.. Assigned] };
    }

    private InitChecker(ClassSymbol owner, VariableSymbol self, DiagnosticBag diagnostics)
    {
        _class = owner;
        _self = self;
        _diagnostics = diagnostics;
    }

    public static void Check(ClassSymbol owner, VariableSymbol self, BoundBlock body, TextSpan name, DiagnosticBag diagnostics)
    {
        var checker = new InitChecker(owner, self, diagnostics);
        // Fields with defaults are set before the body runs.
        var start = new State([.. owner.Fields.Where(f => f.HasDefault)], SuperCalled: false, Terminated: false);
        var end = checker.Block(body.Statements, start, topLevel: true);
        if (!end.Terminated && checker.FirstUnset(end) is { } unset)
            diagnostics.ReportFieldNotSet(name, unset.Name);
        if (owner.Base is not null && !checker._sawSuperInit)
            diagnostics.ReportMissingSuperInit(name, owner.Name);
    }

    private bool IsComplete(State state) => FirstUnset(state) is null && (_class.Base is null || state.SuperCalled);

    // The first of the class's own fields not yet set, in declaration order.
    private FieldSymbol? FirstUnset(State state) => _class.Fields.FirstOrDefault(f => !state.Assigned.Contains(f));

    private bool IsSelf(BoundExpr expr) =>
        expr is BoundVariable { Symbol: var symbol } && symbol == _self
        || expr is BoundNarrowed { Value.Symbol: var narrowed } && narrowed == _self;

    private State Block(IEnumerable<BoundStmt> statements, State state, bool topLevel)
    {
        foreach (var stmt in statements)
        {
            if (state.Terminated)
                break;
            state = Stmt(stmt, state, topLevel);
        }
        return state;
    }

    private State Stmt(BoundStmt stmt, State state, bool topLevel)
    {
        switch (stmt)
        {
            case BoundBlock block:
                return Block(block.Statements, state, topLevel);
            case BoundExprStmt { Expr: BoundSuperCall { IsInit: true } call } when topLevel:
                Expressions(call.Arguments, state);
                if (state.SuperCalled)
                    _diagnostics.ReportMisplacedSuperInit(call.Span);
                else if (FirstUnset(state) is { } unset)
                    _diagnostics.ReportSuperInitEarly(call.Span, unset.Name);
                _sawSuperInit = true;
                return state with { SuperCalled = true };
            case BoundFieldAssign assign when IsSelf(assign.Target):
                Expression(assign.Value, state);
                if (assign.Field.Owner != _class)
                {
                    if (!state.SuperCalled)
                        _diagnostics.ReportInheritedFieldEarly(assign.Target.Span, assign.Field.Name);
                    return state;
                }
                var assigned = state.Copy();
                assigned.Assigned.Add(assign.Field);
                return assigned;
            case BoundIf ifStmt:
                var branches = new List<State>();
                foreach (var clause in ifStmt.Clauses)
                {
                    Expression(clause.Condition, state);
                    branches.Add(Block(clause.Body.Statements, state.Copy(), topLevel: false));
                }
                branches.Add(ifStmt.Else is null ? state : Block(ifStmt.Else.Statements, state.Copy(), topLevel: false));
                return Merge(branches);
            case BoundMatch match:
                Expression(match.Scrutinee, state);
                // Exhaustive once it type-checks (SPEC §5.1), so some arm always runs.
                return Merge([.. match.Arms.Where(a => a.IsReachable).Select(a => Block(a.Body.Statements, state.Copy(), topLevel: false))]);
            case BoundWhile or BoundForRange or BoundForEach:
                // The body may run no times, so what it sets doesn't count after the loop.
                Expressions(BoundTree.Expressions(stmt), state);
                Block(BoundTree.Statements(stmt), state.Copy(), topLevel: false);
                return state;
            case BoundReturn ret:
                if (ret.Value is not null)
                    Expression(ret.Value, state);
                if (!IsComplete(state))
                    _diagnostics.ReportEarlyReturn(ret.Span, FirstUnset(state)?.Name);
                return state with { Terminated = true };
            case BoundLocalFunction function:
                // Its body could run at any time, so it sees the object as it is now.
                Expressions(BoundTree.Expressions(function), state);
                Nested(function.Body, state);
                return state;
            default:
                Expressions(BoundTree.Expressions(stmt), state);
                return state;
        }
    }

    /// <summary>The paths that continue past a branch: a field is set after it if every one of them sets it.</summary>
    private static State Merge(List<State> branches)
    {
        var live = branches.Where(b => !b.Terminated).ToList();
        if (live.Count == 0)
            return branches.Count > 0 ? branches[0] with { Terminated = true } : new State([], false, true);
        var assigned = live.Skip(1).Aggregate(new HashSet<FieldSymbol>(live[0].Assigned), (set, b) =>
        {
            set.IntersectWith(b.Assigned);
            return set;
        });
        return new State(assigned, live.All(b => b.SuperCalled), Terminated: false);
    }

    // Statements inside a nested function, checked against the state where it's defined.
    private void Nested(BoundStmt stmt, State state)
    {
        Expressions(BoundTree.Expressions(stmt), state);
        foreach (var inner in BoundTree.Statements(stmt))
            Nested(inner, state);
    }

    private void Expressions(IEnumerable<BoundExpr> expressions, State state)
    {
        foreach (var expr in expressions)
            Expression(expr, state);
    }

    private void Expression(BoundExpr expr, State state)
    {
        switch (expr)
        {
            case BoundFieldAccess access when IsSelf(access.Target):
                if (access.Field.Owner != _class)
                {
                    if (!state.SuperCalled)
                        _diagnostics.ReportInheritedFieldEarly(access.Span, access.Field.Name);
                }
                else if (!state.Assigned.Contains(access.Field))
                {
                    _diagnostics.ReportFieldReadEarly(access.Span, access.Field.Name);
                }
                return;
            case BoundVariable or BoundNarrowed when IsSelf(expr):
                if (!IsComplete(state))
                    _diagnostics.ReportSelfUsedEarly(expr.Span, FirstUnset(state)?.Name);
                return;
            case BoundSuperCall { IsInit: true } call:
                // Not a statement directly in the body.
                _diagnostics.ReportMisplacedSuperInit(call.Span);
                _sawSuperInit = true;
                Expressions(call.Arguments, state);
                return;
            case BoundSuperCall call:
                if (!IsComplete(state))
                    _diagnostics.ReportSelfUsedEarly(call.Span, FirstUnset(state)?.Name);
                Expressions(call.Arguments, state);
                return;
            default:
                Expressions(BoundTree.Children(expr), state);
                return;
        }
    }
}
