namespace Taipan.Runtime;

/// <summary>The built-in module <c>sys</c> (SPEC §6.1, M37).</summary>
public static class Sys
{
    private static List<string>? _argv;
    private static TextFile? _stdin, _stdout, _stderr;

    /// <summary>
    /// The program's path as <c>taipan run</c> was given it, which <c>sys.argv[0]</c> shows instead
    /// of the host's own path. Null for a built program.
    /// </summary>
    public static string? ProgramPath { get; set; }

    /// <summary>
    /// The program's path and arguments. A library or extension module, which has no <c>Main</c>,
    /// sees the host process's arguments.
    /// </summary>
    public static List<string> @argv => _argv ??= [.. Environment.GetCommandLineArgs()];

    public static TextFile @stdin => _stdin ??= TextFile.Stdin();

    public static TextFile @stdout => _stdout ??= TextFile.Stdout(error: false);

    public static TextFile @stderr => _stderr ??= TextFile.Stdout(error: true);

    public static Exception @exit(long @code) => new SystemExit(code, null);

    public static Exception @exit(string @message) => new SystemExit(1, message);

    public static Exception @exit() => new SystemExit(0, null);

    /// <summary>Sets up the module for a program starting with <paramref name="arguments"/>, from <c>Main</c>.</summary>
    internal static void Start(string[] arguments)
    {
        _argv = [ProgramPath ?? Environment.GetCommandLineArgs()[0], .. arguments];
        (_stdin, _stdout, _stderr) = (null, null, null);
        Os.Start();
    }
}

/// <summary>The built-in module <c>os</c> (SPEC §6.1, M37).</summary>
public static class Os
{
    private static OrderedDictionary<string, string>? _environ;

    /// <summary>The environment as the program started; a plain dict, so changes stay inside the program.</summary>
    public static OrderedDictionary<string, string> @environ => _environ ??= Read();

    public static string? @getenv(string @name) => environ.TryGetValue(name, out var value) ? value : null;

    public static string @getenv(string @name, string @default) => environ.TryGetValue(name, out var value) ? value : @default;

    internal static void Start() => _environ = null;

    private static OrderedDictionary<string, string> Read()
    {
        var environ = new OrderedDictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environ[(string)entry.Key] = (string?)entry.Value ?? "";
        return environ;
    }
}

/// <summary>A class as a value, <c>type[C]</c>: the first argument of <c>__exit__</c> (SPEC §3.17, M37).</summary>
public sealed class ClassValue
{
    private readonly string? _module;

    private ClassValue(string name, string? module) => (__name__, _module) = (name, module);

    public string @__name__ { get; }

    /// <summary>The class of <paramref name="exception"/>; a built-in class has no module in its display.</summary>
    public static ClassValue Of(TaipanBaseException exception) =>
        new(((IObject)exception).TypeName, exception.GetType().Assembly == typeof(ClassValue).Assembly ? null : ((IObject)exception).ModuleName);

    public override string ToString() => _module is null ? $"<class '{__name__}'>" : $"<class '{_module}.{__name__}'>";
}

/// <summary><c>types.TracebackType</c> (SPEC §3.17, M37): opaque until tracebacks are printed (M41).</summary>
public sealed class Traceback
{
    public static Traceback Instance { get; } = new();

    private Traceback() { }

    public override string ToString() => "<traceback object>";
}
