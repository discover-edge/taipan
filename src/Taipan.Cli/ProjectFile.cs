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

    /// <summary>The assemblies <c>references</c> lists, which a packed library carries (SPEC §18.4).</summary>
    public ImmutableArray<string> LocalReferences { get; init; } = [];

    /// <summary>The packages <c>packages</c> lists, by ID, which a packed library depends on (SPEC §18.4).</summary>
    public ImmutableSortedDictionary<string, string> Packages { get; init; } = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>How a library and its package are named (SPEC §18.3, M34); null for a key that isn't given.</summary>
    public LibraryNames Names { get; init; } = new(null, null, null, null, null);

    // A dotted .NET name, such as Acme.Shapes.
    private static readonly Regex DottedName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$");

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
        var names = new Dictionary<string, string>();
        foreach (var property in root.EnumerateObject())
        {
            error = property.Name switch
            {
                "references" => ReadReferences(property.Value, directory, references, path),
                "packages" => ReadPackages(property.Value, packages, path),
                "assembly" or "namespace" or "version" or "authors" or "description" => ReadName(property, names, path),
                _ => $"{path}: unknown key '{property.Name}'",
            };
            if (error is not null)
                return null;
        }

        var project = new ProjectFile([.. references], [.. references])
        {
            LocalReferences = [.. references],
            Packages = packages.ToImmutableSortedDictionary(StringComparer.OrdinalIgnoreCase),
            Names = new(names.GetValueOrDefault("assembly"), names.GetValueOrDefault("namespace"), names.GetValueOrDefault("version"),
                names.GetValueOrDefault("authors"), names.GetValueOrDefault("description")),
        };
        if (packages.Count == 0)
            return project;
        if (PackageRestore.Restore(packages, directory, out error) is not { } restored)
            return null;
        return project with { CompileReferences = [.. references, .. restored.Compile], RuntimeAssemblies = [.. references, .. restored.Runtime] };
    }

    /// <summary>Whether a <c>taipan.json</c> beside the file lists references or packages, which an SDK project may not (SPEC §18.1).</summary>
    public static bool ListsDependencies(string mainPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(mainPath) ?? "", FileName);
        try
        {
            using var document = JsonDocument.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}");
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(p => p.Name is "references" or "packages");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A library's names (SPEC §18.3): dotted names, a NuGet version, and free text.
    private static string? ReadName(JsonProperty property, Dictionary<string, string> names, string path)
    {
        var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : null;
        var valid = property.Name switch
        {
            "assembly" or "namespace" => value is not null && DottedName.IsMatch(value),
            "version" => value is not null && ExactVersion.IsMatch(value),
            _ => value is not null,
        };
        if (!valid)
        {
            return property.Name switch
            {
                "assembly" or "namespace" => $"{path}: '{property.Name}' must be a dotted name",
                "version" => $"{path}: 'version' must be a version, found '{value ?? property.Value.GetRawText()}'",
                _ => $"{path}: '{property.Name}' must be a string",
            };
        }
        names[property.Name] = value!;
        return null;
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

/// <summary>A library's assembly, namespace and version, and its package's authors and description (SPEC §18.3).</summary>
public sealed record LibraryNames(string? Assembly, string? Namespace, string? Version, string? Authors, string? Description);
