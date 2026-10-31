namespace Taipan.Tests;

/// <summary>
/// Runs every <c>tests/cases/**/*.tpn</c> through the CLI and compares against its sidecars:
/// <c>.out</c> (stdout; its presence selects <c>run</c> over <c>check</c>),
/// <c>.err</c> (stderr, exact), and <c>.exit</c> (default 0, or 1 when <c>.err</c> exists).
/// </summary>
public class GoldenTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CasesDir = Path.Combine(RepoRoot, "tests", "cases");

    // Diagnostics print the path as given (SPEC §9), so cases run from the repo root
    // with repo-relative paths to keep .err files machine-independent.
    static GoldenTests() => Directory.SetCurrentDirectory(RepoRoot);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(CasesDir, "*.tpn", SearchOption.AllDirectories).Order())
            data.Add(Path.GetRelativePath(CasesDir, file).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden(string caseName)
    {
        var tpnPath = Path.Combine(CasesDir, caseName);
        var expectedOut = ReadSidecar(tpnPath, ".out");
        var expectedErr = ReadSidecar(tpnPath, ".err");
        var exitText = ReadSidecar(tpnPath, ".exit");
        var expectedExit = exitText is not null ? int.Parse(exitText.Trim()) : expectedErr is not null ? 1 : 0;

        var command = expectedOut is not null ? "run" : "check";
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Cli.Cli.Run([command, "tests/cases/" + caseName], stdout, stderr);

        Assert.Equal(expectedErr ?? "", Normalize(stderr.ToString()));
        Assert.Equal(expectedOut ?? "", Normalize(stdout.ToString()));
        Assert.Equal(expectedExit, exit);
    }

    private static string? ReadSidecar(string tpnPath, string extension)
    {
        var path = Path.ChangeExtension(tpnPath, extension);
        return File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Taipan.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Taipan.slnx not found above " + AppContext.BaseDirectory);
    }
}
