using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Syntax;

public sealed record ParseResult(CompilationUnit Unit, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Recursive-descent parser for SPEC §2, one method per grammar production.
/// </summary>
/// <remarks>
/// Error recovery (SPEC §2.1): the first TP0100 in a statement sets <see cref="_recovering"/>.
/// While it is set, no token is consumed, every <see cref="At"/> test fails and further TP0100s
/// are suppressed, so the remaining parse functions unwind quickly with error nodes. The
/// enclosing statement loop then calls <see cref="Synchronize"/> and clears the flag.
/// </remarks>
public sealed class Parser
{
    private readonly SourceText _source;
    private readonly ImmutableArray<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private readonly bool _isHole;
    private int _index;
    private int _previousEnd;
    private bool _recovering;

    private Parser(SourceText source, ImmutableArray<Token> tokens, DiagnosticBag diagnostics, bool isHole)
    {
        _source = source;
        _tokens = tokens;
        _diagnostics = diagnostics;
        _isHole = isHole;
    }

    public static ParseResult Parse(SourceText source, ImmutableArray<Token> tokens)
    {
        var diagnostics = new DiagnosticBag();
        var unit = new Parser(source, tokens, diagnostics, isHole: false).ParseFile();
        return new ParseResult(unit, diagnostics.ToImmutable());
    }

    // ---- Token helpers ----

    private Token Current => _tokens[_index];

    private Token Peek(int offset) => _tokens[Math.Min(_index + offset, _tokens.Length - 1)];

    private bool At(TokenKind kind) => !_recovering && Current.Kind == kind;

    private Token Next()
    {
        var token = Current;
        if (!_recovering)
            Advance();
        return token;
    }

    private void Advance()
    {
        _previousEnd = Current.Span.End;
        if (_index < _tokens.Length - 1)
            _index++;
    }

    private Token Expect(TokenKind kind, string? expected = null)
    {
        if (At(kind))
            return Next();
        ReportExpected(expected ?? TokenFacts.Display(kind));
        return new Token(kind, new TextSpan(Current.Span.Start, 0), "");
    }

    private Identifier ExpectIdentifier()
    {
        var token = Expect(TokenKind.Name);
        return new Identifier(token.Text, token.Span);
    }

    private TextSpan SpanFrom(int start) => new(start, Math.Max(0, _previousEnd - start));

    private TextSpan Here => new(Current.Span.Start, 0);

    // ---- Errors and recovery ----

    private void ReportExpected(string expected)
    {
        var found = Current.Kind == TokenKind.EndOfFile && _isHole ? "'}'" : TokenFacts.Describe(Current);
        ReportSyntaxError(bag => bag.ReportExpected(Current.Span, expected, found));
    }

    private void ReportSyntaxError(Action<DiagnosticBag> report)
    {
        if (_recovering)
            return;
        _recovering = true;
        report(_diagnostics);
    }

    /// <summary>
    /// Skips to just after the next NEWLINE at the current block depth, plus any block indented
    /// under it, or to a DEDENT that closes the current block (SPEC §2.1).
    /// </summary>
    private void Synchronize()
    {
        var depth = 0;
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.EndOfFile:
                    return;
                case TokenKind.Dedent when depth == 0:
                    return;
                case TokenKind.Dedent:
                    depth--;
                    Advance();
                    if (depth == 0)
                        return;
                    break;
                case TokenKind.Indent:
                    depth++;
                    Advance();
                    break;
                case TokenKind.Newline:
                    Advance();
                    if (depth == 0 && Current.Kind != TokenKind.Indent)
                        return;
                    break;
                default:
                    Advance();
                    break;
            }
        }
    }

    private void EndRecovery()
    {
        if (!_recovering)
            return;
        Synchronize();
        _recovering = false;
    }

    // ---- Top level ----

    // file = { NEWLINE | top_item } EOF
    private CompilationUnit ParseFile()
    {
        var items = ImmutableArray.CreateBuilder<TopItem>();
        while (Current.Kind != TokenKind.EndOfFile)
        {
            if (Current.Kind == TokenKind.Newline)
            {
                Advance();
                continue;
            }
            items.Add(ParseTopItem());
            EndRecovery();
        }
        return new CompilationUnit(items.ToImmutable(), new TextSpan(0, _source.Length));
    }

    // top_item = func_def | struct_def | enum_def | const_def | stmt
    private TopItem ParseTopItem() => Current.Kind switch
    {
        TokenKind.DefKeyword => ParseFuncDef(),
        TokenKind.StructKeyword => ParseStructDef(),
        TokenKind.EnumKeyword => ParseEnumDef(),
        TokenKind.ConstKeyword => ParseConstDef(),
        _ => ParseStmt(),
    };

    // const_def = "const" NAME [ ":" type ] "=" expr NEWLINE
    private ConstDef ParseConstDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var type = ParseOptionalAnnotation();
        Expect(TokenKind.Equals);
        var value = ParseExpr();
        var span = SpanFrom(start);
        Expect(TokenKind.Newline);
        return new ConstDef(name, type, value, span);
    }

    // func_def = "def" NAME "(" [ params ] ")" [ "->" type ] ":" block
    private FuncDef ParseFuncDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        Expect(TokenKind.OpenParen);
        var parameters = ParseParams();
        Expect(TokenKind.CloseParen);

        TypeSyntax? returnType = null;
        if (At(TokenKind.Arrow))
        {
            Next();
            returnType = ParseType();
        }

        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new FuncDef(name, parameters, returnType, body, SpanFrom(start));
    }

    // params = param { "," param } [ "," ]
    private ImmutableArray<Param> ParseParams() => ParseCommaList(TokenKind.CloseParen, ParseParam);

    // param = NAME ":" type
    private Param ParseParam()
    {
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var type = ParseType();
        return new Param(name, type, SpanFrom(name.Span.Start));
    }

    // struct_def = "struct" NAME ":" NEWLINE INDENT struct_member { struct_member } DEDENT
    private StructDef ParseStructDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var fields = ParseIndentedBody(ParseStructMember);
        return new StructDef(name, fields, SpanFrom(start));
    }

    // struct_member = NAME ":" type NEWLINE
    private FieldDef ParseStructMember()
    {
        var field = ParseNamedField();
        Expect(TokenKind.Newline);
        return field;
    }

    // enum_def = "enum" NAME ":" NEWLINE INDENT variant { variant } DEDENT
    private EnumDef ParseEnumDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var variants = ParseIndentedBody(ParseVariant);
        return new EnumDef(name, variants, SpanFrom(start));
    }

    // variant = NAME [ "(" named_fields ")" ] NEWLINE
    private VariantDef ParseVariant()
    {
        var name = ExpectIdentifier();
        var fields = ImmutableArray<FieldDef>.Empty;
        if (At(TokenKind.OpenParen))
        {
            Next();
            fields = ParseNamedFields();
            Expect(TokenKind.CloseParen);
        }
        var span = SpanFrom(name.Span.Start);
        Expect(TokenKind.Newline);
        return new VariantDef(name, fields, span);
    }

    // named_fields = NAME ":" type { "," NAME ":" type } [ "," ]
    private ImmutableArray<FieldDef> ParseNamedFields()
    {
        var fields = ParseCommaList(TokenKind.CloseParen, ParseNamedField);
        if (fields.IsEmpty)
            ReportExpected(TokenFacts.Display(TokenKind.Name));
        return fields;
    }

    private FieldDef ParseNamedField()
    {
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var type = ParseType();
        return new FieldDef(name, type, SpanFrom(name.Span.Start));
    }

    // block = NEWLINE INDENT stmt { stmt } DEDENT
    private Block ParseBlock()
    {
        var start = Current.Span.Start;
        var statements = ParseIndentedBody(ParseStmt);
        return new Block(statements, SpanFrom(start));
    }

    /// <summary>NEWLINE INDENT item { item } DEDENT, recovering after each failed item.</summary>
    private ImmutableArray<T> ParseIndentedBody<T>(Func<T> parseItem)
    {
        Expect(TokenKind.Newline);
        Expect(TokenKind.Indent, "indented block");
        if (_recovering)
            return [];

        var items = ImmutableArray.CreateBuilder<T>();
        while (Current.Kind is not (TokenKind.Dedent or TokenKind.EndOfFile))
        {
            items.Add(parseItem());
            EndRecovery();
        }
        Expect(TokenKind.Dedent);
        return items.ToImmutable();
    }

    /// <summary>
    /// Zero or more items separated by commas, with an optional trailing comma, stopping before
    /// <paramref name="close"/>. The caller consumes <paramref name="close"/>.
    /// </summary>
    private ImmutableArray<T> ParseCommaList<T>(TokenKind close, Func<T> parseItem)
    {
        var items = ImmutableArray.CreateBuilder<T>();
        while (!_recovering && Current.Kind != close)
        {
            items.Add(parseItem());
            if (!At(TokenKind.Comma))
                break;
            Next();
        }
        return items.ToImmutable();
    }

    // ---- Statements ----

    // stmt = simple_stmt NEWLINE | compound_stmt
    private Stmt ParseStmt()
    {
        switch (Current.Kind)
        {
            case TokenKind.IfKeyword:
                return ParseIfStmt();
            case TokenKind.WhileKeyword:
                return ParseWhileStmt();
            case TokenKind.ForKeyword:
                return ParseForStmt();
            case TokenKind.MatchKeyword:
                return ParseMatchStmt();
            default:
                var stmt = ParseSimpleStmt();
                Expect(TokenKind.Newline);
                return stmt;
        }
    }

    // simple_stmt = let_stmt | var_stmt | assign_stmt | return_stmt | "pass" | "break" | "continue" | expr
    private Stmt ParseSimpleStmt()
    {
        switch (Current.Kind)
        {
            case TokenKind.LetKeyword:
                return ParseLetStmt();
            case TokenKind.VarKeyword:
                return ParseVarStmt();
            case TokenKind.ReturnKeyword:
                return ParseReturnStmt();
            case TokenKind.PassKeyword:
                return new PassStmt(Next().Span);
            case TokenKind.BreakKeyword:
                return new BreakStmt(Next().Span);
            case TokenKind.ContinueKeyword:
                return new ContinueStmt(Next().Span);
        }

        if (!CanStartExpression(Current.Kind))
        {
            ReportExpected("statement");
            return new ExprStmt(new ErrorExpr(Here), Here);
        }
        return ParseAssignStmt();
    }

    // let_stmt = "let" NAME [ ":" type ] "=" expr
    private LetStmt ParseLetStmt()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var type = ParseOptionalAnnotation();
        Expect(TokenKind.Equals);
        var value = ParseExpr();
        return new LetStmt(name, type, value, SpanFrom(start));
    }

    // var_stmt = "var" NAME [ ":" type ] "=" expr
    private VarStmt ParseVarStmt()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var type = ParseOptionalAnnotation();
        Expect(TokenKind.Equals);
        var value = ParseExpr();
        return new VarStmt(name, type, value, SpanFrom(start));
    }

    private TypeSyntax? ParseOptionalAnnotation()
    {
        if (!At(TokenKind.Colon))
            return null;
        Next();
        return ParseType();
    }

    // assign_stmt = target ( "=" | "+=" | "-=" | "*=" | "/=" ) expr ;  also the bare `expr` statement
    private Stmt ParseAssignStmt()
    {
        var start = Current.Span.Start;
        var target = ParseExpr();

        AssignOp? op = _recovering ? null : Current.Kind switch
        {
            TokenKind.Equals => AssignOp.Assign,
            TokenKind.PlusEquals => AssignOp.Add,
            TokenKind.MinusEquals => AssignOp.Subtract,
            TokenKind.StarEquals => AssignOp.Multiply,
            TokenKind.SlashEquals => AssignOp.Divide,
            _ => null,
        };
        if (op is null)
            return new ExprStmt(target, SpanFrom(start));

        if (!IsTarget(target))
            ReportSyntaxError(bag => bag.ReportInvalidAssignmentTarget(target.Span));
        Next();
        var value = ParseExpr();
        return new AssignStmt(target, op.Value, value, SpanFrom(start));
    }

    // target = NAME | postfix "." NAME | postfix "[" expr "]"
    private static bool IsTarget(Expr expr) => expr is NameExpr or MemberExpr or IndexExpr;

    // return_stmt = "return" [ expr ]
    private ReturnStmt ParseReturnStmt()
    {
        var start = Next().Span.Start;
        var value = !_recovering && CanStartExpression(Current.Kind) ? ParseExpr() : null;
        return new ReturnStmt(value, SpanFrom(start));
    }

    // if_stmt = "if" condition ":" block { "elif" condition ":" block } [ "else" ":" block ]
    private IfStmt ParseIfStmt()
    {
        var start = Current.Span.Start;
        var clauses = ImmutableArray.CreateBuilder<IfClause>();
        clauses.Add(ParseIfClause());
        while (At(TokenKind.ElifKeyword))
            clauses.Add(ParseIfClause());

        Block? elseBlock = null;
        if (At(TokenKind.ElseKeyword))
        {
            Next();
            Expect(TokenKind.Colon);
            elseBlock = ParseBlock();
        }
        return new IfStmt(clauses.ToImmutable(), elseBlock, SpanFrom(start));
    }

    private IfClause ParseIfClause()
    {
        var start = Next().Span.Start;
        var condition = ParseCondition();
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new IfClause(condition, body, SpanFrom(start));
    }

    // condition = expr "is" "some" NAME | expr "is" "none" | expr
    private Condition ParseCondition()
    {
        var start = Current.Span.Start;
        var expr = ParseExpr();
        if (!At(TokenKind.IsKeyword))
            return new ExprCondition(expr, SpanFrom(start));

        Next();
        if (At(TokenKind.NoneKeyword))
        {
            Next();
            return new IsNoneCondition(expr, SpanFrom(start));
        }
        Expect(TokenKind.SomeKeyword);
        var binding = ExpectIdentifier();
        return new IsSomeCondition(expr, binding, SpanFrom(start));
    }

    // while_stmt = "while" condition ":" block
    private WhileStmt ParseWhileStmt()
    {
        var start = Next().Span.Start;
        var condition = ParseCondition();
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new WhileStmt(condition, body, SpanFrom(start));
    }

    // for_stmt = "for" NAME "in" expr ":" block
    private ForStmt ParseForStmt()
    {
        var start = Next().Span.Start;
        var variable = ExpectIdentifier();
        Expect(TokenKind.InKeyword);
        var iterable = ParseExpr();
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new ForStmt(variable, iterable, body, SpanFrom(start));
    }

    // match_stmt = "match" expr ":" NEWLINE INDENT arm { arm } DEDENT
    private MatchStmt ParseMatchStmt()
    {
        var start = Next().Span.Start;
        var scrutinee = ParseExpr();
        Expect(TokenKind.Colon);
        var arms = ParseIndentedBody(ParseArm);
        return new MatchStmt(scrutinee, arms, SpanFrom(start));
    }

    // arm = pattern ":" ( simple_stmt NEWLINE | block )
    private MatchArm ParseArm()
    {
        var start = Current.Span.Start;
        var pattern = ParsePattern();
        Expect(TokenKind.Colon);

        Block body;
        if (At(TokenKind.Newline))
        {
            body = ParseBlock();
        }
        else
        {
            var stmt = ParseSimpleStmt();
            body = new Block([stmt], stmt.Span);
            Expect(TokenKind.Newline);
        }
        return new MatchArm(pattern, body, new TextSpan(start, Math.Max(0, body.Span.End - start)));
    }

    // pattern = "_" | [ "-" ] INT | STRING | "True" | "False" | qual_name [ "(" [ NAME { "," NAME } ] ")" ]
    private Pattern ParsePattern()
    {
        var start = Current.Span.Start;
        if (At(TokenKind.Name) && Current.Text == "_")
            return new WildcardPattern(Next().Span);
        if (At(TokenKind.Minus) && Peek(1).Kind == TokenKind.Int)
        {
            Next();
            var value = ParseIntValue(Next());
            return new IntPattern(-value, SpanFrom(start));
        }
        if (At(TokenKind.Int))
        {
            var token = Next();
            return new IntPattern(ParseIntValue(token), token.Span);
        }
        if (At(TokenKind.String))
        {
            var token = Next();
            return new StringPattern(token.Value!, token.Span);
        }
        if (At(TokenKind.TrueKeyword) || At(TokenKind.FalseKeyword))
        {
            var token = Next();
            return new BoolPattern(token.Kind == TokenKind.TrueKeyword, token.Span);
        }
        if (At(TokenKind.Name))
            return ParseVariantPattern();

        ReportExpected("pattern");
        return new ErrorPattern(Here);
    }

    // qual_name [ "(" [ NAME { "," NAME } ] ")" ]  where  qual_name = NAME [ "." NAME ]
    private VariantPattern ParseVariantPattern()
    {
        var first = ExpectIdentifier();
        Identifier? enumName = null;
        var variant = first;
        if (At(TokenKind.Dot))
        {
            Next();
            enumName = first;
            variant = ExpectIdentifier();
        }

        ImmutableArray<Identifier>? bindings = null;
        if (At(TokenKind.OpenParen))
        {
            Next();
            var names = ImmutableArray.CreateBuilder<Identifier>();
            if (!At(TokenKind.CloseParen))
            {
                names.Add(ExpectIdentifier());
                while (At(TokenKind.Comma))
                {
                    Next();
                    names.Add(ExpectIdentifier());
                }
            }
            Expect(TokenKind.CloseParen);
            bindings = names.ToImmutable();
        }
        return new VariantPattern(enumName, variant, bindings, SpanFrom(first.Span.Start));
    }

    // ---- Types ----

    // type = base_type { "?" }   — a `??` token counts as two `?` (SPEC §2)
    private TypeSyntax ParseType()
    {
        var start = Current.Span.Start;
        var type = ParseBaseType();
        while (true)
        {
            if (At(TokenKind.Question))
            {
                Next();
                type = new OptionalType(type, SpanFrom(start));
            }
            else if (At(TokenKind.QuestionQuestion))
            {
                var question = Next();
                type = new OptionalType(type, new TextSpan(start, question.Span.Start + 1 - start));
                type = new OptionalType(type, SpanFrom(start));
            }
            else
            {
                return type;
            }
        }
    }

    // base_type = "int" | "float" | "bool" | "str" | "py" | "list" "[" type "]" | "dict" "[" type "," type "]" | NAME
    private TypeSyntax ParseBaseType()
    {
        var start = Current.Span.Start;
        if (!At(TokenKind.Name))
        {
            ReportExpected("type");
            return new ErrorType(Here);
        }

        var name = ExpectIdentifier();
        switch (name.Text)
        {
            case "list":
            {
                Expect(TokenKind.OpenBracket);
                var element = ParseType();
                Expect(TokenKind.CloseBracket);
                return new ListType(element, SpanFrom(start));
            }
            case "dict":
            {
                Expect(TokenKind.OpenBracket);
                var key = ParseType();
                Expect(TokenKind.Comma);
                var value = ParseType();
                Expect(TokenKind.CloseBracket);
                return new DictType(key, value, SpanFrom(start));
            }
            default:
                return new NamedType(name, name.Span);
        }
    }

    // ---- Expressions ----

    // expr = coalesce
    private Expr ParseExpr() => ParseCoalesce();

    // coalesce = or_expr [ "??" coalesce ]
    private Expr ParseCoalesce()
    {
        var left = ParseOrExpr();
        if (!At(TokenKind.QuestionQuestion))
            return left;
        Next();
        var right = ParseCoalesce();
        return Binary(left, BinaryOp.Coalesce, right);
    }

    // or_expr = and_expr { "or" and_expr }
    private Expr ParseOrExpr()
    {
        var left = ParseAndExpr();
        while (At(TokenKind.OrKeyword))
        {
            Next();
            left = Binary(left, BinaryOp.Or, ParseAndExpr());
        }
        return left;
    }

    // and_expr = not_expr { "and" not_expr }
    private Expr ParseAndExpr()
    {
        var left = ParseNotExpr();
        while (At(TokenKind.AndKeyword))
        {
            Next();
            left = Binary(left, BinaryOp.And, ParseNotExpr());
        }
        return left;
    }

    // not_expr = "not" not_expr | comparison
    private Expr ParseNotExpr()
    {
        if (!At(TokenKind.NotKeyword))
            return ParseComparison();
        var start = Next().Span.Start;
        var operand = ParseNotExpr();
        return new UnaryExpr(UnaryOp.Not, operand, SpanFrom(start));
    }

    // comparison = additive [ comp_op additive ]   — a second comp_op is TP0212 (SPEC §2.1)
    private Expr ParseComparison()
    {
        var left = ParseAdditive();
        if (!TryParseCompOp(out var op, out _))
            return left;
        left = Binary(left, op, ParseAdditive());

        var reported = false;
        while (TryParseCompOp(out op, out var opSpan))
        {
            if (!reported)
                _diagnostics.ReportChainedComparison(opSpan);
            reported = true;
            left = Binary(left, op, ParseAdditive());
        }
        return left;
    }

    // comp_op = "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" | "not" "in"
    private bool TryParseCompOp(out BinaryOp op, out TextSpan span)
    {
        span = Current.Span;
        if (At(TokenKind.NotKeyword) && Peek(1).Kind == TokenKind.InKeyword)
        {
            Next();
            Next();
            op = BinaryOp.NotIn;
            span = SpanFrom(span.Start);
            return true;
        }

        BinaryOp? found = _recovering ? null : Current.Kind switch
        {
            TokenKind.EqualsEquals => BinaryOp.Equal,
            TokenKind.BangEquals => BinaryOp.NotEqual,
            TokenKind.Less => BinaryOp.Less,
            TokenKind.LessEquals => BinaryOp.LessEqual,
            TokenKind.Greater => BinaryOp.Greater,
            TokenKind.GreaterEquals => BinaryOp.GreaterEqual,
            TokenKind.InKeyword => BinaryOp.In,
            _ => null,
        };
        op = found.GetValueOrDefault();
        if (found is null)
            return false;
        Next();
        return true;
    }

    // additive = term { ( "+" | "-" ) term }
    private Expr ParseAdditive()
    {
        var left = ParseTerm();
        while (true)
        {
            BinaryOp op;
            if (At(TokenKind.Plus))
                op = BinaryOp.Add;
            else if (At(TokenKind.Minus))
                op = BinaryOp.Subtract;
            else
                return left;
            Next();
            left = Binary(left, op, ParseTerm());
        }
    }

    // term = unary { ( "*" | "/" | "//" | "%" ) unary }
    private Expr ParseTerm()
    {
        var left = ParseUnary();
        while (true)
        {
            BinaryOp op;
            if (At(TokenKind.Star))
                op = BinaryOp.Multiply;
            else if (At(TokenKind.Slash))
                op = BinaryOp.Divide;
            else if (At(TokenKind.SlashSlash))
                op = BinaryOp.FloorDivide;
            else if (At(TokenKind.Percent))
                op = BinaryOp.Modulo;
            else
                return left;
            Next();
            left = Binary(left, op, ParseUnary());
        }
    }

    // unary = "-" unary | cast   — `as` is M10, so cast is just postfix for now
    private Expr ParseUnary()
    {
        if (!At(TokenKind.Minus))
            return ParsePostfix();
        var start = Next().Span.Start;
        var operand = ParseUnary();
        return new UnaryExpr(UnaryOp.Negate, operand, SpanFrom(start));
    }

    // postfix = atom { call | "." NAME | "[" expr "]" }
    private Expr ParsePostfix()
    {
        var expr = ParseAtom();
        var start = expr.Span.Start;
        while (true)
        {
            if (At(TokenKind.OpenParen))
            {
                var arguments = ParseCall();
                expr = new CallExpr(expr, arguments, SpanFrom(start));
            }
            else if (At(TokenKind.Dot))
            {
                Next();
                var member = ExpectIdentifier();
                expr = new MemberExpr(expr, member, SpanFrom(start));
            }
            else if (At(TokenKind.OpenBracket))
            {
                Next();
                var index = ParseExpr();
                Expect(TokenKind.CloseBracket);
                expr = new IndexExpr(expr, index, SpanFrom(start));
            }
            else
            {
                return expr;
            }
        }
    }

    // call = "(" [ arg { "," arg } [ "," ] ] ")"
    private ImmutableArray<Argument> ParseCall()
    {
        Next();
        var arguments = ParseCommaList(TokenKind.CloseParen, ParseArg);
        Expect(TokenKind.CloseParen);

        if (arguments.Length > 0)
        {
            var keyword = arguments[0].Name is not null;
            var mismatch = arguments.FirstOrDefault(a => a.Name is not null != keyword);
            if (mismatch is not null)
                ReportSyntaxError(bag => bag.ReportMixedArguments(mismatch.Span));
        }
        return arguments;
    }

    // arg = expr | NAME "=" expr
    private Argument ParseArg()
    {
        var start = Current.Span.Start;
        Identifier? name = null;
        if (At(TokenKind.Name) && Peek(1).Kind == TokenKind.Equals)
        {
            name = ExpectIdentifier();
            Next();
        }
        var value = ParseExpr();
        return new Argument(name, value, SpanFrom(start));
    }

    // atom = INT | FLOAT | STRING | FSTRING | "True" | "False" | "none" | NAME | "self"
    //      | "(" expr ")" | list literal | dict literal
    private Expr ParseAtom()
    {
        if (_recovering)
            return new ErrorExpr(Here);

        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Int:
                Next();
                return new IntLiteral(ParseIntValue(token), token.Span);
            case TokenKind.Float:
                Next();
                return new FloatLiteral(double.Parse(token.Text.Replace("_", ""), CultureInfo.InvariantCulture), token.Span);
            case TokenKind.String:
                Next();
                return new StringLiteral(token.Value!, token.Span);
            case TokenKind.FString:
                Next();
                return ParseFString(token);
            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
                Next();
                return new BoolLiteral(token.Kind == TokenKind.TrueKeyword, token.Span);
            case TokenKind.NoneKeyword:
                Next();
                return new NoneLiteral(token.Span);
            case TokenKind.Name:
                Next();
                return new NameExpr(token.Text, token.Span);
            case TokenKind.OpenParen:
            {
                Next();
                var inner = ParseExpr();
                Expect(TokenKind.CloseParen);
                return inner;
            }
            case TokenKind.OpenBracket:
            {
                Next();
                var elements = ParseCommaList(TokenKind.CloseBracket, ParseExpr);
                Expect(TokenKind.CloseBracket);
                return new ListExpr(elements, SpanFrom(token.Span.Start));
            }
            case TokenKind.OpenBrace:
            {
                Next();
                var entries = ParseCommaList(TokenKind.CloseBrace, ParseDictEntry);
                Expect(TokenKind.CloseBrace);
                return new DictExpr(entries, SpanFrom(token.Span.Start));
            }
            default:
                ReportExpected("expression");
                return new ErrorExpr(Here);
        }
    }

    private DictEntry ParseDictEntry()
    {
        var key = ParseExpr();
        Expect(TokenKind.Colon);
        var value = ParseExpr();
        return new DictEntry(key, value, SpanFrom(key.Span.Start));
    }

    private FStringExpr ParseFString(Token token)
    {
        var segments = ImmutableArray.CreateBuilder<FStringSegment>();
        foreach (var part in token.Parts)
        {
            segments.Add(part switch
            {
                FStringText text => new FStringTextSegment(text.Value, text.Span),
                FStringHole hole => new FStringHoleSegment(ParseHole(hole.Span), hole.Span),
                _ => throw new InvalidOperationException($"unexpected f-string part {part}"),
            });
        }
        return new FStringExpr(segments.ToImmutable(), token.Span);
    }

    /// <summary>Re-lexes a hole and parses it as exactly one expression (SPEC §2).</summary>
    private Expr ParseHole(TextSpan span)
    {
        var lexed = Lexer.LexHole(_source, span);
        if (lexed.Diagnostics.Length > 0)
        {
            _diagnostics.AddRange(lexed.Diagnostics);
            _recovering = true;
            return new ErrorExpr(span);
        }

        var hole = new Parser(_source, lexed.Tokens, _diagnostics, isHole: true);
        var expr = hole.ParseExpr();
        if (!hole._recovering && hole.Current.Kind != TokenKind.EndOfFile)
            hole.ReportExpected("'}'");
        if (hole._recovering)
            _recovering = true;
        return expr;
    }

    private static BigInteger ParseIntValue(Token token) =>
        BigInteger.Parse(token.Text.Replace("_", ""), CultureInfo.InvariantCulture);

    private BinaryExpr Binary(Expr left, BinaryOp op, Expr right) =>
        new(left, op, right, SpanFrom(left.Span.Start));

    private static bool CanStartExpression(TokenKind kind) => kind is
        TokenKind.Int or TokenKind.Float or TokenKind.String or TokenKind.FString
        or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NoneKeyword
        or TokenKind.Name or TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace
        or TokenKind.Minus or TokenKind.NotKeyword;
}
