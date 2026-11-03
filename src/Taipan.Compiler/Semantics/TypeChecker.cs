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
        // A class, protocol type or class-bounded type parameter that satisfies a protocol is one (SPEC §3.18).
        || (to is ClassType { Symbol.IsProtocol: true } protocol && IsProtocolValue(from) && Protocols.Unsatisfied(from, protocol) is null)
        || (from is OptionalOf { Inner: var inner } && to is OptionalOf { Inner: ClassType { Symbol.IsProtocol: true } optionalProtocol }
            && IsProtocolValue(inner) && Protocols.Unsatisfied(inner, optionalProtocol) is null)
        // A type parameter bounded by a class is one of its subclasses (SPEC §3.18).
        || (from is TypeParameterType { Symbol.ClassBound: { } bound } && IsSubtype(bound, to))
        || (from is OptionalOf { Inner: TypeParameterType { Symbol.ClassBound: { } optionalBound } } && to is OptionalOf && IsSubtype(new OptionalOf(optionalBound), to))
        || (from is ClassOfType fromClassOf && to is ClassOfType toClassOf && Derives(fromClassOf.Class, toClassOf.Class))
        // A class with __call__ is a function value of its shape (SPEC §4.9).
        || (from is ClassType && to is FunctionType && SpecialMethods.CallShape(from) is { } shape && IsSubtype(shape, to))
        // A .NET object parameter takes int, float, bool, str and .NET values (SPEC §13.3).
        || (to is DotNetType { IsObject: true } && from is PrimitiveType { Name: not "py" } or DotNetType)
        || (from is DotNetType fromNet && to is DotNetType toNet && DotNetTypes.Derives(fromNet.Symbol, toNet.Symbol))
        || (from is OptionalOf { Inner: DotNetType fromInner } && to is OptionalOf { Inner: DotNetType toInner } && DotNetTypes.Derives(fromInner.Symbol, toInner.Symbol))
        // Anything iterable is an Iterable, and iterators are covariant for classes (SPEC §3.13).
        || (to is IterableOfType iterable && ElementOf(from) is { } element && IsElementOf(element, iterable.Element))
        || (to is IteratorOfType iterator && IteratorElementOf(from) is { } produced && IsElementOf(produced, iterator.Element))
        || (from is FunctionType f && to is FunctionType t && f.Parameters.Length == t.Parameters.Length
            && t.Parameters.Zip(f.Parameters).All(pair => IsSubtype(pair.First, pair.Second))
            && (f.Result == t.Result || (f.Result is not NothingType && t.Result is not NothingType && IsSubtype(f.Result, t.Result))));

    /// <summary>Whether values of a type can be held by a protocol type: objects, not built-in values (SPEC §3.18).</summary>
    public static bool IsProtocolValue(TaipanType type) => type is ClassType or TypeParameterType { Symbol.ClassBound: not null };

    private static bool IsElementOf(TaipanType element, TaipanType expected) =>
        element == expected || element.ContainsError || expected.ContainsError
        || element is ClassType e && expected is ClassType x && Derives(e, x);

    /// <summary>
    /// The type of the values a <c>for</c> loop over a value of this type takes (SPEC §3.13): a
    /// list's elements, a dict's keys, a str's characters, an iterator's or iterable's values, or a
    /// class's through its <c>__iter__</c> or <c>__next__</c>. Null if it isn't iterable.
    /// </summary>
    public static TaipanType? ElementOf(TaipanType type) => type switch
    {
        ListOfType list => list.Element,
        VarTupleOfType varTuple => varTuple.Element,
        SetOfType set => set.Element,
        DictOfType dict => dict.Key,
        PrimitiveType { Name: "str" } => TaipanType.Str,
        IteratorOfType iterator => iterator.Element,
        IterableOfType iterable => iterable.Element,
        OpaqueType { Name: "TextIO" } => TaipanType.Str,
        ClassType classType when classType.Symbol.FindMethod("__iter__") is { ReturnType: var result } => Substitute(result, MapOf(classType)) is IteratorOfType iterator ? iterator.Element : null,
        ClassType => IteratorElementOf(type),
        _ => null,
    };

    /// <summary>The values a type gives as an iterator: an iterator's, or a class's <c>__next__</c>'s (SPEC §3.13).</summary>
    public static TaipanType? IteratorElementOf(TaipanType type) => type switch
    {
        IteratorOfType iterator => iterator.Element,
        // A file is an iterator over its lines (SPEC §6.1).
        OpaqueType { Name: "TextIO" } => TaipanType.Str,
        ClassType classType when classType.Symbol.FindMethod("__next__") is { ReturnType: var result } => Substitute(result, MapOf(classType)),
        _ => null,
    };

    // A class deriving from a generic one derives from one instantiation of it (SPEC §3.18).
    private static bool Derives(ClassType from, ClassType to) => from.Symbol != to.Symbol && AsBase(from, to.Symbol) == to;

    /// <summary>
    /// The instantiation of <paramref name="target"/> that <paramref name="type"/> is or derives
    /// from, with its type arguments: <c>Box[int]</c> for <c>Labeled[int]</c>. Null if it doesn't derive.
    /// </summary>
    public static ClassType? AsBase(ClassType type, ClassSymbol target)
    {
        for (var current = type; current is not null; current = current.Symbol.BaseType is { } baseType ? (ClassType)Substitute(baseType, Zip(current.Symbol.TypeParameters, current.TypeArguments)) : null)
        {
            if (current.Symbol == target)
                return current;
        }
        return null;
    }

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
        // A protocol value and one assignable to it, or the reverse (SPEC §3.18).
        if (left is ClassType { Symbol.IsProtocol: true } || right is ClassType { Symbol.IsProtocol: true })
            return IsSubtype(left, right) || IsSubtype(right, left);
        if (left is ClassType lc && right is ClassType rc)
            return lc.Symbol != rc.Symbol && (Derives(lc, rc) || Derives(rc, lc));
        if (left is DotNetType ln && right is DotNetType rn)
            return DotNetTypes.Derives(ln.Symbol, rn.Symbol) || DotNetTypes.Derives(rn.Symbol, ln.Symbol);
        if (left is FunctionType && right is FunctionType)
            return IsSubtype(left, right) || IsSubtype(right, left);
        // Element by element (SPEC §3.8).
        if (left is TupleOfType l && right is TupleOfType r)
            return l.Elements.Length == r.Elements.Length && l.Elements.Zip(r.Elements).All(pair => AreComparable(pair.First, pair.Second));
        if (left is VarTupleOfType lv && right is VarTupleOfType rv)
            return AreComparable(lv.Element, rv.Element);
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
            VarTupleOfType varTuple => new VarTupleOfType(Substitute(varTuple.Element, map)),
            IteratorOfType iterator => new IteratorOfType(Substitute(iterator.Element, map)),
            SetOfType set => new SetOfType(Substitute(set.Element, map)),
            AwaitableOfType awaitable => new AwaitableOfType(Substitute(awaitable.Result, map)),
            IterableOfType iterable => new IterableOfType(Substitute(iterable.Element, map)),
            DictOfType dict => new DictOfType(Substitute(dict.Key, map), Substitute(dict.Value, map)),
            ClassType classType => classType with { TypeArguments = [.. classType.TypeArguments.Select(a => Substitute(a, map))] },
            EnumType enumType => enumType with { TypeArguments = [.. enumType.TypeArguments.Select(a => Substitute(a, map))] },
            FunctionType function => new FunctionType([.. function.Parameters.Select(p => Substitute(p, map))], Substitute(function.Result, map)),
            TupleOfType tuple => new TupleOfType([.. tuple.Elements.Select(e => Substitute(e, map))]),
            _ => type,
        };
    }

    /// <summary>The type without the alias names it was written with (SPEC §3.18), for generated code.</summary>
    public static TaipanType Unaliased(TaipanType type)
    {
        var plain = type.Alias is null ? type : type with { Alias = null };
        return plain switch
        {
            OptionalOf optional => Same(optional.Inner, out var inner) ? plain : new OptionalOf(inner),
            ListOfType list => Same(list.Element, out var element) ? plain : new ListOfType(element),
            VarTupleOfType varTuple => Same(varTuple.Element, out var element) ? plain : new VarTupleOfType(element),
            IteratorOfType iterator => Same(iterator.Element, out var element) ? plain : new IteratorOfType(element),
            SetOfType set => Same(set.Element, out var element) ? plain : new SetOfType(element),
            AwaitableOfType awaitable => Same(awaitable.Result, out var result) ? plain : new AwaitableOfType(result),
            IterableOfType iterable => Same(iterable.Element, out var element) ? plain : new IterableOfType(element),
            DictOfType dict => Same(dict.Key, out var key) & Same(dict.Value, out var value) ? plain : new DictOfType(key, value),
            ClassType classType => classType.TypeArguments.Any(HasAlias) ? classType with { TypeArguments = [.. classType.TypeArguments.Select(Unaliased)] } : plain,
            EnumType enumType => enumType.TypeArguments.Any(HasAlias) ? enumType with { TypeArguments = [.. enumType.TypeArguments.Select(Unaliased)] } : plain,
            FunctionType function => function.Parameters.Any(HasAlias) || HasAlias(function.Result)
                ? new FunctionType([.. function.Parameters.Select(Unaliased)], Unaliased(function.Result))
                : plain,
            TupleOfType tuple => tuple.Elements.Any(HasAlias) ? new TupleOfType([.. tuple.Elements.Select(Unaliased)]) : plain,
            ClassOfType classOf => HasAlias(classOf.Class) ? new ClassOfType((ClassType)Unaliased(classOf.Class)) : plain,
            _ => plain,
        };

        static bool Same(TaipanType part, out TaipanType unaliased)
        {
            unaliased = Unaliased(part);
            return ReferenceEquals(unaliased, part);
        }
    }

    private static bool HasAlias(TaipanType type) => !ReferenceEquals(Unaliased(type), type);

    private static TaipanType Flatten(TaipanType inner) => inner is OptionalOf or ErrorType ? inner : new OptionalOf(inner);

    /// <summary>The substitution for a generic class or enum type's members.</summary>
    public static Dictionary<TypeParameterSymbol, TaipanType> MapOf(TaipanType type) => type switch
    {
        // With its bases' type parameters too, so an inherited member's type is right (SPEC §3.18).
        ClassType s => ClassMap(s),
        EnumType e => Zip(e.Symbol.TypeParameters, e.TypeArguments),
        _ => [],
    };

    private static Dictionary<TypeParameterSymbol, TaipanType> ClassMap(ClassType type)
    {
        var map = Zip(type.Symbol.TypeParameters, type.TypeArguments);
        // On a protocol value, Self is the protocol type (SPEC §3.18).
        if (type.Symbol.SelfParameter is { } self)
            map[self] = type;
        for (var baseType = type.Symbol.BaseType; baseType is not null; baseType = baseType.Symbol.BaseType)
        {
            var applied = (ClassType)Substitute(baseType, map);
            foreach (var (parameter, argument) in applied.Symbol.TypeParameters.Zip(applied.TypeArguments))
                map[parameter] = argument;
        }
        return map;
    }

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
            // `**` (SPEC §5.3, M35): an int for ints, a float with a float.
            BinaryOp.Power when ints => TaipanType.Int,
            BinaryOp.Power when numeric => TaipanType.Float,
            // Set operators give a set (SPEC §3.15, M35).
            BinaryOp.Union or BinaryOp.Intersection or BinaryOp.SymmetricDifference or BinaryOp.Subtract
                when left is SetOfType && left == right => left,
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
        BinaryOp.Power => "**",
        BinaryOp.Union => "|",
        BinaryOp.Intersection => "&",
        BinaryOp.SymmetricDifference => "^",
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
        AssignOp.FloorDivide => BinaryOp.FloorDivide,
        AssignOp.Modulo => BinaryOp.Modulo,
        AssignOp.Power => BinaryOp.Power,
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
        // sys.exit raises SystemExit (SPEC §6.1).
        BoundExprStmt { Expr: BoundCall { Function.Builtin: BuiltinFunction.Exit } } => true,
        BoundBlock block => block.Statements.Any(Terminates),
        // One whose __exit__ may suppress can end after any statement (SPEC §3.17).
        BoundWith with => !with.Suppresses && Terminates(with.Body),
        BoundIf { Else: { } elseBlock } ifStmt => ifStmt.Clauses.All(c => Terminates(c.Body)) && Terminates(elseBlock),
        // Exhaustive once it type-checks (SPEC §5.1).
        BoundMatch match => match.Arms.Where(a => a.IsReachable).All(a => Terminates(a.Body)),
        // SPEC §3.10: its finally block terminates, or every except block does and so does the try or else block.
        BoundTry tryStmt => (tryStmt.Finally is { } finallyBlock && Terminates(finallyBlock))
            || (tryStmt.Handlers.All(h => Terminates(h.Body)) && (Terminates(tryStmt.Body) || (tryStmt.Else is { } elseBlock && Terminates(elseBlock)))),
        _ => false,
    };

    /// <summary>A method's result with <c>Self</c> (M39, SPEC §4.9) replaced by the type it's called on.</summary>
    public static TaipanType SubstituteSelf(TaipanType type, TaipanType receiver) => type switch
    {
        SelfType => receiver,
        OptionalOf optional => Flatten(SubstituteSelf(optional.Inner, receiver)),
        ListOfType list => new ListOfType(SubstituteSelf(list.Element, receiver)),
        VarTupleOfType varTuple => new VarTupleOfType(SubstituteSelf(varTuple.Element, receiver)),
        SetOfType set => new SetOfType(SubstituteSelf(set.Element, receiver)),
        IteratorOfType iterator => new IteratorOfType(SubstituteSelf(iterator.Element, receiver)),
        IterableOfType iterable => new IterableOfType(SubstituteSelf(iterable.Element, receiver)),
        AwaitableOfType awaitable => new AwaitableOfType(SubstituteSelf(awaitable.Result, receiver)),
        DictOfType dict => new DictOfType(SubstituteSelf(dict.Key, receiver), SubstituteSelf(dict.Value, receiver)),
        TupleOfType tuple => new TupleOfType([.. tuple.Elements.Select(e => SubstituteSelf(e, receiver))]),
        FunctionType function => new FunctionType([.. function.Parameters.Select(p => SubstituteSelf(p, receiver))], SubstituteSelf(function.Result, receiver)),
        _ => type,
    };

    /// <summary>Whether a type mentions <c>Self</c> (M39).</summary>
    public static bool MentionsSelf(TaipanType type) => SubstituteSelf(type, TaipanType.Error) != type;
}
