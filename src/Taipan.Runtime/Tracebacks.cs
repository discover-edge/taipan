using System.Text;

namespace Taipan.Runtime;

/// <summary>
/// Tracebacks and implicit chaining (SPEC §3.19, §8, §15.3). Each Python frame's exception filter,
/// and each <c>except</c> clause's, adds the frame to an exception's traceback and, the first time
/// one is reached since it was raised, sets its context from the exceptions being handled.
/// </summary>
public static class Tracebacks
{
    // The exceptions being handled, innermost first: by except blocks, by finally blocks an
    // exception is propagating through (or nothing, for one that isn't), and by __exit__.
    private sealed record Handled(TaipanBaseException? Exception, Handled? Next);

    private static readonly AsyncLocal<Handled?> s_handled = new();

    /// <summary>A frame's filter: adds the frame as an exception leaves it. Always false, so it never catches.</summary>
    public static bool Leave(Exception exception, string file, int line, string name)
    {
        Record(exception, file, line, name);
        return false;
    }

    /// <summary>An <c>except</c> or <c>with</c> clause's filter: adds the frame, and catches anything catchable.</summary>
    public static bool Catch(Exception exception, string file, int line, string name)
    {
        Record(exception, file, line, name);
        return Exceptions.IsCatchable(exception);
    }

    /// <summary>
    /// A <c>finally</c> block's filter on its <c>try</c>: the exception it runs for, so that one
    /// raised in it gets that as its context. Always false.
    /// </summary>
    public static bool InFlight(Exception exception, ref TaipanBaseException? inFlight)
    {
        if (Exceptions.IsCatchable(exception))
        {
            inFlight = Exceptions.From(exception);
            Chain(inFlight);
        }
        return false;
    }

    /// <summary>
    /// An exception leaving the frame that caught it, by a bare <c>raise</c> or an unmatched clause:
    /// that frame is already its first entry, so it isn't added again (SPEC §3.19).
    /// </summary>
    public static void Again(Exception exception)
    {
        if (!Exceptions.IsCatchable(exception))
            return;
        var taipan = Exceptions.From(exception);
        (taipan.FrameRecorded, taipan.Unchained) = (true, false);
    }

    /// <summary>Starts handling an exception: an except block, a finally block (null if none is in flight) or an <c>__exit__</c>.</summary>
    public static void Handle(TaipanBaseException? exception) => s_handled.Value = new(exception, s_handled.Value);

    /// <summary>Ends what <see cref="Handle"/> started.</summary>
    public static void Unhandle() => s_handled.Value = s_handled.Value?.Next;

    private static void Record(Exception exception, string file, int line, string name)
    {
        if (exception is TaipanPanic panic)
        {
            panic.Location ??= $"{file}:{line}";
            return;
        }
        var taipan = Exceptions.From(exception);
        Chain(taipan);
        if (taipan.FrameRecorded)
            taipan.FrameRecorded = false;
        else
            taipan.__traceback__ = new Traceback(file, line, name, taipan.__traceback__);
    }

    // Python's rule (SPEC §3.19): the innermost exception being handled becomes the context, unless
    // it's the exception itself, and a link back to the exception in its chain is cut.
    private static void Chain(TaipanBaseException exception)
    {
        if (!exception.Unchained)
            return;
        exception.Unchained = false;
        var handled = s_handled.Value;
        while (handled is { Exception: null })
            handled = handled.Next;
        if (handled?.Exception is not { } current || current == exception)
            return;
        var seen = new HashSet<TaipanBaseException>(ReferenceEqualityComparer.Instance);
        for (var o = current; o.__context__ is { } context && seen.Add(o); o = context)
        {
            if (context == exception)
            {
                o.__context__ = null;
                break;
            }
        }
        exception.__context__ = current;
    }

    /// <summary>PEP 479 (SPEC §3.13): a <c>StopIteration</c> leaving a generator, as the cause of a <c>RuntimeError</c>.</summary>
    internal static RuntimeError FromStopIteration(StopIteration stop) =>
        new("generator raised StopIteration") { __cause__ = stop, __context__ = stop, Unchained = false };

    // ---- Printing (SPEC §3.19) ----

    private const int RecursiveCutoff = 3;

    /// <summary>An uncaught exception's chain, causes and contexts first, as CPython prints it.</summary>
    public static string Format(TaipanBaseException exception)
    {
        var text = new StringBuilder();
        Format(exception, text, new HashSet<TaipanBaseException>(ReferenceEqualityComparer.Instance));
        return text.ToString();
    }

    private static void Format(TaipanBaseException exception, StringBuilder text, HashSet<TaipanBaseException> seen)
    {
        seen.Add(exception);
        if (exception.__cause__ is { } cause && !seen.Contains(cause))
        {
            Format(cause, text, seen);
            text.Append("\nThe above exception was the direct cause of the following exception:\n\n");
        }
        else if (exception.__context__ is { } context && !exception.__suppress_context__ && !seen.Contains(context))
        {
            Format(context, text, seen);
            text.Append("\nDuring handling of the above exception, another exception occurred:\n\n");
        }
        if (exception.__traceback__ is { } traceback)
        {
            text.Append("Traceback (most recent call last):\n");
            FormatEntries(traceback, text);
        }
        text.Append(LastLine(exception)).Append('\n');
    }

    private static void FormatEntries(Traceback? entry, StringBuilder text)
    {
        (string File, long Line, string Name)? last = null;
        var count = 0;
        for (; entry is not null; entry = entry.tb_next)
        {
            var current = (entry.File, entry.tb_lineno, entry.Name);
            if (last != current)
            {
                Repeated(count, text);
                (last, count) = (current, 0);
            }
            if (++count > RecursiveCutoff)
                continue;
            text.Append($"  File \"{entry.File}\", line {entry.tb_lineno}, in {entry.Name}\n");
            if (SourceLine(entry.File, entry.tb_lineno) is { Length: > 0 } line)
                text.Append($"    {line}\n");
        }
        Repeated(count, text);
    }

    private static void Repeated(int count, StringBuilder text)
    {
        if (count <= RecursiveCutoff)
            return;
        var more = count - RecursiveCutoff;
        text.Append($"  [Previous line repeated {more} more time{(more > 1 ? "s" : "")}]\n");
    }

    private static readonly Dictionary<string, string[]?> s_sources = [];

    // A line's text, read from the file as Python's linecache does; null if it can't be read.
    private static string? SourceLine(string file, long line)
    {
        if (!s_sources.TryGetValue(file, out var lines))
        {
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                lines = null;
            }
            s_sources[file] = lines;
        }
        return lines is not null && line >= 1 && line <= lines.Length ? lines[line - 1].Trim() : null;
    }

    // `<class>: <str(e)>`, or the class alone for an empty message (SPEC §3.19).
    private static string LastLine(TaipanBaseException exception)
    {
        string name, message;
        switch (exception)
        {
            case PythonError python:
                (name, message) = (python.QualifiedType ?? python.type, python.message);
                break;
            case DotNetError dotnet:
                (name, message) = (dotnet.type, dotnet.message);
                break;
            default:
                var module = exception.GetType().Assembly == typeof(Tracebacks).Assembly ? null : ((IObject)exception).ModuleName;
                var type = ((IObject)exception).TypeName;
                name = module is null or "__main__" ? type : $"{module}.{type}";
                try
                {
                    message = ((IClass)exception).__str__();
                }
                catch (Exception e) when (Exceptions.IsCatchable(e))
                {
                    message = "<exception str() failed>";
                }
                break;
        }
        return message.Length == 0 ? name : $"{name}: {message}";
    }
}
