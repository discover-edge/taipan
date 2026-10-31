using System.Collections.Immutable;

namespace Taipan.Compiler.Text;

public readonly record struct LinePosition(int Line, int Column);

/// <summary>A source file with line endings normalized to <c>\n</c> (SPEC §1.1).</summary>
public sealed class SourceText
{
    private readonly ImmutableArray<int> _lineStarts;

    private SourceText(string text, string path)
    {
        Text = text;
        Path = path;
        _lineStarts = ComputeLineStarts(text);
    }

    public string Text { get; }

    /// <summary>The path as given on the command line; diagnostics print it verbatim (SPEC §9).</summary>
    public string Path { get; }

    public int Length => Text.Length;

    public static SourceText From(string text, string path = "") => new(text.Replace("\r\n", "\n"), path);

    public string ToString(TextSpan span) => Text.Substring(span.Start, span.Length);

    /// <summary>1-based line and column; columns count Unicode code points (SPEC §9).</summary>
    public LinePosition GetLinePosition(int position)
    {
        var index = _lineStarts.BinarySearch(position);
        var line = index >= 0 ? index : ~index - 1;

        var column = 1;
        for (var i = _lineStarts[line]; i < position; i++)
        {
            if (!char.IsLowSurrogate(Text[i]))
                column++;
        }
        return new LinePosition(line + 1, column);
    }

    private static ImmutableArray<int> ComputeLineStarts(string text)
    {
        var starts = ImmutableArray.CreateBuilder<int>();
        starts.Add(0);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return starts.ToImmutable();
    }
}
