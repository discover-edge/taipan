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

    // top_item = import_stmt | func_def | struct_def | enum_def | const_def | stmt
    private TopItem ParseTopItem() => Current.Kind switch
    {
        TokenKind.ImportKeyword => ParseImportStmt(),
        TokenKind.FromKeyword => ParseFromImportStmt(),
        TokenKind.DefKeyword => ParseFuncDef(),
        TokenKind.StructKeyword => ParseStructDef(),
        TokenKind.EnumKeyword => ParseEnumDef(),
        TokenKind.ConstKeyword => ParseConstDef(),
        _ => ParseStmt(),
    };

    // import_stmt = "import" NAME [ "as" NAME ] NEWLINE
    private ImportStmt ParseImportStmt()
    {
        var start = Next().Span.Start;
        var module = ExpectIdentifier();
        var alias = ParseImportAlias();
        var span = SpanFrom(start);
        Expect(TokenKind.Newline);
        return new ImportStmt(module, alias, span);
    }

    // import_stmt = "from" NAME "import" import_name { "," import_name } NEWLINE
    private FromImportStmt ParseFromImportStmt()
    {
        var start = Next().Span.Start;
        var module = ExpectIdentifier();
        Expect(TokenKind.ImportKeyword);
        var names = ImmutableArray.CreateBuilder<ImportName>();
        names.Add(ParseImportName());
        while (At(TokenKind.Comma))
        {
            Next();
            names.Add(ParseImportName());
        }
        var span = SpanFrom(start);
        Expect(TokenKind.Newline);
        return new FromImportStmt(module, names.ToImmutable(), span);
    }

    // import_name = NAME [ "as" NAME ]
    private ImportName ParseImportName()
    {
        var name = ExpectIdentifier();
        var alias = ParseImportAlias();
        return new ImportName(name, alias, SpanFrom(name.Span.Start));
    }

    private Identifier? ParseImportAlias()
    {
        if (!At(TokenKind.AsKeyword))
            return null;
        Next();
        return ExpectIdentifier();
    }

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

    // func_def = "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block
    private FuncDef ParseFuncDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var typeParams = ParseTypeParams();
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
        return new FuncDef(name, typeParams, parameters, returnType, body, SpanFrom(start));
    }

    // type_params = "[" type_param { "," type_param } "]"
    private ImmutableArray<TypeParam> ParseTypeParams()
    {
        if (!At(TokenKind.OpenBracket))
            return [];
        Next();
        var parameters = ImmutableArray.CreateBuilder<TypeParam>();
        parameters.Add(ParseTypeParam());
        while (At(TokenKind.Comma))
        {
            Next();
            parameters.Add(ParseTypeParam());
        }
        Expect(TokenKind.CloseBracket);
        return parameters.ToImmutable();
    }

    // type_param = NAME [ ":" "(" type { "," type } [ "," ] ")" ]
    private TypeParam ParseTypeParam()
    {
        var name = ExpectIdentifier();
        var constraints = ImmutableArray<TypeSyntax>.Empty;
        if (At(TokenKind.Colon))
        {
            Next();
            Expect(TokenKind.OpenParen);
            constraints = ParseCommaList(TokenKind.CloseParen, ParseType);
            Expect(TokenKind.CloseParen);
        }
        return new TypeParam(name, constraints, SpanFrom(name.Span.Start));
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

    // struct_def = "struct" NAME [ type_params ] ":" NEWLINE INDENT struct_member { struct_member } DEDENT
    private StructDef ParseStructDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var typeParams = ParseTypeParams();
        Expect(TokenKind.Colon);
        var members = ParseIndentedBody(ParseStructMember);
        return new StructDef(name, typeParams, [.. members.OfType<FieldDef>()], [.. members.OfType<MethodDef>()], SpanFrom(start));
    }

    // struct_member = NAME ":" type NEWLINE | method_def
    private Node ParseStructMember()
    {
        if (At(TokenKind.DefKeyword))
            return ParseMethodDef();
        var field = ParseNamedField();
        Expect(TokenKind.Newline);
        return field;
    }

    // method_def = "def" NAME "(" "self" [ "," params ] ")" [ "->" type ] ":" block
    private MethodDef ParseMethodDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        Expect(TokenKind.OpenParen);

        var self = Here;
        if (At(TokenKind.Name) && Current.Text == "self")
            self = Next().Span;
        else
            ReportExpected("'self'");

        var parameters = ImmutableArray<Param>.Empty;
        if (At(TokenKind.Comma))
        {
            Next();
            parameters = ParseParams();
        }
        Expect(TokenKind.CloseParen);

        TypeSyntax? returnType = null;
        if (At(TokenKind.Arrow))
        {
            Next();
            returnType = ParseType();
        }

        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new MethodDef(name, self, parameters, returnType, body, SpanFrom(start));
    }

    // enum_def = "enum" NAME [ type_params ] ":" NEWLINE INDENT variant { variant } DEDENT
    private EnumDef ParseEnumDef()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var typeParams = ParseTypeParams();
        Expect(TokenKind.Colon);
        var variants = ParseIndentedBody(ParseVariant);
        return new EnumDef(name, typeParams, variants, SpanFrom(start));
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

    // stmt = simple_stmt NEWLINE | compound_stmt, where a func_def in a block is a nested function
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
            case TokenKind.DefKeyword:
                var start = Current.Span.Start;
                return new FuncDefStmt(ParseFuncDef(), SpanFrom(start));
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

    // qual_name [ "(" [ NAME { "," NAME } ] ")" ]  where  qual_name = NAME [ "." NAME [ "." NAME ] ]
    private VariantPattern ParseVariantPattern()
    {
        var first = ExpectIdentifier();
        var path = new List<Identifier> { first };
        while (At(TokenKind.Dot) && path.Count < 3)
        {
            Next();
            path.Add(ExpectIdentifier());
        }
        var module = path.Count == 3 ? path[0] : null;
        var enumName = path.Count >= 2 ? path[^2] : null;
        var variant = path[^1];

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
        return new VariantPattern(module, enumName, variant, bindings, SpanFrom(first.Span.Start));
    }

    // ---- Types ----

    // type = base_type { "?" }   — a `??` token counts as two `?` (SPEC §2)
    private TypeSyntax ParseType() => ParseType(splitDoubleQuestion: true);

    private TypeSyntax ParseType(bool splitDoubleQuestion)
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
            else if (splitDoubleQuestion && At(TokenKind.QuestionQuestion))
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

    // base_type = "int" | "float" | "bool" | "str" | "py" | "list" "[" type "]" | "dict" "[" type "," type "]"
    //           | "Callable" "[" "[" [ type { "," type } ] "]" "," ( type | "none" ) "]"
    //           | NAME [ "." NAME ] [ "[" type { "," type } "]" ]
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
            // A bare `Callable` is a NamedType, which the binder reports (TP0209).
            case "Callable" when At(TokenKind.OpenBracket):
            {
                Next();
                Expect(TokenKind.OpenBracket);
                var parameters = ParseCommaList(TokenKind.CloseBracket, ParseType);
                Expect(TokenKind.CloseBracket);
                Expect(TokenKind.Comma);
                TypeSyntax? result = null;
                if (At(TokenKind.NoneKeyword))
                    Next();
                else
                    result = ParseType();
                Expect(TokenKind.CloseBracket);
                return new CallableType(parameters, result, SpanFrom(start));
            }
            default:
                if (At(TokenKind.Dot))
                {
                    // module.Type (SPEC §4.7)
                    Next();
                    var inner = ParseTypeArguments(ExpectIdentifier());
                    return new QualifiedType(name, inner, SpanFrom(start));
                }
                return ParseTypeArguments(name);
        }
    }

    // NAME [ "[" type { "," type } "]" ]
    private TypeSyntax ParseTypeArguments(Identifier name)
    {
        if (!At(TokenKind.OpenBracket))
            return new NamedType(name, name.Span);
        Next();
        var arguments = ImmutableArray.CreateBuilder<TypeSyntax>();
        arguments.Add(ParseType());
        while (At(TokenKind.Comma))
        {
            Next();
            arguments.Add(ParseType());
        }
        Expect(TokenKind.CloseBracket);
        return new GenericType(name, arguments.ToImmutable(), SpanFrom(name.Span.Start));
    }

    // ---- Expressions ----

    // expr = lambda_expr | coalesce
    private Expr ParseExpr() => At(TokenKind.LambdaKeyword) ? ParseLambdaExpr() : ParseCoalesce();

    // lambda_expr = "lambda" [ lambda_param { "," lambda_param } ] ":" expr ; lambda_param = NAME | "_"
    private Expr ParseLambdaExpr()
    {
        var start = Next().Span.Start;
        var parameters = ImmutableArray.CreateBuilder<Identifier>();
        while (At(TokenKind.Name))
        {
            parameters.Add(ExpectIdentifier());
            if (!At(TokenKind.Comma))
                break;
            Next();
        }
        Expect(TokenKind.Colon);
        var body = ParseExpr();
        return new LambdaExpr(parameters.ToImmutable(), body, SpanFrom(start));
    }

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

    // unary = "-" unary | cast
    private Expr ParseUnary()
    {
        if (!At(TokenKind.Minus))
            return ParseCast();
        var start = Next().Span.Start;
        var operand = ParseUnary();
        return new UnaryExpr(UnaryOp.Negate, operand, SpanFrom(start));
    }

    // cast = postfix [ "as" type ]
    private Expr ParseCast()
    {
        var value = ParsePostfix();
        if (!At(TokenKind.AsKeyword))
            return value;
        Next();
        // In a cast, `??` after the type is the coalescing operator: `x as int? ?? 0` (SPEC §2).
        var type = ParseType(splitDoubleQuestion: false);
        return new CastExpr(value, type, SpanFrom(value.Span.Start));
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

        // A positional argument after a keyword argument is never valid. Positional then keyword
        // is valid only in calls to py values, which the binder checks (SPEC §2, §10.2).
        var firstKeyword = arguments.IndexOf(arguments.FirstOrDefault(a => a.Name is not null)!);
        if (firstKeyword >= 0 && arguments.Skip(firstKeyword).FirstOrDefault(a => a.Name is null) is { } positional)
            ReportSyntaxError(bag => bag.ReportMixedArguments(positional.Span));
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
        or TokenKind.Minus or TokenKind.NotKeyword or TokenKind.LambdaKeyword;
}
