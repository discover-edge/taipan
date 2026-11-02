using System.Runtime.ExceptionServices;
using Python.Runtime;

namespace Taipan.Runtime;

/// <summary>
/// Taipan's <c>Exception</c> (SPEC §3.10), the base of every exception a <c>try</c> can catch. It
/// follows the shape the emitter gives a plain class (SPEC §8): a hidden constructor, a static
/// <c>__new__</c> and an <c>__init__</c> method, so compiled classes can derive from it.
/// </summary>
public class TaipanException : Exception, IObject
{
    public string @message { get; set; } = "";

    protected TaipanException() { }

    internal TaipanException(string message) => this.message = message;

    public static TaipanException @__new__(string @message)
    {
        var self = new TaipanException();
        self.@__init__(message);
        return self;
    }

    public void @__init__(string @message) => this.message = message;

    public static string @__init___message_default() => "";

    // A built-in class's name is its C# name; a compiled subclass gives its own.
    string IObject.TypeName => GetType() == typeof(TaipanException) ? "Exception" : GetType().Name;

    // An exception displays as its message, and inside a container with its class (SPEC §6), unless
    // its class defines __str__ or __repr__ (SPEC §4.8).
    string IClass.__str__() => message;

    string IClass.__repr__() => $"{((IObject)this).TypeName}({Display.Repr(message)})";

    public override string Message => message;

    public override string ToString() => Display.Format(this);
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
    internal static T Init<T>(T self, string message) where T : TaipanException
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
    public static TaipanException From(Exception exception) => exception switch
    {
        TaipanException taipan => taipan,
        OverflowException => new OverflowError("integer overflow"),
        TaipanPanic => throw new ArgumentException("a panic is not catchable", nameof(exception)),
        _ => new DotNetError(exception),
    };

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
