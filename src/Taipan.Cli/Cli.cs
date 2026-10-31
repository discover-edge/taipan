using System.Collections.Immutable;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

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
          taipan check <file>
          taipan emit  <file>
          taipan build <file> -o <dir>
          taipan run   <file> [-- args]
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
            return UsageError(stderr, null);

        var command = args[0];
        var rest = args[1..];
        return command switch
        {
            "check" => Check(rest, stderr),
            "emit" => Emit(rest, stderr),
            "build" => Build(rest, stderr),
            "run" => RunFile(rest, stderr),
            _ => UsageError(stderr, $"unknown command '{command}'"),
        };
    }

    private static int Check(string[] args, TextWriter stderr)
    {
        if (args.Length != 1)
            return UsageError(stderr, "check takes exactly one file");
        return Compile("check", args[0], stderr);
    }

    private static int Emit(string[] args, TextWriter stderr)
    {
        if (args.Length != 1)
            return UsageError(stderr, "emit takes exactly one file");
        return NotImplemented("'emit'", stderr);
    }

    private static int Build(string[] args, TextWriter stderr)
    {
        if (args.Length != 3 || args[1] != "-o")
            return UsageError(stderr, "build takes a file and -o <dir>");
        return NotImplemented("'build'", stderr);
    }

    private static int RunFile(string[] args, TextWriter stderr)
    {
        if (args.Length == 0)
            return UsageError(stderr, "run takes a file");
        if (args.Length > 1 && args[1] != "--")
            return UsageError(stderr, "program arguments must follow '--'");
        return Compile("run", args[0], stderr);
    }

    // The pipeline currently ends at the type checker: `check` reports all compile errors, and
    // `run` can only execute an empty program. Each stage runs only if the previous one reported
    // no errors (SPEC §9). Features from later milestones stop compilation as "not implemented".
    private static int Compile(string command, string path, TextWriter stderr)
    {
        if (!File.Exists(path))
        {
            stderr.WriteLine($"taipan: file not found: {path}");
            return ExitUsage;
        }

        var source = SourceText.From(File.ReadAllText(path), path);
        var lexed = Lexer.Lex(source);
        if (Report(lexed.Diagnostics, source, stderr))
            return ExitCompileError;

        var parsed = Parser.Parse(source, lexed.Tokens);
        if (Report(parsed.Diagnostics, source, stderr))
            return ExitCompileError;

        var bound = Binder.Bind(source, parsed.Unit);
        if (bound.Unsupported is not null)
            return NotImplemented(bound.Unsupported, stderr);
        if (Report(bound.Diagnostics, source, stderr))
            return ExitCompileError;

        if (command == "check" || bound.Program.IsEmpty)
            return ExitSuccess;

        return NotImplemented($"'{command}'", stderr);
    }

    private static bool Report(ImmutableArray<Diagnostic> diagnostics, SourceText source, TextWriter stderr)
    {
        foreach (var diagnostic in diagnostics)
            stderr.WriteLine(diagnostic.Format(source));
        return diagnostics.Length > 0;
    }

    private static int NotImplemented(string feature, TextWriter stderr)
    {
        stderr.WriteLine($"taipan: {feature} is not implemented yet");
        return ExitUsage;
    }

    private static int UsageError(TextWriter stderr, string? message)
    {
        if (message is not null)
            stderr.WriteLine($"taipan: {message}");
        stderr.WriteLine(Usage);
        return ExitUsage;
    }
}
