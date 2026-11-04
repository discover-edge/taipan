namespace Taipan.Runtime;

/// <summary>A test function (SPEC §20): its name, and a call of it that gives its task if it's async.</summary>
public sealed record Test(string Name, Func<Task?> Run);

/// <summary>How a test file's tests went (SPEC §20.3).</summary>
public sealed record TestResults(int Passed, int Failed, int Errors);

/// <summary>
/// <c>taipan test</c>'s runner (SPEC §20.2): a test file's top level, then each test, with a line
/// on stdout for each, as a program's output.
/// </summary>
public static class Testing
{
    /// <summary>Whether a run ends with the summary line; <c>taipan test</c> writes one for every file instead.</summary>
    public static bool Summarize { get; set; } = true;

    /// <summary>The last run's results, which <c>taipan test</c> adds up across files.</summary>
    public static TestResults? Last { get; private set; }

    /// <summary>Runs a test file's top level, then its tests, giving the exit code (SPEC §20.3).</summary>
    public static int Run(string file, Action topLevel, Test[] tests, string[] arguments)
    {
        Sys.Start(arguments);
        EventLoop.Start();
        var (passed, failed, errors) = (0, 0, 0);
        try
        {
            if (Attempt(topLevel) is { } setUp)
            {
                Io.Out.WriteLine($"ERROR {file}");
                Io.Out.Write(setUp);
                errors++;
            }
            else
            {
                foreach (var test in tests)
                {
                    var failure = Attempt(() =>
                    {
                        // An async test runs on the event loop, as asyncio.run runs it (SPEC §20.1).
                        if (test.Run() is { } task)
                            Asyncio.run(task);
                    });
                    Io.Out.WriteLine($"{(failure is null ? "PASS" : "FAIL")} {file}::{test.Name}");
                    if (failure is null)
                    {
                        passed++;
                    }
                    else
                    {
                        Io.Out.Write(failure);
                        failed++;
                    }
                }
            }
            Last = new TestResults(passed, failed, errors);
            if (Summarize)
                Io.Out.WriteLine(Summary(Last));
            Io.Out.Flush();
            return failed + errors > 0 || passed == 0 ? 1 : 0;
        }
        finally
        {
            TextFile.CloseAll();
        }
    }

    /// <summary>The summary line (SPEC §20.3): <c>2 passed, 1 failed</c>, or <c>no tests found</c>.</summary>
    public static string Summary(TestResults results) =>
        results is { Passed: 0, Failed: 0, Errors: 0 } ? "no tests found"
            : $"{results.Passed} passed, {results.Failed} failed{(results.Errors switch { 0 => "", 1 => ", 1 error", var n => $", {n} errors" })}";

    // Null when the code returns; otherwise what Panic.Run would print for it, a traceback or a panic.
    private static string? Attempt(Action code)
    {
        try
        {
            code();
            Io.Out.Flush();
            return null;
        }
        catch (TaipanPanic panic)
        {
            Io.Out.Flush();
            return $"panic: {panic.Message}\n" + (panic.Location is { } location ? $"  at {location}\n" : "");
        }
        catch (Exception e)
        {
            Io.Out.Flush();
            return Tracebacks.Format(Exceptions.From(e));
        }
    }
}
