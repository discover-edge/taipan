using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

/// <summary>Assignability (SPEC §3.3), operator typing (§5.3) and return-path analysis (§4.5).</summary>
public static class TypeChecker
{
    /// <summary>
    /// SPEC §3.3: equal; int to float; S or None to an optional of something S is assignable to; a
    /// class to a base; a function type to one whose parameters it accepts and whose result it gives.
    /// </summary>
    public static bool IsAssignable(TaipanType from, TaipanType to) =>
        IsSubtype(from, to) || (to == TaipanType.Py && from.IsConvertibleToPython);

    /// <summary>Assignability without conversion to Python, which a function value's parameters and result never need.</summary>
    private static bool IsSubtype(TaipanType from, TaipanType to) =>
        from.ContainsError || to.ContainsError || from == to
        || (from == TaipanType.Int && to == TaipanType.Float)
        || (to is OptionalOf optional && (from is NoneType || (from is not OptionalOf && IsSubtype(from, optional.Inner))))
        || (from is OptionalOf { Inner: ClassType fromClass } && to is OptionalOf { Inner: ClassType toClass } && Derives(fromClass, toClass))
        || (from is ClassType fromType && to is ClassType toType && Derives(fromType, toType))
        || (from is FunctionType f && to is FunctionType t && f.Parameters.Length == t.Parameters.Length
            && t.Parameters.Zip(f.Parameters).All(pair => IsSubtype(pair.First, pair.Second))
            && (f.Result == t.Result || (f.Result is not NothingType && t.Result is not NothingType && IsSubtype(f.Result, t.Result))));

    // Generic classes can't derive, so a class deriving from another has no type arguments (SPEC §4.3).
    private static bool Derives(ClassType from, ClassType to) => from.Symbol != to.Symbol && from.Symbol.DerivesFrom(to.Symbol);

    /// <summary>Whether a value of one class could be a value of the other: one derives from the other.</summary>
    public static bool AreRelated(ClassSymbol a, ClassSymbol b) => a.DerivesFrom(b) || b.DerivesFrom(a);

    /// <summary>
    /// Whether <c>==</c> may compare the two types: equal, or both numeric. A <c>T?</c> may also be
    /// compared with a <c>T</c> or with <c>none</c> (SPEC §3.4).
    /// </summary>
    public static bool AreComparable(TaipanType left, TaipanType right)
    {
        // Python objects inside Taipan collections have no Taipan equality (SPEC §10.2).
        if (left.ContainsPy || right.ContainsPy)
            return false;
        if (left.ContainsError || right.ContainsError || left == right || (left.IsNumeric && right.IsNumeric))
            return true;
        // A class and a base of it, or function types one of which converts to the other (SPEC §4.3, §3.7).
        if (left is ClassType lc && right is ClassType rc)
            return lc.Symbol != rc.Symbol && AreRelated(lc.Symbol, rc.Symbol);
        if (left is FunctionType && right is FunctionType)
            return IsSubtype(left, right) || IsSubtype(right, left);
        // Element by element (SPEC §3.8).
        if (left is TupleOfType l && right is TupleOfType r)
            return l.Elements.Length == r.Elements.Length && l.Elements.Zip(r.Elements).All(pair => AreComparable(pair.First, pair.Second));
        if (left is not OptionalOf && right is not OptionalOf)
            return false;
        if (left is NoneType || right is NoneType)
            return true;
        return AreComparable(Unwrap(left), Unwrap(right));
    }

    public static TaipanType Unwrap(TaipanType type) => type is OptionalOf optional ? optional.Inner : type;

    /// <summary>
    /// Replaces type parameters by their arguments. Optionals flatten: <c>T?</c> with
    /// <c>T = int?</c> is <c>int?</c> (SPEC §3.6).
    /// </summary>
    public static TaipanType Substitute(TaipanType type, IReadOnlyDictionary<TypeParameterSymbol, TaipanType> map)
    {
        if (map.Count == 0)
            return type;
        return type switch
        {
            TypeParameterType parameter => map.GetValueOrDefault(parameter.Symbol, parameter),
            OptionalOf optional => Flatten(Substitute(optional.Inner, map)),
            ListOfType list => new ListOfType(Substitute(list.Element, map)),
            DictOfType dict => new DictOfType(Substitute(dict.Key, map), Substitute(dict.Value, map)),
            ClassType classType => classType with { TypeArguments = [.. classType.TypeArguments.Select(a => Substitute(a, map))] },
            EnumType enumType => enumType with { TypeArguments = [.. enumType.TypeArguments.Select(a => Substitute(a, map))] },
            FunctionType function => new FunctionType([.. function.Parameters.Select(p => Substitute(p, map))], Substitute(function.Result, map)),
            TupleOfType tuple => new TupleOfType([.. tuple.Elements.Select(e => Substitute(e, map))]),
            _ => type,
        };
    }

    private static TaipanType Flatten(TaipanType inner) => inner is OptionalOf or ErrorType ? inner : new OptionalOf(inner);

    /// <summary>The substitution for a generic class or enum type's members.</summary>
    public static Dictionary<TypeParameterSymbol, TaipanType> MapOf(TaipanType type) => type switch
    {
        ClassType s => Zip(s.Symbol.TypeParameters, s.TypeArguments),
        EnumType e => Zip(e.Symbol.TypeParameters, e.TypeArguments),
        _ => [],
    };

    public static Dictionary<TypeParameterSymbol, TaipanType> Zip(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TaipanType> arguments) =>
        parameters.Zip(arguments).ToDictionary(pair => pair.First, pair => pair.Second);

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
    /// Whether control can't fall off the end of <paramref name="stmt"/>: a <c>return</c> or <c>raise</c>, an
    /// <c>if</c> with <c>else</c> or a <c>match</c> whose branches all terminate, or a block containing one.
    /// Loops never count (SPEC §4.5).
    /// </summary>
    public static bool Terminates(BoundStmt stmt) => stmt switch
    {
        BoundReturn or BoundRaise => true,
        BoundBlock block => block.Statements.Any(Terminates),
        BoundIf { Else: { } elseBlock } ifStmt => ifStmt.Clauses.All(c => Terminates(c.Body)) && Terminates(elseBlock),
        // Exhaustive once it type-checks (SPEC §5.1).
        BoundMatch match => match.Arms.Where(a => a.IsReachable).All(a => Terminates(a.Body)),
        // SPEC §3.10: its finally block terminates, or every except block does and so does the try or else block.
        BoundTry tryStmt => (tryStmt.Finally is { } finallyBlock && Terminates(finallyBlock))
            || (tryStmt.Handlers.All(h => Terminates(h.Body)) && (Terminates(tryStmt.Body) || (tryStmt.Else is { } elseBlock && Terminates(elseBlock)))),
        _ => false,
    };
}
