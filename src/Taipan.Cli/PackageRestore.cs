using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Taipan.Cli;

/// <summary>
/// Restores a program's NuGet packages with <c>dotnet restore</c> and caches the assemblies they
/// bring, keyed by the package list (SPEC §13.2). A cached list needs neither the SDK nor the network.
/// </summary>
public static class PackageRestore
{
    public sealed record Assemblies(IReadOnlyList<string> Compile, IReadOnlyList<string> Runtime);

    private const string ResultFile = "assemblies.txt";

    public static Assemblies? Restore(SortedDictionary<string, string> packages, string mainDirectory, out string? error)
    {
        error = null;
        var key = string.Join("\n", packages.Select(p => $"{p.Key.ToLowerInvariant()}/{p.Value}"));
        var directory = Path.Combine(CacheDirectory(), "packages", Hash(key));
        Directory.CreateDirectory(directory);
        if (ReadResult(directory) is { } cached)
            return cached;

        // One restore at a time per list; whoever waited finds the result the first one wrote.
        using var lockFile = Lock(directory);
        if (ReadResult(directory) is { } restored)
            return restored;

        var framework = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var project = Path.Combine(directory, "restore.csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{framework}</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
            {string.Join("\n", packages.Select(p => $"    <PackageReference Include=\"{p.Key}\" Version=\"[{p.Value}]\" />"))}
              </ItemGroup>
            </Project>
            """);

        // NuGet's settings are those that apply to the program's directory, not the cache's.
        string[] arguments =
        [
            "restore", project, "--nologo", "-v", "q",
            $"-p:RestoreRootConfigDirectory={Path.GetFullPath(mainDirectory == "" ? "." : mainDirectory)}",
            "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
        ];
        if (RunDotNet(arguments, directory, out var output) is { } failure)
        {
            error = $"cannot restore packages: {failure}";
            return null;
        }
        if (!output.Success)
        {
            error = $"cannot restore packages: {FirstError(output.Text)}";
            return null;
        }

        if (ReadAssets(Path.Combine(directory, "obj", "project.assets.json"), framework, out error) is not { } assemblies)
            return null;
        File.WriteAllLines(Path.Combine(directory, ResultFile),
            [.. assemblies.Compile.Select(path => $"compile\t{path}"), .. assemblies.Runtime.Select(path => $"runtime\t{path}")]);
        return assemblies;
    }

    /// <summary>The per-user cache: <c>$TAIPAN_CACHE</c>, or <c>taipan</c> in the user's cache directory.</summary>
    private static string CacheDirectory()
    {
        if (Environment.GetEnvironmentVariable("TAIPAN_CACHE") is { Length: > 0 } configured)
            return configured;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "taipan");
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cache, "taipan");
    }

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    // A cached result, as long as its assemblies are still there.
    private static Assemblies? ReadResult(string directory)
    {
        var path = Path.Combine(directory, ResultFile);
        if (!File.Exists(path))
            return null;
        var entries = File.ReadAllLines(path).Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2).ToList();
        if (entries.Any(parts => !File.Exists(parts[1])))
            return null;
        return new(
            [.. entries.Where(parts => parts[0] == "compile").Select(parts => parts[1])],
            [.. entries.Where(parts => parts[0] == "runtime").Select(parts => parts[1])]);
    }

    private static FileStream Lock(string directory)
    {
        var path = Path.Combine(directory, ".lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 600)
            {
                Thread.Sleep(500);
            }
        }
    }

    private sealed record Output(bool Success, string Text);

    // Runs the SDK's dotnet; the message if it can't be started.
    private static string? RunDotNet(string[] arguments, string directory, out Output output)
    {
        output = new(false, "");
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        // No build servers or reused nodes, which would outlive the restore.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        try
        {
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            output = new(process.ExitCode == 0, stdout + stderr.Result);
            return null;
        }
        catch (Win32Exception)
        {
            return "the .NET SDK was not found";
        }
    }

    // `…/restore.csproj : error NU1101: Unable to find package X. … [/…/restore.csproj]` → `NU1101: Unable to find package X. …`
    private static string FirstError(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var at = line.IndexOf(": error ", StringComparison.Ordinal);
            if (at < 0)
                continue;
            var message = line[(at + ": error ".Length)..].Trim();
            var project = message.LastIndexOf(" [", StringComparison.Ordinal);
            return project > 0 && message.EndsWith(']') ? message[..project] : message;
        }
        return output.Trim() is { Length: > 0 } text ? text.Split('\n')[0].Trim() : "dotnet restore failed";
    }

    /// <summary>
    /// The assemblies <c>project.assets.json</c> lists for the target framework. Those the .NET
    /// runtime also provides are the runtime's; native and runtime-specific assets aren't supported yet.
    /// </summary>
    private static Assemblies? ReadAssets(string path, string framework, out string? error)
    {
        error = null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var packageFolder = root.GetProperty("packageFolders").EnumerateObject().First().Name;
        var libraries = root.GetProperty("libraries");
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var compile = new List<string>();
        var runtime = new List<string>();
        foreach (var library in root.GetProperty("targets").GetProperty(framework).EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
                continue;
            var id = library.Name.Split('/')[0];
            if (HasAny(library.Value, "native") || HasAny(library.Value, "runtimeTargets"))
            {
                error = $"package '{id}' has native or runtime-specific assets, which aren't supported yet";
                return null;
            }
            var folder = Path.Combine(packageFolder, libraries.GetProperty(library.Name).GetProperty("path").GetString()!);
            compile.AddRange(Assets(library.Value, "compile", folder, runtimeDirectory));
            runtime.AddRange(Assets(library.Value, "runtime", folder, runtimeDirectory));
        }
        return new(compile, runtime);
    }

    private static bool HasAny(JsonElement library, string kind) =>
        library.TryGetProperty(kind, out var assets) && assets.EnumerateObject().Any(a => !IsPlaceholder(a.Name));

    // `_._` marks a framework the package supports with nothing to add.
    private static bool IsPlaceholder(string asset) => Path.GetFileName(asset) == "_._";

    private static IEnumerable<string> Assets(JsonElement library, string kind, string folder, string runtimeDirectory)
    {
        if (!library.TryGetProperty(kind, out var assets))
            yield break;
        foreach (var asset in assets.EnumerateObject())
        {
            if (IsPlaceholder(asset.Name) || !asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(Path.Combine(runtimeDirectory, Path.GetFileName(asset.Name))))
                continue;
            yield return Path.GetFullPath(Path.Combine(folder, asset.Name));
        }
    }
}
