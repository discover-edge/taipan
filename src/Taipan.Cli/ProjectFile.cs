using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taipan.Cli;

/// <summary>
/// A program's <c>taipan.json</c> (SPEC §13.2): the assemblies it compiles against, and those it
/// loads when it runs. A package's compile and run-time assemblies may differ.
/// </summary>
public sealed record ProjectFile(ImmutableArray<string> CompileReferences, ImmutableArray<string> RuntimeAssemblies)
{
    public const string FileName = "taipan.json";

    public static ProjectFile Empty { get; } = new([], []);

    /// <summary>The project of the main file at <paramref name="mainPath"/>; null with <paramref name="error"/> set if it's wrong.</summary>
    public static ProjectFile? Load(string mainPath, out string? error)
    {
        error = null;
        var directory = Path.GetDirectoryName(mainPath) ?? "";
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return Empty;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            error = $"{path}: invalid JSON (line {(e.LineNumber ?? 0) + 1})";
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            error = $"{path}: invalid JSON (line 1)";
            return null;
        }

        var references = new List<string>();
        var packages = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            error = property.Name switch
            {
                "references" => ReadReferences(property.Value, directory, references, path),
                "packages" => ReadPackages(property.Value, packages, path),
                _ => $"{path}: unknown key '{property.Name}'",
            };
            if (error is not null)
                return null;
        }

        if (packages.Count == 0)
            return new([.. references], [.. references]);
        if (PackageRestore.Restore(packages, directory, out error) is not { } restored)
            return null;
        return new([.. references, .. restored.Compile], [.. references, .. restored.Runtime]);
    }

    private static string? ReadReferences(JsonElement value, string directory, List<string> references, string path)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            return $"{path}: 'references' must be a list of paths";
        foreach (var entry in value.EnumerateArray())
        {
            var reference = Path.Combine(directory, entry.GetString()!);
            if (!File.Exists(reference))
                return $"assembly not found: {reference}";
            if (!IsAssembly(reference))
                return $"not a .NET assembly: {reference}";
            references.Add(Path.GetFullPath(reference));
        }
        return null;
    }

    // An exact NuGet version: 1.2, 1.2.3 or 1.2.3.4, with an optional prerelease and build.
    private static readonly Regex ExactVersion = new(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");

    private static string? ReadPackages(JsonElement value, SortedDictionary<string, string> packages, string path)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
            return $"{path}: 'packages' must map package IDs to versions";
        foreach (var package in value.EnumerateObject())
        {
            var version = package.Value.GetString()!;
            if (!ExactVersion.IsMatch(version))
                return $"{path}: package '{package.Name}' needs an exact version, found '{version}'";
            packages[package.Name] = version;
        }
        return null;
    }

    private static bool IsAssembly(string path)
    {
        try
        {
            using var reader = new PEReader(File.OpenRead(path));
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
