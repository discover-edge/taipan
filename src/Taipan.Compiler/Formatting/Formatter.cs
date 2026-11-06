using System.Collections.Immutable;
using System.Text;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Formatting;

/// <summary>A file formatted (SPEC §19), or the lexing and parsing errors that kept it from being.</summary>
public sealed record FormatResult(string? Text, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// <c>taipan fmt</c>'s style (SPEC §19): it re-spaces a file's tokens, line by line, keeping every
/// token, every line break and every comment, so only whitespace changes.
/// </summary>
public static class Formatter
{
    /// <summary>Formats a file that lexes and parses, or gives its errors.</summary>
    public static FormatResult Format(SourceText source)
    {
        var lexed = Lexer.Lex(source);
        if (lexed.Diagnostics.Any(d => !d.IsWarning))
            return new FormatResult(null, lexed.Diagnostics);
        var parsed = Parser.Parse(source, lexed.Tokens);
        if (parsed.Diagnostics.Any(d => !d.IsWarning))
            return new FormatResult(null, parsed.Diagnostics);
        var text = new Layout(source, lexed.Tokens).Write();
        // Formatting changes only whitespace; a change to the tokens would be a bug (SPEC §19.1).
        if (!SameTokens(lexed.Tokens, Lexer.Lex(SourceText.From(text, source.Path)).Tokens))
            throw new InvalidOperationException($"formatting {source.Path} changed its tokens");
        return new FormatResult(text, []);
    }

    private static bool SameTokens(ImmutableArray<Token> before, ImmutableArray<Token> after) =>
        before.Length == after.Length && before.Zip(after).All(p => p.First.Kind == p.Second.Kind && p.First.Text == p.Second.Text);

    private enum Bracket { Call, Subscript, TypeParameters, Parameters, Other }

    /// <summary>The file's lines as the style writes them.</summary>
    private sealed class Layout(SourceText source, ImmutableArray<Token> tokens)
    {
        private readonly StringBuilder _out = new();

        // Each block's column, and the bracket depth where it started: a match expression's arms
        // are a block inside brackets (SPEC §5.1.1).
        private readonly Stack<(int Column, int Depth)> _blocks = new([(0, 0)]);
        private readonly Stack<Bracket> _brackets = new();

        private int _pendingBlanks;
        private int? _armsEnd;
        private bool _wroteAny;
        private bool _blockJustOpened;
        private int _previousColumn;
        private Token? _previousCode;
        // The previous line's last token, which decides whether a block starts.
        private int _lastLineColumn;

        public string Write()
        {
            var code = tokens.Where(t => t.Kind is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent or TokenKind.EndOfFile)).ToList();
            var structure = tokens.Where(t => t.Kind is TokenKind.Indent or TokenKind.Dedent).ToList();
            var lines = GroupLines(code);
            var comments = Comments(code);
            var structureIndex = 0;
            var lineIndex = 0;
            var line = 1;
            var lastLine = source.Text.Length == 0 ? 0 : source.GetLinePosition(source.Text.Length).Line;
            var commentIndex = 0;
            for (; line <= lastLine; line++)
            {
                if (lineIndex < lines.Count && lines[lineIndex].Line == line)
                {
                    var codeLine = lines[lineIndex++];
                    // INDENT and DEDENT tokens sit at the start of the line they affect.
                    while (structureIndex < structure.Count && structure[structureIndex].Span.Start <= codeLine.Tokens[0].Span.Start)
                        Structure(structure[structureIndex++]);
                    // A multi-line string's lines belong to its code line, and a comment follows the last.
                    line = codeLine.LastLine;
                    var trailing = commentIndex < comments.Count && comments[commentIndex].Line == line ? comments[commentIndex++] : default;
                    WriteCode(codeLine.Tokens, trailing.Text);
                    continue;
                }
                if (commentIndex < comments.Count && comments[commentIndex].Line == line)
                {
                    var comment = comments[commentIndex++];
                    // Dedents before the next code line apply to a comment that's indented as it.
                    WriteComment(comment, lineIndex < lines.Count ? lines[lineIndex].Tokens[0] : null, structure, structureIndex);
                    continue;
                }
                _pendingBlanks++;
            }
            if (_wroteAny)
                _out.Append('\n');
            return _out.ToString();
        }

        private void Structure(Token token)
        {
            if (token.Kind == TokenKind.Indent)
                _blocks.Push((_lastLineColumn + 4, _brackets.Count));
            else if (_blocks.Count > 1 && _blocks.Pop() is { Depth: > 0 } arms)
                // Inside brackets, a line indented no more than its match's line is what ends the arms.
                _armsEnd = arms.Column - 4;
        }

        private List<(int Line, int LastLine, List<Token> Tokens)> GroupLines(List<Token> code)
        {
            var lines = new List<(int Line, int LastLine, List<Token> Tokens)>();
            foreach (var token in code)
            {
                var line = source.GetLinePosition(token.Span.Start).Line;
                var lastLine = source.GetLinePosition(token.Span.End).Line;
                if (lines.Count == 0 || lines[^1].LastLine != line)
                    lines.Add((line, lastLine, []));
                lines[^1].Tokens.Add(token);
                lines[^1] = lines[^1] with { LastLine = lastLine };
            }
            return lines;
        }

        /// <summary>Every comment, by its line and column: the text from a <c>#</c> outside any token to the end of its line.</summary>
        private List<(int Line, int Column, string Text)> Comments(List<Token> code)
        {
            var comments = new List<(int, int, string)>();
            var text = source.Text;
            var position = 0;
            for (var k = 0; k <= code.Count; k++)
            {
                var gapEnd = k < code.Count ? code[k].Span.Start : text.Length;
                for (var i = position; i < gapEnd; i++)
                {
                    if (text[i] != '#')
                        continue;
                    var end = text.IndexOf('\n', i);
                    end = end < 0 ? text.Length : end;
                    var at = source.GetLinePosition(i);
                    comments.Add((at.Line, at.Column - 1, text[i..end].TrimEnd()));
                    i = end;
                }
                position = k < code.Count ? code[k].Span.End : gapEnd;
            }
            return comments;
        }

        private int BlockColumn => _blocks.Peek().Column;

        private int ContinuationDepth => _brackets.Count - _blocks.Peek().Depth;

        private void Blanks(bool insideBrackets)
        {
            var allowed = !_wroteAny || _blockJustOpened || insideBrackets ? 0 : BlockColumn == 0 && _brackets.Count == 0 ? 2 : 1;
            for (var i = 0; i < Math.Min(_pendingBlanks, allowed); i++)
                _out.Append('\n');
            _pendingBlanks = 0;
        }

        private void NewLine()
        {
            if (_wroteAny)
                _out.Append('\n');
            _wroteAny = true;
        }

        private void WriteComment((int Line, int Column, string Text) comment, Token? next, List<Token> structure, int structureIndex)
        {
            // The next line's own level, after the dedents that come before it.
            var nextColumn = BlockColumn;
            if (next is not null)
            {
                var columns = _blocks.Select(b => b.Column).ToList();
                var depth = 0;
                for (var i = structureIndex; i < structure.Count && structure[i].Span.Start <= next.Span.Start; i++)
                {
                    if (structure[i].Kind == TokenKind.Dedent)
                        depth++;
                    else
                        depth--;
                }
                nextColumn = depth > 0 ? columns[Math.Min(depth, columns.Count - 1)] : depth < 0 ? BlockColumn + 4 : BlockColumn;
                if (ContinuationDepth > 0)
                    nextColumn = BlockColumn + 4 * ContinuationDepth;
            }
            var nextOriginal = next is null ? 0 : source.GetLinePosition(next.Span.Start).Column - 1;
            var column = next is null ? (comment.Column == 0 ? 0 : _previousColumn)
                : comment.Column > nextOriginal ? Math.Max(_previousColumn, nextColumn) : nextColumn;
            Blanks(insideBrackets: ContinuationDepth > 0);
            NewLine();
            _out.Append(' ', column).Append(comment.Text);
            _previousColumn = column;
            _blockJustOpened = false;
        }

        private void WriteCode(List<Token> line, string? trailing)
        {
            var leadingClosers = line.TakeWhile(t => t.Kind is TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace).Count();
            var continuation = Math.Max(0, ContinuationDepth - leadingClosers);
            var column = _armsEnd ?? BlockColumn + 4 * continuation;
            _armsEnd = null;
            Blanks(insideBrackets: ContinuationDepth > 0);
            NewLine();
            _out.Append(' ', column);
            Token? previous = null;
            for (var i = 0; i < line.Count; i++)
            {
                var token = line[i];
                // `**` is two adjacent `*` tokens, kept together, and unary if the first is.
                if (previous is { Kind: TokenKind.Star } && token.Kind == TokenKind.Star && previous.Span.End == token.Span.Start)
                {
                    _out.Append('*');
                    previous = token with { Text = previous.Text == Marker ? Marker : "**" };
                    _previousCode = token;
                    continue;
                }
                // The token before, across lines inside brackets; nothing at a logical line's start.
                var before = i > 0 ? line[i - 1] : ContinuationDepth > 0 ? _previousCode : null;
                if (previous is not null)
                    _out.Append(Space(previous, token, i + 1 < line.Count ? line[i + 1] : null));
                _out.Append(token.Text);
                var unary = token.Kind is TokenKind.Minus or TokenKind.Plus or TokenKind.Star && !EndsOperand(before);
                Track(token, before);
                _previousCode = token;
                previous = unary ? token with { Text = Marker } : token;
            }
            if (trailing is not null)
                _out.Append("  ").Append(trailing);
            _previousColumn = column;
            _lastLineColumn = column;
            _blockJustOpened = line[^1].Kind == TokenKind.Colon && ContinuationDepth == 0;
        }

        // A unary operator, as the token before the next one: nothing follows it (SPEC §19.2).
        private const string Marker = "\0unary";

        // What may follow the `??` operator: the start of an operand.
        private static bool StartsOperand(Token token) => EndsOperand(token) && token.Kind is not (TokenKind.CloseParen or TokenKind.CloseBracket
            or TokenKind.CloseBrace or TokenKind.Question) || token.Kind is TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace
            or TokenKind.Minus or TokenKind.Plus or TokenKind.NotKeyword or TokenKind.LambdaKeyword or TokenKind.AwaitKeyword or TokenKind.MatchKeyword;

        private static bool EndsOperand(Token? token) => token is not null && token.Kind is TokenKind.Name or TokenKind.Int or TokenKind.Float
            or TokenKind.String or TokenKind.FString or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace
            or TokenKind.NoneKeyword or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.Ellipsis or TokenKind.Question;

                /// <summary>The space between two tokens of a line (SPEC §19.2).</summary>
        private string Space(Token previous, Token token, Token? next)
        {
            // A match expression's arms are a block of their own, outside the brackets around them.
            var inside = ContinuationDepth > 0 ? _brackets.Peek() : (Bracket?)null;
            if (token.Kind is TokenKind.Comma or TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace
                or TokenKind.Dot or TokenKind.Question or TokenKind.Colon)
                return "";
            if (previous.Kind is TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace or TokenKind.Dot or TokenKind.At)
                return "";
            if (previous.Text == Marker)
                return "";
            if (previous.Kind == TokenKind.Colon)
                return inside == Bracket.Subscript ? "" : " ";
            // Keyword arguments and the like have no spaces around `=`; a def's defaults do.
            if ((token.Kind == TokenKind.Equals || previous.Kind == TokenKind.Equals) && inside is not null and not Bracket.Parameters)
                return "";
            if (token.Kind is TokenKind.OpenParen or TokenKind.OpenBracket)
                return EndsOperand(previous) && previous.Kind != TokenKind.Question ? "" : " ";
            // `??` in a type is two optionals, as `int??`, not the operator (SPEC §19.2).
            if (token.Kind == TokenKind.QuestionQuestion && (next is null ? ContinuationDepth == 0 : !StartsOperand(next)))
                return "";
            return " ";
        }

        // Brackets as they open and close, by what they're for (SPEC §19.2).
        private void Track(Token token, Token? before)
        {
            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                    _brackets.Push(_defName || _afterTypeParameters ? Bracket.Parameters : Bracket.Call);
                    break;
                case TokenKind.OpenBracket:
                    _brackets.Push(_defName || _className || _aliasName ? Bracket.TypeParameters
                        : EndsOperand(before) ? Bracket.Subscript : Bracket.Other);
                    break;
                case TokenKind.OpenBrace:
                    _brackets.Push(Bracket.Other);
                    break;
                case TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace:
                    var closed = _brackets.Count > 0 ? _brackets.Pop() : Bracket.Other;
                    _afterTypeParameters = closed == Bracket.TypeParameters && _typeParametersOfDef;
                    _defName = _className = _aliasName = false;
                    return;
            }
            // A def's or class's name, and a type alias's, take type parameters; a def's then its parameters.
            var wasDef = _defName;
            _defName = token.Kind == TokenKind.Name && before?.Kind == TokenKind.DefKeyword;
            _className = token.Kind == TokenKind.Name && before?.Kind is TokenKind.ClassKeyword or TokenKind.EnumKeyword;
            _aliasName = token.Kind == TokenKind.Name && before is { Kind: TokenKind.Name, Text: "type" } && _brackets.Count == 0;
            if (token.Kind == TokenKind.OpenBracket)
                _typeParametersOfDef = wasDef;
            if (token.Kind is not (TokenKind.OpenBracket or TokenKind.OpenParen))
                _afterTypeParameters = false;
        }

        private bool _defName;
        private bool _className;
        private bool _aliasName;
        private bool _typeParametersOfDef;
        private bool _afterTypeParameters;
    }
}
