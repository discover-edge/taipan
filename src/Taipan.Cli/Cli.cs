using System.Collections.Immutable;
using static System.Reflection.CustomAttributeExtensions;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Emit;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;
using InformationalVersion = System.Reflection.AssemblyInformationalVersionAttribute;

namespace Taipan.Cli;

/// <summary>
/// The <c>taipan</c> command line. Takes its output writers as parameters so the
/// golden-test runner can drive it in-process.
/// </summary>
public static class Cli
{
    public const int ExitSuccess = 0;
    public const int ExitCompileError = 1;
    public const int ExitUsage = 2;

    private const string Usage = """
        usage:
          taipan check <file> [--library | --native]
          taipan emit  <file> [--library | --native]
          taipan build <file> [--library | --native] -o <dir>
          taipan run   <file> [-- args]
          taipan --version
          taipan --help
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
            return UsageError(stderr, null);

        var command = args[0];
        var rest = args[1..];
        var target = command is "check" or "emit" or "build" ? TargetOf(ref rest) : Target.Program;
        return command switch
        {
            "check" or "emit" or "build" when target is null => UsageError(stderr, "--library and --native can't be combined"),
            "check" => Check(rest, target!.Value, stderr),
            "emit" => Emit(rest, target!.Value, stdout, stderr),
            "build" => Build(rest, target!.Value, stdout, stderr),
            "run" => RunFile(rest, stdout, stderr),
            "--version" or "--help" when rest.Length > 0 => UsageError(stderr, $"{command} takes no arguments"),
            "--version" => Print(stdout, $"taipan {Version}"),
            "--help" => Print(stdout, Usage),
            _ when command.StartsWith('-') => UsageError(stderr, $"unknown option '{command}'"),
            _ => UsageError(stderr, $"unknown command '{command}'"),
        };
    }

    /// <summary>
    /// What a command compiles for: <c>--library</c> (SPEC §14) or <c>--native</c> (SPEC §15), which
    /// may come anywhere among its arguments and are removed from them; null if both are given.
    /// </summary>
    private static Target? TargetOf(ref string[] args)
    {
        var library = args.Contains("--library");
        var native = args.Contains("--native");
        args = [.. args.Where(a => a is not ("--library" or "--native"))];
        return (library, native) switch
        {
            (true, true) => null,
            (true, false) => Target.Library,
            (false, true) => Target.Native,
            _ => Target.Program,
        };
    }

    private static int Check(string[] args, Target target, TextWriter stderr)
    {
        if (args.Length != 1)
            return UsageError(stderr, "check takes exactly one file");
        return Compile(args[0], target, stderr, out _, out _);
    }

    private static int Emit(string[] args, Target target, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 1)
            return UsageError(stderr, "emit takes exactly one file");
        var exit = Compile(args[0], target, stderr, out var program, out _);
        if (program is not null)
            stdout.Write(program.Code);
        return exit;
    }

    private static int Build(string[] args, Target target, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 3 || args[1] != "-o")
            return UsageError(stderr, "build takes a file and -o <dir>");
        var exit = Compile(args[0], target, stderr, out var program, out var project);
        if (program is null)
            return exit;

        var name = Path.GetFileNameWithoutExtension(args[0]);
        string path;
        if (target == Target.Native)
        {
            if (NativeBuild.Publish(program, name, Path.GetDirectoryName(args[0]) ?? "", args[2], project!.RuntimeAssemblies, stderr, out var error) is not { } executable)
            {
                stderr.WriteLine($"taipan: {error}");
                return ExitUsage;
            }
            path = executable;
        }
        else
        {
            path = RoslynCompiler.Save(RoslynCompiler.Compile(program, name), name, args[2], project!.RuntimeAssemblies, target == Target.Library);
        }
        stdout.WriteLine($"built {path}");
        return ExitSuccess;
    }

    // Arguments after `--` are accepted; v0.1 programs have no way to read them.
    private static int RunFile(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
            return UsageError(stderr, "run takes a file");
        if (args.Length > 1 && args[1] != "--")
            return UsageError(stderr, "program arguments must follow '--'");
        var exit = Compile(args[0], Target.Program, stderr, out var program, out var project);
        if (program is null)
            return exit;

        var image = RoslynCompiler.Compile(program, Path.GetFileNameWithoutExtension(args[0]));
        return RoslynCompiler.Run(image, project!.RuntimeAssemblies, stdout, stderr);
    }

    /// <summary>
    /// Runs load (lex and parse every file) → bind → emit. Each stage runs only if the previous
    /// one reported no errors (SPEC §9). Features from later milestones stop compilation as "not implemented".
    /// <paramref name="program"/> and <paramref name="project"/> are set only on success. A wrong
    /// <c>taipan.json</c> isn't an error in the source (SPEC §13.2).
    /// </summary>
    private static int Compile(string path, Target target, TextWriter stderr, out EmittedProgram? program, out ProjectFile? project)
    {
        program = null;
        project = null;
        if (!File.Exists(path))
        {
            stderr.WriteLine($"taipan: file not found: {path}");
            return ExitUsage;
        }
        if (ProjectFile.Load(path, out var error) is not { } loadedProject)
        {
            stderr.WriteLine($"taipan: {error}");
            return ExitUsage;
        }

        var loaded = ModuleLoader.Load(path);
        if (Report(loaded.Diagnostics, stderr))
            return ExitCompileError;

        // A library compiles against the SDK's reference assemblies, so other projects can reference it (SPEC §14.4).
        string? framework = null;
        if (target == Target.Library && (framework = DotNetLibrary.ReferencePackDirectory()) is null)
        {
            stderr.WriteLine("taipan: building a library needs the .NET SDK's reference assemblies (Microsoft.NETCore.App.Ref)");
            return ExitUsage;
        }
        var bound = Binder.Bind(
            loaded.Modules, DotNetLibrary.For(loadedProject.CompileReferences, framework), target == Target.Library, target == Target.Native);
        if (bound.Unsupported is not null)
            return NotImplemented(bound.Unsupported, stderr);
        if (Report(bound.Diagnostics, stderr))
            return ExitCompileError;

        var source = loaded.Modules[0].Source;
        var emitted = CSharpEmitter.Emit(source, bound.Program, native: target == Target.Native);
        if (Report(emitted.Diagnostics, stderr))
            return ExitCompileError;
        program = emitted;
        project = loadedProject;
        return ExitSuccess;
    }

    /// <summary>Prints diagnostics, each in its own file; true if any is an error. Warnings don't fail compilation (SPEC §9).</summary>
    private static bool Report(ImmutableArray<Diagnostic> diagnostics, TextWriter stderr)
    {
        foreach (var diagnostic in diagnostics)
            stderr.WriteLine(diagnostic.Format(diagnostic.Source!));
        return diagnostics.Any(d => !d.IsWarning);
    }

    private static int NotImplemented(string feature, TextWriter stderr)
    {
        stderr.WriteLine($"taipan: {feature} is not implemented yet");
        return ExitUsage;
    }

    /// <summary>The package version, which the build sets once for every package (SPEC §12.2).</summary>
    private static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<InformationalVersion>()?.InformationalVersion ?? "unknown";

    private static int Print(TextWriter stdout, string text)
    {
        stdout.WriteLine(text);
        return ExitSuccess;
    }

    private static int UsageError(TextWriter stderr, string? message)
    {
        if (message is not null)
            stderr.WriteLine($"taipan: {message}");
        stderr.WriteLine(Usage);
        return ExitUsage;
    }
}

/// <summary>What <c>check</c>, <c>emit</c> and <c>build</c> compile a program as.</summary>
public enum Target
{
    Program,
    Library,
    Native,
}
