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

    /// <summary>
    /// The 0-based line and UTF-16 column of a position, as the Language Server Protocol counts
    /// them (SPEC §16.1). The text is UTF-16, so the column is the distance from the line's start.
    /// </summary>
    public (int Line, int Character) GetUtf16Position(int position)
    {
        var index = _lineStarts.BinarySearch(Math.Clamp(position, 0, Text.Length));
        var line = index >= 0 ? index : ~index - 1;
        return (line, Math.Clamp(position, 0, Text.Length) - _lineStarts[line]);
    }

    /// <summary>The position at a 0-based line and UTF-16 column, clamped to the text and to the line.</summary>
    public int GetPosition(int line, int character)
    {
        if (line >= _lineStarts.Length)
            return Text.Length;
        var start = _lineStarts[Math.Max(line, 0)];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : Text.Length;
        return Math.Min(start + Math.Max(character, 0), end);
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
