namespace Taipan.Tests;

/// <summary>
/// Runs every <c>tests/cases/**/*.tpn</c>, and every <c>examples/*.tpn</c> that has a
/// <c>.out</c>, through the CLI and compares against its sidecars. In a directory with a
/// <c>main.tpn</c>, only that file is a case and the others are its modules (SPEC §4.7):
/// <c>.out</c> (stdout; its presence selects <c>run</c> over <c>check</c>),
/// <c>.err</c> (stderr, exact), <c>.exit</c> (default 0, or 1 when <c>.err</c> exists), and
/// <c>.cs</c> (the exact output of <c>taipan emit</c>), <c>.requires</c> (naming <c>python</c>
/// for a case that needs CPython), and <c>.library</c>, <c>.native</c> or <c>.python-extension</c>
/// (empty, for a case checked and emitted with that option, SPEC §14, §15, §17), <c>.args</c>
/// (program arguments, one per line, with <c>{tmp}</c> standing for a fresh temporary directory)
/// and <c>.in</c> (the program's stdin) (SPEC §6.1). Since M44, <c>.fmt</c> is what <c>taipan fmt</c>
/// makes of the case (SPEC §19), and <c>.test</c> (empty) runs it with <c>taipan test</c> (SPEC §20)
/// instead of <c>run</c> or <c>check</c>.
/// </summary>
[Collection(RunningPrograms.Name)]
public class GoldenTests
{
    // Diagnostics and panics print the path as given (SPEC §7, §9), so cases run from the repo
    // root with repo-relative paths to keep sidecars machine-independent.
    static GoldenTests() => Directory.SetCurrentDirectory(RepoPaths.Root);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var path in CasePaths())
            data.Add(path);
        return data;
    }

    /// <summary>Every case, by its path relative to the repository's root.</summary>
    public static IEnumerable<string> CasePaths()
    {
        // A multi-file case is a directory with a main.tpn; its other files are modules, not cases.
        var cases = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", "cases"), "*.tpn", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) == "main.tpn" || !File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "main.tpn")))
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "examples"), "*.tpn")
                .Where(file => File.Exists(Path.ChangeExtension(file, ".out"))));

        return cases.Order().Select(file => Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/'));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden(string casePath)
    {
        // A `.requires` sidecar naming `python` marks a case that needs CPython (SPEC §10.6).
        // Without one installed, the case passes with a note rather than failing (unless
        // TAIPAN_REQUIRE_PYTHON=1).
        if (ReadSidecar(casePath, ".requires")?.Contains("python") == true && !PythonAvailability.Available())
        {
            Console.WriteLine($"skipped {casePath}: no Python installation found");
            return;
        }

        var expectedOut = ReadSidecar(casePath, ".out");
        var expectedErr = ReadSidecar(casePath, ".err");
        var exitText = ReadSidecar(casePath, ".exit");
        var expectedExit = exitText is not null ? int.Parse(exitText.Trim()) : expectedErr is not null ? 1 : 0;

        string[] flags = [.. ((string[])["library", "native", "python-extension"]).Where(f => File.Exists(Path.ChangeExtension(casePath, $".{f}"))).Select(f => $"--{f}")];
        using var input = new ProgramInput(casePath);
        string[] run = File.Exists(Path.ChangeExtension(casePath, ".test")) ? ["test", casePath]
            : expectedOut is not null ? ["run", casePath, .. flags, "--", .. input.Arguments] : ["check", casePath, .. flags];
        var (exit, stdout, stderr) = RunCli(run, input.Stdin);
        Assert.Equal(expectedErr ?? "", stderr);
        Assert.Equal(expectedOut ?? "", stdout);
        Assert.Equal(expectedExit, exit);

        if (ReadSidecar(casePath, ".cs") is { } expectedCSharp)
            Assert.Equal(expectedCSharp, RunCli(["emit", casePath, .. flags]).Stdout);
        if (ReadSidecar(casePath, ".fmt") is { } expectedFormat)
            Assert.Equal(expectedFormat, Compiler.Formatting.Formatter.Format(Compiler.Text.SourceText.From(File.ReadAllText(casePath), casePath)).Text);
    }

    internal static (int Exit, string Stdout, string Stderr) RunCli(string[] args, string stdin = "")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Cli.Cli.Run(args, new StringReader(stdin), stdout, stderr);
        return (exit, Normalize(stdout.ToString()), Normalize(stderr.ToString()));
    }

    internal static string? ReadSidecar(string tpnPath, string extension)
    {
        var path = Path.ChangeExtension(tpnPath, extension);
        return File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}

/// <summary>
/// A case's program arguments, from <c>.args</c> with <c>{tmp}</c> standing for a temporary
/// directory that's created for the run and deleted after it, and its stdin, from <c>.in</c> (SPEC §6.1).
/// </summary>
public sealed class ProgramInput : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), $"taipan-{Guid.NewGuid():N}");

    public ProgramInput(string casePath)
    {
        var args = Path.ChangeExtension(casePath, ".args");
        Arguments = File.Exists(args)
            ? [.. File.ReadAllLines(args).Where(a => a.Length > 0).Select(a => a.Replace("{tmp}", _temporary))]
            : [];
        if (Arguments.Any(a => a.Contains(_temporary)))
            Directory.CreateDirectory(_temporary);
        var stdin = Path.ChangeExtension(casePath, ".in");
        Stdin = File.Exists(stdin) ? File.ReadAllText(stdin) : "";
    }

    public string[] Arguments { get; }

    public string Stdin { get; }

    public void Dispose()
    {
        if (Directory.Exists(_temporary))
            Directory.Delete(_temporary, recursive: true);
    }
}

/// <summary>
/// Test classes that run programs in-process, which share the runtime's output writers
/// (<c>Io.Out</c>, <c>Io.Error</c>), so they can't run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RunningPrograms
{
    public const string Name = "Running programs";
}

/// <summary>
/// Test classes that run <c>dotnet pack</c> on the repository's projects, which writes to their
/// <c>bin/Release</c>, so they can't run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PackingRepo
{
    public const string Name = "Packing the repository";
}
