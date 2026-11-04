using System.Text.RegularExpressions;

namespace Taipan.Tests;

/// <summary>
/// The website's code samples as golden cases (M46). A <c>taipan</c> block followed by an
/// <c>output</c> block, an <c>error</c> block, or both, is run with <c>taipan run</c>: stdout must
/// be the output and stderr the error. The exit code is nonzero when the error shows a failure (an
/// error diagnostic, a traceback or a panic), and 0 otherwise. A titled block that no output
/// follows (<c>```taipan title="shapes.tpn"</c>) is a module, written beside the page's later
/// samples so they can import it. Paths in the error are the sample's file name (its title, or
/// <c>main.tpn</c>). A sample that imports Python needs it, as a golden case with a <c>.requires</c> does.
/// </summary>
[Collection(RunningPrograms.Name)]
public class WebsiteSampleTests
{
    private static readonly string Docs = Path.Combine(RepoPaths.Root, "website", "src", "content", "docs");

    /// <summary>A sample: its page, the line its block starts on, its file and source, and what it gives.</summary>
    public sealed record Sample(string Page, int Line, string File, string Source, string? Output, string? Error,
        IReadOnlyList<(string File, string Source)> Modules);

    private static readonly Regex Fence = new(@"^(?<indent>[ \t]*)(?<ticks>`{3,})(?<lang>[\w-]*)(?<meta>.*)$");
    // An error block that shows a failure, rather than only warnings or the program's own stderr.
    private static readonly Regex Failure = new(@"error TP\d+|^Traceback|^panic:", RegexOptions.Multiline);
    private static readonly Regex Title = new(@"title=""(?<title>[^""]+)""");

    /// <summary>Every sample on the site's hand-written pages; the generated ones come from golden cases already.</summary>
    public static IEnumerable<Sample> Samples()
    {
        var generated = new[] { Path.Combine(Docs, "diagnostics"), Path.Combine(Docs, "reference", "specification.md") };
        foreach (var page in Directory.EnumerateFiles(Docs, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md") || f.EndsWith(".mdx"))
            .Where(f => !generated.Any(g => f.StartsWith(g)))
            .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, page).Replace('\\', '/');
            var blocks = Blocks(File.ReadAllText(page).Replace("\r\n", "\n"));
            var modules = new List<(string, string)>();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Lang != "taipan")
                    continue;
                string? output = null, error = null;
                var next = i + 1;
                for (; next < blocks.Count && blocks[next].Lang is "output" or "error" && blocks[next].Adjacent; next++)
                {
                    if (blocks[next].Lang == "output")
                        output = blocks[next].Text;
                    else
                        error = blocks[next].Text;
                }
                var title = Title.Match(blocks[i].Meta) is { Success: true } m ? m.Groups["title"].Value : null;
                if (output is null && error is null)
                {
                    if (title is not null)
                        modules.Add((title, blocks[i].Text));
                    continue;
                }
                yield return new Sample(relative, blocks[i].Line, title ?? "main.tpn", blocks[i].Text, output, error, [.. modules]);
                i = next - 1;
            }
        }
    }

    // A page's fenced blocks, each with whether only blank lines separate it from the block before.
    private static List<(string Lang, string Meta, string Text, int Line, bool Adjacent)> Blocks(string text)
    {
        var lines = text.Split('\n');
        var blocks = new List<(string, string, string, int, bool)>();
        var lastEnd = -2;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence.Match(lines[i]) is not { Success: true } open)
                continue;
            var indent = open.Groups["indent"].Value;
            var ticks = open.Groups["ticks"].Value;
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length && lines[j].TrimEnd() != indent + ticks; j++)
                body.Add(lines[j].StartsWith(indent) ? lines[j][indent.Length..] : lines[j]);
            var adjacent = lastEnd >= 0 && lines[(lastEnd + 1)..i].All(string.IsNullOrWhiteSpace);
            blocks.Add((open.Groups["lang"].Value, open.Groups["meta"].Value, string.Join("\n", body) + "\n", i + 1, adjacent));
            lastEnd = j;
            i = j;
        }
        return blocks;
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var sample in Samples())
            data.Add($"{sample.Page}:{sample.Line}");
        return data;
    }

    [Fact]
    public void TheSiteHasSamples() => Assert.NotEmpty(Samples());

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sample_RunsAsShown(string id)
    {
        var sample = Samples().Single(s => $"{s.Page}:{s.Line}" == id);
        if (sample.Source.Contains("from python import") && !PythonAvailability.Available())
        {
            Console.WriteLine($"skipped {id}: no Python installation found");
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"taipan-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (file, source) in sample.Modules)
                File.WriteAllText(Path.Combine(directory, file), source);
            var path = Path.Combine(directory, sample.File);
            File.WriteAllText(path, sample.Source);
            var (exit, stdout, stderr) = GoldenTests.RunCli(["run", path]);
            Assert.Equal(sample.Error ?? "", stderr.Replace(path, sample.File).Replace(directory + Path.DirectorySeparatorChar, ""));
            Assert.Equal(sample.Output ?? "", stdout);
            if (sample.Error is { } error && Failure.IsMatch(error))
                Assert.NotEqual(0, exit);
            else
                Assert.Equal(0, exit);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
