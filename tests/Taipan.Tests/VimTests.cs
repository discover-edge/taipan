using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taipan.Tests;

/// <summary>
/// M47 acceptance (SPEC §16.12): Vim and Neovim support in <c>editors/vim</c>. Vim highlights the
/// grammar's sample as the golden syntax list says, typing a block indents it, and Neovim, with
/// <c>lsp/taipan.lua</c>, gets the diagnostics <c>taipan check</c> reports.
/// </summary>
[Collection(RunningPrograms.Name)]
public class VimTests
{
    private static readonly string Runtime = Path.Combine(RepoPaths.Root, "editors", "vim");

    [Fact]
    public void SyntaxGivesTheGoldenGroups()
    {
        if (!EditorAvailability.Available("vim", "TAIPAN_REQUIRE_VIM"))
            return;
        var output = Path.GetTempFileName();
        try
        {
            var sample = Path.Combine(RepoPaths.Root, "editors", "vscode", "test", "sample.tpn");
            var (exitCode, error) = Vim("test/syntax.vim", sample, output);
            Assert.True(exitCode == 0, error);
            Assert.Equal(File.ReadAllText(Path.Combine(Runtime, "test", "sample.syntax")), File.ReadAllText(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void TypingABlockIndentsIt()
    {
        if (!EditorAvailability.Available("vim", "TAIPAN_REQUIRE_VIM"))
            return;
        var output = Path.GetTempFileName();
        try
        {
            var expected = Path.Combine(Runtime, "test", "indent.tpn");
            var (exitCode, error) = Vim("test/indent.vim", expected, output);
            Assert.True(exitCode == 0, error);
            Assert.Equal(File.ReadAllText(expected), File.ReadAllText(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void NeovimGetsTheDiagnostics()
    {
        if (!EditorAvailability.Available("nvim", "TAIPAN_REQUIRE_NEOVIM"))
            return;
        var directory = Path.Combine(Path.GetTempPath(), $"taipan-nvim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "main.tpn");
            File.WriteAllText(file, "let x: int = \"a\"\ndef f() -> int:\n    return 1\n    print(2)\nprint(y)\n");
            var output = Path.Combine(directory, "diagnostics.txt");
            var taipan = new[] { "dotnet", Path.Combine(AppContext.BaseDirectory, "taipan.dll"), "lsp", "--stdio" };
            var start = new ProcessStartInfo("nvim", ["--clean", "--headless", "--cmd", $"set rtp^={Runtime}", "-c", "luafile test/lsp.lua"])
            {
                WorkingDirectory = Runtime,
                Environment = { ["TAIPAN_FILE"] = file, ["TAIPAN_OUT"] = output, ["TAIPAN_COMMAND"] = JsonSerializer.Serialize(taipan) },
            };
            var (exitCode, error) = Run(start);
            Assert.True(exitCode == 0, error + (File.Exists(output) ? File.ReadAllText(output) : ""));

            // `taipan check`'s diagnostics, without the path and with only their first line.
            var (_, _, check) = GoldenTests.RunCli(["check", file]);
            var expected = check.Split('\n').Where(l => l.StartsWith(file + ":", StringComparison.Ordinal))
                .Select(l => Regex.Replace(l[(file.Length + 1)..], @"^(\d+):(\d+): ", "$1:$2 ") + "\n");
            Assert.Equal(3, expected.Count());
            Assert.Equal(string.Concat(expected), File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Vim with no configuration but editors/vim on its runtimepath, running a test script.
    private static (int ExitCode, string Error) Vim(string script, params string[] arguments) =>
        Run(new ProcessStartInfo("vim", ["-Nu", "NONE", "-i", "NONE", "-Es", "--cmd", $"set rtp^={Runtime}", "-S", script, .. arguments])
        {
            WorkingDirectory = Runtime,
        });

    private static (int ExitCode, string Error) Run(ProcessStartInfo start)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }
        return (process.ExitCode, output.Result + error.Result);
    }
}

/// <summary>
/// The Vim and Neovim tests need <c>vim</c>, or Neovim 0.11 or later as <c>nvim</c>, and pass
/// trivially without them. Setting <c>TAIPAN_REQUIRE_VIM=1</c> or <c>TAIPAN_REQUIRE_NEOVIM=1</c>
/// turns their absence into a failure instead, as <see cref="NodeAvailability"/> does for Node.js.
/// </summary>
public static class EditorAvailability
{
    public static bool Available(string command, string require)
    {
        var missing = !NodeAvailability.OnPath(command) ? command
            : command == "nvim" && !RecentNeovim() ? "Neovim 0.11 or later"
            : null;
        if (missing is null)
            return true;
        if (Environment.GetEnvironmentVariable(require) == "1")
            throw new InvalidOperationException($"{require}=1 but {missing} wasn't found");
        return false;
    }

    // `vim.lsp.enable` and `lsp/*.lua` configurations came in Neovim 0.11.
    private static bool RecentNeovim()
    {
        using var process = Process.Start(new ProcessStartInfo("nvim", ["--version"]) { RedirectStandardOutput = true })!;
        var version = Regex.Match(process.StandardOutput.ReadToEnd(), @"NVIM v(\d+)\.(\d+)");
        process.WaitForExit();
        return version.Success && (int.Parse(version.Groups[1].Value), int.Parse(version.Groups[2].Value)).CompareTo((0, 11)) >= 0;
    }
}
