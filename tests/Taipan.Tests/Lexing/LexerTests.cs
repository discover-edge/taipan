using Taipan.Compiler.Lexing;
using Taipan.Compiler.Text;

namespace Taipan.Tests.Lexing;

public class LexerTests
{
    // Token stream as space-separated kinds; names, numbers, and strings show their text.
    private static string Dump(string text)
    {
        var result = Lexer.Lex(SourceText.From(text));
        Assert.Empty(result.Diagnostics);
        return DumpTokens(result.Tokens);
    }

    private static string DumpTokens(IEnumerable<Token> tokens) =>
        string.Join(" ", tokens.Select(t => t.Kind switch
        {
            TokenKind.Name or TokenKind.Int or TokenKind.Float or TokenKind.String or TokenKind.FString
                => $"{t.Kind}({t.Text})",
            _ => t.Kind.ToString(),
        }));

    // Diagnostics as "line:col code" for compact assertions.
    private static string[] Errors(string text)
    {
        var source = SourceText.From(text);
        return [.. Lexer.Lex(source).Diagnostics.Select(d =>
        {
            var p = source.GetLinePosition(d.Span.Start);
            return $"{p.Line}:{p.Column} {d.Code}";
        })];
    }

    [Fact]
    public void EmptyFileIsJustEndOfFile() =>
        Assert.Equal("EndOfFile", Dump(""));

    [Fact]
    public void SimpleStatement() =>
        Assert.Equal("LetKeyword Name(x) Equals Int(1) Newline EndOfFile", Dump("let x = 1\n"));

    [Fact]
    public void MissingTrailingNewlineIsSupplied() =>
        Assert.Equal("Name(x) Newline EndOfFile", Dump("x"));

    [Fact]
    public void CrLfIsNormalized() =>
        Assert.Equal("Name(a) Newline Name(b) Newline EndOfFile", Dump("a\r\nb\r\n"));

    [Fact]
    public void NestedBlocks() =>
        Assert.Equal(
            "IfKeyword Name(a) Colon Newline " +
            "Indent IfKeyword Name(b) Colon Newline " +
            "Indent Name(c) Newline " +
            "Dedent Name(d) Newline " +
            "Dedent Name(e) Newline EndOfFile",
            Dump("if a:\n    if b:\n        c\n    d\ne\n"));

    [Fact]
    public void DedentByMultipleLevels() =>
        Assert.Equal(
            "Name(a) Newline Indent Name(b) Newline Indent Name(c) Newline Indent Name(d) Newline " +
            "Dedent Dedent Dedent Name(e) Newline EndOfFile",
            Dump("a\n  b\n    c\n      d\ne\n"));

    [Fact]
    public void OpenBlocksAreClosedAtEndOfFile() =>
        Assert.Equal(
            "Name(a) Newline Indent Name(b) Newline Indent Name(c) Newline Dedent Dedent EndOfFile",
            Dump("a\n  b\n    c"));

    [Fact]
    public void BracketsJoinLinesAndIgnoreIndentation() =>
        Assert.Equal(
            "Name(f) OpenParen Int(1) Comma OpenBracket Int(2) Comma Int(3) CloseBracket Comma " +
            "OpenBrace String(\"k\") Colon Int(4) CloseBrace CloseParen Newline Name(g) Newline EndOfFile",
            Dump("f(1,\n        [2,\n  3],\n{\"k\":\n 4})\ng\n"));

    [Fact]
    public void CommentsAreSkipped() =>
        Assert.Equal(
            "Name(a) Newline Name(b) Newline EndOfFile",
            Dump("# header\na  # trailing\n    # indented comment-only line\nb\n"));

    [Fact]
    public void CommentInsideBracketsIsSkipped() =>
        Assert.Equal(
            "Name(f) OpenParen Int(1) Comma Int(2) CloseParen Newline EndOfFile",
            Dump("f(1,  # one\n  2)\n"));

    [Fact]
    public void BlankAndWhitespaceOnlyLinesDoNotAffectIndentation() =>
        Assert.Equal(
            "IfKeyword Name(a) Colon Newline Indent Name(b) Newline Name(c) Newline Dedent EndOfFile",
            Dump("\n\nif a:\n\n    b\n        \n\n    c\n\n"));

    [Fact]
    public void CommentOnlyFileIsJustEndOfFile() =>
        Assert.Equal("EndOfFile", Dump("# nothing here\n   # still nothing"));

    [Fact]
    public void Keywords() =>
        Assert.Equal(
            "DefKeyword ClassKeyword EnumKeyword LetKeyword VarKeyword ConstKeyword IfKeyword ElifKeyword " +
            "ElseKeyword WhileKeyword ForKeyword InKeyword MatchKeyword ReturnKeyword PassKeyword BreakKeyword " +
            "ContinueKeyword AndKeyword OrKeyword NotKeyword IsKeyword SomeKeyword NoneKeyword TrueKeyword " +
            "FalseKeyword AsKeyword FromKeyword ImportKeyword LambdaKeyword TryKeyword ExceptKeyword " +
            "FinallyKeyword RaiseKeyword Newline EndOfFile",
            Dump("def class enum let var const if elif else while for in match return pass break " +
                 "continue and or not is some None True False as from import lambda try except finally raise"));

    [Theory]
    [InlineData("...", "Ellipsis EndOfFile")]
    [InlineData("a.b", "Name(a) Dot Name(b) EndOfFile")]
    [InlineData("..", "Dot Dot EndOfFile")]
    [InlineData("....", "Ellipsis Dot EndOfFile")]
    [InlineData("1...", "Int(1) Ellipsis EndOfFile")]
    public void Ellipsis(string text, string expected) =>
        Assert.Equal(expected.Replace(" EndOfFile", " Newline EndOfFile"), Dump(text));

    [Fact]
    public void NamesThatResembleKeywords() =>
        Assert.Equal(
            "Name(self) Name(int) Name(true) Name(none) Name(struct) Name(_) Name(_x1) Name(define) Name(f) Newline EndOfFile",
            Dump("self int true none struct _ _x1 define f"));

    [Fact]
    public void OperatorsUseLongestMatch() =>
        Assert.Equal(
            "Plus Minus Star Slash SlashSlash Percent EqualsEquals BangEquals Less LessEquals Greater " +
            "GreaterEquals Equals PlusEquals MinusEquals StarEquals SlashEquals Arrow QuestionQuestion " +
            "Question Dot Comma Colon OpenParen CloseParen OpenBracket CloseBracket OpenBrace CloseBrace At " +
            "Newline EndOfFile",
            Dump("+ - * / // % == != < <= > >= = += -= *= /= -> ?? ? . , : ( ) [ ] { } @"));

    [Fact]
    public void AdjacentOperatorsWithoutSpaces() =>
        Assert.Equal(
            "Name(x) QuestionQuestion Name(y) Name(T) QuestionQuestion Minus Minus Name(a) Newline EndOfFile",
            Dump("x??y T??--a"));

    [Theory]
    [InlineData("0", "Int(0)")]
    [InlineData("42", "Int(42)")]
    [InlineData("1_000_000", "Int(1_000_000)")]
    [InlineData("9223372036854775808", "Int(9223372036854775808)")]
    [InlineData("1.5", "Float(1.5)")]
    [InlineData("0.25", "Float(0.25)")]
    [InlineData("1e9", "Float(1e9)")]
    [InlineData("2.5e-3", "Float(2.5e-3)")]
    [InlineData("1E+10", "Float(1E+10)")]
    [InlineData("1_0.0_1e1_0", "Float(1_0.0_1e1_0)")]
    public void NumberLiterals(string text, string expected) =>
        Assert.Equal($"{expected} Newline EndOfFile", Dump(text));

    [Fact]
    public void TrailingDotIsIntThenDot() =>
        Assert.Equal("Int(1) Dot Name(x) Newline EndOfFile", Dump("1.x"));

    [Fact]
    public void LeadingDotIsNotANumber() =>
        Assert.Equal("Dot Int(5) Newline EndOfFile", Dump(".5"));

    [Fact]
    public void StringValuesAreDecoded()
    {
        var tokens = Lexer.Lex(SourceText.From("\"a\\nb\" 'it\\'s' \"\\t\\\\\\\"\\{\\}\" '\"' \"'\"")).Tokens;
        Assert.Equal(["a\nb", "it's", "\t\\\"{}", "\"", "'"], tokens.Where(t => t.Kind == TokenKind.String).Select(t => t.Value));
    }

    [Fact]
    public void StringsMayContainNonAscii()
    {
        var tokens = Lexer.Lex(SourceText.From("\"héllo 🐍\"")).Tokens;
        Assert.Equal("héllo 🐍", tokens[0].Value);
    }

    [Fact]
    public void FStringPartsAndHoleSpans()
    {
        const string text = "f\"a{x}b{{c}}{f(1, y)}\\n\"";
        var token = Lexer.Lex(SourceText.From(text)).Tokens[0];

        Assert.Equal(TokenKind.FString, token.Kind);
        Assert.Equal(text, token.Text);
        Assert.Collection(token.Parts,
            p => Assert.Equal("a", Assert.IsType<FStringText>(p).Value),
            p => Assert.Equal("x", Hole(p)),
            p => Assert.Equal("b{c}", Assert.IsType<FStringText>(p).Value),
            p => Assert.Equal("f(1, y)", Hole(p)),
            p => Assert.Equal("\n", Assert.IsType<FStringText>(p).Value));

        string Hole(FStringPart part) => text.Substring(Assert.IsType<FStringHole>(part).Span.Start, part.Span.Length);
    }

    [Fact]
    public void FStringHoleMayContainBracesAndStringsOfEitherQuote()
    {
        const string text = "f\"{d[\"k\"]} {g({'a': 1})} {'}'}\"";
        var token = Lexer.Lex(SourceText.From(text)).Tokens[0];
        Assert.Empty(Lexer.Lex(SourceText.From(text)).Diagnostics);
        var holes = token.Parts.OfType<FStringHole>().Select(h => text.Substring(h.Span.Start, h.Span.Length));
        Assert.Equal(["d[\"k\"]", "g({'a': 1})", "'}'"], holes);
    }

    [Fact]
    public void SingleQuotedFString() =>
        Assert.Equal("FString(f'{x}') Newline EndOfFile", Dump("f'{x}'"));

    [Fact]
    public void FFollowedBySpaceIsAName() =>
        Assert.Equal("Name(f) String(\"x\") Newline EndOfFile", Dump("f \"x\""));

    [Fact]
    public void TabsBetweenTokensAreWhitespace() =>
        Assert.Equal("Name(a) Plus Name(b) Newline EndOfFile", Dump("a\t+\tb"));

    [Fact]
    public void ColumnsCountCodePoints() =>
        Assert.Equal(["1:5 TP0004"], Errors("\"🐍\" $"));

    // TP0001

    [Fact]
    public void TabInIndentation() =>
        Assert.Equal(["2:3 TP0001"], Errors("if a:\n  \tb\n"));

    [Fact]
    public void TabLineKeepsCurrentIndentation()
    {
        var result = Lexer.Lex(SourceText.From("if a:\n    b\n\tc\n    d\n"));
        Assert.Equal(
            "IfKeyword Name(a) Colon Newline Indent Name(b) Newline Name(c) Newline Name(d) Newline Dedent EndOfFile",
            DumpTokens(result.Tokens));
    }

    // TP0002

    [Fact]
    public void InconsistentDedent() =>
        Assert.Equal(["3:3 TP0002"], Errors("if a:\n    b\n  c\n"));

    [Fact]
    public void InconsistentDedentRecoversToOuterLevel()
    {
        var result = Lexer.Lex(SourceText.From("a\n  b\n      c\n    d\ne\n"));
        Assert.Equal(
            "Name(a) Newline Indent Name(b) Newline Indent Name(c) Newline Dedent Name(d) Newline Dedent Name(e) Newline EndOfFile",
            DumpTokens(result.Tokens));
        Assert.Single(result.Diagnostics);
    }

    // TP0003

    [Fact]
    public void UnterminatedString() =>
        Assert.Equal(["1:5 TP0003"], Errors("x = \"abc\ny = 1\n"));

    [Fact]
    public void UnterminatedStringEndsAtEndOfLine()
    {
        var result = Lexer.Lex(SourceText.From("x = 'abc\ny\n"));
        Assert.Equal("Name(x) Equals String('abc) Newline Name(y) Newline EndOfFile", DumpTokens(result.Tokens));
    }

    [Fact]
    public void BackslashAtEndOfLineIsUnterminated() =>
        Assert.Equal(["1:1 TP0003"], Errors("\"abc\\\n"));

    [Fact]
    public void UnterminatedStringAtEndOfFile() =>
        Assert.Equal(["1:1 TP0003"], Errors("'abc"));

    [Fact]
    public void UnterminatedFStringPointsAtQuote() =>
        Assert.Equal(["1:6 TP0003"], Errors("x = f\"abc\n"));

    [Fact]
    public void FStringHoleOpenAtEndOfLine() =>
        Assert.Equal(["1:2 TP0003"], Errors("f\"{x\n"));

    [Fact]
    public void NestedStringInHoleOpenAtEndOfLine() =>
        Assert.Equal(["1:2 TP0003"], Errors("f\"{d[\"k]}\"\n"));

    // TP0004

    [Fact]
    public void InvalidCharacter() =>
        Assert.Equal(["1:3 TP0004"], Errors("a $ b"));

    [Fact]
    public void InvalidCharacterIsSkipped()
    {
        var result = Lexer.Lex(SourceText.From("a ! b"));
        Assert.Equal("Name(a) Name(b) Newline EndOfFile", DumpTokens(result.Tokens));
    }

    [Fact]
    public void InvalidCharacterMessages()
    {
        var messages = Lexer.Lex(SourceText.From("$ é \r \u00a0 🐍")).Diagnostics.Select(d => d.Message);
        Assert.Equal(
            ["invalid character '$'", "invalid character 'é'", "invalid character U+000D",
             "invalid character U+00A0", "invalid character '🐍'"],
            messages);
    }

    // TP0005

    [Fact]
    public void InvalidEscape()
    {
        var result = Lexer.Lex(SourceText.From("\"a\\qb\""));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TP0005", diagnostic.Code);
        Assert.Equal("invalid escape sequence '\\q'", diagnostic.Message);
        Assert.Equal(new TextSpan(2, 2), diagnostic.Span);
        Assert.Equal("ab", result.Tokens[0].Value);
    }

    [Fact]
    public void InvalidEscapeInFStringLiteral() =>
        Assert.Equal(["1:4 TP0005"], Errors("f\"a\\x{y}\""));

    // TP0006

    [Fact]
    public void SingleCloseBraceInFString() =>
        Assert.Equal(["1:4 TP0006"], Errors("f\"a}b\""));

    [Theory]
    [InlineData("f\"{}\"")]
    [InlineData("f\"{   }\"")]
    public void EmptyFStringHole(string text) =>
        Assert.Equal(["1:3 TP0006"], Errors(text));

    [Fact]
    public void NestedFString() =>
        Assert.Equal(["1:4 TP0006"], Errors("f\"{f'x'}\""));

    [Fact]
    public void NameEndingInFBeforeStringInHoleIsNotNested() =>
        Assert.Empty(Errors("f\"{elf['k']}\""));

    // TP0007

    [Theory]
    [InlineData("1__0")]
    [InlineData("1_")]
    [InlineData("007")]
    [InlineData("0_1")]
    [InlineData("12abc")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1.5_")]
    [InlineData("1e5x")]
    public void InvalidNumber(string text) =>
        Assert.Equal(["1:1 TP0007"], Errors(text));

    [Fact]
    public void InvalidNumberIsStillOneToken()
    {
        var result = Lexer.Lex(SourceText.From("x = 12abc + 1"));
        Assert.Equal("Name(x) Equals Int(12abc) Plus Int(1) Newline EndOfFile", DumpTokens(result.Tokens));
    }

    [Fact]
    public void MultipleErrorsAreAllReported() =>
        Assert.Equal(["1:5 TP0004", "2:6 TP0005", "3:1 TP0007"], Errors("x = $\ny = \"\\q\"\n1__2\n"));
}
