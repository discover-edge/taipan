using Taipan.Compiler.Text;

namespace Taipan.Compiler.Diagnostics;

public sealed record Diagnostic(string Code, TextSpan Span, string Message, string? Help = null)
{
    /// <summary>Renders the SPEC §9 format.</summary>
    public string Format(SourceText source)
    {
        var position = source.GetLinePosition(Span.Start);
        var line = $"{source.Path}:{position.Line}:{position.Column}: error {Code}: {Message}";
        return Help is null ? line : $"{line}\n  help: {Help}";
    }
}
