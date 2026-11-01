using System.Diagnostics;

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
    /// <see cref="Io.Error"/>, and returns 101. Any other exception is a compiler bug and propagates.
    /// </summary>
    public static int Run(Action topLevel)
    {
        try
        {
            topLevel();
            Io.Out.Flush();
            return 0;
        }
        catch (Exception e) when (MessageFor(e) is { } message)
        {
            Io.Out.Flush();
            Io.Error.WriteLine($"panic: {message}");
            if (FindLocation(e) is { } location)
                Io.Error.WriteLine($"  at {location}");
            return ExitCode;
        }
    }

    private static string? MessageFor(Exception e) => e switch
    {
        TaipanPanic panic => panic.Message,
        OverflowException => "integer overflow",
        PythonError python => $"Python {python.type}: {python.message}",
        TaipanException exception => MessageFor(exception),
        _ => null,
    };

    /// <summary>
    /// SPEC §3.10: the runtime's own exceptions are written to stand alone, so they print just the
    /// message; a compiled class prints its name too, as Python does.
    /// </summary>
    private static string MessageFor(TaipanException exception)
    {
        var name = ((IObject)exception).TypeName;
        if (exception.message.Length == 0)
            return name;
        return exception.GetType().Assembly == typeof(Panic).Assembly ? exception.message : $"{name}: {exception.message}";
    }

    // The first frame with source info that isn't in this runtime is generated code, whose
    // #line directives point at the .tpn source.
    private static string? FindLocation(Exception e)
    {
        foreach (var frame in new StackTrace(e, fNeedFileInfo: true).GetFrames())
        {
            if (frame.GetMethod()?.DeclaringType?.Assembly == typeof(Panic).Assembly)
                continue;
            var file = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            if (file is not null && line > 0 && line != HiddenLine)
                return $"{file}:{line}";
        }
        return null;
    }
}
