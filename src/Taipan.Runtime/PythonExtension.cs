using System.Runtime.InteropServices;
using System.Text;

namespace Taipan.Runtime;

/// <summary>
/// What a CPython extension module compiled from Taipan uses (SPEC §17): CPython's stable ABI,
/// called through function pointers found in the running Python process, to build the module,
/// bind and convert arguments, convert results and raise exceptions. Object pointers are
/// <c>nint</c>s; a function returning one returns a new reference, or 0 with a Python error set.
/// </summary>
public static unsafe class PythonExtension
{
    // Python's API version for the stable ABI (PYTHON_ABI_VERSION), and METH_VARARGS | METH_KEYWORDS.
    private const int AbiVersion = 3;
    private const int VarArgsAndKeywords = 3;

    /// <summary>A Python error is already set; it's left as it is.</summary>
    private sealed class PythonErrorSet : Exception;

    /// <summary>An argument that doesn't convert: the Python type of the value that doesn't (SPEC §17.3).</summary>
    private sealed class Mismatch(string pythonType) : Exception
    {
        public string PythonType { get; } = pythonType;
    }

    // ---- The CPython API ----

    private static readonly nint Python = FindPython();

    // In an extension module, CPython's symbols are the running process's; on Windows they're in python3.dll.
    private static nint FindPython()
    {
        if (OperatingSystem.IsWindows() && NativeLibrary.TryLoad("python3.dll", out var stable))
            return stable;
        return NativeLibrary.GetMainProgramHandle();
    }

    private static nint Symbol(string name) => NativeLibrary.GetExport(Python, name);

    // A global PyObject* (PyExc_ValueError), or a static object's address (_Py_NoneStruct, PyLong_Type).
    private static nint Global(string name) => *(nint*)Symbol(name);

    private static readonly delegate* unmanaged<nint, int, nint> ModuleCreate = (delegate* unmanaged<nint, int, nint>)Symbol("PyModule_Create2");
    private static readonly delegate* unmanaged<nint, byte*, nint, int> ModuleAddObjectRef = (delegate* unmanaged<nint, byte*, nint, int>)Symbol("PyModule_AddObjectRef");
    private static readonly delegate* unmanaged<byte*, nint, nint, nint> ErrNewException = (delegate* unmanaged<byte*, nint, nint, nint>)Symbol("PyErr_NewException");
    private static readonly delegate* unmanaged<nint, byte*, void> ErrSetString = (delegate* unmanaged<nint, byte*, void>)Symbol("PyErr_SetString");
    private static readonly delegate* unmanaged<nint> ErrOccurred = (delegate* unmanaged<nint>)Symbol("PyErr_Occurred");
    private static readonly delegate* unmanaged<nint, void> IncRef = (delegate* unmanaged<nint, void>)Symbol("Py_IncRef");
    private static readonly delegate* unmanaged<nint, void> DecRef = (delegate* unmanaged<nint, void>)Symbol("Py_DecRef");
    private static readonly delegate* unmanaged<nint, nint, int> IsInstance = (delegate* unmanaged<nint, nint, int>)Symbol("PyObject_IsInstance");
    private static readonly delegate* unmanaged<nint, nint> ObjectType = (delegate* unmanaged<nint, nint>)Symbol("PyObject_Type");
    private static readonly delegate* unmanaged<nint, byte*, nint> GetAttrString = (delegate* unmanaged<nint, byte*, nint>)Symbol("PyObject_GetAttrString");
    private static readonly delegate* unmanaged<nint, long> LongAsLongLong = (delegate* unmanaged<nint, long>)Symbol("PyLong_AsLongLong");
    private static readonly delegate* unmanaged<long, nint> LongFromLongLong = (delegate* unmanaged<long, nint>)Symbol("PyLong_FromLongLong");
    private static readonly delegate* unmanaged<nint, double> FloatAsDouble = (delegate* unmanaged<nint, double>)Symbol("PyFloat_AsDouble");
    private static readonly delegate* unmanaged<double, nint> FloatFromDouble = (delegate* unmanaged<double, nint>)Symbol("PyFloat_FromDouble");
    private static readonly delegate* unmanaged<long, nint> BoolFromLong = (delegate* unmanaged<long, nint>)Symbol("PyBool_FromLong");
    private static readonly delegate* unmanaged<nint, nint*, byte*> UnicodeAsUtf8AndSize = (delegate* unmanaged<nint, nint*, byte*>)Symbol("PyUnicode_AsUTF8AndSize");
    private static readonly delegate* unmanaged<byte*, nint, nint> UnicodeFromStringAndSize = (delegate* unmanaged<byte*, nint, nint>)Symbol("PyUnicode_FromStringAndSize");
    private static readonly delegate* unmanaged<nint, nint> ListNew = (delegate* unmanaged<nint, nint>)Symbol("PyList_New");
    private static readonly delegate* unmanaged<nint, nint> ListSize = (delegate* unmanaged<nint, nint>)Symbol("PyList_Size");
    private static readonly delegate* unmanaged<nint, nint, nint> ListGetItem = (delegate* unmanaged<nint, nint, nint>)Symbol("PyList_GetItem");
    private static readonly delegate* unmanaged<nint, nint, nint, int> ListSetItem = (delegate* unmanaged<nint, nint, nint, int>)Symbol("PyList_SetItem");
    private static readonly delegate* unmanaged<nint, nint> TupleNew = (delegate* unmanaged<nint, nint>)Symbol("PyTuple_New");
    private static readonly delegate* unmanaged<nint, nint> TupleSize = (delegate* unmanaged<nint, nint>)Symbol("PyTuple_Size");
    private static readonly delegate* unmanaged<nint, nint, nint> TupleGetItem = (delegate* unmanaged<nint, nint, nint>)Symbol("PyTuple_GetItem");
    private static readonly delegate* unmanaged<nint, nint, nint, int> TupleSetItem = (delegate* unmanaged<nint, nint, nint, int>)Symbol("PyTuple_SetItem");
    private static readonly delegate* unmanaged<nint> DictNew = (delegate* unmanaged<nint>)Symbol("PyDict_New");
    private static readonly delegate* unmanaged<nint, nint, nint, int> DictSetItem = (delegate* unmanaged<nint, nint, nint, int>)Symbol("PyDict_SetItem");
    private static readonly delegate* unmanaged<nint, nint*, nint*, nint*, int> DictNext = (delegate* unmanaged<nint, nint*, nint*, nint*, int>)Symbol("PyDict_Next");

    private static readonly nint NoneObject = Symbol("_Py_NoneStruct");
    private static readonly nint LongType = Symbol("PyLong_Type");
    private static readonly nint FloatType = Symbol("PyFloat_Type");
    private static readonly nint BoolType = Symbol("PyBool_Type");
    private static readonly nint UnicodeType = Symbol("PyUnicode_Type");
    private static readonly nint ListType = Symbol("PyList_Type");
    private static readonly nint TupleType = Symbol("PyTuple_Type");
    private static readonly nint DictType = Symbol("PyDict_Type");

    private static byte* Utf8(string text) => (byte*)Marshal.StringToCoTaskMemUTF8(text);

    // ---- The module (SPEC §17.2) ----

    // The exception classes the module defines, by their compiled classes.
    private static readonly Dictionary<Type, nint> Exceptions = [];

    /// <summary>
    /// Creates the module, with each function a <c>(self, args, kwargs)</c> entry point. Its
    /// definition and method table live as long as the process, as a C extension's static ones do.
    /// </summary>
    public static nint CreateModule(string name, (string Name, nint Function)[] functions)
    {
        // PyMethodDef: name, function, flags, doc; a zeroed one ends the table.
        var methods = (nint*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * 4 * (functions.Length + 1)));
        for (var i = 0; i < functions.Length; i++)
        {
            methods[i * 4] = (nint)Utf8(functions[i].Name);
            methods[i * 4 + 1] = functions[i].Function;
            methods[i * 4 + 2] = VarArgsAndKeywords;
        }
        // PyModuleDef: PyModuleDef_HEAD_INIT (refcount 1, then four nulls), name, doc, size (-1: global state), methods, and four nulls.
        var definition = (nint*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * 13));
        definition[0] = 1;
        definition[5] = (nint)Utf8(name);
        definition[7] = -1;
        definition[8] = (nint)methods;
        return Check(ModuleCreate((nint)definition, AbiVersion));
    }

    /// <summary>Adds an attribute, taking <paramref name="value"/>'s reference.</summary>
    public static void AddConstant(nint module, string name, nint value)
    {
        var added = ModuleAddObjectRef(module, Utf8(name), Check(value));
        DecRef(value);
        if (added < 0)
            throw new PythonErrorSet();
    }

    /// <summary>
    /// Adds an exception class <c>module.Class</c> for a compiled class, deriving from the Python
    /// class of its nearest base the module defines, or else of its built-in base (SPEC §17.2).
    /// Bases are added before the classes deriving from them.
    /// </summary>
    public static void AddException(nint module, string moduleName, string className, Type type)
    {
        var pythonClass = Check(ErrNewException(Utf8($"{moduleName}.{className}"), PythonClassOf(type.BaseType!), 0));
        Exceptions[type] = pythonClass;
        if (ModuleAddObjectRef(module, Utf8(className), pythonClass) < 0)
            throw new PythonErrorSet();
    }

    // ---- Arguments (SPEC §17.3) ----

    /// <summary>
    /// Binds a call's positional and keyword arguments to the parameters, as Python binds them (SPEC
    /// §17.3, §4.2): a borrowed reference for each parameter, or 0 for one left out (which has a
    /// default) and for <c>*args</c> and <c>**kwargs</c>, whose arguments are returned separately.
    /// <paramref name="kinds"/> holds each parameter's kind: 0 ordinary, 1 positional-only,
    /// 2 keyword-only, 3 <c>*args</c>, 4 <c>**kwargs</c> (M30).
    /// </summary>
    public static nint[] Arguments(nint args, nint kwargs, string function, string[] parameters, int[] kinds, bool[] hasDefault,
        out List<nint> extra, out List<(string Key, nint Value)> extraKeywords)
    {
        var bound = new nint[parameters.Length];
        (extra, extraKeywords) = ([], []);
        var positional = Enumerable.Range(0, parameters.Length).Where(i => kinds[i] is 0 or 1).ToList();
        var (varArgs, varKeywords) = (Array.IndexOf(kinds, 3), Array.IndexOf(kinds, 4));
        var given = (int)TupleSize(args);
        if (given > positional.Count && varArgs < 0)
        {
            var required = positional.Count(i => !hasDefault[i]);
            var takes = required == positional.Count
                ? $"{positional.Count} positional argument{(positional.Count == 1 ? "" : "s")}"
                : $"from {required} to {positional.Count} positional arguments";
            throw TypeError($"{function}() takes {takes} but {given} {(given == 1 ? "was" : "were")} given");
        }
        for (var i = 0; i < given; i++)
        {
            if (i < positional.Count)
                bound[positional[i]] = TupleGetItem(args, i);
            else
                extra.Add(TupleGetItem(args, i));
        }
        var positionalOnly = new List<string>();
        if (kwargs != 0)
        {
            nint position = 0, key, value;
            while (DictNext(kwargs, &position, &key, &value) != 0)
            {
                var name = Str(key);
                var index = Array.IndexOf(parameters, name);
                if (index >= 0 && kinds[index] is 0 or 2)
                {
                    if (bound[index] != 0)
                        throw TypeError($"{function}() got multiple values for argument '{name}'");
                    bound[index] = value;
                }
                else if (varKeywords >= 0)
                {
                    extraKeywords.Add((name, value));
                }
                else if (index >= 0 && kinds[index] == 1)
                {
                    positionalOnly.Add(name);
                }
                else
                {
                    throw TypeError($"{function}() got an unexpected keyword argument '{name}'");
                }
            }
        }
        if (positionalOnly.Count > 0)
            throw TypeError($"{function}() got some positional-only arguments passed as keyword arguments: '{string.Join(", ", positionalOnly)}'");
        for (var i = 0; i < parameters.Length; i++)
        {
            if (bound[i] == 0 && !hasDefault[i] && kinds[i] is 0 or 1)
                throw TypeError($"{function}() missing required argument '{parameters[i]}'");
            if (bound[i] == 0 && !hasDefault[i] && kinds[i] == 2)
                throw TypeError($"{function}() missing required keyword-only argument '{parameters[i]}'");
        }
        return bound;
    }

    /// <summary>A <c>**kwargs</c> parameter's dict, from the keywords no other parameter took, in order.</summary>
    public static OrderedDictionary<string, T> Keywords<T>(List<(string Key, nint Value)> keywords, Func<nint, T> convert)
    {
        var dict = new OrderedDictionary<string, T>();
        foreach (var (key, value) in keywords)
            dict[key] = convert(value);
        return dict;
    }

    /// <summary>One argument converted to its parameter's Taipan type, or a <c>TypeError</c> naming both types.</summary>
    public static T Argument<T>(string function, string parameter, string type, nint value, Func<nint, T> convert)
    {
        try
        {
            return convert(value);
        }
        catch (Mismatch mismatch)
        {
            throw TypeError($"{function}() argument '{parameter}' must be {type}, not {mismatch.PythonType}");
        }
    }

    // ---- Python to Taipan ----

    public static long Int(nint value)
    {
        Expect(value, LongType);
        var result = LongAsLongLong(value);
        if (result == -1 && ErrOccurred() != 0)
            throw new PythonErrorSet();
        return result;
    }

    // An int widens to a float, as in Python.
    public static double Float(nint value)
    {
        if (IsInstance(value, LongType) == 1)
            return Int(value);
        Expect(value, FloatType);
        return FloatAsDouble(value);
    }

    public static bool Bool(nint value)
    {
        Expect(value, BoolType);
        return LongAsLongLong(value) != 0;
    }

    public static string Str(nint value)
    {
        Expect(value, UnicodeType);
        nint length;
        var utf8 = UnicodeAsUtf8AndSize(value, &length);
        if (utf8 is null)
            throw new PythonErrorSet();
        return Encoding.UTF8.GetString(utf8, (int)length);
    }

    public static T? Optional<T>(nint value, Func<nint, T> convert) where T : class => value == NoneObject ? null : convert(value);

    public static T? OptionalValue<T>(nint value, Func<nint, T> convert) where T : struct => value == NoneObject ? null : convert(value);

    public static List<T> List<T>(nint value, Func<nint, T> convert)
    {
        Expect(value, ListType);
        var count = ListSize(value);
        var list = new List<T>((int)count);
        for (nint i = 0; i < count; i++)
            list.Add(convert(ListGetItem(value, i)));
        return list;
    }

    public static OrderedDictionary<K, V> Dict<K, V>(nint value, Func<nint, K> key, Func<nint, V> convert) where K : notnull
    {
        Expect(value, DictType);
        var dict = new OrderedDictionary<K, V>();
        nint position = 0, k, v;
        while (DictNext(value, &position, &k, &v) != 0)
            dict[key(k)] = convert(v);
        return dict;
    }

    /// <summary>A tuple's element, once the tuple is checked to have <paramref name="count"/> elements.</summary>
    public static nint Item(nint value, int index, int count)
    {
        Expect(value, TupleType);
        if (TupleSize(value) != count)
            throw new Mismatch($"a tuple of {TupleSize(value)}");
        return TupleGetItem(value, index);
    }

    private static void Expect(nint value, nint type)
    {
        if (IsInstance(value, type) != 1)
            throw new Mismatch(TypeName(value));
    }

    private static string TypeName(nint value)
    {
        var type = ObjectType(value);
        var name = GetAttrString(type, Utf8("__name__"));
        DecRef(type);
        var text = name == 0 ? "object" : Str(name);
        DecRef(name);
        return text;
    }

    // ---- Taipan to Python ----

    public static nint None()
    {
        IncRef(NoneObject);
        return NoneObject;
    }

    public static nint FromInt(long value) => Check(LongFromLongLong(value));

    public static nint FromFloat(double value) => Check(FloatFromDouble(value));

    public static nint FromBool(bool value) => Check(BoolFromLong(value ? 1 : 0));

    public static nint FromStr(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* utf8 = bytes)
            return Check(UnicodeFromStringAndSize(utf8, bytes.Length));
    }

    public static nint FromOptional<T>(T? value, Func<T, nint> convert) where T : class => value is null ? None() : convert(value);

    public static nint FromOptionalValue<T>(T? value, Func<T, nint> convert) where T : struct => value is { } inner ? convert(inner) : None();

    public static nint FromList<T>(List<T> list, Func<T, nint> convert)
    {
        var result = Check(ListNew(list.Count));
        for (var i = 0; i < list.Count; i++)
            ListSetItem(result, i, convert(list[i]));
        return result;
    }

    public static nint FromDict<K, V>(OrderedDictionary<K, V> dict, Func<K, nint> key, Func<V, nint> convert) where K : notnull
    {
        var result = Check(DictNew());
        foreach (var (k, v) in dict)
        {
            var (pythonKey, pythonValue) = (key(k), convert(v));
            var set = DictSetItem(result, pythonKey, pythonValue);
            DecRef(pythonKey);
            DecRef(pythonValue);
            if (set < 0)
                throw new PythonErrorSet();
        }
        return result;
    }

    /// <summary>A tuple of converted elements, taking their references.</summary>
    public static nint FromTuple(params nint[] items)
    {
        var result = Check(TupleNew(items.Length));
        for (var i = 0; i < items.Length; i++)
            TupleSetItem(result, i, items[i]);
        return result;
    }

    private static nint Check(nint result) => result != 0 ? result : throw new PythonErrorSet();

    // ---- Exceptions (SPEC §17.4) ----

    /// <summary>Raises an exception that reached an entry point in Python, and returns 0 for the entry point to return.</summary>
    public static nint Raise(Exception exception)
    {
        Io.Out.Flush();
        switch (exception)
        {
            case PythonErrorSet:
                break;
            case TaipanException taipan:
                SetError(PythonClassOf(taipan.GetType()), taipan.message);
                break;
            case OverflowException:
                SetError(Global("PyExc_OverflowError"), "integer overflow");
                break;
            default:
                var dotnet = Taipan.Runtime.Exceptions.DotNet(exception);
                SetError(Global("PyExc_RuntimeError"), $".NET {dotnet.GetType().FullName}: {dotnet.Message}");
                break;
        }
        return 0;
    }

    /// <summary>Flushes what the call printed, so it's written before Python's own output that follows.</summary>
    public static nint Return(nint result)
    {
        Io.Out.Flush();
        return result;
    }

    private static PythonErrorSet TypeError(string message)
    {
        SetError(Global("PyExc_TypeError"), message);
        return new PythonErrorSet();
    }

    private static void SetError(nint type, string message)
    {
        var utf8 = Utf8(message);
        ErrSetString(type, utf8);
        Marshal.FreeCoTaskMem((nint)utf8);
    }

    // The module's own class for an exception class or its nearest base, or the built-in of the same name.
    private static nint PythonClassOf(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (Exceptions.TryGetValue(current, out var pythonClass))
                return pythonClass;
            if (current.Assembly == typeof(TaipanException).Assembly && typeof(TaipanException).IsAssignableFrom(current))
                return Global(current == typeof(TaipanException) ? "PyExc_Exception" : $"PyExc_{current.Name}");
        }
        return Global("PyExc_Exception");
    }
}
