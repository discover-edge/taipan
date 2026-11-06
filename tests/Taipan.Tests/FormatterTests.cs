using Taipan.Compiler.Formatting;
using Taipan.Compiler.Text;

namespace Taipan.Tests;

/// <summary>
/// M44's acceptance (SPEC §19): formatting every golden case that parses leaves it unchanged in
/// meaning, and formatting it again changes nothing. A formatted copy of the cases runs from a
/// temporary directory, so relative paths between cases still hold; a case with <c>.out</c> must
/// give the same stdout and exit code, and a checked case the same diagnostic codes. Columns, lines
/// and tracebacks' source lines may differ, since blank lines and spaces do.
/// </summary>
[Collection(RunningPrograms.Name)]
public class FormatterTests
{
    private static readonly Lazy<string> FormattedCopy = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"taipan-fmt-{Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", "cases"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(RepoPaths.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (file.EndsWith(".tpn", StringComparison.Ordinal) && Formatter.Format(SourceText.From(File.ReadAllText(file), file)).Text is { } formatted)
                File.WriteAllText(target, formatted);
            else
                File.Copy(file, target);
        }
        return root;
    });

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in GoldenTests.CasePaths().Where(c => c.StartsWith("tests/", StringComparison.Ordinal)))
            data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FormattingKeepsMeaning(string casePath)
    {
        var path = Path.Combine(RepoPaths.Root, casePath);
        var original = File.ReadAllText(path);
        var formatted = Formatter.Format(SourceText.From(original, casePath)).Text;
        if (formatted is null)
            return;
        Assert.Equal(formatted, Formatter.Format(SourceText.From(formatted, casePath)).Text);
        if (GoldenTests.ReadSidecar(casePath, ".requires")?.Contains("python") == true && !PythonAvailability.Available())
            return;

        string[] flags = [.. ((string[])["library", "native", "python-extension"]).Where(f => File.Exists(Path.ChangeExtension(path, $".{f}"))).Select(f => $"--{f}")];
        var copy = Path.Combine(FormattedCopy.Value, casePath);
        var hasOut = File.Exists(Path.ChangeExtension(path, ".out"));
        var test = File.Exists(Path.ChangeExtension(path, ".test"));
        // Each run has its own temporary directory, since a case may create files there.
        using var input = new ProgramInput(path);
        using var again = new ProgramInput(path);
        string[] Command(string file, ProgramInput given) => test ? ["test", file] : hasOut ? ["run", file, .. flags, "--", .. given.Arguments] : ["check", file, .. flags];
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(RepoPaths.Root);
            var expected = GoldenTests.RunCli(Command(casePath, input), input.Stdin);
            Directory.SetCurrentDirectory(FormattedCopy.Value);
            var actual = GoldenTests.RunCli(Command(casePath, again), again.Stdin);
            Assert.Equal(expected.Exit, actual.Exit);
            if (hasOut || test)
                Assert.Equal(Without(expected.Stdout), Without(actual.Stdout));
            else
                Assert.Equal(Codes(expected.Stderr), Codes(actual.Stderr));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    /// <summary>
    /// The style depends only on the tokens and the lines they're on (SPEC §19): a case with its
    /// indentation doubled and two spaces between its tokens formats as the case itself does.
    /// </summary>
    // A multi-line string's text stays as it is, and the code around it is still formatted (SPEC §1, M49).
    [Fact]
    public void FormattingLeavesMultiLineStringsAlone()
    {
        const string source = "let  a  =  \"\"\"x\n   # not a comment\n\n  y  z\"\"\" + \"w\"   # a comment\n\n\n\nlet   b = \"joined \\\n   line\"\n"
            + "def f( ) -> str:\n      \"\"\"Doc\n         more\n      \"\"\"\n      return   \"\"\"q\"\"\"\n";
        const string expected = "let a = \"\"\"x\n   # not a comment\n\n  y  z\"\"\" + \"w\"  # a comment\n\n\nlet b = \"joined \\\n   line\"\n"
            + "def f() -> str:\n    \"\"\"Doc\n         more\n      \"\"\"\n    return \"\"\"q\"\"\"\n";
        var formatted = Formatter.Format(SourceText.From(source, "t.tpn")).Text;
        Assert.Equal(expected, formatted);
        Assert.Equal(expected, Formatter.Format(SourceText.From(expected, "t.tpn")).Text);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FormattingIgnoresWhitespace(string casePath)
    {
        var original = File.ReadAllText(Path.Combine(RepoPaths.Root, casePath)).Replace("\r\n", "\n");
        if (Formatter.Format(SourceText.From(original, casePath)).Text is not { } formatted)
            return;
        Assert.Equal(formatted, Formatter.Format(SourceText.From(Respace(original), casePath)).Text);
    }

    // Each line's indentation doubled, and two spaces between tokens outside strings and comments.
    private static string Respace(string text)
    {
        var source = SourceText.From(text);
        // This rewrites line by line, which can't split a multi-line string; FormattingLeavesMultiLineStringsAlone covers those.
        if (Compiler.Lexing.Lexer.Lex(source).Tokens.Any(t => t.Kind is Compiler.Lexing.TokenKind.String or Compiler.Lexing.TokenKind.FString && t.Text.Contains('\n')))
            return text;
        var tokens = Compiler.Lexing.Lexer.Lex(source).Tokens.Where(t => t.Span.Length > 0 && t.Kind != Compiler.Lexing.TokenKind.Newline).ToList();
        var lines = text.Split('\n');
        var result = new List<string>();
        var index = 0;
        var offset = 0;
        foreach (var line in lines)
        {
            var end = offset + line.Length;
            var onLine = new List<Compiler.Lexing.Token>();
            while (index < tokens.Count && tokens[index].Span.Start < end)
                onLine.Add(tokens[index++]);
            if (onLine.Count == 0)
            {
                result.Add(line);
            }
            else
            {
                var indent = onLine[0].Span.Start - offset;
                var built = new System.Text.StringBuilder(new string(' ', indent * 2));
                for (var i = 0; i < onLine.Count; i++)
                {
                    // `**` is two adjacent `*` tokens, which must stay together.
                    if (i > 0 && !(onLine[i].Kind == Compiler.Lexing.TokenKind.Star && onLine[i - 1].Kind == Compiler.Lexing.TokenKind.Star
                        && onLine[i - 1].Span.End == onLine[i].Span.Start))
                        built.Append("  ");
                    built.Append(onLine[i].Text);
                }
                var comment = line[(onLine[^1].Span.End - offset)..];
                if (comment.TrimStart().StartsWith('#'))
                    built.Append("   ").Append(comment.TrimStart());
                result.Add(built.ToString());
            }
            offset = end + 1;
        }
        return string.Join("\n", result);
    }

    // A test's report quotes its traceback's source lines, which formatting may re-space.
    private static string Without(string stdout) => string.Join("\n", stdout.Split('\n').Where(l => !l.StartsWith("    ", StringComparison.Ordinal)));

    private static List<string> Codes(string stderr) =>
        [.. stderr.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Match(l, @"(error|warning) (TP\d+)")).Where(m => m.Success).Select(m => m.Groups[2].Value)];
}
