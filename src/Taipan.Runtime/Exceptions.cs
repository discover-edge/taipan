using System.Runtime.ExceptionServices;
using Python.Runtime;

namespace Taipan.Runtime;

/// <summary>
/// Taipan's <c>BaseException</c> (SPEC §3.10, M37), the base of every exception a <c>try</c> can
/// catch. It follows the shape the emitter gives a plain class (SPEC §8): a hidden constructor, a
/// static <c>__new__</c> and an <c>__init__</c> method, so compiled classes can derive from it.
/// </summary>
public class TaipanBaseException : Exception, IObject
{
    public string @message { get; set; } = "";

    protected TaipanBaseException() { }

    internal TaipanBaseException(string message) => this.message = message;

    public static TaipanBaseException @__new__(string @message)
    {
        var self = new TaipanBaseException();
        self.@__init__(message);
        return self;
    }

    public void @__init__(string @message) => this.message = message;

    public static string @__init___message_default() => "";

    // A built-in class's name is its C# name; a compiled subclass gives its own.
    string IObject.TypeName => GetType() == typeof(TaipanBaseException) ? "BaseException"
        : GetType() == typeof(TaipanException) ? "Exception" : GetType().Name;

    // An exception displays as its message, and inside a container with its class (SPEC §6), unless
    // its class defines __str__ or __repr__ (SPEC §4.8).
    string IClass.__str__() => message;

    string IClass.__repr__() => $"{((IObject)this).TypeName}({Display.Repr(message)})";

    public override string Message => message;

    public override string ToString() => Display.Format(this);
}

/// <summary>Taipan's <c>Exception</c> (SPEC §3.10): every exception but <c>SystemExit</c> derives from it.</summary>
public class TaipanException : TaipanBaseException
{
    protected TaipanException() { }

    internal TaipanException(string message) : base(message) { }

    public static new TaipanException @__new__(string @message) => Exceptions.Init(new TaipanException(), message);
}

/// <summary><c>SystemExit</c> (SPEC §3.10, M37), which <c>sys.exit</c> raises.</summary>
public class SystemExit : TaipanBaseException
{
    protected SystemExit() { }

    internal SystemExit(long code, string? message)
        : base(message ?? code.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        this.code = code;
        PrintsMessage = message is not null;
    }

    public long @code { get; set; }

    /// <summary>Whether it came from <c>sys.exit(message)</c>, whose message is printed when it ends the program.</summary>
    internal bool PrintsMessage { get; }

    public static SystemExit @__new__(long @code)
    {
        var self = new SystemExit();
        self.@__init__(code);
        return self;
    }

    public void @__init__(long @code)
    {
        this.code = code;
        message = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static long @__init___code_default() => 0;
}

// Each built-in exception class (SPEC §3.10) has a constructor for the runtime's own errors, and the
// plain-class shape for compiled code.

public class ArithmeticError : TaipanException
{
    protected ArithmeticError() { }

    internal ArithmeticError(string message) : base(message) { }

    public static new ArithmeticError @__new__(string @message) => Exceptions.Init(new ArithmeticError(), message);
}

public class ZeroDivisionError : ArithmeticError
{
    protected ZeroDivisionError() { }

    internal ZeroDivisionError(string message) : base(message) { }

    public static new ZeroDivisionError @__new__(string @message) => Exceptions.Init(new ZeroDivisionError(), message);
}

public class OverflowError : ArithmeticError
{
    protected OverflowError() { }

    internal OverflowError(string message) : base(message) { }

    public static new OverflowError @__new__(string @message) => Exceptions.Init(new OverflowError(), message);
}

public class LookupError : TaipanException
{
    protected LookupError() { }

    internal LookupError(string message) : base(message) { }

    public static new LookupError @__new__(string @message) => Exceptions.Init(new LookupError(), message);
}

public class IndexError : LookupError
{
    protected IndexError() { }

    internal IndexError(string message) : base(message) { }

    public static new IndexError @__new__(string @message) => Exceptions.Init(new IndexError(), message);
}

public class KeyError : LookupError
{
    protected KeyError() { }

    internal KeyError(string message) : base(message) { }

    public static new KeyError @__new__(string @message) => Exceptions.Init(new KeyError(), message);
}

public class ValueError : TaipanException
{
    protected ValueError() { }

    internal ValueError(string message) : base(message) { }

    public static new ValueError @__new__(string @message) => Exceptions.Init(new ValueError(), message);
}

public class StopIteration : TaipanException
{
    protected StopIteration() { }

    internal StopIteration(string message) : base(message) { }

    public static new StopIteration @__new__(string @message) => Exceptions.Init(new StopIteration(), message);
}

public class TypeError : TaipanException
{
    protected TypeError() { }

    internal TypeError(string message) : base(message) { }

    public static new TypeError @__new__(string @message) => Exceptions.Init(new TypeError(), message);
}

public class RuntimeError : TaipanException
{
    protected RuntimeError() { }

    internal RuntimeError(string message) : base(message) { }

    public static new RuntimeError @__new__(string @message) => Exceptions.Init(new RuntimeError(), message);
}

public class EOFError : TaipanException
{
    protected EOFError() { }

    internal EOFError(string message) : base(message) { }

    public static new EOFError @__new__(string @message) => Exceptions.Init(new EOFError(), message);
}

/// <summary><c>OSError</c> (SPEC §3.10, M37), with Python's <c>errno</c>, <c>strerror</c> and <c>filename</c>.</summary>
public class OSError : TaipanException
{
    protected OSError() { }

    internal OSError(string message) : base(message) { }

    internal OSError(long errno, string strerror, string filename)
        : base($"[Errno {errno}] {strerror}: {Display.PythonRepr(filename)}")
    {
        this.errno = errno;
        this.strerror = strerror;
        this.filename = filename;
    }

    public long? @errno { get; set; }

    public string? @strerror { get; set; }

    public string? @filename { get; set; }

    public static new OSError @__new__(string @message) => Exceptions.Init(new OSError(), message);
}

public class FileNotFoundError : OSError
{
    protected FileNotFoundError() { }

    internal FileNotFoundError(long errno, string strerror, string filename) : base(errno, strerror, filename) { }

    public static new FileNotFoundError @__new__(string @message) => Exceptions.Init(new FileNotFoundError(), message);
}

public class FileExistsError : OSError
{
    protected FileExistsError() { }

    internal FileExistsError(long errno, string strerror, string filename) : base(errno, strerror, filename) { }

    public static new FileExistsError @__new__(string @message) => Exceptions.Init(new FileExistsError(), message);
}

public class PermissionError : OSError
{
    protected PermissionError() { }

    internal PermissionError(long errno, string strerror, string filename) : base(errno, strerror, filename) { }

    public static new PermissionError @__new__(string @message) => Exceptions.Init(new PermissionError(), message);
}

public class IsADirectoryError : OSError
{
    protected IsADirectoryError() { }

    internal IsADirectoryError(long errno, string strerror, string filename) : base(errno, strerror, filename) { }

    public static new IsADirectoryError @__new__(string @message) => Exceptions.Init(new IsADirectoryError(), message);
}

public class UnicodeError : ValueError
{
    protected UnicodeError() { }

    internal UnicodeError(string message) : base(message) { }

    public static new UnicodeError @__new__(string @message) => Exceptions.Init(new UnicodeError(), message);
}

public class UnicodeDecodeError : UnicodeError
{
    protected UnicodeDecodeError() { }

    internal UnicodeDecodeError(string message) : base(message) { }

    public static new UnicodeDecodeError @__new__(string @message) => Exceptions.Init(new UnicodeDecodeError(), message);
}

public class UnicodeEncodeError : UnicodeError
{
    protected UnicodeEncodeError() { }

    internal UnicodeEncodeError(string message) : base(message) { }

    public static new UnicodeEncodeError @__new__(string @message) => Exceptions.Init(new UnicodeEncodeError(), message);
}

/// <summary>A Python exception from a <c>py</c> operation (SPEC §10.5). Only the runtime makes one.</summary>
public sealed class PythonError : TaipanException
{
    internal PythonError(string type, string message, PyObject value) : base(message)
    {
        this.type = type;
        this.value = value;
    }

    public string @type { get; set; }

    public PyObject @value { get; set; }
}

/// <summary>
/// A .NET exception, seen by a Taipan class's <c>except</c> clause (SPEC §13.6). Only the runtime
/// makes one; <see cref="Exception.InnerException"/> is the .NET exception.
/// </summary>
public sealed class DotNetError : TaipanException
{
    internal DotNetError(Exception exception) : base(exception.Message)
    {
        type = exception.GetType().FullName ?? exception.GetType().Name;
        Exception = exception;
    }

    public string @type { get; set; }

    internal Exception Exception { get; }
}

/// <summary>What compiled <c>try</c> statements and <c>raise</c> use (SPEC §8).</summary>
public static class Exceptions
{
    internal static T Init<T>(T self, string message) where T : TaipanBaseException
    {
        self.@__init__(message);
        return self;
    }

    /// <summary>
    /// Whether an <c>except</c> clause may see the exception: anything but a failure at startup.
    /// Any exception that isn't Taipan's comes from .NET code (SPEC §13.6).
    /// </summary>
    public static bool IsCatchable(Exception exception) => exception is not TaipanPanic;

    /// <summary>
    /// The Taipan exception a caught one stands for. Checked arithmetic, and .NET code, throw
    /// .NET's own overflow; any other .NET exception is a <see cref="DotNetError"/>.
    /// </summary>
    public static TaipanBaseException From(Exception exception) => exception switch
    {
        TaipanBaseException taipan => taipan,
        OverflowException => new OverflowError("integer overflow"),
        TaipanPanic => throw new ArgumentException("a panic is not catchable", nameof(exception)),
        _ => new DotNetError(exception),
    };

    /// <summary>Throws <paramref name="exception"/>, where C# needs a call rather than a statement: <c>sys.exit</c> as a value (SPEC §6.1).</summary>
    public static void Throw(Exception exception) => throw exception;

    /// <summary><c>raise e</c> in a native program (SPEC §15.3): <c>e</c>, with any location it recorded cleared.</summary>
    public static Exception Raise(Exception exception)
    {
        Panic.Forget(exception);
        return exception;
    }

    /// <summary>A caught exception as a .NET <c>except</c> clause tests it: a <see cref="DotNetError"/> is its .NET exception.</summary>
    public static Exception DotNet(Exception exception) => exception is DotNetError error ? error.Exception : exception;

    /// <summary>
    /// A bare <c>raise</c>: rethrows <paramref name="exception"/> with its stack trace, so an uncaught
    /// one still reports the line where it was first raised (SPEC §3.10). It never returns; its
    /// result type lets the call be written <c>throw Exceptions.Rethrow(e)</c>.
    /// </summary>
    public static Exception Rethrow(Exception exception)
    {
        ExceptionDispatchInfo.Throw(exception);
        return exception;
    }
}
