using System.Diagnostics;

namespace Taipan.Runtime;

/// <summary>A Taipan runtime error (SPEC §7).</summary>
public sealed class TaipanPanic(string message) : Exception(message);

public static class Panic
{
    public const int ExitCode = 101;

    // Line number the C# compiler gives code under `#line hidden`.
    private const int HiddenLine = 0xFEEFEE;

    public static TaipanPanic Create(string message) => new(message);

    /// <summary>
    /// Runs a program's top level. A panic prints <c>panic: message</c> and the <c>.tpn</c>
    /// location of the innermost Taipan frame to <see cref="Io.Error"/>, and returns 101.
    /// Any other exception is a compiler bug and propagates.
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
        _ => null,
    };

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
