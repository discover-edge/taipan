using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Taipan.Compiler.Emit;
using Taipan.Runtime;

namespace Taipan.Cli;

/// <summary>A native build's platform and size options (SPEC §15.4, §15.5, M45): the runtime identifier, or null for this machine's.</summary>
public sealed record NativeOptions(string? Runtime, bool InvariantGlobalization, bool OptimizeSize);

/// <summary>
/// <c>taipan build --native</c> (SPEC §15.1): publishes the emitted C# with NativeAOT through the
/// SDK's <c>dotnet publish</c>, in a scratch project, and copies the executable out.
/// </summary>
public static class NativeBuild
{
    /// <summary>The runtime identifiers <c>--runtime</c> takes (SPEC §15.4).</summary>
    public static readonly string[] RuntimeIdentifiers = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];

    /// <summary>
    /// The executable's path, or the CPython extension module's (SPEC §17.1), or the native
    /// library's (SPEC §15.6), or null with <paramref name="error"/> set.
    /// </summary>
    public static string? Publish(
        EmittedProgram program, string name, string mainDirectory, string outputDirectory, ImmutableArray<string> assemblies, Target target,
        NativeOptions options, TextWriter stderr, out string? error)
    {
        error = null;
        var host = PortableRuntimeIdentifier();
        var rid = options.Runtime ?? host;
        var (os, hostOs) = (rid.Split('-')[0], host.Split('-')[0]);
        // NativeAOT builds for its own operating system, in any of its architectures (SPEC §15.4).
        if (os != hostOs)
        {
            error = $"cannot build for {rid} on {hostOs}: NativeAOT builds only for the operating system it runs on";
            return null;
        }
        string? triple = null;
        if (os == "linux" && rid != host)
        {
            triple = rid == "linux-arm64" ? "aarch64-linux-gnu" : "x86_64-linux-gnu";
            if (!OnPath($"{triple}-gcc"))
            {
                error = $"building for {rid} needs {triple}-gcc on PATH";
                return null;
            }
        }
        var work = Directory.CreateTempSubdirectory("taipan-native-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(work, "program.cs"), program.Code);
            File.WriteAllText(Path.Combine(work, $"{name}.csproj"), Project(name, program.EntryClass, assemblies, crossGcc: triple is not null));

            var published = Path.Combine(work, "out");
            // NuGet's settings, for the ILCompiler package, are those that apply to the program's directory (SPEC §13.2).
            List<string> arguments =
            [
                "publish", Path.Combine(work, $"{name}.csproj"), "-c", "Release", "-r", rid, "-o", published, "--nologo", "-v", "q",
                $"-p:RestoreRootConfigDirectory={Path.GetFullPath(mainDirectory == "" ? "." : mainDirectory)}",
                "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
                "-p:UseSharedCompilation=false",
            ];
            // Linux cross-compiles with the target's GCC and binutils (SPEC §15.4).
            if (triple is not null)
                arguments.AddRange([$"-p:CppCompilerAndLinker={triple}-gcc", $"-p:ObjCopyName={triple}-objcopy"]);
            if (options.InvariantGlobalization)
                arguments.Add("-p:InvariantGlobalization=true");
            if (options.OptimizeSize)
                arguments.Add("-p:OptimizationPreference=Size");
            if (DotNetSdk.Run([.. arguments], work, out var output) is not null)
            {
                error = "building natively needs the .NET SDK";
                return null;
            }
            if (!output.Success)
            {
                error = $"native build failed: {DotNetSdk.FirstError(output.Text)}";
                return null;
            }
            foreach (var warning in DotNetSdk.Messages(output.Text, "warning").Where(w => w.StartsWith("IL", StringComparison.Ordinal)))
                stderr.WriteLine($"taipan: warning: {warning}");

            var (built, final) = target switch
            {
                Target.PythonExtension => ExtensionFiles(name, os),
                Target.NativeLibrary => LibraryFiles(name, os),
                _ => os == "win" ? ($"{name}.exe", $"{name}.exe") : (name, name),
            };
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, final);
            File.Copy(Path.Combine(published, built), path, overwrite: true);
            // A native library comes with its C header (SPEC §15.6).
            if (program.Header is { } header)
                File.WriteAllText(Path.Combine(outputDirectory, $"{name}.h"), header);
            return path;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // The shared library NativeAOT writes, and the name CPython imports a stable-ABI module by (SPEC §17.1).
    private static (string Built, string Final) ExtensionFiles(string name, string os) =>
        os == "win" ? ($"{name}.dll", $"{name}.pyd")
        : os == "osx" ? ($"{name}.dylib", $"{name}.abi3.so")
        : ($"{name}.so", $"{name}.abi3.so");

    // A native library is the shared library NativeAOT writes, by its platform's name (SPEC §15.6).
    private static (string Built, string Final) LibraryFiles(string name, string os)
    {
        var file = os == "win" ? $"{name}.dll" : os == "osx" ? $"{name}.dylib" : $"{name}.so";
        return (file, file);
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, program)));

    /// <summary>
    /// This machine's portable runtime identifier, such as <c>linux-x64</c> (SPEC §15.1). A distribution's
    /// own build of .NET reports its own (<c>ubuntu.24.04-x64</c>), whose NativeAOT packs need the
    /// distribution's libraries; the portable one's come from NuGet and link on their own.
    /// </summary>
    private static string PortableRuntimeIdentifier()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        if (rid.StartsWith("linux-", StringComparison.Ordinal) || rid.StartsWith("osx-", StringComparison.Ordinal) || rid.StartsWith("win-", StringComparison.Ordinal))
            return rid;
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{architecture}";
    }

    // The emitted C# is generated by us, so its C# warnings aren't the user's; only the AOT
    // compiler's (IL…) warnings are reported. Taipan.Runtime and pythonnet are referenced as compiled
    // programs reference them (SPEC §8), and the program's own assemblies (SPEC §13.2) beside them.
    // An extension module (no entry class) is a shared library, with unsafe code for CPython's API.
    // NativeAOT gives the linker clang's --target, which a target's own GCC doesn't take (SPEC §15.4).
    private const string CrossGccTarget = """
          <Target Name="TaipanCrossGcc" AfterTargets="SetupOSSpecificProps">
            <ItemGroup>
              <LinkerArg Remove="--target=$(TargetTriple)" />
            </ItemGroup>
          </Target>
        """;

    private static string Project(string name, string? entryClass, ImmutableArray<string> assemblies, bool crossGcc)
    {
        IEnumerable<string> references = [typeof(Panic).Assembly.Location, Interop.PythonRuntimeAssembly, .. assemblies];
        var items = string.Join("\n", references.Select(path =>
            $"    <Reference Include=\"{Path.GetFileNameWithoutExtension(path)}\" HintPath=\"{System.Security.SecurityElement.Escape(path)}\" />"));
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {(entryClass is null ? "<OutputType>Library</OutputType>\n    <NativeLib>Shared</NativeLib>\n    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>" : $"<OutputType>Exe</OutputType>\n    <StartupObject>{entryClass}</StartupObject>")}
                <TargetFramework>net{Environment.Version.Major}.{Environment.Version.Minor}</TargetFramework>
                <AssemblyName>{name}</AssemblyName>
                <RootNamespace>{name}</RootNamespace>
                <PublishAot>true</PublishAot>
                <Nullable>enable</Nullable>
                <LangVersion>latest</LangVersion>
                <ImplicitUsings>disable</ImplicitUsings>
                <CheckForOverflowUnderflow>true</CheckForOverflowUnderflow>
                <WarningLevel>0</WarningLevel>
                <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="program.cs" />
            {items}
              </ItemGroup>
            {(crossGcc ? CrossGccTarget : "")}
            </Project>
            """;
    }
}
