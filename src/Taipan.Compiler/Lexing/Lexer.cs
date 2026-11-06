using System.Collections.Immutable;
using System.Text;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Lexing;

public sealed record LexResult(ImmutableArray<Token> Tokens, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>Turns source text into tokens, including INDENT/DEDENT/NEWLINE (SPEC §1).</summary>
public sealed class Lexer
{
    private readonly string _text;
    private readonly int _end;
    private readonly bool _isHole;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly ImmutableArray<Token>.Builder _tokens = ImmutableArray.CreateBuilder<Token>();
    private readonly Stack<int> _indents = new([0]);
    private int _pos;
    private int _bracketDepth;
    private bool _atLineStart;

    // A match expression's arms inside brackets are an indented block (SPEC §1.2, M29). Each
    // `match` seen inside brackets waits for its colon, with the bracket depth and the indentation
    // of its line; each block entered keeps what to restore when a line ends it.
    private readonly Stack<(int Depth, int LineIndent)> _pendingMatches = new();
    private readonly Stack<(int Depth, int BaseIndent, int IndentCount)> _armBlocks = new();

    private Lexer(SourceText source, TextSpan span, bool isHole)
    {
        _text = source.Text;
        _pos = span.Start;
        _end = span.End;
        _isHole = isHole;
        _atLineStart = !isHole;
    }

    public static LexResult Lex(SourceText source) =>
        Run(new Lexer(source, new TextSpan(0, source.Length), isHole: false));

    /// <summary>
    /// Re-lexes an f-string hole (SPEC §2). Spans stay relative to the whole file. The result has
    /// no NEWLINE, INDENT or DEDENT tokens: just the expression's tokens and EOF at the hole's end.
    /// </summary>
    public static LexResult LexHole(SourceText source, TextSpan hole) =>
        Run(new Lexer(source, hole, isHole: true));

    private static LexResult Run(Lexer lexer)
    {
        lexer.Run();
        return new LexResult(lexer._tokens.ToImmutable(), lexer._diagnostics.ToImmutable());
    }

    private bool AtEnd => _pos >= _end;

    private char Current => Peek(0);

    private char Peek(int offset) => _pos + offset < _end ? _text[_pos + offset] : '\0';

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

        if (!_atLineStart && !_isHole)
            Emit(TokenKind.Newline, _end, 0);
        while (_indents.Count > 1)
        {
            _indents.Pop();
            Emit(TokenKind.Dedent, _end, 0);
        }
        Emit(TokenKind.EndOfFile, _end, 0);
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
        // A line indented no more than the match's line ends its arms: back to the enclosing brackets.
        if (_armBlocks.TryPeek(out var block) && indent <= block.BaseIndent)
        {
            _armBlocks.Pop();
            while (_indents.Count > block.IndentCount)
            {
                _indents.Pop();
                Emit(TokenKind.Dedent, _pos, 0);
            }
            _bracketDepth = block.Depth;
            return;
        }
        var floor = _armBlocks.TryPeek(out var inner) ? inner.IndentCount : 1;
        if (indent > _indents.Peek())
        {
            _indents.Push(indent);
            Emit(TokenKind.Indent, _pos, 0);
            return;
        }

        while (indent < _indents.Peek() && _indents.Count > floor)
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
                ScanString(prefix: 0, raw: false);
                break;
            case 'f' when Peek(1) is '"' or '\'':
                ScanFString(prefix: 1, raw: false);
                break;
            case 'r' when Peek(1) is '"' or '\'':
                ScanString(prefix: 1, raw: true);
                break;
            case 'r' or 'f' when Peek(1) is 'r' or 'f' && Peek(1) != Current && Peek(2) is '"' or '\'':
                ScanFString(prefix: 2, raw: true);
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
        var kind = TokenFacts.Keywords.GetValueOrDefault(text, TokenKind.Name);
        if (kind == TokenKind.MatchKeyword && _bracketDepth > 0 && !_isHole)
            _pendingMatches.Push((_bracketDepth, LineIndent(start)));
        Emit(kind, start, _pos - start);
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

    // Moves past a string's opening quote or quotes, and says whether it's triple-quoted.
    private bool OpenQuote(char quote)
    {
        var triple = Peek(1) == quote && Peek(2) == quote;
        _pos += triple ? 3 : 1;
        return triple;
    }

    private bool AtCloseQuote(char quote, bool triple) =>
        Current == quote && (!triple || (Peek(1) == quote && Peek(2) == quote));

    // A raw string keeps a backslash and the character after it, which can't close the string. In an
    // f-string, a brace after it still opens a hole, as in Python (SPEC §1).
    private void ReadRawBackslash(StringBuilder value, bool inFString)
    {
        value.Append('\\');
        _pos++;
        if (!AtEnd && !(inFString && Current is '{' or '}'))
        {
            value.Append(Current);
            _pos++;
        }
    }

    private void ScanString(int prefix, bool raw)
    {
        var start = _pos;
        _pos += prefix;
        var quotePos = _pos;
        var quote = Current;
        var triple = OpenQuote(quote);

        var value = new StringBuilder();
        while (true)
        {
            if (AtEnd || (Current == '\n' && !triple))
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(quotePos, 1));
                break;
            }
            if (AtCloseQuote(quote, triple))
            {
                _pos += triple ? 3 : 1;
                break;
            }
            if (Current == '\\')
            {
                if (raw)
                    ReadRawBackslash(value, inFString: false);
                else
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

    private void ScanFString(int prefix, bool raw)
    {
        var start = _pos;
        var quotePos = _pos + prefix;
        var quote = _text[quotePos];
        _pos += prefix;
        var triple = OpenQuote(quote);

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
            if (AtEnd || (Current == '\n' && !triple))
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(quotePos, 1));
                break;
            }
            var c = Current;
            if (AtCloseQuote(quote, triple))
            {
                FlushLiteral();
                _pos += triple ? 3 : 1;
                break;
            }
            if (c == '\\')
            {
                if (raw)
                    ReadRawBackslash(literal, inFString: true);
                else
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
    /// skipping nested string literals. At the hole's top level, a <c>!</c> (not <c>!=</c>) starts
    /// a conversion and a <c>:</c> a format spec, and a trailing <c>=</c> marks a debug hole (SPEC §1).
    /// Returns false if the line ends first (reported as TP0003).
    /// </summary>
    private bool ScanHole(ImmutableArray<FStringPart>.Builder parts, int quotePos)
    {
        var open = _pos;
        _pos++;
        var exprStart = _pos;
        var depth = 0;
        int? conversionAt = null, specAt = null;

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
            // A spec is literal text up to the hole's end; a brace in it would be a nested spec,
            // reported once, with its braces matched so the hole still ends at its own.
            if (specAt is not null)
            {
                if (c == '{' && depth++ == 0)
                    _diagnostics.ReportNestedFormatSpec(new TextSpan(_pos, 1));
                else if (c == '}')
                    depth--;
                _pos++;
                continue;
            }
            if (depth == 0 && c == ':')
            {
                specAt = _pos;
                _pos++;
                continue;
            }
            if (depth == 0 && c == '!' && Peek(1) != '=' && conversionAt is null)
            {
                conversionAt = _pos;
                _pos++;
                continue;
            }
            if (conversionAt is not null)
            {
                _pos++;
                continue;
            }
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}' && depth > 0)
                depth--;
            else if (c is '"' or '\'')
            {
                var prefix = _pos - 1;
                if (prefix >= exprStart && IsFStringPrefix(exprStart, ref prefix))
                    _diagnostics.ReportNestedFString(new TextSpan(prefix, 1));
                if (!SkipNestedString(quotePos))
                    return false;
                continue;
            }
            _pos++;
        }

        var close = _pos;
        var exprEnd = conversionAt ?? specAt ?? close;
        var region = _text[exprStart..exprEnd];
        // `{x=}` and `{x = }`: an `=` ending the expression, which isn't part of ==, !=, <= or >=.
        string? debugText = null;
        var trimmed = region.TrimEnd();
        if (trimmed.EndsWith('=') && (trimmed.Length == 1 || trimmed[^2] is not ('=' or '!' or '<' or '>')))
        {
            debugText = region;
            exprEnd = exprStart + trimmed[..^1].TrimEnd().Length;
        }
        var exprSpan = new TextSpan(exprStart, exprEnd - exprStart);
        if (string.IsNullOrWhiteSpace(_text.Substring(exprSpan.Start, exprSpan.Length)))
            _diagnostics.ReportEmptyFStringHole(new TextSpan(open, 1));

        char? conversion = null;
        if (conversionAt is { } bang)
        {
            var text = _text[(bang + 1)..(specAt ?? close)];
            if (text is "r" or "s")
                conversion = text[0];
            else
                _diagnostics.ReportInvalidConversion(new TextSpan(bang, (specAt ?? close) - bang), text);
        }
        string? spec = null;
        var specSpan = default(TextSpan);
        if (specAt is { } colon)
        {
            spec = _text[(colon + 1)..close];
            // The binder checks it against the mini-language, unless the value's class formats itself (SPEC §6, §4.9).
            specSpan = new TextSpan(colon + 1, close - colon - 1);
        }
        parts.Add(new FStringHole(exprSpan) { DebugText = debugText, Conversion = conversion, Spec = spec, SpecSpan = specSpan });
        _pos++;
        return true;
    }

    // Whether the characters before a quote at prefix + 1 are f, rf or fr, as a whole word.
    private bool IsFStringPrefix(int exprStart, ref int prefix)
    {
        var first = prefix;
        if (first > exprStart && _text[first - 1] is 'r' or 'f' && _text[first - 1] != _text[first] && _text[first] is 'r' or 'f')
            first--;
        else if (_text[first] != 'f')
            return false;
        if (first > exprStart && IsIdentifierChar(_text[first - 1]))
            return false;
        prefix = first;
        return true;
    }

    /// <summary>Skips a string literal inside a hole; the parser validates it when re-lexing the hole.</summary>
    private bool SkipNestedString(int quotePos)
    {
        var quote = Current;
        var triple = OpenQuote(quote);
        while (true)
        {
            if (AtEnd || (Current == '\n' && !triple))
            {
                _diagnostics.ReportUnterminatedString(new TextSpan(quotePos, 1));
                return false;
            }
            if (AtCloseQuote(quote, triple))
            {
                _pos += triple ? 3 : 1;
                return true;
            }
            _pos += Current == '\\' && Peek(1) != '\n' && _pos + 1 < _end ? 2 : 1;
        }
    }

    /// <summary>
    /// Decodes the escape at <c>\</c> into <paramref name="value"/>. A backslash at the end of the
    /// source is consumed alone so the caller reports the string as unterminated, and one before a
    /// newline joins the lines (SPEC §1).
    /// </summary>
    private void ReadEscape(StringBuilder value)
    {
        if (_pos + 1 >= _end)
        {
            _pos++;
            return;
        }
        if (Peek(1) == '\n')
        {
            _pos += 2;
            return;
        }

        var decoded = Peek(1) switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            'a' => '\a',
            'b' => '\b',
            'f' => '\f',
            'v' => '\v',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            _ => (char?)null,
        };

        if (decoded is { } d)
        {
            value.Append(d);
            _pos += 2;
            return;
        }

        if (Peek(1) is >= '0' and <= '7')
        {
            var digits = 0;
            var code = 0;
            while (digits < 3 && Peek(1 + digits) is >= '0' and <= '7' and var digit)
            {
                code = code * 8 + (digit - '0');
                digits++;
            }
            value.Append((char)code);
            _pos += 1 + digits;
            return;
        }

        if (Peek(1) is 'x' or 'u' or 'U')
        {
            ReadHexEscape(value, Peek(1) switch { 'x' => 2, 'u' => 4, _ => 8 });
            return;
        }

        var rune = RuneAt(_pos + 1, out var length);
        _diagnostics.ReportInvalidEscape(new TextSpan(_pos, 1 + length), rune);
        _pos += 1 + length;
    }

    // \xhh, \uhhhh or \Uhhhhhhhh, whose value a Taipan string must be able to hold (SPEC §1).
    private void ReadHexEscape(StringBuilder value, int digits)
    {
        var letter = Peek(1);
        var found = 0;
        while (found < digits && Uri.IsHexDigit(Peek(2 + found)))
            found++;
        var span = new TextSpan(_pos, 2 + found);
        if (found < digits)
            _diagnostics.ReportEscapeNeedsHexDigits(span, letter, digits);
        else
        {
            var hex = _text.Substring(_pos + 2, digits);
            var code = long.Parse(hex, System.Globalization.NumberStyles.HexNumber);
            if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
                _diagnostics.ReportEscapeNotACharacter(span, letter, hex);
            else
                value.Append(char.ConvertFromUtf32((int)code));
        }
        _pos += 2 + found;
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
            '*' when next == '*' && Peek(2) == '=' => (TokenKind.StarStarEquals, 3),
            '*' when next == '=' => (TokenKind.StarEquals, 2),
            '*' => (TokenKind.Star, 1),
            '/' when next == '/' && Peek(2) == '=' => (TokenKind.SlashSlashEquals, 3),
            '/' when next == '/' => (TokenKind.SlashSlash, 2),
            '/' when next == '=' => (TokenKind.SlashEquals, 2),
            '/' => (TokenKind.Slash, 1),
            '%' when next == '=' => (TokenKind.PercentEquals, 2),
            '%' => (TokenKind.Percent, 1),
            '|' => (TokenKind.Pipe, 1),
            '&' => (TokenKind.Ampersand, 1),
            '^' => (TokenKind.Caret, 1),
            '=' when next == '=' => (TokenKind.EqualsEquals, 2),
            '=' => (TokenKind.Equals, 1),
            '!' when next == '=' => (TokenKind.BangEquals, 2),
            '<' when next == '=' => (TokenKind.LessEquals, 2),
            '<' => (TokenKind.Less, 1),
            '>' when next == '=' => (TokenKind.GreaterEquals, 2),
            '>' => (TokenKind.Greater, 1),
            '?' when next == '?' => (TokenKind.QuestionQuestion, 2),
            '?' => (TokenKind.Question, 1),
            '.' when next == '.' && Peek(2) == '.' => (TokenKind.Ellipsis, 3),
            '.' => (TokenKind.Dot, 1),
            ',' => (TokenKind.Comma, 1),
            ':' => (TokenKind.Colon, 1),
            '(' => (TokenKind.OpenParen, 1),
            ')' => (TokenKind.CloseParen, 1),
            '[' => (TokenKind.OpenBracket, 1),
            ']' => (TokenKind.CloseBracket, 1),
            '{' => (TokenKind.OpenBrace, 1),
            '}' => (TokenKind.CloseBrace, 1),
            '@' => (TokenKind.At, 1),
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
        if (kind == TokenKind.Colon && _pendingMatches.TryPeek(out var pending) && pending.Depth == _bracketDepth)
        {
            _pendingMatches.Pop();
            if (AtLineEnd())
            {
                _armBlocks.Push((_bracketDepth, pending.LineIndent, _indents.Count));
                _bracketDepth = 0;
            }
        }
        else if (kind is TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace)
        {
            // A match whose colon never came, inside brackets now closed, waits no longer.
            while (_pendingMatches.TryPeek(out var unfinished) && unfinished.Depth > _bracketDepth)
                _pendingMatches.Pop();
        }
    }

    /// <summary>Whether only spaces or a comment are left on the line.</summary>
    private bool AtLineEnd()
    {
        var i = _pos;
        while (i < _end && _text[i] == ' ')
            i++;
        return i >= _end || _text[i] is '\n' or '#';
    }

    /// <summary>The indentation of the line holding <paramref name="index"/>.</summary>
    private int LineIndent(int index)
    {
        var start = _text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var i = start;
        while (i < index && _text[i] == ' ')
            i++;
        return i - start;
    }

    /// <summary>The code point at <paramref name="index"/>; a lone surrogate is reported as itself.</summary>
    private Rune RuneAt(int index, out int length)
    {
        if (Rune.DecodeFromUtf16(_text.AsSpan(index, _end - index), out var rune, out length) == System.Buffers.OperationStatus.Done)
            return rune;
        length = 1;
        return Rune.ReplacementChar;
    }

    private void Emit(TokenKind kind, int start, int length) =>
        _tokens.Add(new Token(kind, new TextSpan(start, length), _text.Substring(start, length)));

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
