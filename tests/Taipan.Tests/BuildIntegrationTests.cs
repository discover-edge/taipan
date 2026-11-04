using System.Diagnostics;
using System.IO.Compression;

namespace Taipan.Tests;

/// <summary>
/// The packages, packed once into a local feed for <see cref="BuildIntegrationTests"/>, with a
/// NuGet.config that restores from it and from this machine's package cache (SPEC §18).
/// </summary>
public sealed class PackedFeed : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"taipan-m34-{Guid.NewGuid():N}");

    public string Feed => Path.Combine(Root, "feed");

    public string Version { get; } = CliTests.BuildVersion();

    public PackedFeed()
    {
        Directory.CreateDirectory(Feed);
        // The packages only, not the solution's test projects (SPEC §12.2).
        foreach (var project in (string[])["src/Taipan.Runtime", "src/Taipan.Cli", "src/TaipanLang.Sdk"])
        {
            var (exit, output) = BuildIntegrationTests.RunDotnet(RepoPaths.Root, null, "pack", project, "-c", "Release", "-o", Feed);
            Assert.True(exit == 0, output);
        }
    }

    /// <summary>A new directory with a NuGet.config for the feed.</summary>
    public string Workspace(string name)
    {
        var directory = Path.Combine(Root, name);
        Directory.CreateDirectory(directory);
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        File.WriteAllText(Path.Combine(directory, "NuGet.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="taipan" value="{Feed}" />
                <add key="cache" value="{cache}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        return directory;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// M34 acceptance (SPEC §18): a solution with a Taipan project and C# projects referencing each
/// other builds and tests with <c>dotnet test</c>, and a packed Taipan library installs from a
/// local feed into a fresh C# project and is called from it.
/// </summary>
public class BuildIntegrationTests(PackedFeed packed) : IClassFixture<PackedFeed>
{
    /// <summary>Runs <c>dotnet</c> with a package cache of its own, so the feed's packages are always the ones just packed.</summary>
    internal static (int Exit, string Output) RunDotnet(string directory, string? packages, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (packages is not null)
            start.Environment["NUGET_PACKAGES"] = packages;
        // A build inside a test run shouldn't reuse its MSBuild nodes.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }

    private (int Exit, string Output) Dotnet(string directory, params string[] arguments) =>
        RunDotnet(directory, Path.Combine(packed.Root, "packages"), arguments);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // A C# library, a Taipan library using it, and a C# test project using that (SPEC §18.1).
    private string Solution(string name)
    {
        var root = packed.Workspace(name);
        Write(Path.Combine(root, "Greeting", "Greeting.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        Write(Path.Combine(root, "Greeting", "Greeter.cs"), """
            namespace Greeting;

            public static class Greeter
            {
                public static string Greet(string name) => $"hello, {name}";
            }
            """);
        Write(Path.Combine(root, "Shapes", "Shapes.tpnproj"), $"""
            <Project Sdk="TaipanLang.Sdk/{packed.Version}">
              <PropertyGroup>
                <RootNamespace>Acme.Shapes</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Greeting/Greeting.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write(Path.Combine(root, "Shapes", "Shapes.tpn"), """
            from dotnet.Greeting import Greeter
            import sizes

            def area(width: float, height: float) -> float:
                return width * height

            def describe(name: str) -> str:
                return Greeter.Greet(name).upper() + " " + sizes.label(area(2.0, 3.0))
            """);
        Write(Path.Combine(root, "Shapes", "sizes.tpn"), """
            def label(size: float) -> str:
                if size > 5.0:
                    return "large"
                return "small"
            """);
        Write(Path.Combine(root, "Shapes.Tests", "Shapes.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../Shapes/Shapes.tpnproj" />
              </ItemGroup>
            </Project>
            """);
        Write(Path.Combine(root, "Shapes.Tests", "ShapesTests.cs"), """
            using Xunit;

            public class ShapesTests
            {
                [Fact]
                public void Area() => Assert.Equal(6.0, Acme.Shapes.Shapes.area(2.0, 3.0));

                [Fact]
                public void Describe() => Assert.Equal("HELLO, TAIPAN large", Acme.Shapes.Shapes.describe("taipan"));
            }
            """);
        Assert.Equal(0, Dotnet(root, "new", "sln", "-n", name).Exit);
        foreach (var project in (string[])["Greeting/Greeting.csproj", "Shapes/Shapes.tpnproj", "Shapes.Tests/Shapes.Tests.csproj"])
        {
            var (exit, output) = Dotnet(root, "sln", "add", project);
            Assert.True(exit == 0, output);
        }
        return root;
    }

    [Fact]
    public void SolutionBuildsAndTests()
    {
        var root = Solution("solution");
        var (exit, output) = Dotnet(root, "test");
        Assert.True(exit == 0, output);
        Assert.Contains("Passed!  - Failed:     0, Passed:     2", output);

        // An SDK project packs as any .NET project does, depending on the runtime (SPEC §18.4).
        (exit, output) = Dotnet(root, "pack", "Shapes", "-o", Path.Combine(root, "packed"));
        Assert.True(exit == 0, output);
        using var archive = ZipFile.OpenRead(Path.Combine(root, "packed", "Shapes.1.0.0.nupkg"));
        Assert.Contains(archive.Entries, e => e.FullName == "lib/net10.0/Shapes.dll");
        using var nuspec = new StreamReader(archive.GetEntry("Shapes.nuspec")!.Open());
        Assert.Contains("id=\"TaipanLang.Runtime\"", nuspec.ReadToEnd());
    }

    [Fact]
    public void BuildReportsTaipanDiagnostics()
    {
        var root = Solution("diagnostics");
        var shapes = Path.Combine(root, "Shapes");
        File.AppendAllText(Path.Combine(shapes, "Shapes.tpn"), "\ndef broken() -> int:\n    return \"x\"\n");
        var (exit, output) = Dotnet(shapes, "build");
        Assert.NotEqual(0, exit);
        Assert.Contains($"{Path.Combine(shapes, "Shapes.tpn")}(10,12): error TP0200: expected int, found str", output);
        Assert.DoesNotContain("exited with code", output);

        // An SDK project lists its dependencies in the project file (TPN002), and needs its main file (TPN001).
        File.WriteAllText(Path.Combine(shapes, "taipan.json"), """{ "references": [] }""");
        (exit, output) = Dotnet(shapes, "build");
        Assert.NotEqual(0, exit);
        Assert.Contains("error TPN002: list references and packages in the project file, not taipan.json", output);
        File.Delete(Path.Combine(shapes, "taipan.json"));
        File.Move(Path.Combine(shapes, "Shapes.tpn"), Path.Combine(shapes, "Other.tpn"));
        (exit, output) = Dotnet(shapes, "build");
        Assert.NotEqual(0, exit);
        Assert.Contains("error TPN001: the main file", output);
    }

    [Fact]
    public void ProgramRunsWithDotnetRun()
    {
        var root = packed.Workspace("program");
        Write(Path.Combine(root, "Hello", "Hello.tpnproj"), $"""
            <Project Sdk="TaipanLang.Sdk/{packed.Version}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        Write(Path.Combine(root, "Hello", "Hello.tpn"), "print(\"hello from\", \"dotnet run\")\n");
        var (exit, output) = Dotnet(Path.Combine(root, "Hello"), "run");
        Assert.True(exit == 0, output);
        Assert.EndsWith("hello from dotnet run\n", output.Replace("\r\n", "\n"));
    }

    // M44 (SPEC §18.6): a test project runs its main file's tests with dotnet test and dotnet run.
    [Fact]
    public void TestProjectRunsWithDotnetTest()
    {
        var root = packed.Workspace("tests");
        Write(Path.Combine(root, "Checks", "Checks.tpnproj"), $"""
            <Project Sdk="TaipanLang.Sdk/{packed.Version}">
              <PropertyGroup>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
            </Project>
            """);
        Write(Path.Combine(root, "Checks", "Checks.tpn"), """
            import helpers

            def test_double():
                assert helpers.double(2) == 4

            def test_wrong():
                assert helpers.double(2) == 5, "two doubled"
            """);
        Write(Path.Combine(root, "Checks", "helpers.tpn"), "def double(n: int) -> int:\n    return n * 2\n");
        var (exit, output) = Dotnet(Path.Combine(root, "Checks"), "test");
        Assert.NotEqual(0, exit);
        output = output.Replace("\r\n", "\n");
        Assert.Contains("PASS Checks.tpn::test_double", output);
        Assert.Contains("FAIL Checks.tpn::test_wrong", output);
        Assert.Contains("  File \"Checks.tpn\", line 7, in test_wrong", output);
        Assert.Contains("AssertionError: two doubled", output);
        Assert.Contains("1 passed, 1 failed", output);

        File.WriteAllText(Path.Combine(root, "Checks", "Checks.tpn"), "import helpers\n\ndef test_double():\n    assert helpers.double(2) == 4\n");
        (exit, output) = Dotnet(Path.Combine(root, "Checks"), "test");
        Assert.True(exit == 0, output);
        Assert.Contains("1 passed, 0 failed", output);
        (exit, output) = Dotnet(Path.Combine(root, "Checks"), "run");
        Assert.True(exit == 0, output);
        Assert.EndsWith("PASS Checks.tpn::test_double\n1 passed, 0 failed\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void PackedLibraryInstallsIntoACSharpProject()
    {
        var root = packed.Workspace("pack");
        var library = Path.Combine(root, "library");
        Write(Path.Combine(library, "taipan.json"), """
            { "assembly": "Acme.Geometry.Taipan", "namespace": "Acme.Geo", "version": "1.2.0", "authors": "Acme", "description": "Areas." }
            """);
        Write(Path.Combine(library, "geometry.tpn"), """
            import shapes

            def total(sizes: list[float]) -> float:
                var sum = 0.0
                for size in sizes:
                    sum += shapes.square(size)
                return sum
            """);
        Write(Path.Combine(library, "shapes.tpn"), "def square(x: float) -> float:\n    return x * x\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(0, Cli.Cli.Run(["pack", Path.Combine(library, "geometry.tpn"), "-o", packed.Feed], stdout, stderr));
        var package = Path.Combine(packed.Feed, "Acme.Geometry.Taipan.1.2.0.nupkg");
        Assert.Equal($"packed {package}\n", stdout.ToString().Replace("\r\n", "\n"));
        using (var archive = ZipFile.OpenRead(package))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "lib/net10.0/Acme.Geometry.Taipan.dll");
            using var nuspec = new StreamReader(archive.GetEntry("Acme.Geometry.Taipan.nuspec")!.Open());
            Assert.Contains($"""<dependency id="TaipanLang.Runtime" version="{packed.Version}" """, nuspec.ReadToEnd());
        }

        var app = Path.Combine(root, "app");
        Write(Path.Combine(app, "app.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Acme.Geometry.Taipan" Version="1.2.0" /></ItemGroup>
            </Project>
            """);
        Write(Path.Combine(app, "Program.cs"), """
            System.Console.WriteLine(Acme.Geo.geometry.total([1.0, 2.0, 3.0]));
            System.Console.WriteLine(typeof(Acme.Geo.geometry).Assembly.GetName().Version);
            """);
        var (exit, output) = Dotnet(app, "run");
        Assert.True(exit == 0, output);
        Assert.EndsWith("14\n1.2.0.0\n", output.Replace("\r\n", "\n"));
    }
}
