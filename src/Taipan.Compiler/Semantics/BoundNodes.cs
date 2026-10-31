using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// The bound tree: names resolved to symbols, every expression typed. The emitter works from
// this tree only. Spans point back at the source for #line mapping.

public sealed record BoundProgram(
    ImmutableArray<BoundPythonImport> PythonImports,
    ImmutableArray<EnumSymbol> Enums,
    ImmutableArray<BoundStruct> Structs,
    ImmutableArray<BoundConstant> Constants,
    ImmutableArray<BoundFunction> Functions,
    BoundBlock TopLevel);

public sealed record BoundStruct(StructSymbol Symbol, ImmutableArray<BoundFunction> Methods, TextSpan Span);

/// <summary><c>from python import m</c>: <see cref="Symbol"/> holds the module, imported at startup (SPEC §10.1).</summary>
public sealed record BoundPythonImport(VariableSymbol Symbol, string Module, TextSpan Span, SourceText Source);

public sealed record BoundConstant(VariableSymbol Symbol, BoundExpr Value, TextSpan Span)
{
    /// <summary>The file the constant is defined in (SPEC §4.7).</summary>
    public SourceText? Source { get; init; }
}

public sealed record BoundFunction(FunctionSymbol Symbol, BoundBlock Body, TextSpan Span)
{
    /// <summary>The file the function is defined in (SPEC §4.7).</summary>
    public SourceText? Source { get; init; }

    /// <summary>The parameters the body refers to; specialized copies for a constrained body.</summary>
    public ImmutableArray<VariableSymbol> Parameters { get; init; } = Symbol.Parameters;

    /// <summary>
    /// For a function with constrained type parameters, the combination this body was checked for:
    /// one type per type parameter, an unconstrained one standing for itself (SPEC §3.6).
    /// </summary>
    public ImmutableArray<TaipanType> Specialization { get; init; } = [];
}

// ---- Statements ----

public abstract record BoundStmt(TextSpan Span);

public sealed record BoundBlock(ImmutableArray<BoundStmt> Statements, TextSpan Span) : BoundStmt(Span);

/// <summary>A <c>let</c> or <c>var</c>; the symbol's kind says which.</summary>
public sealed record BoundVariableDecl(VariableSymbol Symbol, BoundExpr Value, TextSpan Span) : BoundStmt(Span);

/// <summary>Compound assignments are lowered: <c>x += e</c> binds as <c>x = x + e</c>.</summary>
public sealed record BoundAssign(VariableSymbol Target, BoundExpr Value, TextSpan Span) : BoundStmt(Span);

/// <summary>
/// <c>target.field = value</c>. For a compound assignment, <see cref="Value"/> is a binary
/// expression whose left side reads the field through this same <see cref="Target"/> instance;
/// the emitter evaluates that instance once (SPEC §4.6).
/// </summary>
public sealed record BoundFieldAssign(BoundExpr Target, FieldSymbol Field, BoundExpr Value, bool IsCompound, TextSpan Span)
    : BoundStmt(Span);

/// <summary><c>target[index] = value</c>; compound forms share instances as for <see cref="BoundFieldAssign"/>.</summary>
public sealed record BoundIndexAssign(BoundExpr Target, BoundExpr Index, BoundExpr Value, bool IsCompound, TextSpan Span)
    : BoundStmt(Span);

/// <summary><c>x.name = v</c> on a py value; compound forms share instances as for <see cref="BoundFieldAssign"/>.</summary>
public sealed record BoundPySetAttr(BoundExpr Target, string Name, BoundExpr Value, bool IsCompound, TextSpan Span) : BoundStmt(Span);

/// <summary><c>x[k] = v</c> on a py value.</summary>
public sealed record BoundPySetItem(BoundExpr Target, BoundExpr Index, BoundExpr Value, bool IsCompound, TextSpan Span) : BoundStmt(Span);

public sealed record BoundReturn(BoundExpr? Value, TextSpan Span) : BoundStmt(Span);

public sealed record BoundBreak(TextSpan Span) : BoundStmt(Span);

public sealed record BoundContinue(TextSpan Span) : BoundStmt(Span);

public sealed record BoundExprStmt(BoundExpr Expr, TextSpan Span) : BoundStmt(Span);

/// <summary>
/// Unpacking (SPEC §3.8): <see cref="Value"/> is stored in <see cref="Temp"/>, then each of
/// <see cref="Stores"/> declares or assigns one target from an element of it, in order.
/// </summary>
public sealed record BoundUnpack(VariableSymbol Temp, BoundExpr Value, ImmutableArray<BoundStmt> Stores, TextSpan Span) : BoundStmt(Span);

/// <summary>
/// A nested <c>def</c> (SPEC §3.7). <see cref="Symbol"/> holds the function value; it's declared
/// before the body, which may refer to it.
/// </summary>
public sealed record BoundLocalFunction(
    VariableSymbol Symbol, FunctionSymbol Function, ImmutableArray<VariableSymbol> Parameters, BoundBlock Body, TextSpan Span)
    : BoundStmt(Span);

public sealed record BoundIf(ImmutableArray<BoundIfClause> Clauses, BoundBlock? Else, TextSpan Span) : BoundStmt(Span);

public sealed record BoundIfClause(BoundExpr Condition, BoundBlock Body);

public sealed record BoundWhile(BoundExpr Condition, BoundBlock Body, TextSpan Span) : BoundStmt(Span);

/// <summary><c>for v in range(start, stop)</c>; <see cref="Start"/> is null for <c>range(stop)</c>.</summary>
public sealed record BoundForRange(VariableSymbol Variable, BoundExpr? Start, BoundExpr Stop, BoundBlock Body, TextSpan Span)
    : BoundStmt(Span);

/// <summary><c>for v in e</c> over a list (elements), dict (keys) or str (characters).</summary>
public sealed record BoundForEach(VariableSymbol Variable, BoundExpr Iterable, BoundBlock Body, TextSpan Span) : BoundStmt(Span);

/// <summary>A <c>match</c>; arms marked unreachable (TP0300) are checked but not emitted.</summary>
public sealed record BoundMatch(BoundExpr Scrutinee, ImmutableArray<BoundArm> Arms, TextSpan Span) : BoundStmt(Span);

public sealed record BoundArm(BoundPattern Pattern, BoundBlock Body, bool IsReachable);

public abstract record BoundPattern;

public sealed record BoundWildcardPattern : BoundPattern;

/// <summary>An int, str or bool literal pattern; <see cref="Value"/> is a <c>long</c>, <c>string</c> or <c>bool</c>.</summary>
public sealed record BoundLiteralPattern(object Value) : BoundPattern;

/// <summary>
/// A variant pattern. <see cref="Bindings"/> has one entry per payload field (null for <c>_</c>),
/// or is empty when the pattern binds nothing.
/// </summary>
public sealed record BoundVariantPattern(VariantSymbol Variant, ImmutableArray<VariableSymbol?> Bindings) : BoundPattern;

// ---- Expressions ----

public abstract record BoundExpr(TaipanType Type, TextSpan Span);

/// <summary>A literal; <see cref="Value"/> is a <c>long</c>, <c>double</c>, <c>bool</c> or <c>string</c>.</summary>
public sealed record BoundLiteral(object Value, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundVariable(VariableSymbol Symbol, TextSpan Span) : BoundExpr(Symbol.Type, Span);

public sealed record BoundUnary(UnaryOp Op, BoundExpr Operand, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundBinary(BoundExpr Left, BinaryOp Op, BoundExpr Right, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

public sealed record BoundCall(FunctionSymbol Function, ImmutableArray<BoundExpr> Arguments, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span)
{
    /// <summary>A generic function's inferred type arguments, in parameter order (SPEC §3.6).</summary>
    public ImmutableArray<TaipanType> TypeArguments { get; init; } = [];
}

public sealed record BoundListLiteral(ImmutableArray<BoundExpr> Elements, ListOfType ListType, TextSpan Span)
    : BoundExpr(ListType, Span);

public sealed record BoundDictLiteral(ImmutableArray<(BoundExpr Key, BoundExpr Value)> Entries, DictOfType DictType, TextSpan Span)
    : BoundExpr(DictType, Span);

/// <summary>A struct constructor call. Arguments are in source order, each with the field it sets.</summary>
public sealed record BoundConstruct(StructType StructType, ImmutableArray<(FieldSymbol Field, BoundExpr Value)> Arguments, bool IsKeyword, TextSpan Span)
    : BoundExpr(StructType, Span);

/// <summary>An enum value; arguments are in source order as for <see cref="BoundConstruct"/>.</summary>
public sealed record BoundConstructVariant(VariantSymbol Variant, EnumType EnumType, ImmutableArray<(FieldSymbol Field, BoundExpr Value)> Arguments, bool IsKeyword, TextSpan Span)
    : BoundExpr(EnumType, Span);

/// <summary><see cref="BoundExpr.Type"/> is the field's type with the target's type arguments substituted.</summary>
public sealed record BoundFieldAccess(BoundExpr Target, FieldSymbol Field, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>Indexing a list, dict or str; <see cref="Target"/>'s type says which.</summary>
public sealed record BoundIndex(BoundExpr Target, BoundExpr Index, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundMethodCall(BoundExpr Target, FunctionSymbol Method, ImmutableArray<BoundExpr> Arguments, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

public sealed record BoundBuiltinMethodCall(BoundExpr Target, BuiltinMethod Method, ImmutableArray<BoundExpr> Arguments, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

/// <summary>Literal text parts are <see cref="BoundLiteral"/> strings; holes are any expression.</summary>
public sealed record BoundFString(ImmutableArray<BoundExpr> Parts, TextSpan Span) : BoundExpr(TaipanType.Str, Span);

public sealed record BoundErrorExpr(TextSpan Span) : BoundExpr(TaipanType.Error, Span);

// ---- Tuples and slices (SPEC §3.8, §5.4) ----

public sealed record BoundTupleLiteral(ImmutableArray<BoundExpr> Elements, TupleOfType TupleType, TextSpan Span) : BoundExpr(TupleType, Span);

/// <summary>An element of a tuple; <see cref="Index"/> counts from 0, already normalized.</summary>
public sealed record BoundTupleElement(BoundExpr Tuple, int Index, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>A slice of a list, str or py value; omitted parts are null.</summary>
public sealed record BoundSlice(BoundExpr Target, BoundExpr? Start, BoundExpr? Stop, BoundExpr? Step, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

// ---- Functions as values (SPEC §3.7) ----

/// <summary>A lambda. A parameter named <c>_</c> is never referred to.</summary>
public sealed record BoundLambda(ImmutableArray<VariableSymbol> Parameters, BoundExpr Body, FunctionType FunctionType, TextSpan Span)
    : BoundExpr(FunctionType, Span);

/// <summary>A top-level function used as a value.</summary>
public sealed record BoundFunctionValue(FunctionSymbol Function, FunctionType FunctionType, TextSpan Span) : BoundExpr(FunctionType, Span);

/// <summary>A call to a function value.</summary>
public sealed record BoundInvoke(BoundExpr Callee, ImmutableArray<BoundExpr> Arguments, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// <c>sorted(xs, …)</c>, or <c>xs.sort(…)</c> when <see cref="InPlace"/>. <see cref="Keywords"/>
/// are <c>key</c> and <c>reverse</c>, in source order (SPEC §6).
/// </summary>
public sealed record BoundSort(BoundExpr List, ImmutableArray<(string Name, BoundExpr Value)> Keywords, bool InPlace, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

public sealed record BoundNone(TextSpan Span) : BoundExpr(TaipanType.None, Span);

// ---- Python (SPEC §10) ----

public sealed record BoundPyGetAttr(BoundExpr Target, string Name, TextSpan Span) : BoundExpr(TaipanType.Py, Span);

public sealed record BoundPyGetItem(BoundExpr Target, BoundExpr Index, TextSpan Span) : BoundExpr(TaipanType.Py, Span);

public sealed record BoundPyCall(BoundExpr Callee, ImmutableArray<BoundExpr> Arguments, ImmutableArray<(string Name, BoundExpr Value)> Keywords, TextSpan Span)
    : BoundExpr(TaipanType.Py, Span);

/// <summary>A Taipan value converted to Python where a py is expected (SPEC §10.3).</summary>
public sealed record BoundToPy(BoundExpr Value, TextSpan Span) : BoundExpr(TaipanType.Py, Span);

/// <summary><c>x as T</c> (SPEC §10.4); <see cref="BoundExpr.Type"/> is the target.</summary>
public sealed record BoundCast(BoundExpr Value, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

/// <summary>
/// <c>x is some v</c> as an <c>if</c> or <c>while</c> condition; <see cref="Binding"/> is
/// declared in the guarded body (SPEC §3.4).
/// </summary>
public sealed record BoundIsSome(BoundExpr Operand, VariableSymbol Binding, TextSpan Span) : BoundExpr(TaipanType.Bool, Span);

public sealed record BoundIsNone(BoundExpr Operand, TextSpan Span) : BoundExpr(TaipanType.Bool, Span);
