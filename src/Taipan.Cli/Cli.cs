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
          taipan check <file> [--library | --native | --python-extension]
          taipan emit  <file> [--library | --native | --python-extension]
          taipan build <file> [--library | --native | --python-extension] -o <dir>
          taipan run   <file> [-- args]
          taipan pack  <file> -o <dir>
          taipan compile <file> [--library] --out <dll> [--reference <dll>]... [--assembly <name>] [--namespace <ns>] [--version <v>]
          taipan lsp [--stdio]
          taipan --version
          taipan --help
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
            return UsageError(stderr, null);
        // `@file` stands for the arguments in a file, one per line, as MSBuild writes them (SPEC §18.2).
        args = [.. args.SelectMany(a => a.StartsWith('@') && File.Exists(a[1..]) ? File.ReadAllLines(a[1..]).Where(l => l.Length > 0) : [a])];

        var command = args[0];
        var rest = args[1..];
        var target = command is "check" or "emit" or "build" ? TargetOf(ref rest) : Target.Program;
        return command switch
        {
            "check" or "emit" or "build" when target is null => UsageError(stderr, "--library, --native and --python-extension can't be combined"),
            "check" => Check(rest, target!.Value, stderr),
            "emit" => Emit(rest, target!.Value, stdout, stderr),
            "build" => Build(rest, target!.Value, stdout, stderr),
            "run" => RunFile(rest, stdout, stderr),
            "pack" => Pack(rest, stdout, stderr),
            "compile" => CompileAssembly(rest, stderr),
            // Clients such as VS Code's pass --stdio, the only transport (SPEC §16.1).
            "lsp" when rest.Any(a => a != "--stdio") => UsageError(stderr, "lsp takes no arguments but '--stdio'"),
            "lsp" => new LanguageServer(Console.OpenStandardInput(), Console.OpenStandardOutput()).Run(),
            "--version" or "--help" when rest.Length > 0 => UsageError(stderr, $"{command} takes no arguments"),
            "--version" => Print(stdout, $"taipan {Version}"),
            "--help" => Print(stdout, Usage),
            _ when command.StartsWith('-') => UsageError(stderr, $"unknown option '{command}'"),
            _ => UsageError(stderr, $"unknown command '{command}'"),
        };
    }

    private static readonly Dictionary<string, Target> Kinds = new()
    {
        ["--library"] = Target.Library,
        ["--native"] = Target.Native,
        ["--python-extension"] = Target.PythonExtension,
    };

    /// <summary>
    /// What a command compiles for: <c>--library</c> (SPEC §14), <c>--native</c> (SPEC §15) or
    /// <c>--python-extension</c> (SPEC §17), which may come anywhere among its arguments and are
    /// removed from them; null if more than one is given.
    /// </summary>
    private static Target? TargetOf(ref string[] args)
    {
        var kinds = args.Where(Kinds.ContainsKey).Distinct().ToList();
        args = [.. args.Where(a => !Kinds.ContainsKey(a))];
        return kinds.Count switch
        {
            0 => Target.Program,
            1 => Kinds[kinds[0]],
            _ => null,
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
        if (target is Target.Native or Target.PythonExtension)
        {
            if (NativeBuild.Publish(program, name, Path.GetDirectoryName(args[0]) ?? "", args[2], project!.RuntimeAssemblies, target == Target.PythonExtension, stderr, out var error) is not { } executable)
            {
                stderr.WriteLine($"taipan: {error}");
                return ExitUsage;
            }
            path = executable;
        }
        else
        {
            // A library may be named in taipan.json (SPEC §18.3).
            var library = target == Target.Library;
            if (library)
                name = project!.Names.Assembly ?? name;
            var image = RoslynCompiler.Compile(program, name, version: library ? project!.Names.Version : null);
            path = RoslynCompiler.Save(image, name, args[2], project!.RuntimeAssemblies, library);
        }
        stdout.WriteLine($"built {path}");
        return ExitSuccess;
    }

    /// <summary><c>taipan pack &lt;file&gt; -o &lt;dir&gt;</c>: a library as a NuGet package (SPEC §18.4).</summary>
    private static int Pack(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 3 || args[1] != "-o")
            return UsageError(stderr, "pack takes a file and -o <dir>");
        var exit = Compile(args[0], Target.Library, stderr, out var program, out var project);
        if (program is null)
            return exit;
        var names = project!.Names;
        var assembly = names.Assembly ?? Path.GetFileNameWithoutExtension(args[0]);
        var image = RoslynCompiler.Compile(program, assembly, version: names.Version ?? "1.0.0");
        stdout.WriteLine($"packed {Packing.Pack(image, assembly, names, project, Version, args[2])}");
        return ExitSuccess;
    }

    /// <summary>
    /// <c>taipan compile</c> (SPEC §18.2): one assembly for TaipanLang.Sdk, against exactly the
    /// references MSBuild resolved, with diagnostics in MSBuild's format.
    /// </summary>
    private static int CompileAssembly(string[] args, TextWriter stderr)
    {
        string? file = null, output = null, assembly = null, ns = null, version = null;
        var library = false;
        var references = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            // `--reference=<dll>` is one argument, as a response file's line (SPEC §18.2).
            if (args[i].StartsWith("--reference=", StringComparison.Ordinal))
            {
                references.Add(Path.GetFullPath(args[i]["--reference=".Length..]));
                continue;
            }
            switch (args[i])
            {
                case "--library":
                    library = true;
                    continue;
                case "--out" or "--reference" or "--assembly" or "--namespace" or "--version" when value is null:
                    return UsageError(stderr, $"{args[i]} needs a value");
                case "--out":
                    output = value;
                    break;
                case "--reference":
                    references.Add(Path.GetFullPath(value!));
                    break;
                case "--assembly":
                    assembly = value;
                    break;
                case "--namespace":
                    ns = value;
                    break;
                case "--version":
                    version = value;
                    break;
                case var option when option.StartsWith('-'):
                    return UsageError(stderr, $"unknown option '{option}'");
                case var path when file is null:
                    file = path;
                    continue;
                default:
                    return UsageError(stderr, "compile takes exactly one file");
            }
            i++;
        }
        if (file is null || output is null)
            return UsageError(stderr, "compile takes a file and --out <dll>");

        // An SDK project's references and packages are its project file's (SPEC §18.1).
        if (ProjectFile.ListsDependencies(file))
        {
            stderr.WriteLine($"{Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? "", ProjectFile.FileName))}(1,1): error TPN002: list references and packages in the project file, not taipan.json");
            return ExitCompileError;
        }
        var result = Checker.Check(file, library ? Target.Library : Target.Program, options: new CompileOptions(references.ToImmutable(), ns));
        foreach (var diagnostic in result.Diagnostics)
            stderr.WriteLine(MSBuildFormat(diagnostic));
        if (result.Failure is not null)
            stderr.WriteLine($"taipan : error : {result.Failure}");
        if (result.Program is not { } program)
            return result.ExitCode;
        var name = assembly ?? Path.GetFileNameWithoutExtension(file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, RoslynCompiler.Compile(program, name, version: version));
        return ExitSuccess;
    }

    /// <summary>A diagnostic as MSBuild reads one (SPEC §18.1): <c>path(line,col): error TP0200: message (help: …)</c>.</summary>
    private static string MSBuildFormat(Diagnostic diagnostic)
    {
        var source = diagnostic.Source!;
        var position = source.GetLinePosition(diagnostic.Span.Start);
        var help = diagnostic.Help is null ? "" : $" (help: {diagnostic.Help})";
        return $"{Path.GetFullPath(source.Path)}({position.Line},{position.Column}): {(diagnostic.IsWarning ? "warning" : "error")} {diagnostic.Code}: {diagnostic.Message}{help}";
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
    /// Checks a program (<see cref="Checker"/>) and prints what it found. <paramref name="program"/>
    /// and <paramref name="project"/> are set only on success.
    /// </summary>
    private static int Compile(string path, Target target, TextWriter stderr, out EmittedProgram? program, out ProjectFile? project)
    {
        var result = Checker.Check(path, target);
        foreach (var diagnostic in result.Diagnostics)
            stderr.WriteLine(diagnostic.Format(diagnostic.Source!));
        if (result.Failure is not null)
            stderr.WriteLine($"taipan: {result.Failure}");
        (program, project) = (result.Program, result.Project);
        return result.ExitCode;
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
    PythonExtension,
}
