using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Taipan.Compiler.Semantics;

namespace Taipan.Tests;

/// <summary>
/// <c>taipan build --library</c> (SPEC §14, M22), beyond what the C# tests in
/// <c>tests/Taipan.LibraryTests</c> show: a stable public API, and Python loaded on first use.
/// </summary>
public sealed class LibraryBuildTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
            Directory.Delete(directory, recursive: true);
    }

    private const string Version1 = """
        @dataclass
        class Reading:
            sensor: str
            value: float = 0.0

        def average(readings: list[Reading]) -> float:
            var total = 0.0
            for r in readings:
                total += r.value
            return total / len(readings)

        def label(reading: Reading, unit: str = "C") -> str:
            return f"{reading.sensor}: {reading.value}{unit}"
        """;

    // Version 1 with a local renamed, and private helpers (one generic, so it has an instance) added.
    private const string Version2 = """
        @dataclass
        class Reading:
            sensor: str
            value: float = 0.0

        class _Cache:
            hits: int

            def __init__(self):
                self.hits = 0

        def _first[T: Comparable](xs: list[T]) -> T:
            return xs[0]

        def _sum(readings: list[Reading]) -> float:
            var sum = 0.0
            for reading in readings:
                sum += reading.value
            return sum

        def average(readings: list[Reading]) -> float:
            return _sum(readings) / len(readings)

        def label(reading: Reading, unit: str = "C") -> str:
            let cache = _Cache()
            cache.hits += _first([1, 2])
            return f"{reading.sensor}: {reading.value}{unit}"
        """;

    [Fact]
    public void PublicApiIgnoresLocalsAndPrivateDefinitions()
    {
        var first = PublicApi(Build("sensors", Version1));
        var second = PublicApi(Build("sensors", Version2));
        Assert.Contains("Sensors.sensors.average(System.Collections.Generic.List<Sensors.sensors.Reading>)", first);
        Assert.Contains("Sensors.sensors.label(Sensors.sensors.Reading)", first);
        Assert.Contains("Sensors.sensors.Reading.Reading(string)", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void PythonLoadsOnFirstUse()
    {
        var library = Build("encoder", """
            from python import json

            def encode(n: int) -> str:
                return json.dumps(n) as str
            """);
        var context = new AssemblyLoadContext("taipan-library-test", isCollectible: true);
        try
        {
            var encode = context.LoadFromAssemblyPath(library).GetType("Encoder.encoder")!.GetMethod("encode")!;
            if (PythonAvailability.Available())
            {
                Assert.Equal("3", encode.Invoke(null, [3L]));
            }
            else
            {
                var error = Assert.Throws<TargetInvocationException>(() => encode.Invoke(null, [3L]));
                Assert.StartsWith("cannot load Python", Assert.IsType<Taipan.Runtime.TaipanPanic>(error.InnerException).Message);
            }
        }
        finally
        {
            context.Unload();
        }
    }

    // Builds a one-file library in its own directory; returns the assembly's path.
    private string Build(string name, string source)
    {
        var directory = Directory.CreateTempSubdirectory("taipan-library-").FullName;
        _directories.Add(directory);
        var path = Path.Combine(directory, $"{name}.tpn");
        File.WriteAllText(path, source);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = Cli.Cli.Run(["build", path, "--library", "-o", Path.Combine(directory, "out")], stdout, stderr);
        Assert.True(exit == 0, stderr.ToString());
        Assert.Equal("", stderr.ToString());
        return Path.Combine(directory, "out", $"{name}.dll");
    }

    /// <summary>Every public type and member of an assembly, read from its metadata.</summary>
    private static List<string> PublicApi(string assembly)
    {
        var framework = DotNetLibrary.ReferencePackDirectory()!;
        var references = Directory.GetFiles(framework, "*.dll").Select(p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(Taipan.Runtime.Panic).Assembly.Location));
        var library = MetadataReference.CreateFromFile(assembly);
        var compilation = CSharpCompilation.Create("api", references: [.. references, library]);
        var symbol = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(library)!;

        var api = new List<string>();
        void Visit(INamespaceOrTypeSymbol container)
        {
            foreach (var type in container.GetTypeMembers().Where(t => t.DeclaredAccessibility == Accessibility.Public))
            {
                api.Add($"{type.TypeKind} {type.ToDisplayString()}");
                api.AddRange(type.GetMembers()
                    .Where(m => m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected && m is not INamedTypeSymbol)
                    .Select(m => m.ToDisplayString()));
                Visit(type);
            }
            if (container is INamespaceSymbol ns)
            {
                foreach (var inner in ns.GetNamespaceMembers())
                    Visit(inner);
            }
        }
        Visit(symbol.GlobalNamespace);
        api.Sort(StringComparer.Ordinal);
        return api;
    }
}
