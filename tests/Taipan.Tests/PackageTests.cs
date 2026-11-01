using System.Diagnostics;

namespace Taipan.Tests;

/// <summary>
/// M20 acceptance (SPEC §12.2): packs both packages, installs the tool from the local
/// <c>.nupkg</c> into a clean directory, and runs programs with it there. It takes about a
/// minute, so it runs only with <c>TAIPAN_TEST_PACKAGE=1</c> (as CI sets) and otherwise passes
/// with a note.
/// </summary>
public class PackageTests
{
    [Fact]
    public void InstalledToolRunsPrograms()
    {
        if (Environment.GetEnvironmentVariable("TAIPAN_TEST_PACKAGE") != "1")
        {
            Console.WriteLine("skipped the package test: set TAIPAN_TEST_PACKAGE=1 to run it");
            return;
        }

        var work = Directory.CreateTempSubdirectory("taipan-package-").FullName;
        try
        {
            var version = CliTests.BuildVersion();
            var packages = Path.Combine(work, "packages");
            Succeed("dotnet", ["pack", "-c", "Release", "-o", packages], RepoPaths.Root, freshCache: false);
            Assert.True(File.Exists(Path.Combine(packages, $"TaipanLang.{version}.nupkg")));
            Assert.True(File.Exists(Path.Combine(packages, $"TaipanLang.Runtime.{version}.nupkg")));
            Assert.Equal(2, Directory.GetFiles(packages, "*.nupkg").Length);

            // Only the local packages, so nothing comes from nuget.org.
            var config = Path.Combine(work, "nuget.config");
            File.WriteAllText(config, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{packages}" />
                  </packageSources>
                </configuration>
                """);
            var tools = Path.Combine(work, "tools");
            Succeed("dotnet", ["tool", "install", "TaipanLang", "--version", version, "--tool-path", tools, "--configfile", config], work);
            var taipan = Path.Combine(tools, OperatingSystem.IsWindows() ? "taipan.exe" : "taipan");

            // A directory holding only the programs, away from the repository.
            var project = Path.Combine(work, "project");
            Directory.CreateDirectory(Path.Combine(project, "examples"));
            File.Copy(Path.Combine(RepoPaths.Root, "examples", "health.tpn"), Path.Combine(project, "examples", "health.tpn"));
            File.Copy(Path.Combine(RepoPaths.Root, "tests", "cases", "m10", "json.tpn"), Path.Combine(project, "json.tpn"));

            Assert.Equal($"taipan {version}\n", Succeed(taipan, ["--version"], project));
            var health = Expected("examples", "health.out");
            Assert.Equal(health, Succeed(taipan, ["run", "examples/health.tpn"], project));

            Succeed(taipan, ["build", "examples/health.tpn", "-o", "out"], project);
            Assert.Equal(health, Succeed("dotnet", [Path.Combine("out", "health.dll")], project));

            if (PythonAvailability.Available())
                Assert.Equal(Expected("tests", "cases", "m10", "json.out"), Succeed(taipan, ["run", "json.tpn"], project));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        // An empty package cache for everything after packing, so the install can't use a package
        // cached by an earlier build of the same version.
        string Succeed(string command, string[] args, string directory, bool freshCache = true)
        {
            var start = new ProcessStartInfo(command, args)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (freshCache)
                start.Environment["NUGET_PACKAGES"] = Path.Combine(work, "nuget-cache");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            // MSBuild's reusable nodes and the compiler server outlive the command and would hold
            // its output pipes open, so reading them to the end would never finish.
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            start.Environment["UseSharedCompilation"] = "false";
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var output = stdout.Replace("\r\n", "\n");
            Assert.True(process.ExitCode == 0, $"{command} {string.Join(' ', args)} exited {process.ExitCode}:\n{output}{stderr.Result}");
            return output;
        }
    }

    private static string Expected(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoPaths.Root, .. path])).Replace("\r\n", "\n");
}
