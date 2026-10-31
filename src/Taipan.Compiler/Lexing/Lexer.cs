using System.Collections.Immutable;
using System.Text;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Lexing;

public sealed record LexResult(ImmutableArray<Token> Tokens, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Turns source text into tokens, including INDENT/DEDENT/NEWLINE (SPEC §1).</summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["def"] = TokenKind.DefKeyword,
        ["struct"] = TokenKind.StructKeyword,
        ["enum"] = TokenKind.EnumKeyword,
        ["let"] = TokenKind.LetKeyword,
        ["var"] = TokenKind.VarKeyword,
        ["const"] = TokenKind.ConstKeyword,
        ["if"] = TokenKind.IfKeyword,
        ["elif"] = TokenKind.ElifKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["for"] = TokenKind.ForKeyword,
        ["in"] = TokenKind.InKeyword,
        ["match"] = TokenKind.MatchKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["pass"] = TokenKind.PassKeyword,
        ["break"] = TokenKind.BreakKeyword,
        ["continue"] = TokenKind.ContinueKeyword,
        ["and"] = TokenKind.AndKeyword,
        ["or"] = TokenKind.OrKeyword,
        ["not"] = TokenKind.NotKeyword,
        ["is"] = TokenKind.IsKeyword,
        ["some"] = TokenKind.SomeKeyword,
        ["none"] = TokenKind.NoneKeyword,
        ["True"] = TokenKind.TrueKeyword,
        ["False"] = TokenKind.FalseKeyword,
        ["as"] = TokenKind.AsKeyword,
        ["from"] = TokenKind.FromKeyword,
        ["import"] = TokenKind.ImportKeyword,
    };

    private readonly string _text;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly ImmutableArray<Token>.Builder _tokens = ImmutableArray.CreateBuilder<Token>();
    private readonly Stack<int> _indents = new([0]);
    private int _pos;
    private int _bracketDepth;
    private bool _atLineStart = true;

    private Lexer(SourceText source) => _text = source.Text;

    public static LexResult Lex(SourceText source)
    {
        var lexer = new Lexer(source);
        lexer.Run();
        return new LexResult(lexer._tokens.ToImmutable(), lexer._diagnostics.ToImmutable());
    }

    private bool AtEnd => _pos >= _text.Length;

    private char Current => Peek(0);

    private char Peek(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private void Run()
    {
        // _atLineStart is only set by a NEWLINE token, which is only emitted outside brackets.
        while (!AtEnd)
        {
            if (_atLineStart)
                ReadIndentation();
            else
                ScanToken();
        }

        if (!_atLineStart)
            Emit(TokenKind.Newline, _text.Length, 0);
        while (_indents.Count > 1)
        {
            _indents.Pop();
            Emit(TokenKind.Dedent, _text.Length, 0);
        }
        Emit(TokenKind.EndOfFile, _text.Length, 0);
    }

    /// <summary>
    /// Handles the start of a physical line outside brackets. Blank and comment-only lines are
    /// consumed whole and leave us at a line start; otherwise emits INDENT/DEDENT (SPEC §1.2, §1.4).
    /// </summary>
    private void ReadIndentation()
    {
        var lineStart = _pos;
        var tabPos = -1;
        while (Current is ' ' or '\t' && !AtEnd)
        {
            if (Current == '\t' && tabPos < 0)
                tabPos = _pos;
            _pos++;
        }

        if (AtEnd)
            return;
        if (Current == '\n')
        {
            _pos++;
            return;
        }
        if (Current == '#')
        {
            SkipComment();
            if (!AtEnd)
                _pos++;
            return;
        }

        _atLineStart = false;
        if (tabPos >= 0)
        {
            _diagnostics.ReportTabInIndentation(new TextSpan(tabPos, 1));
            return;
        }

        var indent = _pos - lineStart;
        if (indent > _indents.Peek())
        {
            _indents.Push(indent);
            Emit(TokenKind.Indent, _pos, 0);
            return;
        }

        while (indent < _indents.Peek())
        {
            _indents.Pop();
            Emit(TokenKind.Dedent, _pos, 0);
        }
        if (indent != _indents.Peek())
            _diagnostics.ReportInconsistentDedent(new TextSpan(_pos, 1));
    }

    private void ScanToken()
    {
        var c = Current;
        switch (c)
        {
            case ' ' or '\t':
                _pos++;
                break;
            case '\n' when _bracketDepth > 0:
                _pos++;
                break;
            case '\n':
                Emit(TokenKind.Newline, _pos, 1);
                _pos++;
                _atLineStart = true;
                break;
            case '#':
                SkipComment();
                break;
            case '"' or '\'':
                ScanString();
                break;
            case 'f' when Peek(1) is '"' or '\'':
                ScanFString();
                break;
            case >= '0' and <= '9':
                ScanNumber();
                break;
            case '_' or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'):
                ScanName();
                break;
            default:
                ScanOperator();
                break;
        }
    }

    private void SkipComment()
    {
        while (!AtEnd && Current != '\n')
            _pos++;
    }

    private void ScanName()
    {
        var start = _pos;
        while (IsIdentifierChar(Current) && !AtEnd)
            _pos++;
        var text = _text[start.._pos];
        Emit(Keywords.GetValueOrDefault(text, TokenKind.Name), start, _pos - start);
    }

    private void ScanNumber()
    {
        var start = _pos;
        var valid = ScanDigits();
        if (_pos - start > 1 && _text[start] == '0')
            valid = false;

        var isFloat = false;
        if (Current == '.' && char.IsAsciiDigit(Peek(1)))
        {
            _pos++;
            valid &= ScanDigits();
            isFloat = true;
        }

        var exponentDigit = Peek(1) is '+' or '-' ? Peek(2) : Peek(1);
        if (Current is 'e' or 'E' && char.IsAsciiDigit(exponentDigit))
        {
            _pos += Peek(1) is '+' or '-' ? 2 : 1;
            valid &= ScanDigits();
            isFloat = true;
        }

        if (IsIdentifierChar(Current) && !AtEnd)
        {
            while (IsIdentifierChar(Current) && !AtEnd)
                _pos++;
            valid = false;
        }

        if (!valid)
            _diagnostics.ReportInvalidNumber(new TextSpan(start, _pos - start));
        Emit(isFloat ? TokenKind.Float : TokenKind.Int, start, _pos - start);
    }

    /// <summary>Scans <c>[0-9_]*</c> from a digit; false if an <c>_</c> isn't between two digits.</summary>
    private bool ScanDigits()
    {
        var valid = true;
        var previous = '\0';
        while (char.IsAsciiDigit(Current) || Current == '_')
        {
            if (Current == '_' && previous == '_')
                valid = false;
            previous = Current;
            _pos++;
        }
        return valid && previous != '_';
    }

    private void ScanString()
    {
        var start = _pos;
        var quote = Current;
        _pos++;

        var value = new StringBuilder();
        while (true)
        {
            if (AtEnd || Current == '\n')
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(start, 1));
                break;
            }
            if (Current == quote)
            {
                _pos++;
                break;
            }
            if (Current == '\\')
            {
                ReadEscape(value);
                continue;
            }
            value.Append(Current);
            _pos++;
        }

        _tokens.Add(new Token(TokenKind.String, new TextSpan(start, _pos - start), _text[start.._pos])
        {
            Value = value.ToString(),
        });
    }

    private void ScanFString()
    {
        var start = _pos;
        var quotePos = _pos + 1;
        var quote = _text[quotePos];
        _pos += 2;

        var parts = ImmutableArray.CreateBuilder<FStringPart>();
        var literal = new StringBuilder();
        var literalStart = _pos;

        void FlushLiteral()
        {
            if (literal.Length > 0)
                parts.Add(new FStringText(literal.ToString(), new TextSpan(literalStart, _pos - literalStart)));
            literal.Clear();
        }

        while (true)
        {
            if (AtEnd || Current == '\n')
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(quotePos, 1));
                break;
            }
            var c = Current;
            if (c == quote)
            {
                FlushLiteral();
                _pos++;
                break;
            }
            if (c == '\\')
            {
                ReadEscape(literal);
                continue;
            }
            if ((c == '{' || c == '}') && Peek(1) == c)
            {
                literal.Append(c);
                _pos += 2;
                continue;
            }
            if (c == '{')
            {
                FlushLiteral();
                if (!ScanHole(parts, quotePos))
                    break;
                literalStart = _pos;
                continue;
            }
            if (c == '}')
                _diagnostics.ReportSingleCloseBraceInFString(new TextSpan(_pos, 1));
            else
                literal.Append(c);
            _pos++;
        }
        FlushLiteral();

        _tokens.Add(new Token(TokenKind.FString, new TextSpan(start, _pos - start), _text[start.._pos])
        {
            Parts = parts.ToImmutable(),
        });
    }

    /// <summary>
    /// Scans a hole from its <c>{</c> through its matching <c>}</c>, tracking nested brackets and
    /// skipping nested string literals. Returns false if the line ends first (reported as TP0003).
    /// </summary>
    private bool ScanHole(ImmutableArray<FStringPart>.Builder parts, int quotePos)
    {
        var open = _pos;
        _pos++;
        var exprStart = _pos;
        var depth = 0;

        while (true)
        {
            if (AtEnd || Current == '\n')
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(quotePos, 1));
                return false;
            }
            var c = Current;
            if (c == '}' && depth == 0)
                break;
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}' && depth > 0)
                depth--;
            else if (c is '"' or '\'')
            {
                var prefix = _pos - 1;
                if (prefix >= exprStart && _text[prefix] == 'f' && (prefix == exprStart || !IsIdentifierChar(_text[prefix - 1])))
                    _diagnostics.ReportNestedFString(new TextSpan(prefix, 1));
                if (!SkipNestedString(quotePos))
                    return false;
                continue;
            }
            _pos++;
        }

        var exprSpan = new TextSpan(exprStart, _pos - exprStart);
        if (string.IsNullOrWhiteSpace(_text.Substring(exprSpan.Start, exprSpan.Length)))
            _diagnostics.ReportEmptyFStringHole(new TextSpan(open, 1));
        parts.Add(new FStringHole(exprSpan));
        _pos++;
        return true;
    }

    /// <summary>Skips a string literal inside a hole; the parser validates it when re-lexing the hole.</summary>
    private bool SkipNestedString(int quotePos)
    {
        var quote = Current;
        _pos++;
        while (true)
        {
            if (AtEnd || Current == '\n')
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(quotePos, 1));
                return false;
            }
            if (Current == quote)
            {
                _pos++;
                return true;
            }
            _pos += Current == '\\' && Peek(1) != '\n' && _pos + 1 < _text.Length ? 2 : 1;
        }
    }

    /// <summary>
    /// Decodes the escape at <c>\</c> into <paramref name="value"/>. A backslash at end of line is
    /// consumed alone so the caller reports the string as unterminated.
    /// </summary>
    private void ReadEscape(StringBuilder value)
    {
        if (_pos + 1 >= _text.Length || Peek(1) == '\n')
        {
            _pos++;
            return;
        }

        var decoded = Peek(1) switch
        {
            'n' => '\n',
            't' => '\t',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '{' => '{',
            '}' => '}',
            _ => (char?)null,
        };

        if (decoded is { } d)
        {
            value.Append(d);
            _pos += 2;
            return;
        }

        var rune = RuneAt(_pos + 1, out var length);
        _diagnostics.ReportInvalidEscape(new TextSpan(_pos, 1 + length), rune);
        _pos += 1 + length;
    }

    private void ScanOperator()
    {
        var next = Peek(1);
        var (kind, length) = Current switch
        {
            '+' when next == '=' => (TokenKind.PlusEquals, 2),
            '+' => (TokenKind.Plus, 1),
            '-' when next == '=' => (TokenKind.MinusEquals, 2),
            '-' when next == '>' => (TokenKind.Arrow, 2),
            '-' => (TokenKind.Minus, 1),
            '*' when next == '=' => (TokenKind.StarEquals, 2),
            '*' => (TokenKind.Star, 1),
            '/' when next == '/' => (TokenKind.SlashSlash, 2),
            '/' when next == '=' => (TokenKind.SlashEquals, 2),
            '/' => (TokenKind.Slash, 1),
            '%' => (TokenKind.Percent, 1),
            '=' when next == '=' => (TokenKind.EqualsEquals, 2),
            '=' => (TokenKind.Equals, 1),
            '!' when next == '=' => (TokenKind.BangEquals, 2),
            '<' when next == '=' => (TokenKind.LessEquals, 2),
            '<' => (TokenKind.Less, 1),
            '>' when next == '=' => (TokenKind.GreaterEquals, 2),
            '>' => (TokenKind.Greater, 1),
            '?' when next == '?' => (TokenKind.QuestionQuestion, 2),
            '?' => (TokenKind.Question, 1),
            '.' => (TokenKind.Dot, 1),
            ',' => (TokenKind.Comma, 1),
            ':' => (TokenKind.Colon, 1),
            '(' => (TokenKind.OpenParen, 1),
            ')' => (TokenKind.CloseParen, 1),
            '[' => (TokenKind.OpenBracket, 1),
            ']' => (TokenKind.CloseBracket, 1),
            '{' => (TokenKind.OpenBrace, 1),
            '}' => (TokenKind.CloseBrace, 1),
            _ => (TokenKind.EndOfFile, 0),
        };

        if (length == 0)
        {
            var rune = RuneAt(_pos, out var runeLength);
            _diagnostics.ReportInvalidCharacter(new TextSpan(_pos, runeLength), rune);
            _pos += runeLength;
            return;
        }

        if (kind is TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace)
            _bracketDepth++;
        else if (kind is TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace && _bracketDepth > 0)
            _bracketDepth--;

        Emit(kind, _pos, length);
        _pos += length;
    }

    /// <summary>The code point at <paramref name="index"/>; a lone surrogate is reported as itself.</summary>
    private Rune RuneAt(int index, out int length)
    {
        if (Rune.DecodeFromUtf16(_text.AsSpan(index), out var rune, out length) == System.Buffers.OperationStatus.Done)
            return rune;
        length = 1;
        return Rune.ReplacementChar;
    }

    private void Emit(TokenKind kind, int start, int length) =>
        _tokens.Add(new Token(kind, new TextSpan(start, length), _text.Substring(start, length)));

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
