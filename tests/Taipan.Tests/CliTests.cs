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
    public void UnknownOptionIsAUsageError()
    {
        var (exit, stdout, stderr) = Run("--frobnicate");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("taipan: unknown option '--frobnicate'\nusage:\n", stderr);
    }
}
