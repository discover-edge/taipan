using System.Collections.Immutable;
using System.Numerics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Syntax;

// AST for SPEC §2. Every node carries the span of the source it was parsed from.
// Error* nodes stand in for input that failed to parse; they only appear alongside a TP0100.

public abstract record Node(TextSpan Span);

public sealed record Identifier(string Text, TextSpan Span) : Node(Span);

public sealed record CompilationUnit(ImmutableArray<TopItem> Items, TextSpan Span) : Node(Span);

public abstract record TopItem(TextSpan Span) : Node(Span);

// ---- Declarations ----

/// <summary><see cref="TypeParams"/> is empty for a non-generic function (SPEC §3.6).</summary>
public sealed record FuncDef(Identifier Name, ImmutableArray<Identifier> TypeParams, ImmutableArray<Param> Params, TypeSyntax? ReturnType, Block Body, TextSpan Span)
    : TopItem(Span);

public sealed record Param(Identifier Name, TypeSyntax Type, TextSpan Span) : Node(Span);

public sealed record StructDef(Identifier Name, ImmutableArray<Identifier> TypeParams, ImmutableArray<FieldDef> Fields, ImmutableArray<MethodDef> Methods, TextSpan Span)
    : TopItem(Span);

/// <summary>A method; <see cref="Params"/> excludes the leading <c>self</c>, whose span is <see cref="Self"/>.</summary>
public sealed record MethodDef(Identifier Name, TextSpan Self, ImmutableArray<Param> Params, TypeSyntax? ReturnType, Block Body, TextSpan Span)
    : Node(Span);

public sealed record FieldDef(Identifier Name, TypeSyntax Type, TextSpan Span) : Node(Span);

public sealed record EnumDef(Identifier Name, ImmutableArray<Identifier> TypeParams, ImmutableArray<VariantDef> Variants, TextSpan Span) : TopItem(Span);

/// <summary>An enum variant; <see cref="Fields"/> is empty for a variant without a payload.</summary>
public sealed record VariantDef(Identifier Name, ImmutableArray<FieldDef> Fields, TextSpan Span) : Node(Span);

public sealed record ConstDef(Identifier Name, TypeSyntax? Type, Expr Value, TextSpan Span) : TopItem(Span);

// ---- Statements ----

public abstract record Stmt(TextSpan Span) : TopItem(Span);

public sealed record Block(ImmutableArray<Stmt> Statements, TextSpan Span) : Node(Span);

public sealed record LetStmt(Identifier Name, TypeSyntax? Type, Expr Value, TextSpan Span) : Stmt(Span);

public sealed record VarStmt(Identifier Name, TypeSyntax? Type, Expr Value, TextSpan Span) : Stmt(Span);

public enum AssignOp { Assign, Add, Subtract, Multiply, Divide }

/// <summary><see cref="Target"/> is a <see cref="NameExpr"/>, <see cref="MemberExpr"/> or <see cref="IndexExpr"/>.</summary>
public sealed record AssignStmt(Expr Target, AssignOp Op, Expr Value, TextSpan Span) : Stmt(Span);

public sealed record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);

public sealed record PassStmt(TextSpan Span) : Stmt(Span);

public sealed record BreakStmt(TextSpan Span) : Stmt(Span);

public sealed record ContinueStmt(TextSpan Span) : Stmt(Span);

public sealed record ExprStmt(Expr Expr, TextSpan Span) : Stmt(Span);

/// <summary>The <c>if</c> clause followed by any <c>elif</c> clauses.</summary>
public sealed record IfStmt(ImmutableArray<IfClause> Clauses, Block? Else, TextSpan Span) : Stmt(Span);

public sealed record IfClause(Condition Condition, Block Body, TextSpan Span) : Node(Span);

public sealed record WhileStmt(Condition Condition, Block Body, TextSpan Span) : Stmt(Span);

public sealed record ForStmt(Identifier Variable, Expr Iterable, Block Body, TextSpan Span) : Stmt(Span);

public sealed record MatchStmt(Expr Scrutinee, ImmutableArray<MatchArm> Arms, TextSpan Span) : Stmt(Span);

/// <summary>An arm written inline (<c>Ok: print("ok")</c>) has a one-statement body.</summary>
public sealed record MatchArm(Pattern Pattern, Block Body, TextSpan Span) : Node(Span);

// ---- Conditions ----

public abstract record Condition(TextSpan Span) : Node(Span);

public sealed record ExprCondition(Expr Expr, TextSpan Span) : Condition(Span);

public sealed record IsSomeCondition(Expr Expr, Identifier Binding, TextSpan Span) : Condition(Span);

public sealed record IsNoneCondition(Expr Expr, TextSpan Span) : Condition(Span);

// ---- Patterns ----

public abstract record Pattern(TextSpan Span) : Node(Span);

public sealed record WildcardPattern(TextSpan Span) : Pattern(Span);

/// <summary><see cref="Value"/> includes the sign of a negative pattern.</summary>
public sealed record IntPattern(BigInteger Value, TextSpan Span) : Pattern(Span);

public sealed record StringPattern(string Value, TextSpan Span) : Pattern(Span);

public sealed record BoolPattern(bool Value, TextSpan Span) : Pattern(Span);

/// <summary>
/// <c>Variant</c>, <c>Enum.Variant</c>, or either with bindings. <see cref="Bindings"/> is null
/// when there are no parentheses.
/// </summary>
public sealed record VariantPattern(Identifier? Enum, Identifier Variant, ImmutableArray<Identifier>? Bindings, TextSpan Span)
    : Pattern(Span);

public sealed record ErrorPattern(TextSpan Span) : Pattern(Span);

// ---- Types ----

public abstract record TypeSyntax(TextSpan Span) : Node(Span);

/// <summary>A built-in scalar type (<c>int</c>, <c>str</c>, …) or a user-defined type name.</summary>
public sealed record NamedType(Identifier Name, TextSpan Span) : TypeSyntax(Span);

/// <summary>An instantiated generic struct or enum: <c>Box[int]</c>.</summary>
public sealed record GenericType(Identifier Name, ImmutableArray<TypeSyntax> Arguments, TextSpan Span) : TypeSyntax(Span);

public sealed record ListType(TypeSyntax Element, TextSpan Span) : TypeSyntax(Span);

public sealed record DictType(TypeSyntax Key, TypeSyntax Value, TextSpan Span) : TypeSyntax(Span);

public sealed record OptionalType(TypeSyntax Inner, TextSpan Span) : TypeSyntax(Span);

public sealed record ErrorType(TextSpan Span) : TypeSyntax(Span);

// ---- Expressions ----

public abstract record Expr(TextSpan Span) : Node(Span);

/// <summary>The literal's magnitude; range checking is the type checker's job (SPEC §1.3).</summary>
public sealed record IntLiteral(BigInteger Value, TextSpan Span) : Expr(Span);

public sealed record FloatLiteral(double Value, TextSpan Span) : Expr(Span);

public sealed record StringLiteral(string Value, TextSpan Span) : Expr(Span);

public sealed record FStringExpr(ImmutableArray<FStringSegment> Segments, TextSpan Span) : Expr(Span);

public abstract record FStringSegment(TextSpan Span) : Node(Span);

public sealed record FStringTextSegment(string Value, TextSpan Span) : FStringSegment(Span);

public sealed record FStringHoleSegment(Expr Expr, TextSpan Span) : FStringSegment(Span);

public sealed record BoolLiteral(bool Value, TextSpan Span) : Expr(Span);

public sealed record NoneLiteral(TextSpan Span) : Expr(Span);

/// <summary>A name reference, including <c>self</c>.</summary>
public sealed record NameExpr(string Name, TextSpan Span) : Expr(Span);

public sealed record ListExpr(ImmutableArray<Expr> Elements, TextSpan Span) : Expr(Span);

public sealed record DictExpr(ImmutableArray<DictEntry> Entries, TextSpan Span) : Expr(Span);

public sealed record DictEntry(Expr Key, Expr Value, TextSpan Span) : Node(Span);

public enum UnaryOp { Negate, Not }

public sealed record UnaryExpr(UnaryOp Op, Expr Operand, TextSpan Span) : Expr(Span);

public enum BinaryOp
{
    Add, Subtract, Multiply, Divide, FloorDivide, Modulo,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, In, NotIn,
    And, Or, Coalesce,
}

public sealed record BinaryExpr(Expr Left, BinaryOp Op, Expr Right, TextSpan Span) : Expr(Span);

public sealed record CallExpr(Expr Callee, ImmutableArray<Argument> Arguments, TextSpan Span) : Expr(Span);

/// <summary><see cref="Name"/> is set for a keyword argument.</summary>
public sealed record Argument(Identifier? Name, Expr Value, TextSpan Span) : Node(Span);

public sealed record MemberExpr(Expr Target, Identifier Member, TextSpan Span) : Expr(Span);

public sealed record IndexExpr(Expr Target, Expr Index, TextSpan Span) : Expr(Span);

public sealed record ErrorExpr(TextSpan Span) : Expr(Span);
