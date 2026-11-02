using System.Diagnostics;
using System.IO.Compression;

namespace Taipan.Tests;

/// <summary>
/// NuGet packages in <c>taipan.json</c> (SPEC §13.2), offline: the golden cases' test assembly is
/// packed into a local feed, and <c>taipan</c> runs in its own process with its own cache and
/// NuGet settings, so nothing is read from nuget.org or the user's caches.
/// </summary>
public class DotNetPackageTests
{
    [Fact]
    public void PackagesAreRestoredCachedAndCopied()
    {
        var work = Directory.CreateTempSubdirectory("taipan-packages-").FullName;
        try
        {
            var feed = Path.Combine(work, "feed");
            Directory.CreateDirectory(feed);
            Pack(Path.Combine(feed, "Acme.Geometry.1.0.0.nupkg"));

            var project = Path.Combine(work, "project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "NuGet.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{Path.Combine(work, "nuget")}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feed}" />
                  </packageSources>
                </configuration>
                """);
            File.WriteAllText(Path.Combine(project, "taipan.json"), """{ "packages": { "Acme.Geometry": "1.0.0" } }""");
            File.WriteAllText(Path.Combine(project, "main.tpn"), """
                from dotnet.Acme.Geometry import Point

                print(Point(0, 0).DistanceTo(Point(3, 4)))
                """);

            Assert.Equal((0, "5.0\n", ""), Taipan(project, "run", "main.tpn"));
            // NuGet's settings are the project's, so the package came from its feed into its folder.
            Assert.True(Directory.Exists(Path.Combine(work, "nuget", "acme.geometry", "1.0.0")));

            // The second run uses the cache: the restore project it would run again is broken.
            var restore = Directory.GetFiles(Path.Combine(work, "cache"), "restore.csproj", SearchOption.AllDirectories).Single();
            File.WriteAllText(restore, "not a project");
            Assert.Equal((0, "5.0\n", ""), Taipan(project, "run", "main.tpn"));

            Assert.Equal(0, Taipan(project, "build", "main.tpn", "-o", "out").Exit);
            Assert.True(File.Exists(Path.Combine(project, "out", "Acme.Geometry.dll")));

            File.WriteAllText(Path.Combine(project, "taipan.json"), """{ "packages": { "Acme.Missing": "1.0.0" } }""");
            var (exit, stdout, stderr) = Taipan(project, "run", "main.tpn");
            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.StartsWith("taipan: cannot restore packages: NU1101: ", stderr);
            Assert.Contains("Acme.Missing", stderr);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        (int Exit, string Stdout, string Stderr) Taipan(string directory, params string[] args)
        {
            var start = new ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "taipan.dll"), .. args])
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["TAIPAN_CACHE"] = Path.Combine(work, "cache");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout.Replace("\r\n", "\n"), stderr.Result.Replace("\r\n", "\n"));
        }
    }

    // A package is a zip of its .nuspec and its assemblies.
    private static void Pack(string path)
    {
        using var package = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(package.CreateEntry("Acme.Geometry.nuspec").Open()))
        {
            writer.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>Acme.Geometry</id>
                    <version>1.0.0</version>
                    <authors>Taipan tests</authors>
                    <description>The M21 golden cases' test assembly.</description>
                  </metadata>
                </package>
                """);
        }
        var assembly = Path.Combine(RepoPaths.Root, "tests", "cases", "m21", "lib", "Acme.Geometry.dll");
        package.CreateEntryFromFile(assembly, "lib/net10.0/Acme.Geometry.dll");
    }
}
