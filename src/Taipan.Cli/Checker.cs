using System.Collections.Immutable;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Emit;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;

namespace Taipan.Cli;

/// <summary>
/// What checking a program found: its diagnostics in the order <c>taipan check</c> prints them, and
/// a <paramref name="Failure"/> that isn't an error in the source (exit code 2, SPEC §9). The
/// program and its project are set only on success; the names it resolved, whenever it parsed.
/// </summary>
public sealed record CheckResult(
    ImmutableArray<Diagnostic> Diagnostics, string? Failure, EmittedProgram? Program, ProjectFile? Project, SemanticIndex? Index)
{
    public int ExitCode => Failure is not null ? Cli.ExitUsage : Diagnostics.Any(d => !d.IsWarning) ? Cli.ExitCompileError : Cli.ExitSuccess;
}

/// <summary>
/// How <c>taipan compile</c> builds for an SDK project (SPEC §18.2): against exactly
/// <paramref name="References"/>, without reading <c>taipan.json</c>, with a library's namespace.
/// </summary>
public sealed record CompileOptions(ImmutableArray<string> References, string? Namespace);

/// <summary>The compiler's stages for one program, shared by the commands and the language server (SPEC §16.2).</summary>
public static class Checker
{
    /// <summary>
    /// Runs load (lex and parse every file) → bind → emit. Each stage runs only if the previous
    /// one reported no errors (SPEC §9). Features from later milestones stop compilation as "not
    /// implemented". A wrong <c>taipan.json</c> isn't an error in the source (SPEC §13.2).
    /// <paramref name="openText"/> gives an editor's unsaved text for a path (SPEC §16.1).
    /// </summary>
    public static CheckResult Check(string path, Target target, Func<string, string?>? openText = null, CompileOptions? options = null)
    {
        if (openText?.Invoke(path) is null && !File.Exists(path))
            return Fail([], $"file not found: {path}");
        ProjectFile? project;
        string? error = null;
        if (options is not null)
            project = new ProjectFile(options.References, options.References);
        else if ((project = ProjectFile.Load(path, out error)) is null)
            return Fail([], error!);

        var loaded = ModuleLoader.Load(path, openText);
        if (loaded.Diagnostics.Any(d => !d.IsWarning))
            return new(loaded.Diagnostics, null, null, null, null);
        var diagnostics = loaded.Diagnostics;

        // A library compiles against the SDK's reference assemblies, so other projects can reference it (SPEC §14.4).
        string? framework = null;
        if (options is null && target == Target.Library && (framework = DotNetLibrary.ReferencePackDirectory()) is null)
            return Fail(diagnostics, "building a library needs the .NET SDK's reference assemblies (Microsoft.NETCore.App.Ref)");
        var library = options is null ? DotNetLibrary.For(project.CompileReferences, framework) : DotNetLibrary.Exactly(options.References);
        var bound = Binder.Bind(
            loaded.Modules, library,
            asLibrary: target == Target.Library, asNative: target == Target.Native, asExtension: target == Target.PythonExtension,
            libraryNamespace: options is null ? project.Names.Namespace : options.Namespace);
        if (bound.Unsupported is not null)
            return Fail(diagnostics, $"{bound.Unsupported} is not implemented yet") with { Index = bound.Index };
        diagnostics = [.. diagnostics, .. bound.Diagnostics];
        if (bound.Diagnostics.Any(d => !d.IsWarning))
            return new(diagnostics, null, null, null, bound.Index);

        var emitted = CSharpEmitter.Emit(loaded.Modules[0].Source, bound.Program);
        diagnostics = [.. diagnostics, .. emitted.Diagnostics];
        if (emitted.Diagnostics.Any(d => !d.IsWarning))
            return new(diagnostics, null, null, null, bound.Index);
        return new(diagnostics, null, emitted, project, bound.Index);
    }

    private static CheckResult Fail(ImmutableArray<Diagnostic> diagnostics, string failure) => new(diagnostics, failure, null, null, null);
}
