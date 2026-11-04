using System.Diagnostics;

namespace Taipan.Tests;

/// <summary>
/// Starting Python (SPEC §10.5), in a <c>taipan run</c> process of its own, since in this process
/// another test may have started Python already: a program that converts to <c>py</c> without
/// importing a Python module starts it, and panics as §10.5 says if it can't be loaded.
/// </summary>
public sealed class PythonStartupTests : IDisposable
{
    private const string Program = "let p: py = [1, 2]\nprint(p)\n";

    private readonly string _directory = Directory.CreateTempSubdirectory("taipan-python-startup-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ConvertingWithoutAnImportStartsPython()
    {
        if (!PythonAvailability.Available())
            return;
        Assert.Equal((0, "[1, 2]\n", ""), await Run(pythonLibrary: null));
    }

    [Fact]
    public async Task PythonThatCantBeLoadedPanics()
    {
        var (exit, stdout, stderr) = await Run(pythonLibrary: Path.Combine(_directory, "no-libpython.so"));
        Assert.Equal(101, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith("panic: cannot load Python: ", stderr);
        Assert.EndsWith($"  at {Path.Combine(_directory, "main.tpn")}:1\n", stderr);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> Run(string? pythonLibrary)
    {
        var path = Path.Combine(_directory, "main.tpn");
        await File.WriteAllTextAsync(path, Program);
        var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "taipan.dll"), "run", path])
        {
            // Not this process's, which another test may have moved to a directory it then deletes.
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (pythonLibrary is not null)
            start.Environment["PYTHONNET_PYDLL"] = pythonLibrary;
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout.Replace("\r\n", "\n"), (await stderr).Replace("\r\n", "\n"));
    }
}
