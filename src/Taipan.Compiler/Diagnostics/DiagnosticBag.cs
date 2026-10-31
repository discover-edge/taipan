using System.Collections.Immutable;
using System.Text;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Diagnostics;

/// <summary>
/// Collects diagnostics for one compilation stage. Each code in SPEC §9 has one Report method,
/// which owns its exact message text.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>Diagnostics in source order (stable for equal positions).</summary>
    public ImmutableArray<Diagnostic> ToImmutable() => [.. _items.OrderBy(d => d.Span.Start)];

    public void ReportTabInIndentation(TextSpan span) =>
        Add("TP0001", span, "tab in indentation");

    public void ReportInconsistentDedent(TextSpan span) =>
        Add("TP0002", span, "dedent does not match any outer indentation level");

    public void ReportUnterminatedString(TextSpan span) =>
        Add("TP0003", span, "unterminated string");

    public void ReportInvalidCharacter(TextSpan span, Rune character) =>
        Add("TP0004", span, $"invalid character {Describe(character)}");

    public void ReportInvalidEscape(TextSpan span, Rune character) =>
        Add("TP0005", span, $"invalid escape sequence '\\{character}'");

    public void ReportSingleCloseBraceInFString(TextSpan span) =>
        Add("TP0006", span, "single '}' is not allowed in an f-string");

    public void ReportEmptyFStringHole(TextSpan span) =>
        Add("TP0006", span, "empty f-string hole");

    public void ReportNestedFString(TextSpan span) =>
        Add("TP0006", span, "f-strings cannot be nested");

    public void ReportInvalidNumber(TextSpan span) =>
        Add("TP0007", span, "invalid number literal");

    public void ReportExpected(TextSpan span, string expected, string found) =>
        Add("TP0100", span, $"expected {expected}, found {found}");

    public void ReportInvalidAssignmentTarget(TextSpan span) =>
        Add("TP0100", span, "cannot assign to this expression");

    public void ReportMixedArguments(TextSpan span) =>
        Add("TP0100", span, "cannot mix positional and keyword arguments");

    public void ReportChainedComparison(TextSpan span) =>
        Add("TP0212", span, "chained comparison not supported", "write 'a < b and b < c'");

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    private void Add(string code, TextSpan span, string message, string? help = null) =>
        _items.Add(new Diagnostic(code, span, message, help));

    private static string Describe(Rune character) =>
        Rune.IsControl(character) || Rune.IsWhiteSpace(character)
            ? $"U+{character.Value:X4}"
            : $"'{character}'";
}
