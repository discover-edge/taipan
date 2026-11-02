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

// ---- Imports (SPEC §4.7) ----

/// <summary><c>import m</c> or <c>import m as alias</c>.</summary>
public sealed record ImportStmt(Identifier Module, Identifier? Alias, TextSpan Span) : TopItem(Span);

/// <summary><c>from m import a, b as c</c>.</summary>
public sealed record FromImportStmt(Identifier Module, ImmutableArray<ImportName> Names, TextSpan Span) : TopItem(Span);

public sealed record ImportName(Identifier Name, Identifier? Alias, TextSpan Span) : Node(Span);

// ---- Declarations ----

/// <summary><see cref="TypeParams"/> is empty for a non-generic function (SPEC §3.6).</summary>
public sealed record FuncDef(
    ImmutableArray<Decorator> Decorators, Identifier Name, ImmutableArray<TypeParam> TypeParams, ImmutableArray<Param> Params,
    TypeSyntax? ReturnType, Block Body, TextSpan Span) : TopItem(Span);

/// <summary><c>@name</c> on the line before a class or <c>def</c> (SPEC §2, M15).</summary>
/// <summary><c>@name</c>, or <c>@name.member</c> for a property's setter (SPEC §4.8, M28).</summary>
public sealed record Decorator(Identifier Name, Identifier? Member, TextSpan Span) : Node(Span);

/// <summary>A parameter; <see cref="Default"/> is its default value, if any (SPEC §4.2, M14).</summary>
public sealed record Param(Identifier Name, TypeSyntax Type, Expr? Default, TextSpan Span) : Node(Span);

/// <summary>A class (SPEC §4.3, M15). <see cref="Bases"/> has at most one entry once it type-checks.</summary>
public sealed record ClassDef(
    ImmutableArray<Decorator> Decorators, Identifier Name, ImmutableArray<TypeParam> TypeParams, ImmutableArray<TypeSyntax> Bases,
    ImmutableArray<FieldDef> Fields, ImmutableArray<MethodDef> Methods, TextSpan Span) : TopItem(Span);

/// <summary>What a method's first parameter is: <c>self</c>, <c>cls</c>, or neither (SPEC §4.8, M28).</summary>
public enum Receiver { Self, Cls, None }

/// <summary>
/// A method; <see cref="Params"/> excludes the leading <c>self</c> or <c>cls</c>, whose span is
/// <see cref="ReceiverSpan"/> (empty, at the parameters, for <see cref="Receiver.None"/>).
/// </summary>
public sealed record MethodDef(
    ImmutableArray<Decorator> Decorators, Identifier Name, Receiver Receiver, TextSpan ReceiverSpan, ImmutableArray<Param> Params,
    TypeSyntax? ReturnType, Block Body, TextSpan Span) : Node(Span);

/// <summary>A class field or variant payload field; only a class field may have a <see cref="Default"/>.</summary>
public sealed record FieldDef(Identifier Name, TypeSyntax Type, Expr? Default, TextSpan Span) : Node(Span);

public sealed record EnumDef(Identifier Name, ImmutableArray<TypeParam> TypeParams, ImmutableArray<VariantDef> Variants, TextSpan Span) : TopItem(Span);

/// <summary>An enum variant; <see cref="Fields"/> is empty for a variant without a payload.</summary>
public sealed record VariantDef(Identifier Name, ImmutableArray<FieldDef> Fields, TextSpan Span) : Node(Span);

public sealed record ConstDef(Identifier Name, TypeSyntax? Type, Expr Value, TextSpan Span) : TopItem(Span);

/// <summary><c>type Name[T] = type</c>; <see cref="TypeParams"/> is empty for a non-generic alias (SPEC §3.12, M18).</summary>
public sealed record TypeAliasDef(Identifier Name, ImmutableArray<TypeParam> TypeParams, TypeSyntax Type, TextSpan Span) : TopItem(Span);

/// <summary>
/// A type parameter; <see cref="Constraints"/> is empty unless it has a constraint tuple (SPEC §3.6),
/// and <see cref="Bound"/> is set when it has a protocol bound (§3.11, M17).
/// </summary>
public sealed record TypeParam(Identifier Name, ImmutableArray<TypeSyntax> Constraints, TypeSyntax? Bound, TextSpan Span) : Node(Span);

// ---- Statements ----

public abstract record Stmt(TextSpan Span) : TopItem(Span);

public sealed record Block(ImmutableArray<Stmt> Statements, TextSpan Span) : Node(Span);

public sealed record LetStmt(Identifier Name, TypeSyntax? Type, Expr Value, TextSpan Span) : Stmt(Span);

public sealed record VarStmt(Identifier Name, TypeSyntax? Type, Expr Value, TextSpan Span) : Stmt(Span);

/// <summary><c>let a, b = e</c> or <c>var a, b = e</c>: unpacks a tuple or a list (SPEC §3.8).</summary>
public sealed record UnpackStmt(bool IsVar, ImmutableArray<Unpacked> Names, Expr Value, TextSpan Span) : Stmt(Span);

/// <summary><c>a, b = e</c>: assigns a tuple's or list's elements to the targets, left to right (SPEC §3.8).</summary>
public sealed record TupleAssignStmt(ImmutableArray<Unpacked> Targets, Expr Value, TextSpan Span) : Stmt(Span);

/// <summary>One place an element is unpacked to (SPEC §3.8): a name, a target, or a nested group (M29).</summary>
public abstract record Unpacked(TextSpan Span) : Node(Span);

/// <summary>A name a <c>let</c>, <c>var</c> or <c>for</c> declares; <c>*name</c> takes the rest (M29).</summary>
public sealed record UnpackedName(Identifier Name, bool Starred, TextSpan Span) : Unpacked(Span);

/// <summary>A target an assignment stores to; <c>*target</c> takes the rest (M29).</summary>
public sealed record UnpackedTarget(Expr Target, bool Starred, TextSpan Span) : Unpacked(Span);

/// <summary><c>(a, b)</c> inside an unpacking: unpacks that element in turn (M29).</summary>
public sealed record UnpackedGroup(ImmutableArray<Unpacked> Items, TextSpan Span) : Unpacked(Span);

public enum AssignOp { Assign, Add, Subtract, Multiply, Divide }

/// <summary><see cref="Target"/> is a <see cref="NameExpr"/>, <see cref="MemberExpr"/> or <see cref="IndexExpr"/>.</summary>
public sealed record AssignStmt(Expr Target, AssignOp Op, Expr Value, TextSpan Span) : Stmt(Span);

public sealed record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);

/// <summary><c>raise e</c>, or a bare <c>raise</c> when <see cref="Value"/> is null (SPEC §3.10).</summary>
public sealed record RaiseStmt(Expr? Value, TextSpan Span) : Stmt(Span);

/// <summary><c>del a[i], d[k]</c>: its targets, deleted left to right (SPEC §5.5, M26).</summary>
public sealed record DelStmt(ImmutableArray<Expr> Targets, TextSpan Span) : Stmt(Span);

public sealed record PassStmt(TextSpan Span) : Stmt(Span);

public sealed record BreakStmt(TextSpan Span) : Stmt(Span);

public sealed record ContinueStmt(TextSpan Span) : Stmt(Span);

public sealed record ExprStmt(Expr Expr, TextSpan Span) : Stmt(Span);

/// <summary>A <c>def</c> inside a block: a nested function (SPEC §3.7).</summary>
public sealed record FuncDefStmt(FuncDef Function, TextSpan Span) : Stmt(Span);

/// <summary>The <c>if</c> clause followed by any <c>elif</c> clauses.</summary>
public sealed record IfStmt(ImmutableArray<IfClause> Clauses, Block? Else, TextSpan Span) : Stmt(Span);

public sealed record IfClause(Condition Condition, Block Body, TextSpan Span) : Node(Span);

public sealed record WhileStmt(Condition Condition, Block Body, TextSpan Span) : Stmt(Span);

/// <summary>One plain name is the loop variable; anything else unpacks each element (SPEC §5.2).</summary>
public sealed record ForStmt(ImmutableArray<Unpacked> Variables, Expr Iterable, Block Body, TextSpan Span) : Stmt(Span);

public sealed record MatchStmt(Expr Scrutinee, ImmutableArray<MatchArm> Arms, TextSpan Span) : Stmt(Span);

/// <summary><c>try</c> with its clauses; a <c>try</c> has at least one <c>except</c> or a <c>finally</c> (SPEC §3.10).</summary>
public sealed record TryStmt(Block Body, ImmutableArray<ExceptClause> Handlers, Block? Else, Block? Finally, TextSpan Span) : Stmt(Span);

/// <summary>
/// An <c>except</c> clause. <see cref="Types"/> is empty for a bare <c>except:</c>; each type is a
/// <see cref="NameExpr"/> or <see cref="MemberExpr"/> chain naming a class or a Python class.
/// <see cref="TypesSpan"/> covers the types, with their parentheses if they have them.
/// </summary>
public sealed record ExceptClause(ImmutableArray<Expr> Types, TextSpan TypesSpan, Identifier? Binding, Block Body, TextSpan Span) : Node(Span);

/// <summary>An arm written inline (<c>Ok: print("ok")</c>) has a one-statement body.</summary>
public sealed record MatchArm(Pattern Pattern, Block Body, TextSpan Span) : Node(Span);

// ---- Conditions ----

public abstract record Condition(TextSpan Span) : Node(Span);

public sealed record ExprCondition(Expr Expr, TextSpan Span) : Condition(Span);

public sealed record IsSomeCondition(Expr Expr, Identifier Binding, TextSpan Span) : Condition(Span);


// ---- Patterns ----

public abstract record Pattern(TextSpan Span) : Node(Span);

public sealed record WildcardPattern(TextSpan Span) : Pattern(Span);

/// <summary><see cref="Value"/> includes the sign of a negative pattern.</summary>
public sealed record IntPattern(BigInteger Value, TextSpan Span) : Pattern(Span);

public sealed record StringPattern(string Value, TextSpan Span) : Pattern(Span);

public sealed record BoolPattern(bool Value, TextSpan Span) : Pattern(Span);

/// <summary>
/// <c>Variant</c>, <c>Enum.Variant</c>, <c>module.Enum.Variant</c>, or any of them with bindings; or a class pattern,
/// <c>Class(…)</c> or <c>module.Class(…)</c> (SPEC §5.1). <see cref="Bindings"/> is null when there are no parentheses.
/// </summary>
public sealed record VariantPattern(Identifier? Module, Identifier? Enum, Identifier Variant, ImmutableArray<PatternBinding>? Bindings, TextSpan Span)
    : Pattern(Span);

/// <summary>An argument of a variant or class pattern: positional, or <c>field=pattern</c> when <see cref="Field"/> is set (M15; a sub-pattern since M29).</summary>
public sealed record PatternBinding(Identifier? Field, Pattern Pattern, TextSpan Span) : Node(Span);

/// <summary><c>(p, q)</c>: one sub-pattern per element of a tuple (SPEC §5.1, M29).</summary>
public sealed record TuplePattern(ImmutableArray<Pattern> Elements, TextSpan Span) : Pattern(Span);

/// <summary><c>[p, *rest, q]</c>: a list's elements, with at most one <see cref="StarPattern"/> (SPEC §5.1, M29).</summary>
public sealed record ListPattern(ImmutableArray<Pattern> Elements, TextSpan Span) : Pattern(Span);

/// <summary><c>*name</c> or <c>*_</c> in a list pattern: the rest of the list.</summary>
public sealed record StarPattern(Identifier Name, TextSpan Span) : Pattern(Span);

public sealed record ErrorPattern(TextSpan Span) : Pattern(Span);

// ---- Types ----

public abstract record TypeSyntax(TextSpan Span) : Node(Span);

/// <summary>A built-in scalar type (<c>int</c>, <c>str</c>, …) or a user-defined type name.</summary>
public sealed record NamedType(Identifier Name, TextSpan Span) : TypeSyntax(Span);

/// <summary>A type from an imported module: <c>m.Point</c> or <c>m.Box[int]</c> (SPEC §4.7).</summary>
public sealed record QualifiedType(Identifier Module, TypeSyntax Type, TextSpan Span) : TypeSyntax(Span);

/// <summary>An instantiated generic class or enum: <c>Box[int]</c>.</summary>
public sealed record GenericType(Identifier Name, ImmutableArray<TypeSyntax> Arguments, TextSpan Span) : TypeSyntax(Span);

public sealed record ListType(TypeSyntax Element, TextSpan Span) : TypeSyntax(Span);

public sealed record DictType(TypeSyntax Key, TypeSyntax Value, TextSpan Span) : TypeSyntax(Span);

public sealed record OptionalType(TypeSyntax Inner, TextSpan Span) : TypeSyntax(Span);

/// <summary><c>tuple[A, B]</c> (SPEC §3.8).</summary>
public sealed record TupleType(ImmutableArray<TypeSyntax> Elements, TextSpan Span) : TypeSyntax(Span);

/// <summary><c>Callable[[A, B], R]</c>; <see cref="Result"/> is null for a result of <c>None</c> (SPEC §3.7).</summary>
public sealed record CallableType(ImmutableArray<TypeSyntax> Parameters, TypeSyntax? Result, TextSpan Span) : TypeSyntax(Span);

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

/// <summary>A hole: its expression, with an optional debug text, conversion and format spec (SPEC §1, M26).</summary>
public sealed record FStringHoleSegment(Expr Expr, TextSpan Span) : FStringSegment(Span)
{
    public string? DebugText { get; init; }

    public char? Conversion { get; init; }

    public string? Spec { get; init; }

    public TextSpan SpecSpan { get; init; }
}

public sealed record BoolLiteral(bool Value, TextSpan Span) : Expr(Span);

public sealed record NoneLiteral(TextSpan Span) : Expr(Span);

/// <summary>A name reference, including <c>self</c>.</summary>
public sealed record NameExpr(string Name, TextSpan Span) : Expr(Span);

/// <summary><c>...</c>, valid only as a protocol method's body (SPEC §3.11, M17).</summary>
public sealed record EllipsisExpr(TextSpan Span) : Expr(Span);

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

/// <summary><c>target[start:stop:step]</c>, with omitted parts null (SPEC §5.4).</summary>
public sealed record SliceExpr(Expr Target, Expr? Start, Expr? Stop, Expr? Step, TextSpan Span) : Expr(Span);

/// <summary>A tuple, parenthesized or not (SPEC §3.8). The parser accepts one element; the binder reports it.</summary>
public sealed record TupleExpr(ImmutableArray<Expr> Elements, TextSpan Span) : Expr(Span);

/// <summary><c>lambda x, y: body</c>; a parameter may be <c>_</c> (SPEC §3.7).</summary>
public sealed record LambdaExpr(ImmutableArray<Identifier> Parameters, Expr Body, TextSpan Span) : Expr(Span);

/// <summary><c>x is None</c>, or <c>x is not None</c> when <see cref="Negated"/> (SPEC §3.4).</summary>
public sealed record IsNoneExpr(Expr Operand, bool Negated, TextSpan Span) : Expr(Span);

/// <summary><c>x as T</c>: converts a py value to a Taipan type, or between classes (SPEC §10.4, §4.3).</summary>
public sealed record CastExpr(Expr Value, TypeSyntax Type, TextSpan Span) : Expr(Span);

/// <summary>A <c>match</c> producing a value (SPEC §5.1.1, M19): an atom since M29.</summary>
public sealed record MatchExpr(Expr Scrutinee, ImmutableArray<ValueArm> Arms, TextSpan Span) : Expr(Span);

/// <summary>An arm of a <see cref="MatchExpr"/>: exactly one of <see cref="Value"/>, <see cref="Raise"/> and <see cref="Block"/> (M29) is set.</summary>
public sealed record ValueArm(Pattern Pattern, Expr? Value, RaiseStmt? Raise, TextSpan Span) : Node(Span)
{
    public Block? Block { get; init; }
}

/// <summary><c>*x</c>: only an unpacking target that takes the rest (SPEC §3.8, M29).</summary>
public sealed record StarredExpr(Expr Value, TextSpan Span) : Expr(Span);

public sealed record ErrorExpr(TextSpan Span) : Expr(Span);
