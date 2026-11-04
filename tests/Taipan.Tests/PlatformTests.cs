using System.Diagnostics;

namespace Taipan.Tests;

/// <summary>
/// M45 acceptance (SPEC §15.4–§15.6): a native build for another runtime identifier is an
/// executable of that platform, by its file header; size options still build a working program;
/// and a native library is called from C. The builds run only with <c>TAIPAN_TEST_NATIVE=1</c>,
/// as other native builds do, and the cross build needs the target's GCC (§15.4).
/// </summary>
[Collection(RunningPrograms.Name)]
public sealed class PlatformTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("taipan-platform-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static bool NativeBuilds()
    {
        if (Environment.GetEnvironmentVariable("TAIPAN_TEST_NATIVE") == "1")
            return true;
        Console.WriteLine("skipped a native build: set TAIPAN_TEST_NATIVE=1 to run it");
        return false;
    }

    private static (int Exit, string Stdout, string Stderr) Taipan(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Directory.SetCurrentDirectory(RepoPaths.Root);
        var exit = Cli.Cli.Run(args, stdout, stderr);
        return (exit, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    private static (int Exit, string Output) Run(string file, string arguments, string directory)
    {
        var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, (output + error.Result).Replace("\r\n", "\n"));
    }

    // ELF's e_machine, at offset 18: 0xB7 for AArch64, 0x3E for x86-64.
    private static int ElfMachine(string path)
    {
        var header = File.ReadAllBytes(path).AsSpan(0, 20);
        Assert.True(header[..4].SequenceEqual((byte[])[0x7F, (byte)'E', (byte)'L', (byte)'F']), "not an ELF file");
        return header[18] | header[19] << 8;
    }

    [Fact]
    public void BuildsForAnotherArchitecture()
    {
        if (!OperatingSystem.IsLinux() || !NativeBuilds())
            return;
        var (rid, triple, machine) = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? ("linux-x64", "x86_64-linux-gnu", 0x3E)
            : ("linux-arm64", "aarch64-linux-gnu", 0xB7);
        var gcc = $"{triple}-gcc";
        if (!(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, gcc))))
        {
            Assert.Fail($"the cross build needs {gcc} on PATH (SPEC §15.4)");
            return;
        }
        var (exit, stdout, stderr) = Taipan("build", "examples/health.tpn", "--native", "--runtime", rid, "-o", _directory);
        Assert.True(exit == 0, stderr);
        Assert.Equal($"built {Path.Combine(_directory, "health")}\n", stdout);
        Assert.Equal(machine, ElfMachine(Path.Combine(_directory, "health")));
    }

    [Fact]
    public void SizeOptionsBuildAWorkingProgram()
    {
        if (!NativeBuilds())
            return;
        var (exit, _, stderr) = Taipan("build", "examples/health.tpn", "--native", "--invariant-globalization", "--optimize-size", "-o", _directory);
        Assert.True(exit == 0, stderr);
        var expected = File.ReadAllText(Path.Combine(RepoPaths.Root, "examples", "health.out")).Replace("\r\n", "\n");
        Assert.Equal((0, expected), Run(Path.Combine(_directory, OperatingSystem.IsWindows() ? "health.exe" : "health"), "", RepoPaths.Root));
    }

    [Fact]
    public void NativeLibraryIsCalledFromC()
    {
        if (!OperatingSystem.IsLinux() || !NativeBuilds())
            return;
        File.WriteAllText(Path.Combine(_directory, "geometry.tpn"), """
            const SCALE = 2.0

            def area(width: float, height: float) -> float:
                return width * height * SCALE

            def label(name: str, sides: int) -> str:
                if sides < 3:
                    raise ValueError(f"{name} needs 3 sides, not {sides}")
                return f"{name} with {sides} sides"

            def is_square(sides: int, regular: bool) -> bool:
                return sides == 4 and regular
            """);
        File.WriteAllText(Path.Combine(_directory, "main.c"), """
            #include "geometry.h"
            #include <stdio.h>

            int main(void) {
                printf("%.1f\n", geometry_area(2.0, 3.0));
                char *text = geometry_label("square", 4);
                printf("%s %s\n", text, geometry_last_error() == NULL ? "ok" : "error");
                geometry_free(text);
                char *bad = geometry_label("line", 2);
                printf("%s | %s\n", bad == NULL ? "NULL" : bad, geometry_last_error());
                printf("%d %d\n", geometry_is_square(4, true), geometry_is_square(4, false));
                return 0;
            }
            """);
        var (exit, stdout, stderr) = Taipan("build", Path.Combine(_directory, "geometry.tpn"), "--library", "--native", "-o", Path.Combine(_directory, "out"));
        Assert.True(exit == 0, stderr);
        Assert.Equal($"built {Path.Combine(_directory, "out", "geometry.so")}\n", stdout);
        Assert.Contains("char *geometry_label(const char *name, int64_t sides);", File.ReadAllText(Path.Combine(_directory, "out", "geometry.h")));
        var compiled = Run("gcc", "main.c -Iout out/geometry.so -Wl,-rpath,$ORIGIN/out -o main", _directory);
        Assert.True(compiled.Exit == 0, compiled.Output);
        Assert.Equal((0, "12.0\nsquare with 4 sides ok\nNULL | ValueError: line needs 3 sides, not 2\n1 0\n"), Run(Path.Combine(_directory, "main"), "", _directory));
    }

    [Theory]
    [InlineData("--runtime", "linux-riscv64", "taipan: unknown runtime 'linux-riscv64'; use one of linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64, win-arm64\n")]
    [InlineData("--optimize-size", null, "taipan: --runtime, --invariant-globalization and --optimize-size need --native or --python-extension\n")]
    public void RejectsWrongOptions(string option, string? value, string message)
    {
        string[] args = value is null ? ["build", "examples/health.tpn", option, "-o", "out"] : ["build", "examples/health.tpn", "--native", option, value, "-o", "out"];
        var (exit, stdout, stderr) = Taipan(args);
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith(message, stderr);
    }

    [Fact]
    public void RefusesAnotherOperatingSystem()
    {
        var other = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var host = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        Assert.Equal((2, "", $"taipan: cannot build for {other} on {host}: NativeAOT builds only for the operating system it runs on\n"),
            Taipan("build", "examples/health.tpn", "--native", "--runtime", other, "-o", _directory));
    }
}
