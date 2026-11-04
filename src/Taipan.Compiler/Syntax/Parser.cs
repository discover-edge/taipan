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

    // The token index just after the last match expression's arms, whose DEDENT ends a statement (SPEC §2).
    private int _matchArmsEnd = -1;
    private int _previousEnd;
    private bool _recovering;

    // Parsing a with item's expression, outside any brackets: `as` ends it rather than converting (SPEC §2).
    private bool _withItem;

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

    // top_item = import_stmt | func_def | class_def | enum_def | const_def | type_alias | stmt
    private TopItem ParseTopItem() => Current.Kind switch
    {
        TokenKind.Name when AtTypeAlias() => ParseTypeAlias(),
        // `async for` and `async with` are statements (SPEC §3.20, M42).
        TokenKind.AsyncKeyword when Peek(1).Kind is TokenKind.ForKeyword or TokenKind.WithKeyword => ParseStmt(),
        TokenKind.ImportKeyword => ParseImportStmt(),
        TokenKind.FromKeyword => ParseFromImportStmt(),
        TokenKind.DefKeyword or TokenKind.ClassKeyword or TokenKind.At or TokenKind.AsyncKeyword => ParseDecoratedDef(),
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

    // import_stmt = "from" module_path "import" import_name { "," import_name } NEWLINE
    private FromImportStmt ParseFromImportStmt()
    {
        var start = Next().Span.Start;
        var module = ParseModulePath();
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

    // module_path = NAME { "." NAME }, as one identifier spanning the path (SPEC §13.1)
    private Identifier ParseModulePath()
    {
        var first = ExpectIdentifier();
        var parts = new List<string> { first.Text };
        while (At(TokenKind.Dot))
        {
            Next();
            parts.Add(ExpectIdentifier().Text);
        }
        return new Identifier(string.Join(".", parts), SpanFrom(first.Span.Start));
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

    /// <summary>The soft keyword <c>type</c> starts an alias before a name and <c>=</c> or <c>[</c> (SPEC §1.3).</summary>
    private bool AtTypeAlias() =>
        At(TokenKind.Name) && Current.Text == "type" && Peek(1).Kind == TokenKind.Name && Peek(2).Kind is TokenKind.Equals or TokenKind.OpenBracket;

    // type_alias = "type" NAME [ type_params ] "=" type NEWLINE
    private TypeAliasDef ParseTypeAlias()
    {
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        var typeParams = ParseTypeParams();
        Expect(TokenKind.Equals);
        var type = ParseType();
        var span = SpanFrom(start);
        Expect(TokenKind.Newline);
        return new TypeAliasDef(name, typeParams, type, span);
    }

    // { decorator } ( func_def | class_def ) at the top level
    private TopItem ParseDecoratedDef()
    {
        var start = Current.Span.Start;
        var decorators = ParseDecorators();
        if (At(TokenKind.ClassKeyword))
            return ParseClassDef(start, decorators);
        if (At(TokenKind.DefKeyword) || At(TokenKind.AsyncKeyword))
            return ParseFuncDef(start, decorators);
        ReportExpected("'def' or 'class'");
        return new ExprStmt(new ErrorExpr(Here), Here);
    }

    // decorator = "@" NAME [ "." NAME ] NEWLINE
    private ImmutableArray<Decorator> ParseDecorators()
    {
        var decorators = ImmutableArray.CreateBuilder<Decorator>();
        while (At(TokenKind.At))
        {
            var start = Next().Span.Start;
            var name = ExpectIdentifier();
            Identifier? member = null;
            if (At(TokenKind.Dot))
            {
                Next();
                member = ExpectIdentifier();
            }
            // decorator arguments (M39, SPEC §4.9): `@dataclass(frozen=True)`
            ImmutableArray<Argument>? arguments = At(TokenKind.OpenParen) ? ParseCall() : null;
            var span = SpanFrom(start);
            Expect(TokenKind.Newline);
            decorators.Add(new Decorator(name, member, span) { Arguments = arguments });
        }
        return decorators.ToImmutable();
    }

    // func_def = { decorator } [ "async" ] "def" NAME [ type_params ] "(" [ params ] ")" [ "->" type ] ":" block
    private FuncDef ParseFuncDef(int start, ImmutableArray<Decorator> decorators)
    {
        var isAsync = ParseAsync();
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
            // `-> None` says it returns nothing, as no return type does (SPEC §3.2, M26).
            if (At(TokenKind.NoneKeyword))
                Next();
            else
                returnType = ParseType();
        }

        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new FuncDef(decorators, name, typeParams, parameters, returnType, body, SpanFrom(start)) { IsAsync = isAsync };
    }

    // The "async" before "def" (SPEC §3.14, M33).
    private bool ParseAsync()
    {
        if (!At(TokenKind.AsyncKeyword))
            return false;
        Next();
        return true;
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

    // type_param = NAME [ ":" ( "(" type { "," type } [ "," ] ")" | type ) ]
    private TypeParam ParseTypeParam()
    {
        var name = ExpectIdentifier();
        var constraints = ImmutableArray<TypeSyntax>.Empty;
        TypeSyntax? bound = null;
        if (At(TokenKind.Colon))
        {
            Next();
            if (At(TokenKind.OpenParen))
            {
                Next();
                constraints = ParseCommaList(TokenKind.CloseParen, ParseType);
                Expect(TokenKind.CloseParen);
            }
            else
            {
                bound = ParseType();
            }
        }
        return new TypeParam(name, constraints, bound, SpanFrom(name.Span.Start));
    }

    // params     = param_item { "," param_item } [ "," ]
    // param_item = param | "/" | "*" [ NAME ":" type ] | "**" NAME ":" type   (M30)
    // Each parameter gets its kind from the markers around it (SPEC §4.2).
    private ImmutableArray<Param> ParseParams(bool afterReceiver = false)
    {
        var parameters = ImmutableArray.CreateBuilder<Param>();
        var (slash, star, starStar) = ((TextSpan?)null, (TextSpan?)null, (Param?)null);
        var bareStar = false;
        while (!_recovering && !At(TokenKind.CloseParen))
        {
            var start = Current.Span.Start;
            if (starStar is not null)
                ReportSyntaxError(bag => bag.ReportParameterOrder(Current.Span, "nothing can follow '**' parameters"));
            if (At(TokenKind.Slash))
            {
                var span = Next().Span;
                // In a method, self or cls counts as the parameter before it.
                if (slash is not null || star is not null || parameters.Count == 0 && !afterReceiver)
                    ReportSyntaxError(bag => bag.ReportParameterOrder(span, parameters.Count == 0 && !afterReceiver ? "'/' must follow a parameter"
                        : slash is not null ? "'/' may appear only once" : "'/' must come before '*'"));
                slash = span;
                // Every parameter so far is positional-only.
                for (var i = 0; i < parameters.Count; i++)
                    parameters[i] = parameters[i] with { Kind = ParamKind.PositionalOnly };
            }
            else if (At(TokenKind.Star) && Peek(1).Kind == TokenKind.Star && Peek(1).Span.Start == Current.Span.End)
            {
                Next();
                Next();
                var parameter = ParseParam() with { Kind = ParamKind.VarKeywords };
                parameter = parameter with { Span = SpanFrom(start) };
                if (parameter.Default is { } value)
                    ReportSyntaxError(bag => bag.ReportParameterOrder(value.Span, "'**' parameters can't have a default"));
                starStar = parameter;
                parameters.Add(parameter);
            }
            else if (At(TokenKind.Star))
            {
                var span = Next().Span;
                if (star is not null)
                    ReportSyntaxError(bag => bag.ReportParameterOrder(span, "'*' may appear only once"));
                star = span;
                if (At(TokenKind.Name))
                {
                    var parameter = ParseParam() with { Kind = ParamKind.VarArgs };
                    if (parameter.Default is { } value)
                        ReportSyntaxError(bag => bag.ReportParameterOrder(value.Span, "'*' parameters can't have a default"));
                    parameters.Add(parameter with { Span = SpanFrom(start) });
                }
                else
                {
                    bareStar = true;
                }
            }
            else
            {
                var parameter = ParseParam();
                parameters.Add(star is null ? parameter : parameter with { Kind = ParamKind.KeywordOnly });
                bareStar = false;
            }
            if (!At(TokenKind.Comma))
                break;
            Next();
        }
        if (bareStar && star is { } bare)
            ReportSyntaxError(bag => bag.ReportParameterOrder(bare, "named arguments must follow bare '*'"));

        // As in Python, a parameter without a default can't follow one with a default, before any '*' (SPEC §2).
        var positional = parameters.Where(p => p.Kind is ParamKind.Normal or ParamKind.PositionalOnly).ToList();
        var firstDefault = positional.FindIndex(p => p.Default is not null);
        if (firstDefault >= 0 && positional.Skip(firstDefault).FirstOrDefault(p => p.Default is null) is { } required)
            ReportSyntaxError(bag => bag.ReportRequiredAfterDefault(required.Span));
        return parameters.ToImmutable();
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
        return new ClassDef(decorators, name, typeParams, bases, [.. members.OfType<FieldDef>()], [.. members.OfType<MethodDef>()], SpanFrom(start))
        {
            Aliases = [.. members.OfType<TypeAliasDef>()],
        };
    }

    // class_member = NAME ":" type [ "=" expr ] NEWLINE | method_def | type_alias | "pass" NEWLINE
    private Node ParseClassMember()
    {
        if (AtTypeAlias())
            return ParseTypeAlias();
        if (At(TokenKind.DefKeyword) || At(TokenKind.At) || At(TokenKind.AsyncKeyword))
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

    // method_def    = { decorator } "def" NAME "(" [ method_params ] ")" [ "->" type ] ":" ( block | "..." NEWLINE )
    // method_params = ( "self" | "cls" ) { "," param } [ "," ] | params
    // Which receiver a method needs depends on its decorators, so the binder checks it (SPEC §2, M28).
    private MethodDef ParseMethodDef(int start, ImmutableArray<Decorator> decorators)
    {
        var isAsync = ParseAsync();
        Expect(TokenKind.DefKeyword);
        var name = ExpectIdentifier();
        Expect(TokenKind.OpenParen);

        var (receiver, receiverSpan) = (Receiver.None, Here);
        var parameters = ImmutableArray<Param>.Empty;
        if (At(TokenKind.Name) && Current.Text is "self" or "cls" && Peek(1).Kind != TokenKind.Colon)
        {
            receiver = Current.Text == "self" ? Receiver.Self : Receiver.Cls;
            receiverSpan = Next().Span;
            if (At(TokenKind.Comma))
            {
                Next();
                parameters = ParseParams(afterReceiver: true);
            }
        }
        else
        {
            parameters = ParseParams();
        }
        Expect(TokenKind.CloseParen);

        TypeSyntax? returnType = null;
        if (At(TokenKind.Arrow))
        {
            Next();
            // `-> None` says it returns nothing, as no return type does (SPEC §3.2, M26).
            if (At(TokenKind.NoneKeyword))
                Next();
            else
                returnType = ParseType();
        }

        Expect(TokenKind.Colon);
        var body = At(TokenKind.Ellipsis) ? ParseEllipsisBody() : ParseBlock();
        return new MethodDef(decorators, name, receiver, receiverSpan, parameters, returnType, body, SpanFrom(start)) { IsAsync = isAsync };
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

    // A protocol method's "..." after the colon, as a block holding it (SPEC §3.11).
    private Block ParseEllipsisBody()
    {
        var ellipsis = Next().Span;
        Expect(TokenKind.Newline);
        return new Block([new ExprStmt(new EllipsisExpr(ellipsis), ellipsis)], ellipsis);
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
        if (AtTypeAlias())
        {
            var alias = ParseTypeAlias();
            return new TypeAliasStmt(alias, alias.Span);
        }
        switch (Current.Kind)
        {
            case TokenKind.IfKeyword:
                return ParseIfStmt();
            case TokenKind.WhileKeyword:
                return ParseWhileStmt();
            case TokenKind.ForKeyword:
                return ParseForStmt();
            // `async for` and `async with` (SPEC §3.20, M42); otherwise `async def`.
            case TokenKind.AsyncKeyword when Peek(1).Kind == TokenKind.ForKeyword:
                return ParseAsync(ParseForStmt);
            case TokenKind.AsyncKeyword when Peek(1).Kind == TokenKind.WithKeyword:
                return ParseAsync(ParseWithStmt);
            case TokenKind.MatchKeyword:
                return ParseMatchStmt();
            case TokenKind.TryKeyword:
                return ParseTryStmt();
            case TokenKind.WithKeyword:
                return ParseWithStmt();
            case TokenKind.DefKeyword or TokenKind.At or TokenKind.AsyncKeyword:
                var start = Current.Span.Start;
                var decorators = ParseDecorators();
                return new FuncDefStmt(ParseFuncDef(start, decorators), SpanFrom(start));
            default:
                var stmt = ParseSimpleStmt();
                ExpectStatementEnd(stmt);
                return stmt;
        }
    }

    private Stmt ParseAsync<T>(Func<T> parse) where T : Stmt
    {
        var start = Next().Span.Start;
        return parse() switch
        {
            ForStmt forStmt => forStmt with { IsAsync = true, Span = SpanFrom(start) },
            WithStmt with => with with { IsAsync = true, Span = SpanFrom(start) },
            var other => other,
        };
    }

    /// <summary>
    /// A simple statement's NEWLINE, unless its last token ended a match expression's arms, whose
    /// DEDENT ends it (SPEC §2).
    /// </summary>
    private void ExpectStatementEnd(Stmt stmt)
    {
        if (_index != _matchArmsEnd)
            Expect(TokenKind.Newline);
    }

    /// <summary>The value of a <c>let</c>, <c>var</c>, assignment or <c>return</c>: an expression list.</summary>
    private Expr ParseValue(bool list = true) => list ? ParseExprList() : ParseExpr();

    /// <summary>The right side of a <c>let</c>, <c>var</c> or assignment, which may also be a <c>yield</c> (SPEC §3.20, M42).</summary>
    private Expr ParseAssignedValue(bool list = true) => At(TokenKind.YieldKeyword) ? ParseYieldExpr() : ParseValue(list);

    // simple_stmt = let_stmt | var_stmt | assign_stmt | return_stmt | raise_stmt | del_stmt | "pass" | "break" | "continue" | expr
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
            case TokenKind.DelKeyword:
                return ParseDelStmt();
            case TokenKind.YieldKeyword:
                return ParseYieldStmt();
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

    private Stmt ParseBindingStmt(bool isVar)
    {
        var start = Next().Span.Start;
        if (!(At(TokenKind.Name) && Peek(1).Kind == TokenKind.Comma || At(TokenKind.Star) || At(TokenKind.OpenParen)))
        {
            var name = ExpectIdentifier();
            var type = ParseOptionalAnnotation();
            Expect(TokenKind.Equals);
            var value = ParseAssignedValue();
            return isVar ? new VarStmt(name, type, value, SpanFrom(start)) : new LetStmt(name, type, value, SpanFrom(start));
        }
        var names = ParseNames();
        Expect(TokenKind.Equals);
        return new UnpackStmt(isVar, names, ParseAssignedValue(), SpanFrom(start));
    }

    // names    = unpacked "," unpacked { "," unpacked } [ "," ]
    // unpacked = NAME | "*" NAME | "(" names ")"
    // One group alone, `(a, b)`, is its names (M29).
    private ImmutableArray<Unpacked> ParseNames()
    {
        var names = ImmutableArray.CreateBuilder<Unpacked>();
        do
        {
            if (names.Count > 0)
                Next();
            // A trailing comma ends the names, once there are two (SPEC §2).
            if (names.Count > 1 && (At(TokenKind.Equals) || At(TokenKind.InKeyword) || At(TokenKind.CloseParen)))
                break;
            names.Add(ParseUnpacked());
        }
        while (At(TokenKind.Comma));
        CheckStars(names);
        if (names is [UnpackedGroup group])
            return group.Items;
        if (names is [UnpackedName { Starred: true } alone])
            ReportSyntaxError(bag => bag.ReportLoneStar(alone.Span));
        return names.ToImmutable();
    }

    private Unpacked ParseUnpacked()
    {
        var start = Current.Span.Start;
        if (At(TokenKind.Star))
        {
            Next();
            var name = ExpectIdentifier();
            return new UnpackedName(name, Starred: true, SpanFrom(start));
        }
        if (At(TokenKind.OpenParen))
        {
            Next();
            var items = ParseNames();
            Expect(TokenKind.CloseParen);
            return new UnpackedGroup(items, SpanFrom(start));
        }
        var plain = ExpectIdentifier();
        return new UnpackedName(plain, Starred: false, plain.Span);
    }

    // At most one starred name or target per level (SPEC §3.8, M29).
    private void CheckStars(IEnumerable<Unpacked> items)
    {
        var stars = items.Where(i => i is UnpackedName { Starred: true } or UnpackedTarget { Starred: true }).ToList();
        if (stars.Count > 1)
            ReportSyntaxError(bag => bag.ReportTwoStars(stars[1].Span, "an unpacking"));
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
        // `(a, b) = e` unpacks too, and `*a = e` is an unpacking with nothing to unpack into (M29).
        if (!_recovering && target is TupleExpr or StarredExpr && At(TokenKind.Equals))
            return ParseTupleAssignStmt(start, target);

        AssignOp? op = _recovering ? null : Current.Kind switch
        {
            TokenKind.Equals => AssignOp.Assign,
            TokenKind.PlusEquals => AssignOp.Add,
            TokenKind.MinusEquals => AssignOp.Subtract,
            TokenKind.StarEquals => AssignOp.Multiply,
            TokenKind.SlashEquals => AssignOp.Divide,
            TokenKind.SlashSlashEquals => AssignOp.FloorDivide,
            TokenKind.PercentEquals => AssignOp.Modulo,
            TokenKind.StarStarEquals => AssignOp.Power,
            _ => null,
        };
        if (op is null)
            return new ExprStmt(target, SpanFrom(start));

        // A slice is a target since M26 (SPEC §5.5).
        if (!IsTarget(target) && target is not SliceExpr)
            ReportSyntaxError(bag => bag.ReportInvalidAssignmentTarget(target.Span));
        Next();
        var value = ParseAssignedValue(list: op == AssignOp.Assign);
        return new AssignStmt(target, op.Value, value, SpanFrom(start));
    }

    // targets       = unpack_target "," unpack_target { "," unpack_target } [ "," ]
    // unpack_target = target | "*" target | "(" targets ")"
    private Stmt ParseTupleAssignStmt(int start, Expr first)
    {
        var expressions = ImmutableArray.CreateBuilder<Expr>();
        expressions.Add(first);
        while (At(TokenKind.Comma))
        {
            Next();
            if (At(TokenKind.Equals))
                break;
            expressions.Add(ParseExpr());
        }
        var targets = expressions is [TupleExpr only] ? ToTargets(only.Elements) : ToTargets(expressions.ToImmutable());
        if (targets is [UnpackedTarget { Starred: true } alone])
            ReportSyntaxError(bag => bag.ReportLoneStar(alone.Span));
        Expect(TokenKind.Equals);
        var value = ParseValue();
        return new TupleAssignStmt(targets, value, SpanFrom(start));
    }

    // Unpacking targets from the expressions parsed: tuples nest, and `*x` takes the rest (M29).
    private ImmutableArray<Unpacked> ToTargets(ImmutableArray<Expr> expressions)
    {
        var targets = ImmutableArray.CreateBuilder<Unpacked>();
        foreach (var expr in expressions)
        {
            switch (expr)
            {
                case TupleExpr group:
                    targets.Add(new UnpackedGroup(ToTargets(group.Elements), group.Span));
                    break;
                case StarredExpr { Value: var target } starred when IsTarget(target):
                    targets.Add(new UnpackedTarget(target, Starred: true, starred.Span));
                    break;
                case var target when IsTarget(target):
                    targets.Add(new UnpackedTarget(target, Starred: false, target.Span));
                    break;
                default:
                    ReportSyntaxError(bag => bag.ReportInvalidAssignmentTarget(expr.Span));
                    targets.Add(new UnpackedTarget(expr, Starred: false, expr.Span));
                    break;
            }
        }
        CheckStars(targets);
        return targets.ToImmutable();
    }

    // target = NAME | postfix "." NAME | postfix "[" subscript "]"; a slice isn't a target in a tuple assignment.
    private static bool IsTarget(Expr expr) => expr is NameExpr or MemberExpr or IndexExpr;

    // del_stmt = "del" target { "," target }
    private DelStmt ParseDelStmt()
    {
        var start = Next().Span.Start;
        var targets = ImmutableArray.CreateBuilder<Expr>();
        do
        {
            if (targets.Count > 0)
                Next();
            var target = ParseExpr();
            if (!IsTarget(target) && target is not SliceExpr && !_recovering)
                ReportSyntaxError(bag => bag.ReportInvalidDeleteTarget(target.Span));
            targets.Add(target);
        }
        while (At(TokenKind.Comma) && !_recovering);
        return new DelStmt(targets.ToImmutable(), SpanFrom(start));
    }

    // return_stmt = "return" [ expr_list | match_expr ]
    private ReturnStmt ParseReturnStmt()
    {
        var start = Next().Span.Start;
        var value = !_recovering && CanStartExpression(Current.Kind) ? ParseValue() : null;
        return new ReturnStmt(value, SpanFrom(start));
    }

    // yield_stmt = yield_expr
    private YieldStmt ParseYieldStmt()
    {
        var yield = ParseYieldExpr();
        return new YieldStmt(yield.Value, yield.From, yield.Span);
    }

    // yield_expr = "yield" [ [ "from" ] expr ]   — a bare `yield` since M42 (SPEC §3.20)
    private YieldExpr ParseYieldExpr()
    {
        var keyword = Next();
        var from = At(TokenKind.FromKeyword);
        if (from)
            Next();
        if (!from && (_recovering || !CanStartExpression(Current.Kind)))
            return new YieldExpr(null, from, keyword.Span);
        var value = ParseExpr();
        return new YieldExpr(value, from, SpanFrom(keyword.Span.Start));
    }

    // raise_stmt = "raise" [ expr [ "from" expr ] ]
    private RaiseStmt ParseRaiseStmt()
    {
        var start = Next().Span.Start;
        var value = !_recovering && CanStartExpression(Current.Kind) ? ParseExpr() : null;
        Expr? cause = null;
        if (value is not null && At(TokenKind.FromKeyword))
        {
            Next();
            cause = ParseExpr();
        }
        return new RaiseStmt(value, cause, SpanFrom(start));
    }

    // with_stmt = [ "async" ] "with" with_item { "," with_item } ":" block   — `async` is ParseAsync's
    private WithStmt ParseWithStmt()
    {
        var start = Next().Span.Start;
        var items = ImmutableArray.CreateBuilder<WithItem>();
        do
        {
            if (items.Count > 0)
                Next();
            items.Add(ParseWithItem());
        }
        while (At(TokenKind.Comma));
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new WithStmt(items.ToImmutable(), body, SpanFrom(start));
    }

    // with_item = expr [ "as" NAME ], where a top-level `as` names the item's variable (SPEC §2)
    private WithItem ParseWithItem()
    {
        var start = Current.Span.Start;
        var outer = _withItem;
        _withItem = true;
        var manager = ParseExpr();
        _withItem = outer;
        Identifier? binding = null;
        if (At(TokenKind.AsKeyword))
        {
            Next();
            binding = ExpectIdentifier();
        }
        return new WithItem(manager, binding, SpanFrom(start));
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

    // for_stmt = [ "async" ] "for" ( NAME | names ) "in" expr ":" block   — `async` is ParseAsync's
    private ForStmt ParseForStmt()
    {
        var start = Next().Span.Start;
        var variables = ParseNames();
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

    // arm = pattern [ guard ] ":" ( simple_stmt NEWLINE | block )
    private MatchArm ParseArm()
    {
        var start = Current.Span.Start;
        var pattern = ParsePattern();
        var guard = ParseGuard();
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
            ExpectStatementEnd(stmt);
        }
        return new MatchArm(pattern, body, new TextSpan(start, Math.Max(0, body.Span.End - start))) { Guard = guard };
    }

    // guard = "if" expr (M38)
    private Expr? ParseGuard()
    {
        if (!At(TokenKind.IfKeyword))
            return null;
        Next();
        return ParseExpr();
    }

    // match_expr = "match" expr ":" NEWLINE INDENT value_arm { value_arm } DEDENT
    // An atom since M29: its colon ends its line, and its arms are indented under it (SPEC §5.1.1).
    private MatchExpr ParseMatchExpr()
    {
        var start = Next().Span.Start;
        var scrutinee = ParseExpr();
        Expect(TokenKind.Colon);
        var arms = ParseIndentedBody(ParseValueArm);
        _matchArmsEnd = _index;
        return new MatchExpr(scrutinee, arms, SpanFrom(start));
    }

    // value_arm = pattern [ guard ] ":" ( ( expr | raise_stmt ) NEWLINE | block )
    private ValueArm ParseValueArm()
    {
        var start = Current.Span.Start;
        var pattern = ParsePattern();
        var guard = ParseGuard();
        Expect(TokenKind.Colon);
        if (At(TokenKind.Newline))
        {
            var block = ParseBlock();
            return new ValueArm(pattern, null, null, SpanFrom(start)) { Block = block, Guard = guard };
        }
        var raise = At(TokenKind.RaiseKeyword) ? ParseRaiseStmt() : null;
        var value = raise is null ? ParseExpr() : null;
        var arm = new ValueArm(pattern, value, raise, SpanFrom(start)) { Guard = guard };
        if (_index != _matchArmsEnd)
            Expect(TokenKind.Newline);
        return arm;
    }

    // pattern = or_pattern [ "as" NAME ] (M38)
    private Pattern ParsePattern()
    {
        var start = Current.Span.Start;
        var pattern = ParseOrPattern();
        if (!At(TokenKind.AsKeyword))
            return pattern;
        Next();
        var name = ExpectIdentifier();
        return new AsPattern(pattern, name, SpanFrom(start));
    }

    // or_pattern = closed_pattern { "|" closed_pattern } (M38)
    private Pattern ParseOrPattern()
    {
        var start = Current.Span.Start;
        var first = ParseClosedPattern();
        if (!At(TokenKind.Pipe))
            return first;
        var alternatives = ImmutableArray.CreateBuilder<Pattern>();
        alternatives.Add(first);
        while (At(TokenKind.Pipe))
        {
            Next();
            alternatives.Add(ParseClosedPattern());
        }
        return new OrPattern(alternatives.ToImmutable(), SpanFrom(start));
    }

    // closed_pattern = "_" | [ "-" ] INT | [ "-" ] FLOAT | STRING | "True" | "False" | "None"
    //                | qual_name [ "(" [ pat_args ] ")" ] | "(" pattern ")"
    //                | "(" list_pat "," [ list_pat { "," list_pat } [ "," ] ] ")" | "[" [ list_pat { "," list_pat } [ "," ] ] "]"
    //                | "{" [ map_pat { "," map_pat } [ "," ] ] "}"
    // list_pat = pattern | "*" NAME
    private Pattern ParseClosedPattern()
    {
        var start = Current.Span.Start;
        if (At(TokenKind.Name) && Current.Text == "_")
            return new WildcardPattern(Next().Span);
        if (At(TokenKind.NoneKeyword))
            return new NonePattern(Next().Span);
        if (At(TokenKind.OpenParen))
            return ParseParenthesizedPattern();
        if (At(TokenKind.OpenBrace))
            return ParseMappingPattern();
        if (At(TokenKind.Minus) && Peek(1).Kind == TokenKind.Float)
        {
            Next();
            return new FloatPattern(-ParseFloatValue(Next()), SpanFrom(start));
        }
        if (At(TokenKind.Float))
        {
            var token = Next();
            return new FloatPattern(ParseFloatValue(token), token.Span);
        }
        if (At(TokenKind.OpenBracket))
        {
            Next();
            var elements = ParseCommaList(TokenKind.CloseBracket, ParseListElementPattern);
            Expect(TokenKind.CloseBracket);
            var stars = elements.OfType<StarPattern>().ToList();
            if (stars.Count > 1)
                ReportSyntaxError(bag => bag.ReportTwoStars(stars[1].Span, "a list pattern"));
            return new ListPattern(elements, SpanFrom(start));
        }
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

    // "(" pattern ")" is a group; with a comma, a tuple (M29), with at most one "*" (M38).
    private Pattern ParseParenthesizedPattern()
    {
        var start = Next().Span.Start;
        if (At(TokenKind.CloseParen))
        {
            Next();
            ReportSyntaxError(bag => bag.ReportOneElementTuplePattern(SpanFrom(start)));
            return new TuplePattern([], SpanFrom(start));
        }
        var first = ParseListElementPattern();
        if (At(TokenKind.CloseParen) && first is not StarPattern)
        {
            Next();
            return first;
        }
        var elements = ImmutableArray.CreateBuilder<Pattern>();
        elements.Add(first);
        while (At(TokenKind.Comma))
        {
            Next();
            if (At(TokenKind.CloseParen))
                break;
            elements.Add(ParseListElementPattern());
        }
        Expect(TokenKind.CloseParen);
        if (elements.Count < 2)
            ReportSyntaxError(bag => bag.ReportOneElementTuplePattern(SpanFrom(start)));
        var stars = elements.OfType<StarPattern>().ToList();
        if (stars.Count > 1)
            ReportSyntaxError(bag => bag.ReportTwoStars(stars[1].Span, "a tuple pattern"));
        return new TuplePattern(elements.ToImmutable(), SpanFrom(start));
    }

    // "{" [ map_pat { "," map_pat } [ "," ] ] "}"  where  map_pat = map_key ":" pattern | "**" NAME (M38)
    private MappingPattern ParseMappingPattern()
    {
        var start = Next().Span.Start;
        var entries = ImmutableArray.CreateBuilder<MappingEntry>();
        Identifier? rest = null;
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            var entryStart = Current.Span.Start;
            if (At(TokenKind.Star) && Peek(1).Kind == TokenKind.Star)
            {
                Next();
                Next();
                var name = ExpectIdentifier();
                if (name.Text == "_")
                    ReportSyntaxError(bag => bag.ReportRestWildcard(SpanFrom(entryStart)));
                else if (rest is not null)
                    ReportSyntaxError(bag => bag.ReportRestNotLast(rest.Span));
                rest ??= name;
            }
            else
            {
                if (rest is not null)
                    ReportSyntaxError(bag => bag.ReportRestNotLast(rest.Span));
                var key = ParseClosedPattern();
                if (key is not (IntPattern or StringPattern or BoolPattern or ErrorPattern))
                    ReportSyntaxError(bag => bag.ReportExpected(key.Span, "a literal key", "a pattern"));
                Expect(TokenKind.Colon);
                var value = ParsePattern();
                entries.Add(new MappingEntry(key, value, SpanFrom(entryStart)));
            }
            if (!At(TokenKind.Comma))
                break;
            Next();
        }
        Expect(TokenKind.CloseBrace);
        return new MappingPattern(entries.ToImmutable(), rest is { Text: "_" } ? null : rest, SpanFrom(start));
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

    private Pattern ParseListElementPattern()
    {
        if (!At(TokenKind.Star))
            return ParsePattern();
        var start = Next().Span.Start;
        var name = ExpectIdentifier();
        return new StarPattern(name, SpanFrom(start));
    }

    // pat_arg = pattern | NAME "=" pattern
    private PatternBinding ParsePatternBinding()
    {
        var start = Current.Span.Start;
        if (At(TokenKind.Name) && Peek(1).Kind == TokenKind.Equals)
        {
            var field = ExpectIdentifier();
            Next();
            var pattern = ParsePattern();
            return new PatternBinding(field, pattern, SpanFrom(start));
        }
        var positional = ParsePattern();
        return new PatternBinding(null, positional, positional.Span);
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
                // tuple[T, ...] (M41): '...' only after exactly one type.
                if (At(TokenKind.Comma) && Peek(1).Kind == TokenKind.Ellipsis && elements.Count == 1)
                {
                    Next();
                    Next();
                    Expect(TokenKind.CloseBracket);
                    return new VarTupleType(elements[0], SpanFrom(start));
                }
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
            // Awaitable[T], or Awaitable[None] (SPEC §3.14, M33).
            case "Awaitable" when At(TokenKind.OpenBracket):
            {
                Next();
                TypeSyntax? result = null;
                if (At(TokenKind.NoneKeyword))
                    Next();
                else
                    result = ParseType();
                Expect(TokenKind.CloseBracket);
                return new AwaitableType(result, SpanFrom(start));
            }
            // Generator[Y, S, R] and AsyncGenerator[Y, S], whose arguments may be None (SPEC §3.20, M42).
            case "Generator" or "AsyncGenerator" when At(TokenKind.OpenBracket):
            {
                Next();
                var arguments = ParseCommaList(TokenKind.CloseBracket, ParseTypeOrNone);
                Expect(TokenKind.CloseBracket);
                return new GenericType(name, arguments, SpanFrom(start));
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

    private TypeSyntax ParseTypeOrNone() => At(TokenKind.NoneKeyword) ? new NoneTypeSyntax(Next().Span) : ParseType();

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
            // Python's `lambda i=i:` captures a loop variable, which Taipan does anyway (SPEC §3.7, M26).
            if (At(TokenKind.Equals))
            {
                var equals = Next().Span;
                ReportSyntaxError(bag => bag.ReportLambdaDefault(equals));
                return new ErrorExpr(SpanFrom(start));
            }
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

    // comparison = additive [ comp_op additive | "is" [ "not" ] ( "None" | additive ) ]   — a second comp_op is TP0212 (SPEC §2.1)
    private Expr ParseComparison()
    {
        var left = ParseBitOr();
        if (At(TokenKind.IsKeyword) && Peek(1).Kind != TokenKind.SomeKeyword)
        {
            Next();
            var negated = At(TokenKind.NotKeyword);
            if (negated)
                Next();
            if (At(TokenKind.NoneKeyword))
            {
                Next();
                left = new IsNoneExpr(left, negated, SpanFrom(left.Span.Start));
            }
            else
            {
                left = new IsExpr(left, ParseBitOr(), negated, SpanFrom(left.Span.Start));
            }
        }
        else if (TryParseCompOp(out var op, out _))
        {
            left = Binary(left, op, ParseBitOr());
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

    // bit_or = bit_xor { "|" bit_xor }; bit_xor = bit_and { "^" bit_and }; bit_and = additive { "&" additive }
    // At Python's precedence, between comparisons and arithmetic; sets use them (SPEC §3.15, M35).
    private Expr ParseBitOr() => ParseLeftAssociative(TokenKind.Pipe, BinaryOp.Union, ParseBitXor);

    private Expr ParseBitXor() => ParseLeftAssociative(TokenKind.Caret, BinaryOp.SymmetricDifference, ParseBitAnd);

    private Expr ParseBitAnd() => ParseLeftAssociative(TokenKind.Ampersand, BinaryOp.Intersection, ParseAdditive);

    private Expr ParseLeftAssociative(TokenKind token, BinaryOp op, Func<Expr> operand)
    {
        var left = operand();
        while (At(token))
        {
            Next();
            left = Binary(left, op, operand());
        }
        return left;
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

    // unary = "-" unary | "await" unary | cast; `*x` is parsed here too, only to be an unpacking target (M29)
    private Expr ParseUnary()
    {
        if (At(TokenKind.AwaitKeyword))
        {
            var awaitStart = Next().Span.Start;
            var awaited = ParseUnary();
            return new AwaitExpr(awaited, SpanFrom(awaitStart));
        }
        if (At(TokenKind.Star))
        {
            var starStart = Next().Span.Start;
            var starred = ParseUnary();
            return new StarredExpr(starred, SpanFrom(starStart));
        }
        if (!At(TokenKind.Minus))
            return ParsePower();
        var start = Next().Span.Start;
        var operand = ParseUnary();
        return new UnaryExpr(UnaryOp.Negate, operand, SpanFrom(start));
    }

    // power = cast [ "**" unary ]: right-associative, and tighter than a unary minus on its left (M35).
    // `**` is two adjacent '*' tokens, as in `**kwargs`.
    private Expr ParsePower()
    {
        var left = ParseCast();
        if (!At(TokenKind.Star) || Peek(1).Kind != TokenKind.Star || Peek(1).Span.Start != Current.Span.End)
            return left;
        Next();
        Next();
        return Binary(left, BinaryOp.Power, ParseUnary());
    }

    // cast = postfix [ "as" type ]. In a with item, a top-level `as` is the item's (SPEC §2).
    private Expr ParseCast()
    {
        var withItem = _withItem;
        _withItem = false;
        var value = ParsePostfix();
        _withItem = withItem;
        if (withItem || !At(TokenKind.AsKeyword))
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
        if (first is not null && At(TokenKind.Comma))
        {
            // `x[a, b]` indexes with the tuple (a, b), as in Python (M33).
            var elements = ImmutableArray.CreateBuilder<Expr>();
            elements.Add(first);
            while (At(TokenKind.Comma))
            {
                Next();
                if (At(TokenKind.CloseBracket))
                    break;
                elements.Add(ParseExpr());
            }
            first = new TupleExpr(elements.ToImmutable(), SpanFrom(first.Span.Start));
        }
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
        // A generator expression that's the only argument drops its parentheses: sum(x for x in xs) (M36).
        if (!At(TokenKind.CloseParen))
        {
            var start = Current.Span.Start;
            var first = ParseArg();
            if (At(TokenKind.ForKeyword) && first is { Name: null, Unpack: ArgumentUnpack.None })
            {
                var generator = ParseComprehension(ComprehensionKind.Generator, first.Value, null, TokenKind.CloseParen, start);
                return [new Argument(null, generator, generator.Span)];
            }
            if (At(TokenKind.Comma))
            {
                Next();
                var rest = ParseCommaList(TokenKind.CloseParen, ParseArg);
                Expect(TokenKind.CloseParen);
                return CheckArgumentOrder([first, .. rest]);
            }
            Expect(TokenKind.CloseParen);
            return [first];
        }
        var arguments = ParseCommaList(TokenKind.CloseParen, ParseArg);
        Expect(TokenKind.CloseParen);
        return CheckArgumentOrder(arguments);
    }

    private ImmutableArray<Argument> CheckArgumentOrder(ImmutableArray<Argument> arguments)
    {

        // A positional argument after a keyword argument is never valid (SPEC §2); `*e` counts as
        // positional, and `**e` as a keyword (M30).
        static bool IsKeyword(Argument a) => a.Name is not null || a.Unpack == ArgumentUnpack.Keyword;
        var firstKeyword = arguments.IndexOf(arguments.FirstOrDefault(IsKeyword)!);
        if (firstKeyword >= 0 && arguments.Skip(firstKeyword).FirstOrDefault(a => !IsKeyword(a)) is { } positional)
            ReportSyntaxError(bag => bag.ReportMixedArguments(positional.Span));
        return arguments;
    }

    // arg = expr | NAME "=" expr
    private Argument ParseArg()
    {
        var start = Current.Span.Start;
        // `*e` and `**e` unpack into arguments (M30); `**` is two adjacent '*' tokens.
        if (At(TokenKind.Star))
        {
            var unpack = ArgumentUnpack.Positional;
            Next();
            if (At(TokenKind.Star) && Current.Span.Start == start + 1)
            {
                Next();
                unpack = ArgumentUnpack.Keyword;
            }
            var unpacked = ParseExpr();
            return new Argument(null, unpacked, SpanFrom(start)) { Unpack = unpack };
        }
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
            case TokenKind.Ellipsis:
                Next();
                return new EllipsisExpr(token.Span);
            case TokenKind.Name:
                Next();
                return new NameExpr(token.Text, token.Span);
            // "(" expr ")" | "(" expr "," [ expr { "," expr } [ "," ] ] ")"
            case TokenKind.OpenParen:
            {
                Next();
                // "(" yield_expr ")" (SPEC §3.20, M42)
                if (At(TokenKind.YieldKeyword))
                {
                    var yield = ParseYieldExpr();
                    Expect(TokenKind.CloseParen);
                    return yield;
                }
                var inner = ParseExpr();
                if (At(TokenKind.ForKeyword))
                    return ParseComprehension(ComprehensionKind.Generator, inner, null, TokenKind.CloseParen, token.Span.Start);
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
                if (!At(TokenKind.CloseBracket))
                {
                    var first = ParseExpr();
                    if (At(TokenKind.ForKeyword))
                        return ParseComprehension(ComprehensionKind.List, first, null, TokenKind.CloseBracket, token.Span.Start);
                    var rest = ImmutableArray<Expr>.Empty;
                    if (At(TokenKind.Comma))
                    {
                        Next();
                        rest = ParseCommaList(TokenKind.CloseBracket, ParseExpr);
                    }
                    Expect(TokenKind.CloseBracket);
                    return new ListExpr([first, .. rest], SpanFrom(token.Span.Start));
                }
                var elements = ParseCommaList(TokenKind.CloseBracket, ParseExpr);
                Expect(TokenKind.CloseBracket);
                return new ListExpr(elements, SpanFrom(token.Span.Start));
            }
            case TokenKind.OpenBrace:
            {
                Next();
                // `{a, b}` is a set, and `{}` or `{k: v}` a dict (SPEC §3.15, M35).
                if (!At(TokenKind.CloseBrace))
                {
                    var first = ParseExpr();
                    if (At(TokenKind.ForKeyword))
                        return ParseComprehension(ComprehensionKind.Set, first, null, TokenKind.CloseBrace, token.Span.Start);
                    if (!At(TokenKind.Colon))
                    {
                        var elements = ImmutableArray.CreateBuilder<Expr>();
                        elements.Add(first);
                        while (At(TokenKind.Comma))
                        {
                            Next();
                            if (At(TokenKind.CloseBrace))
                                break;
                            elements.Add(ParseExpr());
                        }
                        Expect(TokenKind.CloseBrace);
                        return new SetExpr(elements.ToImmutable(), SpanFrom(token.Span.Start));
                    }
                    Next();
                    var firstValue = ParseExpr();
                    if (At(TokenKind.ForKeyword))
                        return ParseComprehension(ComprehensionKind.Dict, first, firstValue, TokenKind.CloseBrace, token.Span.Start);
                    var firstEntry = new DictEntry(first, firstValue, SpanFrom(first.Span.Start));
                    var rest = ImmutableArray<DictEntry>.Empty;
                    if (At(TokenKind.Comma))
                    {
                        Next();
                        rest = ParseCommaList(TokenKind.CloseBrace, ParseDictEntry);
                    }
                    Expect(TokenKind.CloseBrace);
                    return new DictExpr([firstEntry, .. rest], SpanFrom(token.Span.Start));
                }
                Next();
                return new DictExpr([], SpanFrom(token.Span.Start));
            }
            case TokenKind.MatchKeyword:
                return ParseMatchExpr();
            default:
                ReportExpected("expression");
                return new ErrorExpr(Here);
        }
    }

    // comp_for = "for" ( NAME | names ) "in" coalesce { "for" … "in" coalesce | "if" coalesce } (SPEC §3.16, M36)
    private ComprehensionExpr ParseComprehension(ComprehensionKind kind, Expr element, Expr? value, TokenKind close, int start)
    {
        var clauses = ImmutableArray.CreateBuilder<ComprehensionClause>();
        while (At(TokenKind.ForKeyword) || clauses.Count > 0 && At(TokenKind.IfKeyword))
        {
            var clauseStart = Current.Span.Start;
            if (Next().Kind == TokenKind.IfKeyword)
            {
                clauses.Add(new ComprehensionIf(ParseCoalesce(), SpanFrom(clauseStart)));
                continue;
            }
            var variables = ParseNames();
            Expect(TokenKind.InKeyword);
            clauses.Add(new ComprehensionFor(variables, ParseCoalesce(), SpanFrom(clauseStart)));
        }
        Expect(close);
        return new ComprehensionExpr(kind, element, value, clauses.ToImmutable(), SpanFrom(start));
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
                FStringHole hole => new FStringHoleSegment(ParseHole(hole.Span), hole.Span)
                {
                    DebugText = hole.DebugText,
                    Conversion = hole.Conversion,
                    Spec = hole.Spec,
                    SpecSpan = hole.SpecSpan,
                },
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

    private static double ParseFloatValue(Token token) =>
        double.Parse(token.Text.Replace("_", ""), CultureInfo.InvariantCulture);

    private BinaryExpr Binary(Expr left, BinaryOp op, Expr right) =>
        new(left, op, right, SpanFrom(left.Span.Start));

    private static bool CanStartExpression(TokenKind kind) => kind is
        TokenKind.Int or TokenKind.Float or TokenKind.String or TokenKind.FString
        or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NoneKeyword
        or TokenKind.Name or TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace
        or TokenKind.Minus or TokenKind.NotKeyword or TokenKind.LambdaKeyword or TokenKind.Ellipsis
        or TokenKind.MatchKeyword or TokenKind.Star or TokenKind.AwaitKeyword;
}
