namespace Taipan.Runtime;

/// <summary>A failure at startup, before any <c>try</c> runs, so never caught: loading Python (SPEC §3.10, §10.5).</summary>
public sealed class TaipanPanic(string message) : Exception(message)
{
    /// <summary>The <c>.tpn</c> location of the statement running, from the first frame it passed (SPEC §7).</summary>
    internal string? Location { get; set; }
}

public static class Panic
{
    public const int ExitCode = 101;

    public static TaipanPanic Create(string message) => new(message);

    /// <summary>
    /// Runs a program's top level with its arguments (<c>sys.argv</c>, SPEC §6.1). An exception
    /// nothing caught prints its traceback (SPEC §3.19) to <see cref="Io.Error"/>, and a failure
    /// that isn't an exception <c>panic: message</c> and its location; either returns 101. An
    /// uncaught <c>SystemExit</c> returns its code (SPEC §3.10). Files left open are closed.
    /// </summary>
    public static int Run(Action topLevel, string[] arguments)
    {
        Sys.Start(arguments);
        try
        {
            topLevel();
            Io.Out.Flush();
            return 0;
        }
        catch (SystemExit exit)
        {
            Io.Out.Flush();
            if (exit.PrintsMessage)
                Io.Error.WriteLine(exit.message);
            return unchecked((int)exit.code);
        }
        catch (TaipanPanic panic)
        {
            Io.Out.Flush();
            Io.Error.WriteLine($"panic: {panic.Message}");
            if (panic.Location is { } location)
                Io.Error.WriteLine($"  at {location}");
            return ExitCode;
        }
        catch (Exception e)
        {
            Io.Out.Flush();
            Io.Error.Write(Tracebacks.Format(Exceptions.From(e)));
            return ExitCode;
        }
        finally
        {
            TextFile.CloseAll();
        }
    }

    /// <summary>Sets a method's current line inside a condition, such as an <c>elif</c>'s. Always true.</summary>
    public static bool At(ref int current, int line)
    {
        current = line;
        return true;
    }
}
