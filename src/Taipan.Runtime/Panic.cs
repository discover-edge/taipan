using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Taipan.Runtime;

/// <summary>A failure at startup, before any <c>try</c> runs, so never caught: loading Python (SPEC §3.10, §10.5).</summary>
public sealed class TaipanPanic(string message) : Exception(message);

public static class Panic
{
    public const int ExitCode = 101;

    // Line number the C# compiler gives code under `#line hidden`.
    private const int HiddenLine = 0xFEEFEE;

    public static TaipanPanic Create(string message) => new(message);

    /// <summary>
    /// Runs a program's top level. A panic, an exception nothing caught, prints <c>panic: message</c>
    /// and the <c>.tpn</c> location of the innermost Taipan frame where it was raised to
    /// <see cref="Io.Error"/>, and returns 101.
    /// </summary>
    public static int Run(Action topLevel)
    {
        try
        {
            topLevel();
            Io.Out.Flush();
            return 0;
        }
        catch (Exception e)
        {
            var message = MessageFor(e);
            Io.Out.Flush();
            Io.Error.WriteLine($"panic: {message}");
            if ((Recorded.TryGetValue(e, out var recorded) ? recorded.Text : FindLocation(e)) is { } location)
                Io.Error.WriteLine($"  at {location}");
            return ExitCode;
        }
    }

    private static string MessageFor(Exception e) => e switch
    {
        TaipanPanic panic => panic.Message,
        OverflowException => "integer overflow",
        PythonError python => $"Python {python.type}: {python.message}",
        DotNetError dotnet => DotNetMessage(dotnet.Exception),
        TaipanException exception => MessageFor(exception),
        _ => DotNetMessage(e),
    };

    // SPEC §13.6: an uncaught .NET exception, by its full class name.
    private static string DotNetMessage(Exception e) => $".NET {e.GetType().FullName}: {e.Message}";

    /// <summary>
    /// SPEC §3.10: the runtime's own exceptions are written to stand alone, so they print just the
    /// message; a compiled class prints its name too, as Python does.
    /// </summary>
    private static string MessageFor(TaipanException exception)
    {
        var name = ((IObject)exception).TypeName;
        // As Python prints str(e), a class's __str__ gives the text (SPEC §4.8).
        var text = ((IClass)exception).__str__();
        if (text.Length == 0)
            return name;
        return exception.GetType().Assembly == typeof(Panic).Assembly ? text : $"{name}: {text}";
    }

    private sealed record Location(string Text);

    // Where each exception was raised, as a native program records it (SPEC §15.3).
    private static readonly ConditionalWeakTable<Exception, Location> Recorded = [];

    /// <summary>
    /// A native program's exception filter around each method body (SPEC §15.3): records the
    /// location the first time an exception passes, which is in the innermost Taipan frame, since
    /// filters run innermost first. Returns false, so it never catches.
    /// </summary>
    public static bool Locate(Exception exception, string file, int line)
    {
        Recorded.TryAdd(exception, new Location($"{file}:{line}"));
        return false;
    }

    /// <summary>Sets a native method's current line inside a condition, such as an <c>elif</c>'s. Always true.</summary>
    public static bool At(ref int current, int line)
    {
        current = line;
        return true;
    }

    /// <summary><c>raise e</c> in a native program: <c>e</c> is raised anew, so it's located anew (SPEC §3.10).</summary>
    internal static void Forget(Exception exception) => Recorded.Remove(exception);

    // The first frame with source info that isn't in this runtime is generated code, whose
    // #line directives point at the .tpn source.
    private static string? FindLocation(Exception e)
    {
        foreach (var frame in new StackTrace(e, fNeedFileInfo: true).GetFrames())
        {
            // By name, without reflection, so the runtime stays trim-safe for native builds (SPEC §15.1).
            if (DiagnosticMethodInfo.Create(frame)?.DeclaringAssemblyName is { } assembly && IsRuntime(assembly))
                continue;
            var file = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            if (file is not null && line > 0 && line != HiddenLine)
                return $"{file}:{line}";
        }
        return null;
    }

    private static bool IsRuntime(string assemblyName) =>
        new System.Reflection.AssemblyName(assemblyName).Name == typeof(Panic).Assembly.GetName().Name;
}
