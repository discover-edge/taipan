using System.IO.Compression;
using System.Security;
using System.Text;

namespace Taipan.Cli;

/// <summary>
/// <c>taipan pack</c> (SPEC §18.4, M34): a library as a NuGet package, written directly, with its
/// assembly and its local references in <c>lib/net10.0/</c>, depending on <c>TaipanLang.Runtime</c>
/// and the packages <c>taipan.json</c> lists.
/// </summary>
public static class Packing
{
    private const string Framework = "net10.0";

    /// <summary>Writes the package into <paramref name="directory"/> and returns its path.</summary>
    public static string Pack(byte[] image, string assembly, LibraryNames names, ProjectFile project, string runtimeVersion, string directory)
    {
        var version = names.Version ?? "1.0.0";
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{assembly}.{version}.nupkg");
        File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, $"{assembly}.nuspec", Encoding.UTF8.GetBytes(Nuspec(assembly, version, names, project, runtimeVersion)));
        Write(archive, $"lib/{Framework}/{assembly}.dll", image);
        foreach (var reference in project.LocalReferences)
            Write(archive, $"lib/{Framework}/{Path.GetFileName(reference)}", File.ReadAllBytes(reference));
        Write(archive, "[Content_Types].xml", Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="nuspec" ContentType="application/octet" />
              <Default Extension="dll" ContentType="application/octet" />
            </Types>
            """));
        return path;
    }

    private static string Nuspec(string assembly, string version, LibraryNames names, ProjectFile project, string runtimeVersion)
    {
        var dependencies = new StringBuilder();
        dependencies.Append($"""        <dependency id="TaipanLang.Runtime" version="{Xml(runtimeVersion)}" exclude="Build,Analyzers" />""").Append('\n');
        foreach (var (id, packageVersion) in project.Packages)
            dependencies.Append($"""        <dependency id="{Xml(id)}" version="{Xml(packageVersion)}" exclude="Build,Analyzers" />""").Append('\n');
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Xml(assembly)}</id>
                <version>{Xml(version)}</version>
                <authors>{Xml(names.Authors ?? assembly)}</authors>
                <description>{Xml(names.Description ?? "A Taipan library.")}</description>
                <dependencies>
                  <group targetFramework="{Framework}">
            {dependencies.ToString().TrimEnd('\n')}
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;
    }

    private static string Xml(string text) => SecurityElement.Escape(text);

    private static void Write(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }
}
