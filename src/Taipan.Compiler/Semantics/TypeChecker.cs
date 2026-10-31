using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

/// <summary>Assignability (SPEC §3.3), operator typing (§5.3) and return-path analysis (§4.5).</summary>
public static class TypeChecker
{
    /// <summary>SPEC §3.3: equal; int to float; S or none to an optional of something S is assignable to.</summary>
    public static bool IsAssignable(TaipanType from, TaipanType to) =>
        from.ContainsError || to.ContainsError || from == to
        || (from == TaipanType.Int && to == TaipanType.Float)
        || (to is OptionalOf optional && (from is NoneType || (from is not OptionalOf && IsAssignable(from, optional.Inner))));

    /// <summary>
    /// Whether <c>==</c> may compare the two types: equal, or both numeric. A <c>T?</c> may also be
    /// compared with a <c>T</c> or with <c>none</c> (SPEC §3.4).
    /// </summary>
    public static bool AreComparable(TaipanType left, TaipanType right)
    {
        if (left.ContainsError || right.ContainsError || left == right || (left.IsNumeric && right.IsNumeric))
            return true;
        if (left is not OptionalOf && right is not OptionalOf)
            return false;
        if (left is NoneType || right is NoneType)
            return true;
        return AreComparable(Unwrap(left), Unwrap(right));
    }

    public static TaipanType Unwrap(TaipanType type) => type is OptionalOf optional ? optional.Inner : type;

    /// <summary>Result of an arithmetic operator, or null if it doesn't apply to these operands.</summary>
    public static TaipanType? Arithmetic(BinaryOp op, TaipanType left, TaipanType right)
    {
        var ints = left == TaipanType.Int && right == TaipanType.Int;
        var numeric = left.IsNumeric && right.IsNumeric;
        return op switch
        {
            BinaryOp.Add when left == TaipanType.Str && right == TaipanType.Str => TaipanType.Str,
            BinaryOp.Add when left is ListOfType && left == right => left,
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply when ints => TaipanType.Int,
            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply when numeric => TaipanType.Float,
            BinaryOp.Divide when numeric => TaipanType.Float,
            BinaryOp.FloorDivide or BinaryOp.Modulo when ints => TaipanType.Int,
            _ => null,
        };
    }

    public static string Display(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.FloorDivide => "//",
        BinaryOp.Modulo => "%",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.In => "in",
        BinaryOp.NotIn => "not in",
        BinaryOp.And => "and",
        BinaryOp.Or => "or",
        BinaryOp.Coalesce => "??",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    public static BinaryOp ToBinaryOp(AssignOp op) => op switch
    {
        AssignOp.Add => BinaryOp.Add,
        AssignOp.Subtract => BinaryOp.Subtract,
        AssignOp.Multiply => BinaryOp.Multiply,
        AssignOp.Divide => BinaryOp.Divide,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    /// <summary>
    /// Whether control can't fall off the end of <paramref name="stmt"/>: a <c>return</c>, an
    /// <c>if</c> with <c>else</c> or a <c>match</c> whose branches all terminate, or a block containing one.
    /// Loops never count (SPEC §4.5).
    /// </summary>
    public static bool Terminates(BoundStmt stmt) => stmt switch
    {
        BoundReturn => true,
        BoundBlock block => block.Statements.Any(Terminates),
        BoundIf { Else: { } elseBlock } ifStmt => ifStmt.Clauses.All(c => Terminates(c.Body)) && Terminates(elseBlock),
        // Exhaustive once it type-checks (SPEC §5.1).
        BoundMatch match => match.Arms.Where(a => a.IsReachable).All(a => Terminates(a.Body)),
        _ => false,
    };
}
