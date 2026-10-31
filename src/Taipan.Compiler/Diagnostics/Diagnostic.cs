using Taipan.Compiler.Text;

namespace Taipan.Compiler.Diagnostics;

/// <param name="IsWarning">Warnings (TP03xx) are reported but don't fail compilation (SPEC §9).</param>
public sealed record Diagnostic(string Code, TextSpan Span, string Message, string? Help = null, bool IsWarning = false)
{
    /// <summary>Renders the SPEC §9 format.</summary>
    public string Format(SourceText source)
    {
        var position = source.GetLinePosition(Span.Start);
        var severity = IsWarning ? "warning" : "error";
        var line = $"{source.Path}:{position.Line}:{position.Column}: {severity} {Code}: {Message}";
        return Help is null ? line : $"{line}\n  help: {Help}";
    }
}
