using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// The bound tree: names resolved to symbols, every expression typed. The emitter works from
// this tree only. Spans point back at the source for #line mapping.

public sealed record BoundProgram(
    ImmutableArray<BoundStruct> Structs,
    ImmutableArray<BoundConstant> Constants,
    ImmutableArray<BoundFunction> Functions,
    BoundBlock TopLevel);

public sealed record BoundStruct(StructSymbol Symbol, ImmutableArray<BoundFunction> Methods, TextSpan Span);

public sealed record BoundConstant(VariableSymbol Symbol, BoundExpr Value, TextSpan Span);

public sealed record BoundFunction(FunctionSymbol Symbol, BoundBlock Body, TextSpan Span);

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

public sealed record BoundReturn(BoundExpr? Value, TextSpan Span) : BoundStmt(Span);

public sealed record BoundBreak(TextSpan Span) : BoundStmt(Span);

public sealed record BoundContinue(TextSpan Span) : BoundStmt(Span);

public sealed record BoundExprStmt(BoundExpr Expr, TextSpan Span) : BoundStmt(Span);

public sealed record BoundIf(ImmutableArray<BoundIfClause> Clauses, BoundBlock? Else, TextSpan Span) : BoundStmt(Span);

public sealed record BoundIfClause(BoundExpr Condition, BoundBlock Body);

public sealed record BoundWhile(BoundExpr Condition, BoundBlock Body, TextSpan Span) : BoundStmt(Span);

/// <summary><c>for v in range(start, stop)</c>; <see cref="Start"/> is null for <c>range(stop)</c>.</summary>
public sealed record BoundForRange(VariableSymbol Variable, BoundExpr? Start, BoundExpr Stop, BoundBlock Body, TextSpan Span)
    : BoundStmt(Span);

/// <summary><c>for v in e</c> over a list (elements), dict (keys) or str (characters).</summary>
public sealed record BoundForEach(VariableSymbol Variable, BoundExpr Iterable, BoundBlock Body, TextSpan Span) : BoundStmt(Span);

// ---- Expressions ----

public abstract record BoundExpr(TaipanType Type, TextSpan Span);

/// <summary>A literal; <see cref="Value"/> is a <c>long</c>, <c>double</c>, <c>bool</c> or <c>string</c>.</summary>
public sealed record BoundLiteral(object Value, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundVariable(VariableSymbol Symbol, TextSpan Span) : BoundExpr(Symbol.Type, Span);

public sealed record BoundUnary(UnaryOp Op, BoundExpr Operand, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundBinary(BoundExpr Left, BinaryOp Op, BoundExpr Right, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

public sealed record BoundCall(FunctionSymbol Function, ImmutableArray<BoundExpr> Arguments, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

public sealed record BoundListLiteral(ImmutableArray<BoundExpr> Elements, ListOfType ListType, TextSpan Span)
    : BoundExpr(ListType, Span);

public sealed record BoundDictLiteral(ImmutableArray<(BoundExpr Key, BoundExpr Value)> Entries, DictOfType DictType, TextSpan Span)
    : BoundExpr(DictType, Span);

/// <summary>A struct constructor call. Arguments are in source order, each with the field it sets.</summary>
public sealed record BoundConstruct(StructSymbol Struct, ImmutableArray<(FieldSymbol Field, BoundExpr Value)> Arguments, bool IsKeyword, TextSpan Span)
    : BoundExpr(Struct.Type, Span);

public sealed record BoundFieldAccess(BoundExpr Target, FieldSymbol Field, TextSpan Span) : BoundExpr(Field.Type, Span);

/// <summary>Indexing a list, dict or str; <see cref="Target"/>'s type says which.</summary>
public sealed record BoundIndex(BoundExpr Target, BoundExpr Index, TaipanType Type, TextSpan Span) : BoundExpr(Type, Span);

public sealed record BoundMethodCall(BoundExpr Target, FunctionSymbol Method, ImmutableArray<BoundExpr> Arguments, TextSpan Span)
    : BoundExpr(Method.ReturnType, Span);

public sealed record BoundBuiltinMethodCall(BoundExpr Target, BuiltinMethod Method, ImmutableArray<BoundExpr> Arguments, TaipanType Type, TextSpan Span)
    : BoundExpr(Type, Span);

/// <summary>Literal text parts are <see cref="BoundLiteral"/> strings; holes are any expression.</summary>
public sealed record BoundFString(ImmutableArray<BoundExpr> Parts, TextSpan Span) : BoundExpr(TaipanType.Str, Span);

public sealed record BoundErrorExpr(TextSpan Span) : BoundExpr(TaipanType.Error, Span);

public sealed record BoundNone(TextSpan Span) : BoundExpr(TaipanType.None, Span);

/// <summary>
/// <c>x is some v</c> as an <c>if</c> or <c>while</c> condition; <see cref="Binding"/> is
/// declared in the guarded body (SPEC §3.4).
/// </summary>
public sealed record BoundIsSome(BoundExpr Operand, VariableSymbol Binding, TextSpan Span) : BoundExpr(TaipanType.Bool, Span);

public sealed record BoundIsNone(BoundExpr Operand, TextSpan Span) : BoundExpr(TaipanType.Bool, Span);
