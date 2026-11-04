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

    private static readonly delegate* unmanaged<nint, nint, nint> TypeFromSpecWithBases = (delegate* unmanaged<nint, nint, nint>)Symbol("PyType_FromSpecWithBases");
    private static readonly delegate* unmanaged<nint, int, nint> TypeGetSlot = (delegate* unmanaged<nint, int, nint>)Symbol("PyType_GetSlot");
    private static readonly delegate* unmanaged<nint, byte*, nint, int> SetAttrString = (delegate* unmanaged<nint, byte*, nint, int>)Symbol("PyObject_SetAttrString");
    private static readonly delegate* unmanaged<nint, nint, nint, nint> CFunctionNewEx = (delegate* unmanaged<nint, nint, nint, nint>)Symbol("PyCFunction_NewEx");
    private static readonly delegate* unmanaged<nint, nint, nint, nint> ObjectCall = (delegate* unmanaged<nint, nint, nint, nint>)Symbol("PyObject_Call");
    private static readonly delegate* unmanaged<nint, nint> ObjectStr = (delegate* unmanaged<nint, nint>)Symbol("PyObject_Str");
    private static readonly delegate* unmanaged<nint*, nint*, nint*, void> ErrFetch = (delegate* unmanaged<nint*, nint*, nint*, void>)Symbol("PyErr_Fetch");
    private static readonly delegate* unmanaged<nint, nint, nint, void> ErrRestore = (delegate* unmanaged<nint, nint, nint, void>)Symbol("PyErr_Restore");
    private static readonly delegate* unmanaged<int> GilEnsure = (delegate* unmanaged<int>)Symbol("PyGILState_Ensure");
    private static readonly delegate* unmanaged<int, void> GilRelease = (delegate* unmanaged<int, void>)Symbol("PyGILState_Release");
    private static readonly delegate* unmanaged<int> IsInitialized = (delegate* unmanaged<int>)Symbol("Py_IsInitialized");

    private static readonly nint NoneObject = Symbol("_Py_NoneStruct");
    private static readonly nint NotImplementedObject = Symbol("_Py_NotImplementedStruct");
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

    // ---- Classes and enums as Python types (SPEC §17.5, M45) ----

    // typeslots.h's slots, and object.h's type flags.
    private const int TpAlloc = 47, TpDealloc = 52, TpHash = 59, TpMethods = 64, TpNew = 65, TpRepr = 66, TpRichCompare = 67, TpStr = 70, TpGetSet = 73, TpFree = 74;
    private const uint DefaultFlags = 1u << 18, BaseTypeFlag = 1u << 10, DisallowInstantiation = 1u << 7;

    // An instance is a PyObject header (refcount, type) followed by a GCHandle to the Taipan object.
    private static readonly int HandleOffset = 2 * sizeof(nint);

    // Each compiled class's Python type, and the reverse.
    private static readonly Dictionary<Type, nint> Types = [];
    private static readonly HashSet<nint> OwnTypes = [];

    /// <summary>An attribute of a Python type: how to read it from the Taipan object, and to set it, or null when it's read-only.</summary>
    public sealed record Attribute(string Name, Func<object, nint> Get, Action<object, nint>? Set);

    /// <summary>
    /// Adds a class's Python type <c>module.Class</c> (SPEC §17.5): constructed by <paramref name="construct"/>
    /// (0 when it can't be), with its attributes and methods, and deriving from its base's Python type.
    /// </summary>
    public static void AddClass(nint module, string moduleName, string name, Type type, nint construct, Attribute[] attributes,
        (string Name, nint Function)[] methods, bool hashable, bool hasSubclasses)
    {
        var pythonType = CreateType($"{moduleName}.{name}", type, construct, attributes, methods, hashable, hasSubclasses);
        if (ModuleAddObjectRef(module, Utf8(name), pythonType) < 0)
            throw new PythonErrorSet();
    }

    /// <summary>
    /// Adds an enum's Python type (SPEC §17.5): its variants without fields are class attributes
    /// holding their values, and those with fields class attributes that make one.
    /// </summary>
    public static void AddEnum(nint module, string moduleName, string name, Type type, Attribute[] attributes, (string Name, object? Value, nint Construct)[] variants)
    {
        var pythonType = CreateType($"{moduleName}.{name}", type, 0, attributes, [], hashable: true, hasSubclasses: false);
        foreach (var (variant, value, construct) in variants)
        {
            var attribute = value is not null ? Wrap(value) : Check(CFunctionNewEx((nint)MethodTable([(variant, construct)]), 0, 0));
            var set = SetAttrString(pythonType, Utf8(variant), attribute);
            DecRef(attribute);
            if (set < 0)
                throw new PythonErrorSet();
        }
        if (ModuleAddObjectRef(module, Utf8(name), pythonType) < 0)
            throw new PythonErrorSet();
    }

    private static nint CreateType(string qualifiedName, Type type, nint construct, Attribute[] attributes, (string Name, nint Function)[] methods,
        bool hashable, bool hasSubclasses)
    {
        // PyGetSetDef: name, get, set, doc, closure; a zeroed one ends the table.
        var getset = (nint*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * 5 * (attributes.Length + 1)));
        for (var i = 0; i < attributes.Length; i++)
        {
            getset[i * 5] = (nint)Utf8(attributes[i].Name);
            getset[i * 5 + 1] = (nint)(delegate* unmanaged<nint, nint, nint>)&GetAttribute;
            getset[i * 5 + 2] = attributes[i].Set is null ? 0 : (nint)(delegate* unmanaged<nint, nint, nint, int>)&SetAttribute;
            getset[i * 5 + 4] = GCHandle.ToIntPtr(GCHandle.Alloc(attributes[i]));
        }
        var slots = new List<(int Slot, nint Function)>
        {
            (TpDealloc, (nint)(delegate* unmanaged<nint, void>)&Dealloc),
            (TpStr, (nint)(delegate* unmanaged<nint, nint>)&StrSlot),
            (TpRepr, (nint)(delegate* unmanaged<nint, nint>)&ReprSlot),
            (TpRichCompare, (nint)(delegate* unmanaged<nint, nint, int, nint>)&RichCompare),
            (TpGetSet, (nint)getset),
            (TpMethods, (nint)MethodTable(methods)),
        };
        if (hashable)
            slots.Add((TpHash, (nint)(delegate* unmanaged<nint, nint>)&HashSlot));
        if (construct != 0)
            slots.Add((TpNew, construct));
        // PyType_Slot: slot, function; a zeroed one ends the list.
        var slotTable = (nint*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * 2 * (slots.Count + 1)));
        for (var i = 0; i < slots.Count; i++)
        {
            *(int*)&slotTable[i * 2] = slots[i].Slot;
            slotTable[i * 2 + 1] = slots[i].Function;
        }
        // PyType_Spec: name, basicsize, itemsize, flags, slots.
        var spec = (byte*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * 4));
        *(nint*)spec = (nint)Utf8(qualifiedName);
        *(int*)(spec + sizeof(nint)) = HandleOffset + sizeof(nint);
        *(uint*)(spec + sizeof(nint) + 8) = DefaultFlags | (hasSubclasses ? BaseTypeFlag : 0) | (construct == 0 ? DisallowInstantiation : 0);
        *(nint*)(spec + sizeof(nint) + 16) = (nint)slotTable;

        // The Python type of the nearest base with one, as the only base.
        nint bases = 0;
        for (var current = type.BaseType; current is not null && bases == 0; current = current.BaseType)
        {
            if (Types.TryGetValue(current, out var baseType))
            {
                IncRef(baseType);
                bases = Check(TupleNew(1));
                TupleSetItem(bases, 0, baseType);
            }
        }
        var created = Check(TypeFromSpecWithBases((nint)spec, bases));
        if (bases != 0)
            DecRef(bases);
        // An unhashable class's instances aren't hashable in Python either.
        if (!hashable && SetAttrString(created, Utf8("__hash__"), NoneObject) < 0)
            throw new PythonErrorSet();
        Types[type] = created;
        OwnTypes.Add(created);
        return created;
    }

    // A PyMethodDef table of (self, args, kwargs) functions, which lives as long as the process.
    private static nint* MethodTable((string Name, nint Function)[] methods)
    {
        var table = (nint*)NativeMemory.AllocZeroed((nuint)(sizeof(nint) * 4 * (methods.Length + 1)));
        for (var i = 0; i < methods.Length; i++)
        {
            table[i * 4] = (nint)Utf8(methods[i].Name);
            table[i * 4 + 1] = methods[i].Function;
            table[i * 4 + 2] = VarArgsAndKeywords;
        }
        return table;
    }

    /// <summary>A Taipan object as a new instance of its Python type, or of its nearest exported base's (SPEC §17.5).</summary>
    public static nint Wrap(object value)
    {
        for (var type = value.GetType(); type is not null; type = type.BaseType)
        {
            if (!Types.TryGetValue(type, out var pythonType))
                continue;
            var alloc = (delegate* unmanaged<nint, nint, nint>)TypeGetSlot(pythonType, TpAlloc);
            var instance = Check(alloc(pythonType, 0));
            *(nint*)(instance + HandleOffset) = GCHandle.ToIntPtr(GCHandle.Alloc(value));
            return instance;
        }
        throw new InvalidOperationException($"{value.GetType().Name} has no Python type");
    }

    /// <summary>A Python argument as the Taipan object it holds, if it's an instance of <typeparamref name="T"/>'s Python type (SPEC §17.5).</summary>
    public static T Unwrap<T>(nint value) where T : class
    {
        if (!Types.TryGetValue(typeof(T), out var pythonType) || IsInstance(value, pythonType) != 1)
            throw new Mismatch(TypeName(value));
        return (T)Target(value);
    }

    /// <summary>The Taipan object a method's <c>self</c> holds.</summary>
    public static T Self<T>(nint self) where T : class => (T)Target(self);

    private static object Target(nint instance) => GCHandle.FromIntPtr(*(nint*)(instance + HandleOffset)).Target!;

    /// <summary>Whether <c>tp_new</c> was called for the class's own type, not a Python subclass of it (SPEC §17.5).</summary>
    public static void CheckConstructed(nint type, Type expected)
    {
        if (!Types.TryGetValue(expected, out var own) || own != type)
            throw TypeError("a Python class can't derive from a Taipan class");
    }

    /// <summary>An attribute's new value, converted to the field's type, or a <c>TypeError</c> naming both types (SPEC §17.5).</summary>
    public static T Assigned<T>(string owner, string attribute, string type, nint value, Func<nint, T> convert)
    {
        try
        {
            return convert(value);
        }
        catch (Mismatch mismatch)
        {
            throw TypeError($"'{owner}.{attribute}' must be {type}, not {mismatch.PythonType}");
        }
    }

    /// <summary>An enum value's field that its variant doesn't have.</summary>
    public static Exception NoAttribute(string type, object value, string attribute)
    {
        SetError(Global("PyExc_AttributeError"), $"'{type}' value {value.GetType().Name} has no attribute '{attribute}'");
        return new PythonErrorSet();
    }

    [UnmanagedCallersOnly]
    private static nint GetAttribute(nint self, nint closure)
    {
        try
        {
            return Return(((Attribute)GCHandle.FromIntPtr(closure).Target!).Get(Target(self)));
        }
        catch (Exception e)
        {
            return Raise(e);
        }
    }

    [UnmanagedCallersOnly]
    private static int SetAttribute(nint self, nint value, nint closure)
    {
        var attribute = (Attribute)GCHandle.FromIntPtr(closure).Target!;
        try
        {
            if (value == 0)
                throw TypeError($"can't delete attribute '{attribute.Name}'");
            attribute.Set!(Target(self), value);
            return 0;
        }
        catch (Exception e)
        {
            Raise(e);
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static void Dealloc(nint self)
    {
        GCHandle.FromIntPtr(*(nint*)(self + HandleOffset)).Free();
        var type = *(nint*)(self + sizeof(nint));
        var free = (delegate* unmanaged<nint, void>)TypeGetSlot(type, TpFree);
        free(self);
        DecRef(type);
    }

    [UnmanagedCallersOnly]
    private static nint StrSlot(nint self)
    {
        try
        {
            return FromStr(Display.Format(Target(self)));
        }
        catch (Exception e)
        {
            return Raise(e);
        }
    }

    [UnmanagedCallersOnly]
    private static nint ReprSlot(nint self)
    {
        try
        {
            return FromStr(Display.Repr(Target(self)));
        }
        catch (Exception e)
        {
            return Raise(e);
        }
    }

    // == and != compare as Taipan does; with another kind of object, and for ordering, Python's NotImplemented.
    [UnmanagedCallersOnly]
    private static nint RichCompare(nint self, nint other, int op)
    {
        try
        {
            if (op is not (2 or 3) || !OwnTypes.Contains(*(nint*)(other + sizeof(nint))) && !OwnTypes.Any(t => IsInstance(other, t) == 1))
            {
                IncRef(NotImplementedObject);
                return NotImplementedObject;
            }
            var equal = Equality.Equal(Target(self), Target(other));
            return FromBool(op == 2 ? equal : !equal);
        }
        catch (Exception e)
        {
            return Raise(e);
        }
    }

    [UnmanagedCallersOnly]
    private static nint HashSlot(nint self)
    {
        try
        {
            var hash = Ops.Hash(Target(self));
            // -1 means an error to Python.
            return hash == -1 ? -2 : (nint)hash;
        }
        catch (Exception e)
        {
            Raise(e);
            return -1;
        }
    }

    // ---- Python objects as arguments (SPEC §17.6, M45) ----

    /// <summary>A Python object an extension holds as a <c>py</c> value, taking a reference.</summary>
    public static PythonHandle Handle(nint value) => new(value);

    /// <summary>A <c>py</c> value as a new Python reference.</summary>
    public static nint FromHandle(PythonHandle handle)
    {
        IncRef(handle.Pointer);
        return handle.Pointer;
    }

    /// <summary>Calls a Python callable with converted arguments, taking their references, and gives its result's new reference.</summary>
    public static nint Call(PythonHandle callable, params nint[] arguments)
    {
        var tuple = FromTuple(arguments);
        var result = ObjectCall(callable.Pointer, tuple, 0);
        DecRef(tuple);
        return result != 0 ? result : throw Fetch();
    }

    /// <summary>What a Python callable returned, converted, or a <c>TypeError</c> when it doesn't convert (SPEC §17.6).</summary>
    public static T Returned<T>(nint result, string type, Func<nint, T> convert)
    {
        try
        {
            return convert(result);
        }
        catch (Mismatch mismatch)
        {
            throw new TypeError($"a Python function returned {mismatch.PythonType}, not {type}");
        }
        finally
        {
            DecRef(result);
        }
    }

    public static void Discard(nint result) => DecRef(result);

    // The Python error a call raised, as a PythonError that restores it if it reaches Python again.
    private static PythonError Fetch()
    {
        nint type, value, traceback;
        ErrFetch(&type, &value, &traceback);
        var name = GetAttrString(type, Utf8("__name__"));
        var typeName = name == 0 ? "Exception" : Str(name);
        DecRef(name);
        var message = "";
        if (value != 0 && ObjectStr(value) is var text and not 0)
        {
            message = Str(text);
            DecRef(text);
        }
        return new PythonError(typeName, message) { Pending = (type, value, traceback) };
    }

    internal static string Show(nint value)
    {
        var text = Check(ObjectStr(value));
        try
        {
            return Str(text);
        }
        finally
        {
            DecRef(text);
        }
    }

    // A handle's reference is released with the GIL, from the finalizer thread, while Python runs.
    internal static void Release(nint value)
    {
        if (IsInitialized() == 0)
            return;
        var state = GilEnsure();
        DecRef(value);
        GilRelease(state);
    }

    internal static void Keep(nint value) => IncRef(value);

    // ---- Exceptions (SPEC §17.4) ----

    /// <summary>Raises an exception that reached an entry point in Python, and returns 0 for the entry point to return.</summary>
    public static nint Raise(Exception exception)
    {
        Io.Out.Flush();
        switch (exception)
        {
            case PythonErrorSet:
                break;
            // A Python callable's exception goes back to Python as itself (SPEC §17.6).
            case PythonError { Pending: var (type, value, traceback) }:
                ErrRestore(type, value, traceback);
                break;
            case TaipanBaseException taipan:
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
            if (current.Assembly == typeof(TaipanException).Assembly && typeof(TaipanBaseException).IsAssignableFrom(current))
            {
                // Python's UnicodeDecodeError and UnicodeEncodeError can't be made from a message alone.
                return Global(current == typeof(TaipanException) ? "PyExc_Exception"
                    : current == typeof(TaipanBaseException) ? "PyExc_BaseException"
                    : current == typeof(UnicodeDecodeError) || current == typeof(UnicodeEncodeError) ? "PyExc_UnicodeError"
                    : $"PyExc_{current.Name}");
            }
        }
        return Global("PyExc_Exception");
    }
}
