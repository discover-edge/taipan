using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// Slice assignment and `del` (SPEC §5.5, M26).
public sealed partial class Binder
{
    /// <summary><c>xs[a:b:c] = ys</c>: a list's slice takes a list of its element type; a <c>py</c> value's, any convertible value.</summary>
    private BoundStmt BindSliceAssign(AssignStmt assign, SliceExpr sliceExpr, BoundExpr? preset)
    {
        if (assign.Op != AssignOp.Assign)
        {
            _diagnostics.ReportCompoundSliceAssign(sliceExpr.Span, TypeChecker.Display(TypeChecker.ToBinaryOp(assign.Op)) + "=");
            return OrphanAssign(assign, preset);
        }
        if (BindValue(sliceExpr) is not BoundSlice slice)
            return OrphanAssign(assign, preset);
        if (slice.Type == TaipanType.Str)
        {
            _diagnostics.ReportCannotAssignStrElement(sliceExpr.Span);
            return OrphanAssign(assign, preset);
        }
        var value = slice.Type == TaipanType.Py
            ? preset ?? BindPyArgument(assign.Value)
            : BindAssignedValue(assign, slice, slice.Type, preset);
        return new BoundSliceAssign(slice, value, assign.Span);
    }

    private BoundDelete BindDelete(DelStmt delete) =>
        new([.. delete.Targets.Select(BindDeleteTarget).OfType<BoundExpr>()], delete.Span);

    // A target as it would be read, if it's one that can be deleted; null after reporting one that can't.
    private BoundExpr? BindDeleteTarget(Expr target)
    {
        switch (target)
        {
            case NameExpr name:
                LookupName(name.Name, name.Span);
                _diagnostics.ReportCannotDeleteName(name.Span, name.Name);
                return null;
            case MemberExpr member:
                BindValue(member);
                _diagnostics.ReportCannotDeleteField(member.Span);
                return null;
        }
        var bound = BindValue(target);
        switch (bound)
        {
            case BoundIndex { Target.Type: ListOfType or DictOfType } or BoundPyGetItem:
            case BoundSlice { Type: ListOfType or PrimitiveType { Name: "py" } }:
                return bound;
            case BoundErrorExpr:
                return null;
            case BoundTupleElement element:
                _diagnostics.ReportCannotDeleteElement(target.Span, element.Tuple.Type);
                return null;
            default:
                var owner = bound switch
                {
                    BoundIndex index => index.Target.Type,
                    BoundSlice slice => slice.Target.Type,
                    _ => bound.Type,
                };
                _diagnostics.ReportCannotDeleteElement(target.Span, owner);
                return null;
        }
    }
}
