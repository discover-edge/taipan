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
        return CompileStub("check", args[0], stderr);
    }

    private static int Emit(string[] args, TextWriter stderr)
    {
        if (args.Length != 1)
            return UsageError(stderr, "emit takes exactly one file");
        return NotImplemented("emit", stderr);
    }

    private static int Build(string[] args, TextWriter stderr)
    {
        if (args.Length != 3 || args[1] != "-o")
            return UsageError(stderr, "build takes a file and -o <dir>");
        return NotImplemented("build", stderr);
    }

    private static int RunFile(string[] args, TextWriter stderr)
    {
        if (args.Length == 0)
            return UsageError(stderr, "run takes a file");
        if (args.Length > 1 && args[1] != "--")
            return UsageError(stderr, "program arguments must follow '--'");
        return CompileStub("run", args[0], stderr);
    }

    // M0: there is no compiler pipeline yet. An empty file is a valid program with
    // nothing to do; anything else needs a later milestone.
    private static int CompileStub(string command, string path, TextWriter stderr)
    {
        if (!File.Exists(path))
        {
            stderr.WriteLine($"taipan: file not found: {path}");
            return ExitUsage;
        }

        if (File.ReadAllText(path).Length == 0)
            return ExitSuccess;

        return NotImplemented(command, stderr);
    }

    private static int NotImplemented(string command, TextWriter stderr)
    {
        stderr.WriteLine($"taipan: '{command}' is not implemented yet");
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
