using Taipan.Compiler.Lexing;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Tests.Syntax;

public class ParserTests
{
    private static ParseResult ParseText(string text)
    {
        var source = SourceText.From(text);
        var lexed = Lexer.Lex(source);
        Assert.Empty(lexed.Diagnostics);
        return Parser.Parse(source, lexed.Tokens);
    }

    // Snapshot of every top-level item, one per line.
    private static string Snapshot(string text)
    {
        var result = ParseText(text);
        Assert.Empty(result.Diagnostics);
        return string.Join("\n", result.Unit.Items.Select(AstPrinter.Print));
    }

    // Snapshot of a single expression, parsed as an expression statement.
    private static string Expr(string text)
    {
        var result = ParseText(text + "\n");
        Assert.Empty(result.Diagnostics);
        var stmt = Assert.IsType<ExprStmt>(Assert.Single(result.Unit.Items));
        return AstPrinter.Print(stmt.Expr);
    }

    // Diagnostics as "line:col code: message".
    private static string[] Errors(string text)
    {
        var source = SourceText.From(text);
        var result = ParseText(text);
        return [.. result.Diagnostics.Select(d =>
        {
            var p = source.GetLinePosition(d.Span.Start);
            return $"{p.Line}:{p.Column} {d.Code}: {d.Message}";
        })];
    }

    // ---- file / top_item ----

    [Fact]
    public void EmptyFile() =>
        Assert.Empty(ParseText("").Unit.Items);

    [Fact]
    public void FileWithSeveralItems() =>
        Assert.Equal(
            "ConstDef(N, null, IntLiteral(3))\n" +
            "FuncDef(f, [], [], null, Block([PassStmt]))\n" +
            "ExprStmt(CallExpr(NameExpr(f), []))",
            Snapshot("const N = 3\n\ndef f():\n    pass\n\nf()\n"));

    // ---- const_def ----

    [Fact]
    public void ConstDef() =>
        Assert.Equal(
            "ConstDef(LIMIT, NamedType(int), BinaryExpr(IntLiteral(1000), Multiply, IntLiteral(2)))",
            Snapshot("const LIMIT: int = 1_000 * 2\n"));

    // ---- func_def / params / param ----

    [Fact]
    public void FuncDefWithParamsAndReturnType() =>
        Assert.Equal(
            "FuncDef(add, [], [Param(a, NamedType(int)), Param(b, NamedType(float))], NamedType(float), " +
            "Block([ReturnStmt(BinaryExpr(NameExpr(a), Add, NameExpr(b)))]))",
            Snapshot("def add(a: int, b: float) -> float:\n    return a + b\n"));

    [Fact]
    public void FuncDefParamsTrailingComma() =>
        Assert.Equal(
            "FuncDef(f, [], [Param(a, NamedType(int))], null, Block([PassStmt]))",
            Snapshot("def f(\n    a: int,\n):\n    pass\n"));

    [Fact]
    public void FuncDefWithDefaults() =>
        Assert.Equal(
            "FuncDef(f, [], [Param(a, NamedType(int)), Param(b, ListType(NamedType(str)), ListExpr([])), " +
            "Param(c, NamedType(int), BinaryExpr(IntLiteral(1), Add, IntLiteral(2)))], null, Block([PassStmt]))",
            Snapshot("def f(a: int, b: list[str] = [], c: int = 1 + 2):\n    pass\n"));

    [Theory]
    [InlineData("def f(a: int = 1, b: int):\n    pass\n", "1:19")]
    [InlineData("def f(a: int, b: int = 1, c: int, d: int):\n    pass\n", "1:27")]
    public void RequiredParameterAfterDefault(string text, string position) =>
        Assert.Equal([$"{position} TP0100: parameter without a default follows parameter with a default"], Errors(text));

    // ---- struct_def / struct_member ----

    [Fact]
    public void ClassDef() =>
        Assert.Equal(
            "ClassDef(Service, [], [], [FieldDef(name, NamedType(str)), FieldDef(tags, ListType(NamedType(str)))], [])",
            Snapshot("class Service:\n    name: str\n    tags: list[str]\n"));

    // ---- method_def ----

    [Fact]
    public void StructWithMethods() =>
        Assert.Equal(
            "ClassDef(Counter, [], [], [FieldDef(n, NamedType(int))], [" +
            "MethodDef(bump, [Param(by, NamedType(int))], null, Block([AssignStmt(MemberExpr(NameExpr(self), n), Add, NameExpr(by))])), " +
            "MethodDef(get, [], NamedType(int), Block([ReturnStmt(MemberExpr(NameExpr(self), n))]))])",
            Snapshot("class Counter:\n    n: int\n    def bump(self, by: int,):\n        self.n += by\n    def get(self) -> int:\n        return self.n\n"));

    [Fact]
    public void ClassWithDecoratorsBaseAndFieldDefault() =>
        Assert.Equal(
            "ClassDef([Decorator(dataclass)], P, [], [NamedType(Base)], [FieldDef(x, NamedType(int), IntLiteral(0))], [])",
            Snapshot("@dataclass\nclass P(Base):\n    x: int = 0\n"));

    [Fact]
    public void DecoratedMethodAndPassMember() =>
        Assert.Equal(
            "ClassDef(C, [], [QualifiedType(m, NamedType(B))], [], [MethodDef([Decorator(override)], f, [], null, Block([PassStmt]))])",
            Snapshot("class C(m.B):\n    pass\n    @override\n    def f(self):\n        pass\n"));

    [Fact]
    public void IsNotNoneIsAComparison() =>
        Assert.Equal(
            "ExprStmt(BinaryExpr(IsNoneExpr(NameExpr(x), True), And, IsNoneExpr(NameExpr(y), False)))",
            Snapshot("x is not None and y is None\n"));

    [Fact]
    public void ClassPatternBindings() =>
        Assert.Equal(
            "MatchStmt(NameExpr(e), [MatchArm(VariantPattern(null, null, Pair, [a, PatternBinding(right, b)]), Block([PassStmt]))])",
            Snapshot("match e:\n    Pair(a, right=b): pass\n"));

    [Theory]
    [InlineData("@dataclass(frozen=True)\nclass A:\n    x: int\n", "1:11 TP0100: expected newline, found '('")]
    [InlineData("@dataclass\nlet y = 1\n", "2:1 TP0100: expected 'def' or 'class', found 'let'")]
    [InlineData("def f():\n    class C:\n        pass\n", "2:5 TP0100: expected statement, found 'class'")]
    [InlineData("if x is not 3:\n    pass\n", "1:13 TP0100: expected 'None', found integer")]
    public void ClassSyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    [Theory]
    [InlineData("class S:\n    def m():\n        pass\n", "2:11 TP0100: expected 'self', found ')'")]
    [InlineData("class S:\n    def m(x: int):\n        pass\n", "2:11 TP0100: expected 'self', found name 'x'")]
    [InlineData("class S:\n    def m(self x: int):\n        pass\n", "2:16 TP0100: expected ')', found name 'x'")]
    public void MethodNeedsSelf(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    // ---- enum_def / variant / named_fields ----

    [Fact]
    public void EnumDef() =>
        Assert.Equal(
            "EnumDef(Health, [], [VariantDef(Ok, []), " +
            "VariantDef(Degraded, [FieldDef(healthy, NamedType(int)), FieldDef(total, NamedType(int))]), " +
            "VariantDef(Down, [])])",
            Snapshot("enum Health:\n    Ok\n    Degraded(healthy: int, total: int)\n    Down\n"));

    [Fact]
    public void NamedFieldsTrailingComma() =>
        Assert.Equal(
            "EnumDef(E, [], [VariantDef(V, [FieldDef(a, NamedType(int))])])",
            Snapshot("enum E:\n    V(a: int,)\n"));

    // ---- block ----

    [Fact]
    public void NestedBlocks() =>
        Assert.Equal(
            "IfStmt([IfClause(ExprCondition(NameExpr(a)), Block([" +
            "WhileStmt(ExprCondition(NameExpr(b)), Block([BreakStmt, ContinueStmt])), PassStmt]))], null)",
            Snapshot("if a:\n    while b:\n        break\n        continue\n    pass\n"));

    // ---- let_stmt / var_stmt ----

    [Fact]
    public void LetAndVar() =>
        Assert.Equal(
            "LetStmt(x, null, IntLiteral(1))\n" +
            "VarStmt(y, OptionalType(NamedType(str)), NoneLiteral)",
            Snapshot("let x = 1\nvar y: str? = None\n"));

    // ---- assign_stmt / target ----

    [Fact]
    public void AssignOperators() =>
        Assert.Equal(
            "AssignStmt(NameExpr(a), Assign, IntLiteral(1))\n" +
            "AssignStmt(NameExpr(a), Add, IntLiteral(2))\n" +
            "AssignStmt(NameExpr(a), Subtract, IntLiteral(3))\n" +
            "AssignStmt(NameExpr(a), Multiply, IntLiteral(4))\n" +
            "AssignStmt(NameExpr(a), Divide, IntLiteral(5))",
            Snapshot("a = 1\na += 2\na -= 3\na *= 4\na /= 5\n"));

    [Fact]
    public void AssignTargets() =>
        Assert.Equal(
            "AssignStmt(MemberExpr(NameExpr(s), healthy), Assign, IntLiteral(2))\n" +
            "AssignStmt(IndexExpr(MemberExpr(CallExpr(NameExpr(f), []), xs), IntLiteral(0)), Add, IntLiteral(1))",
            Snapshot("s.healthy = 2\nf().xs[0] += 1\n"));

    // ---- return_stmt / pass / break / continue / expr ----

    [Fact]
    public void ReturnWithAndWithoutValue() =>
        Assert.Equal(
            "FuncDef(f, [], [], null, Block([ReturnStmt(null)]))\n" +
            "FuncDef(g, [], [], NamedType(int), Block([ReturnStmt(IntLiteral(1))]))",
            Snapshot("def f():\n    return\ndef g() -> int:\n    return 1\n"));

    [Fact]
    public void ExpressionStatement() =>
        Assert.Equal(
            "ExprStmt(CallExpr(NameExpr(print), [Argument(null, StringLiteral(\"hi\"))]))",
            Snapshot("print(\"hi\")\n"));

    // ---- if_stmt / condition ----

    [Fact]
    public void IfElifElse() =>
        Assert.Equal(
            "IfStmt([" +
            "IfClause(ExprCondition(BinaryExpr(NameExpr(n), Less, IntLiteral(0))), Block([ExprStmt(NameExpr(a))])), " +
            "IfClause(ExprCondition(BinaryExpr(NameExpr(n), Equal, IntLiteral(0))), Block([ExprStmt(NameExpr(b))])), " +
            "IfClause(ExprCondition(BoolLiteral(True)), Block([ExprStmt(NameExpr(c))]))], " +
            "Block([ExprStmt(NameExpr(d))]))",
            Snapshot("if n < 0:\n    a\nelif n == 0:\n    b\nelif True:\n    c\nelse:\n    d\n"));

    [Fact]
    public void ConditionIsSomeAndIsNone() =>
        Assert.Equal(
            "IfStmt([IfClause(IsSomeCondition(CallExpr(NameExpr(find), []), s), Block([PassStmt])), " +
            "IfClause(ExprCondition(IsNoneExpr(NameExpr(x), False)), Block([PassStmt]))], null)",
            Snapshot("if find() is some s:\n    pass\nelif x is None:\n    pass\n"));

    // ---- while_stmt ----

    [Fact]
    public void WhileWithIsSome() =>
        Assert.Equal(
            "WhileStmt(IsSomeCondition(NameExpr(x), v), Block([PassStmt]))",
            Snapshot("while x is some v:\n    pass\n"));

    // ---- for_stmt ----

    [Fact]
    public void ForStmt() =>
        Assert.Equal(
            "ForStmt([i], CallExpr(NameExpr(range), [Argument(null, IntLiteral(1)), Argument(null, IntLiteral(16))]), " +
            "Block([ExprStmt(CallExpr(NameExpr(print), [Argument(null, NameExpr(i))]))]))",
            Snapshot("for i in range(1, 16):\n    print(i)\n"));

    // ---- tuples, unpacking and slices (SPEC §3.8, §5.4) ----

    [Theory]
    [InlineData("(1, (a, b,))", "TupleExpr([IntLiteral(1), TupleExpr([NameExpr(a), NameExpr(b)])])")]
    [InlineData("(x,)", "TupleExpr([NameExpr(x)])")]
    [InlineData("(y)", "NameExpr(y)")]
    public void TupleLiterals(string text, string expected) =>
        Assert.Equal(expected, Expr(text));

    [Fact]
    public void UnparenthesizedTuples() =>
        Assert.Equal(
            "ReturnStmt(TupleExpr([NameExpr(a), NameExpr(b)]))\n" +
            "LetStmt(p, null, TupleExpr([IntLiteral(1), IntLiteral(2)]))\n" +
            "UnpackStmt(True, [x, _], NameExpr(p))\n" +
            "TupleAssignStmt([NameExpr(x), IndexExpr(NameExpr(xs), IntLiteral(0))], TupleExpr([NameExpr(y), NameExpr(x)]))",
            Snapshot("return a, b\nlet p = 1, 2\nvar x, _ = p\nx, xs[0] = y, x\n"));

    [Fact]
    public void ForUnpacking() =>
        Assert.Equal(
            "ForStmt([i, x], CallExpr(NameExpr(enumerate), [Argument(null, NameExpr(xs))]), Block([PassStmt]))",
            Snapshot("for i, x in enumerate(xs):\n    pass\n"));

    [Fact]
    public void TupleTypes() =>
        Assert.Equal(
            "LetStmt(t, OptionalType(TupleType([NamedType(int), ListType(NamedType(str))])), NameExpr(u))",
            Snapshot("let t: tuple[int, list[str]]? = u\n"));

    [Theory]
    [InlineData("xs[1:2]", "SliceExpr(NameExpr(xs), IntLiteral(1), IntLiteral(2), null)")]
    [InlineData("xs[:]", "SliceExpr(NameExpr(xs), null, null, null)")]
    [InlineData("xs[::]", "SliceExpr(NameExpr(xs), null, null, null)")]
    [InlineData("xs[::-1]", "SliceExpr(NameExpr(xs), null, null, UnaryExpr(Negate, IntLiteral(1)))")]
    [InlineData("xs[a:]", "SliceExpr(NameExpr(xs), NameExpr(a), null, null)")]
    [InlineData("xs[:b:c]", "SliceExpr(NameExpr(xs), null, NameExpr(b), NameExpr(c))")]
    public void Slices(string text, string expected) =>
        Assert.Equal(expected, Expr(text));

    [Theory]
    [InlineData("let a, = t\n", "1:8 TP0100: expected name, found '='")]
    [InlineData("a, b\n", "1:5 TP0100: expected '=', found newline")]
    [InlineData("a, b += 1\n", "1:6 TP0100: expected '=', found '+='")]
    [InlineData("a, f() = 1, 2\n", "1:4 TP0100: cannot assign to this expression")]
    [InlineData("xs[1:] = ys\n", "1:1 TP0100: cannot assign to this expression")]
    [InlineData("let t = ()\n", "1:10 TP0100: expected expression, found ')'")]
    [InlineData("for a, in xs:\n    pass\n", "1:8 TP0100: expected name, found 'in'")]
    public void TupleSyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    // ---- match_stmt / arm / pattern / qual_name ----

    [Fact]
    public void MatchWithInlineAndBlockArms() =>
        Assert.Equal(
            "MatchStmt(NameExpr(h), [" +
            "MatchArm(VariantPattern(null, null, Ok, null), Block([ExprStmt(CallExpr(NameExpr(print), [Argument(null, StringLiteral(\"ok\"))]))])), " +
            "MatchArm(VariantPattern(null, null, Degraded, [h, t]), Block([PassStmt, ReturnStmt(null)])), " +
            "MatchArm(VariantPattern(null, Health, Down, []), Block([PassStmt]))])",
            Snapshot("match h:\n    Ok: print(\"ok\")\n    Degraded(h, t):\n        pass\n        return\n    Health.Down(): pass\n"));

    [Fact]
    public void LiteralAndWildcardPatterns() =>
        Assert.Equal(
            "MatchStmt(NameExpr(x), [" +
            "MatchArm(IntPattern(0), Block([PassStmt])), " +
            "MatchArm(IntPattern(-1), Block([PassStmt])), " +
            "MatchArm(StringPattern(\"a\"), Block([PassStmt])), " +
            "MatchArm(BoolPattern(True), Block([PassStmt])), " +
            "MatchArm(BoolPattern(False), Block([PassStmt])), " +
            "MatchArm(WildcardPattern, Block([PassStmt]))])",
            Snapshot("match x:\n    0: pass\n    -1: pass\n    \"a\": pass\n    True: pass\n    False: pass\n    _: pass\n"));

    // ---- type / base_type ----

    [Theory]
    [InlineData("int", "NamedType(int)")]
    [InlineData("py", "NamedType(py)")]
    [InlineData("Service", "NamedType(Service)")]
    [InlineData("list[int]", "ListType(NamedType(int))")]
    [InlineData("dict[str, list[int]]", "DictType(NamedType(str), ListType(NamedType(int)))")]
    [InlineData("int?", "OptionalType(NamedType(int))")]
    [InlineData("list[int?]?", "OptionalType(ListType(OptionalType(NamedType(int))))")]
    [InlineData("int??", "OptionalType(OptionalType(NamedType(int)))")]
    [InlineData("int???", "OptionalType(OptionalType(OptionalType(NamedType(int))))")]
    public void Types(string type, string expected) =>
        Assert.Equal($"LetStmt(x, {expected}, NameExpr(y))", Snapshot($"let x: {type} = y\n"));

    [Fact]
    public void DoubleQuestionTypeSpans()
    {
        var let = Assert.IsType<LetStmt>(ParseText("let x: int?? = y\n").Unit.Items[0]);
        var outer = Assert.IsType<OptionalType>(let.Type);
        var inner = Assert.IsType<OptionalType>(outer.Inner);
        Assert.Equal(new TextSpan(7, 5), outer.Span);
        Assert.Equal(new TextSpan(7, 4), inner.Span);
    }

    // ---- functions as values (SPEC §3.7) ----

    [Fact]
    public void LambdaBodyIsAWholeExpression() =>
        Assert.Equal(
            "LambdaExpr([x, _], BinaryExpr(NameExpr(x), Coalesce, NameExpr(y)))",
            Expr("lambda x, _: x ?? y"));

    [Fact]
    public void LambdaWithoutParameters() =>
        Assert.Equal("LambdaExpr([], LambdaExpr([], IntLiteral(1)))", Expr("lambda: lambda: 1"));

    [Fact]
    public void LambdaArgumentsEndAtComma() =>
        Assert.Equal(
            "CallExpr(NameExpr(f), [Argument(null, LambdaExpr([x], NameExpr(x))), Argument(key, LambdaExpr([], IntLiteral(0)))])",
            Expr("f(lambda x: x, key=lambda: 0)"));

    [Fact]
    public void CallableTypes() =>
        Assert.Equal(
            "LetStmt(f, CallableType([NamedType(int), NamedType(str)], null), NameExpr(g))\n" +
            "LetStmt(h, OptionalType(CallableType([], ListType(NamedType(int)))), NameExpr(g))",
            Snapshot("let f: Callable[[int, str], None] = g\nlet h: Callable[[], list[int]]? = g\n"));

    [Fact]
    public void BareCallableIsANamedType() =>
        Assert.Equal("LetStmt(f, NamedType(Callable), NameExpr(g))", Snapshot("let f: Callable = g\n"));

    [Fact]
    public void DefInABlockIsANestedFunction() =>
        Assert.Equal(
            "FuncDef(f, [], [], null, Block([FuncDefStmt(FuncDef(g, [], [Param(x, NamedType(int))], NamedType(int), Block([ReturnStmt(NameExpr(x))])))]))",
            Snapshot("def f():\n    def g(x: int) -> int:\n        return x\n"));

    [Theory]
    [InlineData("let f = lambda x y: x\n", "1:18 TP0100: expected ':', found name 'y'")]
    [InlineData("let f = lambda (x): x\n", "1:16 TP0100: expected ':', found '('")]
    [InlineData("let f: Callable[int, str] = g\n", "1:17 TP0100: expected '[', found name 'int'")]
    [InlineData("let f: Callable[[int] int] = g\n", "1:23 TP0100: expected ',', found name 'int'")]
    [InlineData("let f: Callable[[int], None?] = g\n", "1:28 TP0100: expected ']', found '?'")]
    public void FunctionSyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    // ---- expressions: precedence and associativity ----

    [Fact]
    public void CoalesceIsRightAssociativeAndLowest() =>
        Assert.Equal(
            "BinaryExpr(BinaryExpr(NameExpr(a), Or, NameExpr(b)), Coalesce, BinaryExpr(NameExpr(c), Coalesce, NameExpr(d)))",
            Expr("a or b ?? c ?? d"));

    [Fact]
    public void OrAndNotPrecedence() =>
        Assert.Equal(
            "BinaryExpr(BinaryExpr(NameExpr(a), Or, BinaryExpr(UnaryExpr(Not, NameExpr(b)), And, NameExpr(c))), Or, NameExpr(d))",
            Expr("a or not b and c or d"));

    [Fact]
    public void NotBindsLooserThanComparison() =>
        Assert.Equal(
            "UnaryExpr(Not, BinaryExpr(NameExpr(a), Equal, NameExpr(b)))",
            Expr("not a == b"));

    [Theory]
    [InlineData("==", "Equal")]
    [InlineData("!=", "NotEqual")]
    [InlineData("<", "Less")]
    [InlineData("<=", "LessEqual")]
    [InlineData(">", "Greater")]
    [InlineData(">=", "GreaterEqual")]
    [InlineData("in", "In")]
    [InlineData("not in", "NotIn")]
    public void ComparisonOperators(string op, string expected) =>
        Assert.Equal($"BinaryExpr(NameExpr(a), {expected}, NameExpr(b))", Expr($"a {op} b"));

    [Fact]
    public void ArithmeticPrecedenceAndLeftAssociativity() =>
        Assert.Equal(
            "BinaryExpr(BinaryExpr(NameExpr(a), Subtract, BinaryExpr(BinaryExpr(BinaryExpr(NameExpr(b), Multiply, NameExpr(c)), " +
            "Divide, NameExpr(d)), FloorDivide, NameExpr(e))), Add, BinaryExpr(NameExpr(f), Modulo, NameExpr(g)))",
            Expr("a - b * c / d // e + f % g"));

    [Fact]
    public void ComparisonBindsLooserThanArithmetic() =>
        Assert.Equal(
            "BinaryExpr(BinaryExpr(NameExpr(n), Modulo, IntLiteral(15)), Equal, IntLiteral(0))",
            Expr("n % 15 == 0"));

    [Fact]
    public void UnaryMinus() =>
        Assert.Equal(
            "BinaryExpr(UnaryExpr(Negate, UnaryExpr(Negate, NameExpr(a))), Multiply, UnaryExpr(Negate, IntLiteral(9223372036854775808)))",
            Expr("--a * -9223372036854775808"));

    [Fact]
    public void ParenthesesOverridePrecedence() =>
        Assert.Equal(
            "BinaryExpr(BinaryExpr(NameExpr(a), Add, NameExpr(b)), Multiply, NameExpr(c))",
            Expr("(a + b) * c"));

    [Fact]
    public void ParenthesizedComparisonIsNotChained() =>
        Assert.Equal(
            "BinaryExpr(BinaryExpr(NameExpr(a), Less, NameExpr(b)), Less, NameExpr(c))",
            Expr("(a < b) < c"));

    // ---- postfix / call / arg ----

    [Fact]
    public void PostfixChain() =>
        Assert.Equal(
            "IndexExpr(CallExpr(MemberExpr(NameExpr(self), items), []), UnaryExpr(Negate, IntLiteral(1)))",
            Expr("self.items()[-1]"));

    [Fact]
    public void KeywordArguments() =>
        Assert.Equal(
            "CallExpr(NameExpr(Service), [Argument(name, StringLiteral(\"api\")), Argument(replicas, IntLiteral(3))])",
            Expr("Service(name=\"api\", replicas=3,)"));

    [Fact]
    public void CallAcrossLines() =>
        Assert.Equal(
            "CallExpr(NameExpr(f), [Argument(null, IntLiteral(1)), Argument(null, IntLiteral(2))])",
            Expr("f(\n    1,\n    2,\n)"));

    // ---- atom ----

    [Theory]
    [InlineData("42", "IntLiteral(42)")]
    [InlineData("1_000", "IntLiteral(1000)")]
    [InlineData("2.5e-3", "FloatLiteral(0.0025)")]
    [InlineData("1e9", "FloatLiteral(1000000000)")]
    [InlineData("\"a\\nb\"", "StringLiteral(\"a\\nb\")")]
    [InlineData("True", "BoolLiteral(True)")]
    [InlineData("False", "BoolLiteral(False)")]
    [InlineData("None", "NoneLiteral")]
    [InlineData("x", "NameExpr(x)")]
    [InlineData("self", "NameExpr(self)")]
    [InlineData("[]", "ListExpr([])")]
    [InlineData("[1, 2,]", "ListExpr([IntLiteral(1), IntLiteral(2)])")]
    [InlineData("{}", "DictExpr([])")]
    [InlineData("{\"a\": 1, \"b\": 2}", "DictExpr([DictEntry(StringLiteral(\"a\"), IntLiteral(1)), DictEntry(StringLiteral(\"b\"), IntLiteral(2))])")]
    public void Atoms(string text, string expected) =>
        Assert.Equal(expected, Expr(text));

    [Fact]
    public void FStringHolesAreParsed() =>
        Assert.Equal(
            "FStringExpr([FStringTextSegment(\"degraded (\"), FStringHoleSegment(NameExpr(h)), FStringTextSegment(\"/\"), " +
            "FStringHoleSegment(BinaryExpr(CallExpr(NameExpr(len), [Argument(null, NameExpr(xs))]), Add, IntLiteral(1))), " +
            "FStringTextSegment(\")\")])",
            Expr("f\"degraded ({h}/{len(xs) + 1})\""));

    [Fact]
    public void FStringHoleWithNestedString() =>
        Assert.Equal(
            "FStringExpr([FStringHoleSegment(IndexExpr(NameExpr(d), StringLiteral(\"k\")))])",
            Expr("f\"{d[\"k\"]}\""));

    [Fact]
    public void FStringHoleSpansReferToTheFile()
    {
        var stmt = Assert.IsType<ExprStmt>(ParseText("f\"ab{x + y}\"\n").Unit.Items[0]);
        var hole = Assert.IsType<FStringHoleSegment>(Assert.IsType<FStringExpr>(stmt.Expr).Segments[1]);
        Assert.Equal(new TextSpan(5, 5), hole.Expr.Span);
    }

    // ---- examples and fixtures ----

    [Fact]
    public void AllProductionsFixtureParses()
    {
        var path = Path.Combine(RepoPaths.Root, "tests", "Taipan.Tests", "Syntax", "Fixtures", "all_productions.tpn");
        Assert.Empty(ParseText(File.ReadAllText(path)).Diagnostics);
    }

    [Theory]
    [InlineData("fizzbuzz.tpn")]
    [InlineData("health.tpn")]
    public void ExamplesParse(string name)
    {
        var path = Path.Combine(RepoPaths.Root, "examples", name);
        var result = ParseText(File.ReadAllText(path));
        Assert.Empty(result.Diagnostics);
    }

    // ---- TP0100 messages ----

    [Theory]
    [InlineData("let = 1\n", "1:5 TP0100: expected name, found '='")]
    [InlineData("let x 1\n", "1:7 TP0100: expected '=', found integer")]
    [InlineData("let x =\n", "1:8 TP0100: expected expression, found newline")]
    [InlineData("let x: = 1\n", "1:8 TP0100: expected type, found '='")]
    [InlineData("if x\n    pass\n", "1:5 TP0100: expected ':', found newline")]
    [InlineData("if x: pass\n", "1:7 TP0100: expected newline, found 'pass'")]
    [InlineData("if x:\npass\n", "2:1 TP0100: expected indented block, found 'pass'")]
    [InlineData("x = 1 2\n", "1:7 TP0100: expected newline, found integer")]
    [InlineData("f(1\n", "2:1 TP0100: expected ')', found newline")]  // the open bracket joins the line ending
    [InlineData("else:\n    pass\n", "1:1 TP0100: expected statement, found 'else'")]
    [InlineData("    x\n", "1:5 TP0100: expected statement, found indent")]
    [InlineData("x = y z\n", "1:7 TP0100: expected newline, found name 'z'")]
    [InlineData("if x is y:\n    pass\n", "1:9 TP0100: expected 'some', 'not' or 'None', found name 'y'")]
    [InlineData("match x:\n    +: pass\n", "2:5 TP0100: expected pattern, found '+'")]
    [InlineData("def f(:\n    pass\n", "1:7 TP0100: expected name, found ':'")]
    [InlineData("enum E:\n    V()\n", "2:7 TP0100: expected name, found ')'")]
    [InlineData("f(", "1:3 TP0100: expected expression, found newline")]
    [InlineData("f(1\nlet x = 2\n", "2:1 TP0100: expected ')', found 'let'")]
    public void ExpectedMessages(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    [Fact]
    public void ConstOnlyAtTopLevel() =>
        Assert.Equal(["2:5 TP0100: expected statement, found 'const'"], Errors("def f():\n    const X = 1\n"));

    [Theory]
    [InlineData("f() = 1\n", "1:1")]
    [InlineData("a + b += 1\n", "1:1")]
    [InlineData("-a = 1\n", "1:1")]
    public void InvalidAssignmentTarget(string text, string position) =>
        Assert.Equal([$"{position} TP0100: cannot assign to this expression"], Errors(text));

    [Theory]
    [InlineData("S(a=1, 2)\n", "1:8")]
    [InlineData("S(1, a=1, 2)\n", "1:11")]
    public void PositionalAfterKeyword(string text, string position) =>
        Assert.Equal([$"{position} TP0100: positional argument follows keyword argument"], Errors(text));

    [Fact]
    public void KeywordAfterPositionalParses() =>
        Assert.Equal(
            "CallExpr(MemberExpr(NameExpr(json), dumps), [Argument(null, NameExpr(v)), Argument(indent, IntLiteral(2))])",
            Expr("json.dumps(v, indent=2)"));

    // ---- cast (M10) ----

    [Theory]
    [InlineData("x as int", "CastExpr(NameExpr(x), NamedType(int))")]
    [InlineData("d[\"n\"] as list[int?]?", "CastExpr(IndexExpr(NameExpr(d), StringLiteral(\"n\")), OptionalType(ListType(OptionalType(NamedType(int)))))")]
    [InlineData("-x as float", "UnaryExpr(Negate, CastExpr(NameExpr(x), NamedType(float)))")]
    [InlineData("a.b() as str ?? \"\"", "BinaryExpr(CastExpr(CallExpr(MemberExpr(NameExpr(a), b), []), NamedType(str)), Coalesce, StringLiteral(\"\"))")]
    [InlineData("x as int? ?? 0", "BinaryExpr(CastExpr(NameExpr(x), OptionalType(NamedType(int))), Coalesce, IntLiteral(0))")]
    public void Casts(string text, string expected) =>
        Assert.Equal(expected, Expr(text));

    // ---- TP0212 ----

    [Fact]
    public void ChainedComparison()
    {
        var result = ParseText("let ok = a < b < c\n");
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TP0212", diagnostic.Code);
        Assert.Equal("chained comparison not supported", diagnostic.Message);
        Assert.Equal("write 'a < b and b < c'", diagnostic.Help);
        Assert.Equal(new TextSpan(15, 1), diagnostic.Span);
    }

    [Fact]
    public void ChainedComparisonReportedOncePerChainAndParsingContinues() =>
        Assert.Equal(
            ["1:7 TP0212: chained comparison not supported", "2:7 TP0100: expected '=', found integer"],
            Errors("a < b == c not in d\nlet y 1\n"));

    // ---- Recovery ----

    [Fact]
    public void ThreeIndependentErrorsAreAllReported() =>
        Assert.Equal(
            ["1:7 TP0100: expected '=', found integer",
             "3:10 TP0100: expected expression, found ','",
             "6:6 TP0100: expected ':', found 'if'"],
            Errors("let x 1\nlet ok = 2\nprint(ok,, 1)\nif ok:\n    pass\nelse if:\n    pass\nprint(ok)\n"));

    [Fact]
    public void OneErrorPerStatement() =>
        Assert.Equal(["1:7 TP0100: expected '=', found integer"], Errors("let x 1 ) ( ]\n"));

    [Fact]
    public void BadBlockHeaderSkipsItsBlock() =>
        Assert.Equal(
            ["1:9 TP0100: expected type, found ')'", "5:8 TP0100: expected expression, found newline"],
            Errors("def f(a:):\n    let = = =\n    if (:\n        ]]]\nlet y =\n"));

    [Fact]
    public void ErrorInNestedBlockRecoversWithinThatBlock()
    {
        var result = ParseText("def f():\n    let = 1\n    let y = 2\nlet z = 3\n");
        Assert.Single(result.Diagnostics);
        Assert.Equal(
            "FuncDef(f, [], [], null, Block([LetStmt(, null, ErrorExpr), LetStmt(y, null, IntLiteral(2))]))\n" +
            "LetStmt(z, null, IntLiteral(3))",
            string.Join("\n", result.Unit.Items.Select(AstPrinter.Print)));
    }

    [Fact]
    public void ErrorOnLastStatementOfBlock() =>
        Assert.Equal(
            ["2:9 TP0100: expected expression, found newline", "4:8 TP0100: expected expression, found newline"],
            Errors("if a:\n    x = \ny = 1\nlet z =\n"));

    [Fact]
    public void ErrorsInEachMatchArmAndStructField() =>
        Assert.Equal(
            ["2:5 TP0100: expected pattern, found '+'", "3:10 TP0100: expected newline, found name 'y'",
             "6:8 TP0100: expected type, found integer"],
            Errors("match x:\n    +: pass\n    _: x y\n    0: pass\nclass S:\n    a: 1\n    b: int\n"));

    // ---- f-string holes ----

    [Theory]
    [InlineData("f\"{a +}\"\n", "1:7 TP0100: expected expression, found '}'")]
    [InlineData("f\"{a b}\"\n", "1:6 TP0100: expected '}', found name 'b'")]
    [InlineData("x = f\"ok {1 2}\"\n", "1:13 TP0100: expected '}', found integer")]
    public void FStringHoleSyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    [Fact]
    public void FStringHoleLexicalErrorsAreReported() =>
        Assert.Equal(
            ["1:4 TP0004: invalid character '$'", "1:11 TP0005: invalid escape sequence '\\q'"],
            Errors("f\"{$} {d[\"\\q\"]}\"\n"));

    [Fact]
    public void FStringHoleErrorCountsAsTheStatementsError() =>
        Assert.Equal(["1:12 TP0100: expected expression, found '+'"], Errors("let a = f\"{+}\" 1 2\n"));

    // ---- type_params and generic types (M8) ----

    [Fact]
    public void GenericFunction() =>
        Assert.Equal(
            "FuncDef(first, [T], [Param(xs, ListType(NamedType(T)))], OptionalType(NamedType(T)), Block([ReturnStmt(NoneLiteral)]))",
            Snapshot("def first[T](xs: list[T]) -> T?:\n    return None\n"));

    [Fact]
    public void GenericStructAndEnum() =>
        Assert.Equal(
            "ClassDef(Pair, [A, B], [], [FieldDef(a, NamedType(A)), FieldDef(b, NamedType(B))], [])\n" +
            "EnumDef(Maybe, [T], [VariantDef(Just, [FieldDef(value, NamedType(T))]), VariantDef(Nothing, [])])",
            Snapshot("class Pair[A, B]:\n    a: A\n    b: B\nenum Maybe[T]:\n    Just(value: T)\n    Nothing\n"));

    [Theory]
    [InlineData("Box[int]", "GenericType(Box, [NamedType(int)])")]
    [InlineData("Pair[str, list[int]]?", "OptionalType(GenericType(Pair, [NamedType(str), ListType(NamedType(int))]))")]
    [InlineData("Box[Box[int?]]", "GenericType(Box, [GenericType(Box, [OptionalType(NamedType(int))])])")]
    public void GenericTypes(string type, string expected) =>
        Assert.Equal($"LetStmt(x, {expected}, NameExpr(y))", Snapshot($"let x: {type} = y\n"));

    [Fact]
    public void ConstrainedTypeParams() =>
        Assert.Equal(
            "FuncDef(total, [TypeParam(N, [NamedType(int), NamedType(float)]), T, TypeParam(K, [NamedType(str), ListType(NamedType(int))])], " +
            "[Param(xs, ListType(NamedType(N)))], NamedType(N), Block([ReturnStmt(IntLiteral(0))]))",
            Snapshot("def total[N: (int, float), T, K: (str, list[int],)](xs: list[N]) -> N:\n    return 0\n"));

    [Theory]
    [InlineData("def f[T: int](x: T):\n    pass\n", "1:10 TP0100: expected '(', found name 'int'")]
    [InlineData("def f[T: (int, float](x: T):\n    pass\n", "1:21 TP0100: expected ')', found ']'")]
    [InlineData("def f[](x: int):\n    pass\n", "1:7 TP0100: expected name, found ']'")]
    [InlineData("let b: Box[] = x\n", "1:12 TP0100: expected type, found ']'")]
    public void TypeParamSyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    // ---- import_stmt and qualified names (M9) ----

    [Fact]
    public void Imports() =>
        Assert.Equal(
            "ImportStmt(geometry, null)\nImportStmt(geometry, g)\n" +
            "FromImportStmt(shapes, [ImportName(area, null), ImportName(Point, P)])",
            Snapshot("import geometry\nimport geometry as g\nfrom shapes import area, Point as P\n"));

    [Theory]
    [InlineData("m.Point", "QualifiedType(m, NamedType(Point))")]
    [InlineData("m.Box[int]?", "OptionalType(QualifiedType(m, GenericType(Box, [NamedType(int)])))")]
    [InlineData("list[m.Point]", "ListType(QualifiedType(m, NamedType(Point)))")]
    public void QualifiedTypes(string type, string expected) =>
        Assert.Equal($"LetStmt(x, {expected}, NameExpr(y))", Snapshot($"let x: {type} = y\n"));

    [Fact]
    public void ModuleQualifiedPattern() =>
        Assert.Equal(
            "MatchStmt(NameExpr(h), [MatchArm(VariantPattern(m, Health, Ok, null), Block([PassStmt])), " +
            "MatchArm(VariantPattern(m, Health, Degraded, [a, b]), Block([PassStmt]))])",
            Snapshot("match h:\n    m.Health.Ok: pass\n    m.Health.Degraded(a, b): pass\n"));

    [Theory]
    [InlineData("from m import *\n", "1:15 TP0100: expected name, found '*'")]
    [InlineData("def f():\n    import m\n", "2:5 TP0100: expected statement, found 'import'")]
    [InlineData("import m.n\n", "1:9 TP0100: expected newline, found '.'")]
    [InlineData("from m import\n", "1:14 TP0100: expected name, found newline")]
    public void ImportSyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));

    // ---- try and raise (M16) ----

    [Fact]
    public void TryWithEveryClause() =>
        Assert.Equal(
            "TryStmt(Block([PassStmt]), [ExceptClause([NameExpr(KeyError)], null, Block([PassStmt])), " +
            "ExceptClause([NameExpr(KeyError), MemberExpr(NameExpr(m), E)], e, Block([RaiseStmt(null)])), " +
            "ExceptClause([], null, Block([PassStmt]))], Block([PassStmt]), Block([PassStmt]))",
            Snapshot("try:\n    pass\nexcept KeyError:\n    pass\nexcept (KeyError, m.E,) as e:\n    raise\n" +
                "except:\n    pass\nelse:\n    pass\nfinally:\n    pass\n"));

    [Fact]
    public void TryFinally() =>
        Assert.Equal(
            "TryStmt(Block([PassStmt]), [], null, Block([PassStmt]))",
            Snapshot("try:\n    pass\nfinally:\n    pass\n"));

    [Fact]
    public void QualifiedPythonClass() =>
        Assert.Equal(
            "TryStmt(Block([PassStmt]), [ExceptClause([MemberExpr(MemberExpr(NameExpr(json), decoder), JSONDecodeError)], e, Block([PassStmt]))], null, null)",
            Snapshot("try:\n    pass\nexcept json.decoder.JSONDecodeError as e:\n    pass\n"));

    [Theory]
    [InlineData("raise\n", "RaiseStmt(null)")]
    [InlineData("raise ValueError(\"x\")\n", "RaiseStmt(CallExpr(NameExpr(ValueError), [Argument(null, StringLiteral(\"x\"))]))")]
    public void Raise(string text, string expected) =>
        Assert.Equal(expected, Snapshot(text));

    [Theory]
    [InlineData("try:\n    pass\nx = 1\n", "3:1 TP0100: expected 'except' or 'finally', found name 'x'")]
    [InlineData("try:\n    pass\nelse:\n    pass\n", "3:1 TP0100: expected 'except' or 'finally', found 'else'")]
    [InlineData("try:\n    pass\nexcept:\n    pass\nexcept E:\n    pass\n", "3:1 TP0100: a bare 'except' must be the last except clause")]
    [InlineData("try:\n    pass\nexcept E as:\n    pass\n", "3:12 TP0100: expected name, found ':'")]
    [InlineData("try:\n    pass\nexcept a.b.c.d:\n    pass\n", "3:13 TP0100: expected ':', found '.'")]
    [InlineData("try:\n    pass\nexcept E(1):\n    pass\n", "3:9 TP0100: expected ':', found '('")]
    [InlineData("raise a, b\n", "1:8 TP0100: expected newline, found ','")]
    public void TrySyntaxErrors(string text, string expected) =>
        Assert.Equal([expected], Errors(text));


}
