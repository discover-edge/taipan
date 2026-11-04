namespace Taipan.Tests;

/// <summary><c>taipan fmt</c> (SPEC §19) and <c>taipan test</c> (SPEC §20) on files and directories (M44).</summary>
[Collection(RunningPrograms.Name)]
public sealed class ToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"taipan-tools-{Guid.NewGuid():N}");

    public ToolTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // In this process, with paths under the temporary directory; the working directory is shared, so it isn't changed.
    private (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Cli.Cli.Run([args[0], .. args[1..].Select(a => a.StartsWith('-') ? a : Path.Combine(_directory, a))], new StringReader(""), stdout, stderr);
        return (exit, Relative(stdout.ToString()), Relative(stderr.ToString()));
    }

    private string Relative(string output) => output.Replace("\r\n", "\n").Replace(_directory + Path.DirectorySeparatorChar, "");

    // In a taipan process of its own, in the temporary directory, for `taipan test` with no paths.
    private (int Exit, string Stdout, string Stderr) RunHere(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "taipan.dll"), .. args])
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Replace("\r\n", "\n"), stderr.Result.Replace("\r\n", "\n"));
    }

    [Fact]
    public void FmtRewritesFilesUnderADirectory()
    {
        var a = Write("src/a.tpn", "let x=1\nprint( x )\n");
        var b = Write("src/inner/b.tpn", "print(1)\n");
        Assert.Equal((0, "", ""), Run("fmt", "src"));
        Assert.Equal("let x = 1\nprint(x)\n", File.ReadAllText(a));
        Assert.Equal("print(1)\n", File.ReadAllText(b));
    }

    [Fact]
    public void FmtCheckNamesFilesAndChangesNothing()
    {
        var a = Write("a.tpn", "let x=1\n");
        Write("b.tpn", "let y = 2\n");
        Assert.Equal((1, "would reformat a.tpn\n", ""), Run("fmt", "--check", "a.tpn", "b.tpn"));
        Assert.Equal("let x=1\n", File.ReadAllText(a));
        Assert.Equal((0, "", ""), Run("fmt", "--check", "b.tpn"));
    }

    [Fact]
    public void FmtLeavesAFileWithSyntaxErrors()
    {
        var a = Write("a.tpn", "let x = (1\n");
        var (exit, stdout, stderr) = Run("fmt", "a.tpn");
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("a.tpn:", stderr);
        Assert.Equal("let x = (1\n", File.ReadAllText(a));
    }

    [Fact]
    public void FmtNeedsAPath()
    {
        Assert.Equal(2, Cli.Cli.Run(["fmt"], new StringReader(""), new StringWriter(), new StringWriter()));
        Assert.Equal((2, "", "taipan: file not found: missing.tpn\n"), Run("fmt", "missing.tpn"));
    }

    [Fact]
    public void TestFindsTestFilesInADirectory()
    {
        Write("test_one.tpn", "def test_a():\n    assert True\n");
        Write("tests/two_test.tpn", "def test_b():\n    assert 1 == 2\n");
        Write("tests/helper.tpn", "def test_ignored():\n    assert False\n");
        var (exit, stdout, stderr) = RunHere("test");
        Assert.Equal(1, exit);
        Assert.Equal("", stderr);
        Assert.Equal($"""
            PASS test_one.tpn::test_a
            FAIL tests{Path.DirectorySeparatorChar}two_test.tpn::test_b
            Traceback (most recent call last):
              File "tests{Path.DirectorySeparatorChar}two_test.tpn", line 2, in test_b
                assert 1 == 2
            AssertionError
            1 passed, 1 failed

            """, stdout);
    }

    [Fact]
    public void TestWithoutTestsFails()
    {
        Write("test_empty.tpn", "print(\"no tests here\")\n");
        Assert.Equal((1, "no tests here\nno tests found\n", ""), RunHere("test"));
    }

    [Fact]
    public void TestOfAMissingPathIsAUsageError() =>
        Assert.Equal((2, "", "taipan: file not found: nope\n"), Run("test", "nope"));
}
