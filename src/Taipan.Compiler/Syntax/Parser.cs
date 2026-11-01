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

    // top_item = import_stmt | func_def | class_def | enum_def | const_def | stmt
    private TopItem ParseTopItem() => Current.Kind switch
    {
        TokenKind.ImportKeyword => ParseImportStmt(),
        TokenKind.FromKeyword => ParseFromImportStmt(),
        TokenKind.DefKeyword or TokenKind.ClassKeyword or TokenKind.At => ParseDecoratedDef(),
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

    // { decorator } ( func_def | class_def ) at the top level
    private TopItem ParseDecoratedDef()
    {
        var start = Current.Span.Start;
        var decorators = ParseDecorators();
        if (At(TokenKind.ClassKeyword))
            return ParseClassDef(start, decorators);
        if (At(TokenKind.DefKeyword))
            return ParseFuncDef(start, decorators);
        ReportExpected("'def' or 'class'");
        return new ExprStmt(new ErrorExpr(Here), Here);
    }

    // decorator = "@" NAME NEWLINE
    private ImmutableArray<Decorator> ParseDecorators()
    {
        var decorators = ImmutableArray.CreateBuilder<Decorator>();
        while (At(TokenKind.At))
        {
            var start = Next().Span.Start;
            var name = ExpectIdentifier();
            var span = SpanFrom(start);
            Expect(TokenKind.Newline);
            decorators.Add(new Decorator(name, span));
        }
        return decorators.ToImmutable();
    }

    // func_def = { decorator } "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block
    private FuncDef ParseFuncDef(int start, ImmutableArray<Decorator> decorators)
    {
        Expect(TokenKind.DefKeyword);
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
        return new FuncDef(decorators, name, typeParams, parameters, returnType, body, SpanFrom(start));
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
    private ImmutableArray<Param> ParseParams()
    {
        var parameters = ParseCommaList(TokenKind.CloseParen, ParseParam);

        // As in Python, a parameter without a default can't follow one with a default (SPEC §2).
        var firstDefault = parameters.IndexOf(parameters.FirstOrDefault(p => p.Default is not null)!);
        if (firstDefault >= 0 && parameters.Skip(firstDefault).FirstOrDefault(p => p.Default is null) is { } required)
            ReportSyntaxError(bag => bag.ReportRequiredAfterDefault(required.Span));
        return parameters;
    }

    // param = NAME ":" type [ "=" expr ]
    private Param ParseParam()
    {
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var type = ParseType();
        Expr? defaultValue = null;
        if (At(TokenKind.Equals))
        {
            Next();
            defaultValue = ParseExpr();
        }
        return new Param(name, type, defaultValue, SpanFrom(name.Span.Start));
    }

    // class_def = { decorator } "class" NAME [ type_params ] [ "(" base { "," base } ")" ] ":"
    //             NEWLINE INDENT class_member { class_member } DEDENT
    private ClassDef ParseClassDef(int start, ImmutableArray<Decorator> decorators)
    {
        Expect(TokenKind.ClassKeyword);
        var name = ExpectIdentifier();
        var typeParams = ParseTypeParams();
        var bases = ImmutableArray<TypeSyntax>.Empty;
        if (At(TokenKind.OpenParen))
        {
            Next();
            bases = ParseCommaList(TokenKind.CloseParen, ParseType);
            Expect(TokenKind.CloseParen);
        }
        Expect(TokenKind.Colon);
        var members = ParseIndentedBody(ParseClassMember);
        return new ClassDef(decorators, name, typeParams, bases, [.. members.OfType<FieldDef>()], [.. members.OfType<MethodDef>()], SpanFrom(start));
    }

    // class_member = NAME ":" type [ "=" expr ] NEWLINE | method_def | "pass" NEWLINE
    private Node ParseClassMember()
    {
        if (At(TokenKind.DefKeyword) || At(TokenKind.At))
        {
            var start = Current.Span.Start;
            var decorators = ParseDecorators();
            return ParseMethodDef(start, decorators);
        }
        if (At(TokenKind.PassKeyword))
        {
            var pass = new PassStmt(Next().Span);
            Expect(TokenKind.Newline);
            return pass;
        }
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var type = ParseType();
        Expr? defaultValue = null;
        if (At(TokenKind.Equals))
        {
            Next();
            defaultValue = ParseExpr();
        }
        var field = new FieldDef(name, type, defaultValue, SpanFrom(name.Span.Start));
        Expect(TokenKind.Newline);
        return field;
    }

    // method_def = { decorator } "def" NAME "(" "self" [ "," params ] ")" [ "->" type ] ":" block
    private MethodDef ParseMethodDef(int start, ImmutableArray<Decorator> decorators)
    {
        Expect(TokenKind.DefKeyword);
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
        return new MethodDef(decorators, name, self, parameters, returnType, body, SpanFrom(start));
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
        return new FieldDef(name, type, null, SpanFrom(name.Span.Start));
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
            case TokenKind.TryKeyword:
                return ParseTryStmt();
            case TokenKind.DefKeyword or TokenKind.At:
                var start = Current.Span.Start;
                var decorators = ParseDecorators();
                return new FuncDefStmt(ParseFuncDef(start, decorators), SpanFrom(start));
            default:
                var stmt = ParseSimpleStmt();
                Expect(TokenKind.Newline);
                return stmt;
        }
    }

    // simple_stmt = let_stmt | var_stmt | assign_stmt | return_stmt | raise_stmt | "pass" | "break" | "continue" | expr
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
            case TokenKind.RaiseKeyword:
                return ParseRaiseStmt();
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

    // let_stmt = "let" ( NAME [ ":" type ] | names ) "=" expr_list
    private Stmt ParseLetStmt() => ParseBindingStmt(isVar: false);

    // var_stmt = "var" ( NAME [ ":" type ] | names ) "=" expr_list
    private Stmt ParseVarStmt() => ParseBindingStmt(isVar: true);

    // names = NAME "," NAME { "," NAME }
    private Stmt ParseBindingStmt(bool isVar)
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        if (At(TokenKind.Comma))
        {
            var names = ParseMoreNames(name);
            Expect(TokenKind.Equals);
            return new UnpackStmt(isVar, names, ParseExprList(), SpanFrom(start));
        }
        var type = ParseOptionalAnnotation();
        Expect(TokenKind.Equals);
        var value = ParseExprList();
        return isVar ? new VarStmt(name, type, value, SpanFrom(start)) : new LetStmt(name, type, value, SpanFrom(start));
    }

    // The rest of a comma-separated list of names, after the first.
    private ImmutableArray<Identifier> ParseMoreNames(Identifier first)
    {
        var names = ImmutableArray.CreateBuilder<Identifier>();
        names.Add(first);
        while (At(TokenKind.Comma))
        {
            Next();
            names.Add(ExpectIdentifier());
        }
        return names.ToImmutable();
    }

    // expr_list = expr { "," expr }; two or more make a tuple (SPEC §3.8)
    private Expr ParseExprList()
    {
        var first = ParseExpr();
        if (!At(TokenKind.Comma))
            return first;
        var elements = ImmutableArray.CreateBuilder<Expr>();
        elements.Add(first);
        while (At(TokenKind.Comma))
        {
            Next();
            elements.Add(ParseExpr());
        }
        return new TupleExpr(elements.ToImmutable(), SpanFrom(first.Span.Start));
    }

    private TypeSyntax? ParseOptionalAnnotation()
    {
        if (!At(TokenKind.Colon))
            return null;
        Next();
        return ParseType();
    }

    // assign_stmt = target ( "=" | "+=" | "-=" | "*=" | "/=" ) expr
    //             | target "," target { "," target } "=" expr_list ;  also the bare `expr` statement
    private Stmt ParseAssignStmt()
    {
        var start = Current.Span.Start;
        var target = ParseExpr();
        if (!_recovering && At(TokenKind.Comma))
            return ParseTupleAssignStmt(start, target);

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
        var value = op == AssignOp.Assign ? ParseExprList() : ParseExpr();
        return new AssignStmt(target, op.Value, value, SpanFrom(start));
    }

    private Stmt ParseTupleAssignStmt(int start, Expr first)
    {
        var targets = ImmutableArray.CreateBuilder<Expr>();
        targets.Add(first);
        while (At(TokenKind.Comma))
        {
            Next();
            targets.Add(ParseExpr());
        }
        foreach (var target in targets)
        {
            if (!IsTarget(target))
            {
                ReportSyntaxError(bag => bag.ReportInvalidAssignmentTarget(target.Span));
                break;
            }
        }
        Expect(TokenKind.Equals);
        var value = ParseExprList();
        return new TupleAssignStmt(targets.ToImmutable(), value, SpanFrom(start));
    }

    // target = NAME | postfix "." NAME | postfix "[" expr "]"
    private static bool IsTarget(Expr expr) => expr is NameExpr or MemberExpr or IndexExpr;

    // return_stmt = "return" [ expr_list ]
    private ReturnStmt ParseReturnStmt()
    {
        var start = Next().Span.Start;
        var value = !_recovering && CanStartExpression(Current.Kind) ? ParseExprList() : null;
        return new ReturnStmt(value, SpanFrom(start));
    }

    // raise_stmt = "raise" [ expr ]
    private RaiseStmt ParseRaiseStmt()
    {
        var start = Next().Span.Start;
        var value = !_recovering && CanStartExpression(Current.Kind) ? ParseExpr() : null;
        return new RaiseStmt(value, SpanFrom(start));
    }

    // try_stmt = "try" ":" block { except_clause } [ "else" ":" block ] [ "finally" ":" block ]
    private TryStmt ParseTryStmt()
    {
        var start = Next().Span.Start;
        Expect(TokenKind.Colon);
        var body = ParseBlock();

        var handlers = ImmutableArray.CreateBuilder<ExceptClause>();
        ExceptClause? bare = null;
        while (At(TokenKind.ExceptKeyword))
        {
            var handler = ParseExceptClause();
            // A bare `except:` must be last (SPEC §2.1); parsing carries on after reporting it.
            if (bare is not null)
            {
                _diagnostics.ReportBareExceptNotLast(new TextSpan(bare.Span.Start, "except".Length));
                bare = null;
            }
            if (handler.Types.IsEmpty)
                bare = handler;
            handlers.Add(handler);
        }

        Block? elseBlock = null;
        if (handlers.Count > 0 && At(TokenKind.ElseKeyword))
        {
            Next();
            Expect(TokenKind.Colon);
            elseBlock = ParseBlock();
        }

        Block? finallyBlock = null;
        if (At(TokenKind.FinallyKeyword))
        {
            Next();
            Expect(TokenKind.Colon);
            finallyBlock = ParseBlock();
        }
        else if (handlers.Count == 0)
        {
            ReportExpected("'except' or 'finally'");
        }
        return new TryStmt(body, handlers.ToImmutable(), elseBlock, finallyBlock, SpanFrom(start));
    }

    // except_clause = "except" [ except_types [ "as" NAME ] ] ":" block
    // except_types  = qual_name | "(" qual_name { "," qual_name } [ "," ] ")"
    private ExceptClause ParseExceptClause()
    {
        var start = Next().Span.Start;
        var types = ImmutableArray<Expr>.Empty;
        var typesStart = Current.Span.Start;
        Identifier? binding = null;
        if (!At(TokenKind.Colon))
        {
            if (At(TokenKind.OpenParen))
            {
                Next();
                types = ParseCommaList(TokenKind.CloseParen, ParseExceptType);
                Expect(TokenKind.CloseParen);
            }
            else
            {
                types = [ParseExceptType()];
            }
        }
        var typesSpan = types.IsEmpty ? new TextSpan(typesStart, 0) : SpanFrom(typesStart);
        if (!types.IsEmpty)
        {
            if (At(TokenKind.AsKeyword))
            {
                Next();
                binding = ExpectIdentifier();
            }
        }
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new ExceptClause(types, typesSpan, binding, body, SpanFrom(start));
    }

    // qual_name = NAME [ "." NAME [ "." NAME ] ], as a name or member chain
    private Expr ParseExceptType()
    {
        var start = Current.Span.Start;
        var first = ExpectIdentifier();
        Expr type = new NameExpr(first.Text, first.Span);
        for (var i = 0; i < 2 && At(TokenKind.Dot); i++)
        {
            Next();
            var member = ExpectIdentifier();
            type = new MemberExpr(type, member, SpanFrom(start));
        }
        return type;
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

    // condition = expr "is" "some" NAME | expr   — `is None` is a comparison (SPEC §2)
    private Condition ParseCondition()
    {
        var start = Current.Span.Start;
        var expr = ParseExpr();
        if (!At(TokenKind.IsKeyword))
            return new ExprCondition(expr, SpanFrom(start));

        Next();
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

    // for_stmt = "for" ( NAME | names ) "in" expr ":" block
    private ForStmt ParseForStmt()
    {
        var start = Next().Span.Start;
        var variables = ParseMoreNames(ExpectIdentifier());
        Expect(TokenKind.InKeyword);
        var iterable = ParseExpr();
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new ForStmt(variables, iterable, body, SpanFrom(start));
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

    // qual_name [ "(" [ pat_args ] ")" ]  where  qual_name = NAME [ "." NAME [ "." NAME ] ]
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

        ImmutableArray<PatternBinding>? bindings = null;
        if (At(TokenKind.OpenParen))
        {
            Next();
            bindings = ParseCommaList(TokenKind.CloseParen, ParsePatternBinding);
            Expect(TokenKind.CloseParen);
        }
        return new VariantPattern(module, enumName, variant, bindings, SpanFrom(first.Span.Start));
    }

    // pat_arg = NAME | NAME "=" NAME
    private PatternBinding ParsePatternBinding()
    {
        var first = ExpectIdentifier();
        if (!At(TokenKind.Equals))
            return new PatternBinding(null, first, first.Span);
        Next();
        var name = ExpectIdentifier();
        return new PatternBinding(first, name, SpanFrom(first.Span.Start));
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
    //           | "Callable" "[" "[" [ type { "," type } ] "]" "," ( type | "None" ) "]"
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
            case "tuple":
            {
                // One element parses; the binder reports it (TP0209).
                Expect(TokenKind.OpenBracket);
                var elements = ImmutableArray.CreateBuilder<TypeSyntax>();
                elements.Add(ParseType());
                while (At(TokenKind.Comma))
                {
                    Next();
                    elements.Add(ParseType());
                }
                Expect(TokenKind.CloseBracket);
                return new TupleType(elements.ToImmutable(), SpanFrom(start));
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

    // comparison = additive [ comp_op additive | "is" [ "not" ] "None" ]   — a second comp_op is TP0212 (SPEC §2.1)
    private Expr ParseComparison()
    {
        var left = ParseAdditive();
        if (At(TokenKind.IsKeyword) && Peek(1).Kind != TokenKind.SomeKeyword)
        {
            Next();
            var negated = At(TokenKind.NotKeyword);
            if (negated)
                Next();
            Expect(TokenKind.NoneKeyword, negated ? null : "'some', 'not' or 'None'");
            left = new IsNoneExpr(left, negated, SpanFrom(left.Span.Start));
        }
        else if (TryParseCompOp(out var op, out _))
        {
            left = Binary(left, op, ParseAdditive());
        }
        else
        {
            return left;
        }

        var reported = false;
        while (TryParseCompOp(out var op, out var opSpan))
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
                expr = ParseSubscript(expr, start);
            }
            else
            {
                return expr;
            }
        }
    }

    // subscript = expr | [ expr ] ":" [ expr ] [ ":" [ expr ] ], after the "["
    private Expr ParseSubscript(Expr target, int start)
    {
        var first = At(TokenKind.Colon) ? null : ParseExpr();
        if (first is not null && !At(TokenKind.Colon))
        {
            Expect(TokenKind.CloseBracket);
            return new IndexExpr(target, first, SpanFrom(start));
        }
        Expect(TokenKind.Colon);
        var stop = At(TokenKind.Colon) || At(TokenKind.CloseBracket) ? null : ParseExpr();
        Expr? step = null;
        if (At(TokenKind.Colon))
        {
            Next();
            step = At(TokenKind.CloseBracket) ? null : ParseExpr();
        }
        Expect(TokenKind.CloseBracket);
        return new SliceExpr(target, first, stop, step, SpanFrom(start));
    }

    // call = "(" [ arg { "," arg } [ "," ] ] ")"
    private ImmutableArray<Argument> ParseCall()
    {
        Next();
        var arguments = ParseCommaList(TokenKind.CloseParen, ParseArg);
        Expect(TokenKind.CloseParen);

        // A positional argument after a keyword argument is never valid (SPEC §2).
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

    // atom = INT | FLOAT | STRING | FSTRING | "True" | "False" | "None" | NAME | "self"
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
            // "(" expr ")" | "(" expr "," [ expr { "," expr } [ "," ] ] ")"
            case TokenKind.OpenParen:
            {
                Next();
                var inner = ParseExpr();
                if (!At(TokenKind.Comma))
                {
                    Expect(TokenKind.CloseParen);
                    return inner;
                }
                var elements = ImmutableArray.CreateBuilder<Expr>();
                elements.Add(inner);
                while (At(TokenKind.Comma))
                {
                    Next();
                    if (At(TokenKind.CloseParen))
                        break;
                    elements.Add(ParseExpr());
                }
                Expect(TokenKind.CloseParen);
                return new TupleExpr(elements.ToImmutable(), SpanFrom(token.Span.Start));
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
