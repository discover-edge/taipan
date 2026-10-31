using System.Runtime.CompilerServices;
using System.Collections;
using System.Diagnostics;
using Python.Runtime;

namespace Taipan.Runtime;

/// <summary>
/// Python interop (SPEC §10). Only this class uses pythonnet's types, so a program that never
/// imports from Python never loads Python.Runtime.dll or CPython. Every operation takes the GIL
/// for its duration and turns a Python exception into a panic (§10.5).
/// </summary>
public static class Interop
{
    private static PyObject? _operator;

    /// <summary>Python.Runtime.dll, which the compiler references and <c>taipan build</c> copies.</summary>
    public static string PythonRuntimeAssembly => typeof(PyObject).Assembly.Location;

    // ---- Startup (§10.1, §10.6) ----

    /// <summary><c>from python import name</c>: imports the module, starting Python first if needed.</summary>
    public static PyObject Import(string module)
    {
        EnsureStarted();
        return Run(() => Py.Import(module));
    }

    private static void EnsureStarted()
    {
        if (PythonEngine.IsInitialized)
            return;
        var library = FindLibrary()
            ?? throw Panic.Create("cannot load Python: no Python installation found (set PYTHONNET_PYDLL)");
        try
        {
            global::Python.Runtime.Runtime.PythonDLL = library;
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();
        }
        catch (Exception e) when (e is not TaipanPanic)
        {
            throw Panic.Create($"cannot load Python: {e.Message}");
        }
        Display.ExternalFormat = (value, nested) => value is PyObject python ? Run(() => Show(python, nested)) : null;
    }

    /// <summary>
    /// The CPython shared library: <c>PYTHONNET_PYDLL</c>, else what <c>python3</c> or
    /// <c>python</c> on PATH reports through sysconfig. Null if there's no Python.
    /// </summary>
    public static string? FindLibrary()
    {
        if (Environment.GetEnvironmentVariable("PYTHONNET_PYDLL") is { Length: > 0 } configured)
            return configured;

        const string query =
            "import os, sysconfig; " +
            "print(os.path.join(sysconfig.get_config_var('LIBDIR') or '', sysconfig.get_config_var('LDLIBRARY') or ''))";
        foreach (var executable in (string[])["python3", "python"])
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(executable, ["-c", query])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process is null)
                    continue;
                var path = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && File.Exists(path))
                    return path;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not on PATH; try the next name.
            }
        }
        return null;
    }

    // ---- Operations on py values (§10.2) ----

    public static PyObject GetAttr(PyObject target, string name) => Run(() => target.GetAttr(name));

    public static void SetAttr(PyObject target, string name, object? value) => Run(() => target.SetAttr(name, From(value)));

    public static PyObject GetItem(PyObject target, object? key) => Run(() => target.GetItem(From(key)));

    /// <summary><c>x[a:b:c]</c> on a py value: indexes with a Python <c>slice</c> (SPEC §5.4).</summary>
    public static PyObject Slice(PyObject target, object? start, object? stop, object? step) => Run(() =>
    {
        using var builtins = Py.Import("builtins");
        using var slice = builtins.GetAttr("slice").Invoke(Convert(start), Convert(stop), Convert(step));
        return target.GetItem(slice);
    });

    public static void SetItem(PyObject target, object? key, object? value) => Run(() => target.SetItem(From(key), From(value)));

    /// <summary>A call, with keyword arguments as (name, value) pairs.</summary>
    public static PyObject Call(PyObject target, object?[] arguments, (string Name, object? Value)[] keywords) => Run(() =>
    {
        var positional = arguments.Select(From).ToArray();
        if (keywords.Length == 0)
            return target.Invoke(positional);
        using var named = new PyDict();
        foreach (var (name, value) in keywords)
            named[name] = From(value);
        return target.Invoke(positional, named);
    });

    /// <summary>
    /// A binary operator with a py operand, through Python's <c>operator</c> module. The result is
    /// py even for comparisons (§10.2). <c>x in c</c> is <c>operator.contains(c, x)</c>.
    /// </summary>
    public static PyObject Binary(string op, object? left, object? right) => Run(() =>
    {
        var (l, r) = (From(left), From(right));
        _operator ??= Py.Import("operator");
        return op switch
        {
            "in" => _operator.InvokeMethod("contains", r, l),
            "not in" => _operator.InvokeMethod("not_", _operator.InvokeMethod("contains", r, l)),
            _ => _operator.InvokeMethod(OperatorName(op), l, r),
        };
    });

    private static string OperatorName(string op) => op switch
    {
        "+" => "add",
        "-" => "sub",
        "*" => "mul",
        "/" => "truediv",
        "//" => "floordiv",
        "%" => "mod",
        "==" => "eq",
        "!=" => "ne",
        "<" => "lt",
        "<=" => "le",
        ">" => "gt",
        ">=" => "ge",
        _ => throw new ArgumentException($"no Python operator for {op}"),
    };

    public static PyObject Negate(PyObject value) => Run(() =>
    {
        _operator ??= Py.Import("operator");
        return _operator.InvokeMethod("neg", value);
    });

    public static long Len(PyObject value) => Run(() => value.Length());

    public static string Str(PyObject value) => Run(() => value.ToString() ?? "");

    /// <summary>
    /// The display form of a py value (SPEC §10.2): Taipan's rules (§6) for None, bool, int,
    /// float, str, list, tuple and dict, so they look like the Taipan values they convert to;
    /// Python's <c>str()</c> for anything else. The caller holds the GIL.
    /// </summary>
    private static string Show(PyObject value, bool nested) => TypeName(value) switch
    {
        "NoneType" => "None",
        "float" => Display.Format(value.As<double>()),
        "str" when nested => Display.Repr(value.As<string>()),
        "list" => "[" + string.Join(", ", Items(value).Select(item => Show(item, nested: true))) + "]",
        "tuple" => Items(value).Select(item => Show(item, nested: true)).ToList() is [var single]
            ? $"({single},)"
            : "(" + string.Join(", ", Items(value).Select(item => Show(item, nested: true))) + ")",
        "dict" => "{" + string.Join(", ", Items(value).Select(key => $"{Show(key, true)}: {Show(value[key], true)}")) + "}",
        _ => value.ToString() ?? "",
    };

    /// <summary><c>for v in x</c>: Python iteration, one GIL acquisition per step.</summary>
    public static IEnumerable<PyObject> Iterate(PyObject value)
    {
        var iterator = Run(() => value.GetIterator());
        while (true)
        {
            PyObject? next = null;
            var more = Run(() =>
            {
                if (!iterator.MoveNext())
                    return false;
                next = iterator.Current;
                return true;
            });
            if (!more)
                yield break;
            yield return next!;
        }
    }

    // ---- Taipan → Python (§10.3) ----

    /// <summary>
    /// Converts a convertible Taipan value: int, float, bool, str, none, and lists and dicts of
    /// them (copied). A py value passes through.
    /// </summary>
    public static PyObject From(object? value) => Run(() => Convert(value));

    private static PyObject Convert(object? value) => value switch
    {
        null => PyObject.None,
        PyObject python => python,
        long l => new PyInt(l),
        double d => new PyFloat(d),
        bool b => b.ToPython(),
        string s => new PyString(s),
        IDictionary dict => ConvertDict(dict),
        ITuple tuple => new PyTuple([.. Enumerable.Range(0, tuple.Length).Select(i => Convert(tuple[i]))]),
        IList list => new PyList([.. list.Cast<object?>().Select(Convert)]),
        _ => throw new ArgumentException($"{value.GetType()} can't be passed to Python"),
    };

    private static PyDict ConvertDict(IDictionary dict)
    {
        var result = new PyDict();
        foreach (var key in dict.Keys)
            result[Convert(key)] = Convert(dict[key]);
        return result;
    }

    // ---- Python → Taipan: `as` (§10.4) ----

    /// <summary>A failed conversion; <c>As</c> turns it into the §10.5 panic, <c>AsOptional</c> into none.</summary>
    private sealed class ConversionFailed(string pythonType) : Exception(pythonType)
    {
        public string PythonType { get; } = pythonType;
    }

    /// <summary><c>x as T</c>: converts, or panics with <c>cannot convert Python &lt;type&gt; to &lt;T&gt;</c>.</summary>
    public static T As<T>(PyObject value, Func<PyObject, T> convert, string target)
    {
        try
        {
            return Run(() => convert(value));
        }
        catch (ConversionFailed failed)
        {
            throw Panic.Create($"cannot convert Python {failed.PythonType} to {target}");
        }
    }

    /// <summary><c>x as T?</c> for an int, float or bool <c>T</c>: none for None or on a mismatch.</summary>
    public static T? AsOptionalValue<T>(PyObject value, Func<PyObject, T> convert) where T : struct
    {
        try
        {
            return Run(() => convert(value));
        }
        catch (ConversionFailed)
        {
            return null;
        }
    }

    /// <summary><c>x as T?</c> for any other <c>T</c>: none for None or on a mismatch.</summary>
    public static T? AsOptionalRef<T>(PyObject value, Func<PyObject, T> convert) where T : class
    {
        try
        {
            return Run(() => convert(value));
        }
        catch (ConversionFailed)
        {
            return null;
        }
    }

    public static long ToInt(PyObject value)
    {
        if (TypeName(value) != "int")
            throw new ConversionFailed(TypeName(value));
        try
        {
            return value.As<long>();
        }
        catch (Exception e) when (e is OverflowException or PythonException or InvalidCastException)
        {
            throw new ConversionFailed("int");
        }
    }

    public static double ToFloat(PyObject value) => TypeName(value) switch
    {
        "float" => value.As<double>(),
        "int" => value.As<double>(),
        var other => throw new ConversionFailed(other),
    };

    public static bool ToBool(PyObject value) =>
        TypeName(value) == "bool" ? value.As<bool>() : throw new ConversionFailed(TypeName(value));

    public static string ToStr(PyObject value) =>
        TypeName(value) == "str" ? value.As<string>() : throw new ConversionFailed(TypeName(value));

    public static PyObject ToPy(PyObject value) => value;

    public static List<T> ToList<T>(PyObject value, Func<PyObject, T> element)
    {
        if (TypeName(value) is not ("list" or "tuple"))
            throw new ConversionFailed(TypeName(value));
        var result = new List<T>();
        foreach (var item in Items(value))
            result.Add(element(item));
        return result;
    }

    /// <summary>
    /// A Python tuple or list of exactly <paramref name="count"/> elements, built into a Taipan
    /// tuple from its items (SPEC §10.4).
    /// </summary>
    public static T ToTuple<T>(PyObject value, int count, Func<PyObject[], T> build)
    {
        if (TypeName(value) is not ("list" or "tuple"))
            throw new ConversionFailed(TypeName(value));
        var items = Items(value).ToArray();
        if (items.Length != count)
            throw new ConversionFailed(TypeName(value));
        return build(items);
    }

    public static OrderedDictionary<TKey, TValue> ToDict<TKey, TValue>(
        PyObject value, Func<PyObject, TKey> key, Func<PyObject, TValue> item) where TKey : notnull
    {
        if (TypeName(value) != "dict")
            throw new ConversionFailed(TypeName(value));
        var result = new OrderedDictionary<TKey, TValue>();
        foreach (var k in Items(value))
            result[key(k)] = item(value[k]);
        return result;
    }

    /// <summary>A <c>T?</c> inside a target, for value types: None becomes none.</summary>
    public static T? NoneOrValue<T>(PyObject value, Func<PyObject, T> convert) where T : struct =>
        value.IsNone() ? null : convert(value);

    /// <summary>A <c>T?</c> inside a target, for reference types: None becomes none.</summary>
    public static T? NoneOrRef<T>(PyObject value, Func<PyObject, T> convert) where T : class =>
        value.IsNone() ? null : convert(value);

    private static string TypeName(PyObject value) => value.GetPythonType().Name;

    // Python iteration; the caller holds the GIL.
    private static IEnumerable<PyObject> Items(PyObject value)
    {
        using var iterator = value.GetIterator();
        while (iterator.MoveNext())
            yield return iterator.Current;
    }

    // ---- Errors (§10.5) ----

    private static void Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    private static T Run<T>(Func<T> operation)
    {
        using (Py.GIL())
        {
            try
            {
                return operation();
            }
            catch (PythonException e)
            {
                throw Panic.Create($"Python {e.Type.Name}: {e.Message}");
            }
        }
    }
}
