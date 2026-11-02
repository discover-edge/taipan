using System.Collections.Immutable;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Syntax;

/// <summary>One file of a program; <see cref="Name"/> is its module name (the file name without <c>.tpn</c>).</summary>
public sealed record SourceModule(string Name, SourceText Source, CompilationUnit Unit);

/// <param name="Modules">The main file first, then the modules it reaches, in the order they were found.</param>
public sealed record LoadResult(ImmutableArray<SourceModule> Modules, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Finds, lexes and parses every file of a program (SPEC §4.7). Stage gating applies across files:
/// if any file has lexical errors, only those are reported.
/// </summary>
public static class ModuleLoader
{
    /// <summary>Reserved for Python interop (SPEC §10); the binder reports it as not implemented.</summary>
    public const string PythonModule = "python";

    /// <summary>Reserved for .NET interop: the root of .NET namespaces, which the binder resolves (SPEC §13.1).</summary>
    public const string DotNetModule = "dotnet";

    /// <summary>Whether an import names .NET rather than a Taipan module: <c>dotnet</c> or <c>dotnet.&lt;namespace&gt;</c>.</summary>
    public static bool IsDotNet(string module) => module == DotNetModule || module.StartsWith(DotNetModule + ".", StringComparison.Ordinal);

    /// <summary>
    /// Loads a program from its main file. <paramref name="openText"/> gives an editor's unsaved
    /// text for a path, or null to read the file (SPEC §16.1).
    /// </summary>
    public static LoadResult Load(string mainPath, Func<string, string?>? openText = null)
    {
        string? Read(string path) => openText?.Invoke(path) ?? (File.Exists(path) ? File.ReadAllText(path) : null);

        var modules = new List<SourceModule>();
        var loaded = new HashSet<string>();
        var lexical = new List<Diagnostic>();
        var syntax = new List<Diagnostic>();

        var main = Parse(Path.GetFileNameWithoutExtension(mainPath), mainPath, Read(mainPath) ?? "", lexical, syntax);
        if (main is not null)
        {
            modules.Add(main);
            loaded.Add(main.Name);
        }

        for (var i = 0; i < modules.Count; i++)
        {
            var importer = modules[i];
            foreach (var import in Imports(importer.Unit))
            {
                if (import.Text == PythonModule || IsDotNet(import.Text) || !loaded.Add(import.Text))
                    continue;
                // Modules aren't dotted; only .NET namespaces are (SPEC §4.7).
                if (import.Text.Contains('.'))
                {
                    syntax.Add(new Diagnostic("TP0221", import.Span, $"module '{import.Text}' not found") { Source = importer.Source });
                    continue;
                }
                var path = ModulePath(mainPath, import.Text);
                if (Read(path) is not { } text)
                {
                    syntax.Add(new Diagnostic("TP0221", import.Span, $"module '{import.Text}' not found", $"looked for {path}")
                    {
                        Source = importer.Source,
                    });
                    continue;
                }
                if (Parse(import.Text, path, text, lexical, syntax) is { } module)
                    modules.Add(module);
            }
        }

        var diagnostics = lexical.Count > 0 ? lexical : syntax;
        return new LoadResult([.. modules], [.. diagnostics]);
    }

    /// <summary>The module names a file imports, at their import statements.</summary>
    public static IEnumerable<Identifier> Imports(CompilationUnit unit) => unit.Items.Select(item => item switch
    {
        ImportStmt import => import.Module,
        FromImportStmt from => from.Module,
        _ => null,
    }).OfType<Identifier>();

    // Modules live beside the main file, written with the same separator so diagnostics are stable.
    private static string ModulePath(string mainPath, string module)
    {
        var directory = Path.GetDirectoryName(mainPath);
        var separator = mainPath.Contains('\\') && !mainPath.Contains('/') ? '\\' : '/';
        return string.IsNullOrEmpty(directory) ? $"{module}.tpn" : $"{directory}{separator}{module}.tpn";
    }

    private static SourceModule? Parse(string name, string path, string text, List<Diagnostic> lexical, List<Diagnostic> syntax)
    {
        var source = SourceText.From(text, path);
        var lexed = Lexer.Lex(source);
        lexical.AddRange(lexed.Diagnostics.Select(d => d with { Source = source }));
        if (lexed.Diagnostics.Length > 0)
            return null;

        var parsed = Parser.Parse(source, lexed.Tokens);
        syntax.AddRange(parsed.Diagnostics.Select(d => d with { Source = source }));
        return new SourceModule(name, source, parsed.Unit);
    }
}
