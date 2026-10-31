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

/// <summary>A hole; <see cref="FStringPart.Span"/> covers the expression text between the braces.</summary>
public sealed record FStringHole(TextSpan Span) : FStringPart(Span);
