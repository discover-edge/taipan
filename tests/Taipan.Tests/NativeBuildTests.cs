using System.Diagnostics;

namespace Taipan.Tests;

/// <summary>
/// M23 acceptance (SPEC §15): <c>taipan build --native</c> publishes real NativeAOT executables,
/// which print the same output and exit with the same codes as <c>taipan run</c>, panics included.
/// Each build takes several seconds and needs the SDK's native toolchain, so this runs only with
/// <c>TAIPAN_TEST_NATIVE=1</c> (as CI sets) and otherwise passes with a note.
/// <see cref="NativeEmissionTests"/> checks the same emission for every case, without NativeAOT.
/// </summary>
public class NativeBuildTests
{
    public static TheoryData<string> Cases() =>
    [
        "examples/health.tpn",
        "tests/cases/m4/panic_overflow.tpn",
        "tests/cases/m4/panic_elif.tpn",
        "tests/cases/m16/user_exceptions.tpn",
        "tests/cases/m16/uncaught_raise_e.tpn",
        "tests/cases/m19/values.tpn",
        "tests/cases/m21/strings.tpn",
        "tests/cases/m21/references/main.tpn",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeExecutableMatchesRun(string casePath)
    {
        if (Environment.GetEnvironmentVariable("TAIPAN_TEST_NATIVE") != "1")
        {
            Console.WriteLine($"skipped the native build of {casePath}: set TAIPAN_TEST_NATIVE=1 to run it");
            return;
        }

        var output = Directory.CreateTempSubdirectory("taipan-native-test-").FullName;
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Directory.SetCurrentDirectory(RepoPaths.Root);
            var built = Cli.Cli.Run(["build", casePath, "--native", "-o", output], stdout, stderr);
            Assert.True(built == 0, stderr.ToString());
            Assert.Equal("", stderr.ToString());

            var name = Path.GetFileNameWithoutExtension(casePath);
            var start = new ProcessStartInfo(Path.Combine(output, OperatingSystem.IsWindows() ? $"{name}.exe" : name))
            {
                WorkingDirectory = RepoPaths.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var expectedErr = ReadSidecar(casePath, ".err");
            var exitText = ReadSidecar(casePath, ".exit");
            Assert.Equal(expectedErr ?? "", Normalize(await error));
            Assert.Equal(ReadSidecar(casePath, ".out") ?? "", Normalize(text));
            Assert.Equal(exitText is not null ? int.Parse(exitText.Trim()) : expectedErr is not null ? 1 : 0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static string? ReadSidecar(string tpnPath, string extension)
    {
        var path = Path.Combine(RepoPaths.Root, Path.ChangeExtension(tpnPath, extension));
        return File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
