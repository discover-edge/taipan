using System.Text.RegularExpressions;

namespace Taipan.Tests;

/// <summary>The options of the <c>taipan</c> command itself (SPEC §12.1, M20).</summary>
public class CliTests
{
    /// <summary>The version set once in the build, which every package and <c>--version</c> share (SPEC §12.2).</summary>
    public static string BuildVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepoPaths.Root, "Directory.Build.props"));
        return Regex.Match(props, "<Version>(.+?)</Version>").Groups[1].Value;
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Cli.Cli.Run(args, stdout, stderr);
        return (exit, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void VersionPrintsTheBuildVersion()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", BuildVersion());
        Assert.Equal((0, $"taipan {BuildVersion()}\n", ""), Run("--version"));
    }

    [Fact]
    public void HelpPrintsUsageToStdout()
    {
        var (exit, stdout, stderr) = Run("--help");
        Assert.Equal(0, exit);
        Assert.StartsWith("usage:\n", stdout);
        Assert.Contains("taipan run   <file> [-- args]", stdout);
        Assert.Contains("taipan --version", stdout);
        Assert.Equal("", stderr);
    }

    [Theory]
    [InlineData("--version", "extra")]
    [InlineData("--help", "run")]
    public void OptionsTakeNoArguments(params string[] args)
    {
        var (exit, stdout, stderr) = Run(args);
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith($"taipan: {args[0]} takes no arguments\nusage:\n", stderr);
    }

    [Fact]
    public void LspTakesOnlyStdio()
    {
        var (exit, stdout, stderr) = Run("lsp", "--tcp");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("taipan: lsp takes no arguments but '--stdio'\nusage:\n", stderr);
    }

    [Fact]
    public void LibraryAndNativeDontCombine()
    {
        var (exit, stdout, stderr) = Run("build", "examples/health.tpn", "--library", "--native", "-o", "out");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("taipan: --library, --native and --python-extension can't be combined\nusage:\n", stderr);
    }

    [Fact]
    public void UnknownOptionIsAUsageError()
    {
        var (exit, stdout, stderr) = Run("--frobnicate");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("taipan: unknown option '--frobnicate'\nusage:\n", stderr);
    }

    // ---- taipan compile (SPEC §18.2, M34) ----

    private static string TempFile(string name, string text)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"taipan-compile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void CompileWritesOneAssembly()
    {
        var main = TempFile("tally.tpn", "def twice(n: int) -> int:\n    return n * 2\n");
        var output = Path.Combine(Path.GetDirectoryName(main)!, "obj", "Acme.Tally.dll");
        var (exit, stdout, stderr) = Run("compile", main, "--library", "--out", output, "--assembly", "Acme.Tally", "--namespace", "Acme.Tally", "--version", "1.2.0");
        Assert.Equal((0, "", ""), (exit, stdout, stderr));
        Assert.Equal(["Acme.Tally.dll"], Directory.GetFiles(Path.GetDirectoryName(output)!).Select(Path.GetFileName));
        var assembly = System.Reflection.Metadata.MetadataReader.GetAssemblyName(output);
        Assert.Equal(("Acme.Tally", new Version(1, 2, 0, 0)), (assembly.Name, assembly.Version));
    }

    [Fact]
    public void CompileReportsInMSBuildFormat()
    {
        var main = TempFile("broken.tpn", "let x: int = \"a\"\nlet y = nope\n");
        var (exit, _, stderr) = Run("compile", main, "--out", Path.Combine(Path.GetDirectoryName(main)!, "broken.dll"));
        Assert.Equal(1, exit);
        Assert.Equal(
            $"{main}(1,14): error TP0200: expected int, found str\n{main}(2,9): error TP0201: undefined name 'nope'\n",
            stderr);
    }

    [Fact]
    public void CompileRejectsDependenciesInTaipanJson()
    {
        var main = TempFile("app.tpn", "print(1)\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(main)!, "taipan.json"), """{ "packages": {} }""");
        var (exit, _, stderr) = Run("compile", main, "--out", Path.Combine(Path.GetDirectoryName(main)!, "app.dll"));
        Assert.Equal(1, exit);
        Assert.EndsWith("taipan.json(1,1): error TPN002: list references and packages in the project file, not taipan.json\n", stderr);
        Assert.Equal(2, Run("compile", main).Exit);
    }
}
