namespace Taipan.Tests;

/// <summary>
/// Runs every <c>tests/cases/**/*.tpn</c>, and every <c>examples/*.tpn</c> that has a
/// <c>.out</c>, through the CLI and compares against its sidecars. In a directory with a
/// <c>main.tpn</c>, only that file is a case and the others are its modules (SPEC §4.7):
/// <c>.out</c> (stdout; its presence selects <c>run</c> over <c>check</c>),
/// <c>.err</c> (stderr, exact), <c>.exit</c> (default 0, or 1 when <c>.err</c> exists), and
/// <c>.cs</c> (the exact output of <c>taipan emit</c>), and <c>.requires</c> (naming <c>python</c>
/// for a case that needs CPython).
/// </summary>
public class GoldenTests
{
    // Diagnostics and panics print the path as given (SPEC §7, §9), so cases run from the repo
    // root with repo-relative paths to keep sidecars machine-independent.
    static GoldenTests() => Directory.SetCurrentDirectory(RepoPaths.Root);

    public static TheoryData<string> Cases()
    {
        // A multi-file case is a directory with a main.tpn; its other files are modules, not cases.
        var cases = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "tests", "cases"), "*.tpn", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) == "main.tpn" || !File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "main.tpn")))
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "examples"), "*.tpn")
                .Where(file => File.Exists(Path.ChangeExtension(file, ".out"))));

        var data = new TheoryData<string>();
        foreach (var file in cases.Order())
            data.Add(Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden(string casePath)
    {
        // A `.requires` sidecar naming `python` marks a case that needs CPython (SPEC §10.6).
        // Without one installed, the case passes with a note rather than failing.
        if (ReadSidecar(casePath, ".requires")?.Contains("python") == true && Taipan.Runtime.Interop.FindLibrary() is null)
        {
            Console.WriteLine($"skipped {casePath}: no Python installation found");
            return;
        }

        var expectedOut = ReadSidecar(casePath, ".out");
        var expectedErr = ReadSidecar(casePath, ".err");
        var exitText = ReadSidecar(casePath, ".exit");
        var expectedExit = exitText is not null ? int.Parse(exitText.Trim()) : expectedErr is not null ? 1 : 0;

        var (exit, stdout, stderr) = RunCli(expectedOut is not null ? "run" : "check", casePath);
        Assert.Equal(expectedErr ?? "", stderr);
        Assert.Equal(expectedOut ?? "", stdout);
        Assert.Equal(expectedExit, exit);

        if (ReadSidecar(casePath, ".cs") is { } expectedCSharp)
            Assert.Equal(expectedCSharp, RunCli("emit", casePath).Stdout);
    }

    private static (int Exit, string Stdout, string Stderr) RunCli(string command, string path)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Cli.Cli.Run([command, path], stdout, stderr);
        return (exit, Normalize(stdout.ToString()), Normalize(stderr.ToString()));
    }

    private static string? ReadSidecar(string tpnPath, string extension)
    {
        var path = Path.ChangeExtension(tpnPath, extension);
        return File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
