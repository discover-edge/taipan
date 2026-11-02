using System.Collections.Immutable;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Lexing;

/// <summary>
/// A lexed token. <see cref="Text"/> is the raw source text. String tokens carry their decoded
/// <see cref="Value"/>; f-string tokens carry their <see cref="Parts"/>.
/// </summary>
public sealed record Token(TokenKind Kind, TextSpan Span, string Text)
{
    public string? Value { get; init; }

    public ImmutableArray<FStringPart> Parts { get; init; } = [];
}

public abstract record FStringPart(TextSpan Span);

/// <summary>Literal text between holes, with escapes and doubled braces decoded.</summary>
public sealed record FStringText(string Value, TextSpan Span) : FStringPart(Span);

/// <summary>
/// A hole; <see cref="FStringPart.Span"/> covers its expression. Since M26 it may also have an
/// <c>=</c>, which writes <see cref="DebugText"/> (the expression's text as written, through the
/// <c>=</c> and the spaces after it) before the value, a conversion (<c>r</c> or <c>s</c>), and a
/// format spec, the text after the <c>:</c> (SPEC §1, §6).
/// </summary>
public sealed record FStringHole(TextSpan Span) : FStringPart(Span)
{
    public string? DebugText { get; init; }

    public char? Conversion { get; init; }

    public string? Spec { get; init; }

    public TextSpan SpecSpan { get; init; }
}
