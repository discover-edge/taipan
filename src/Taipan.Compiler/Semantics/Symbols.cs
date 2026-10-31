using System.Collections.Immutable;

namespace Taipan.Compiler.Semantics;

// Symbols are classes, not records: two variables with the same name and type are still
// different symbols.

public abstract class Symbol(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;
}

public enum VariableKind { Let, Var, Parameter, LoopVariable, Constant }

public sealed class VariableSymbol(string name, TaipanType type, VariableKind kind) : Symbol(name)
{
    public TaipanType Type { get; } = type;

    public VariableKind Kind { get; } = kind;
}

public enum BuiltinFunction { Print, Len, Str, Int, Float, Range }

/// <summary>A function, or a method when <see cref="Self"/> is set.</summary>
public sealed class FunctionSymbol(
    string name,
    ImmutableArray<VariableSymbol> parameters,
    TaipanType returnType,
    BuiltinFunction? builtin = null,
    VariableSymbol? self = null) : Symbol(name)
{
    /// <summary>For a method, its implicit <c>self</c> parameter, typed as the struct.</summary>
    public VariableSymbol? Self { get; } = self;

    public ImmutableArray<VariableSymbol> Parameters { get; } = parameters;

    /// <summary><see cref="TaipanType.Nothing"/> for a function without a return type.</summary>
    public TaipanType ReturnType { get; } = returnType;

    /// <summary>Set for built-in functions (SPEC §6), whose calls are checked specially.</summary>
    public BuiltinFunction? Builtin { get; } = builtin;

    public static ImmutableArray<FunctionSymbol> CreateBuiltins() =>
    [
        new("print", [], TaipanType.Nothing, BuiltinFunction.Print),
        new("len", [], TaipanType.Int, BuiltinFunction.Len),
        new("str", [], TaipanType.Str, BuiltinFunction.Str),
        new("int", [], TaipanType.Int, BuiltinFunction.Int),
        new("float", [], TaipanType.Float, BuiltinFunction.Float),
        new("range", [], TaipanType.Nothing, BuiltinFunction.Range),
    ];
}

public sealed class FieldSymbol(string name, TaipanType type) : Symbol(name)
{
    public TaipanType Type { get; } = type;
}

/// <summary>
/// A struct. It is also the struct's constructor in the value namespace (SPEC §4.6). Fields and
/// methods are filled in after every struct name is known, since they may refer to each other.
/// </summary>
public sealed class StructSymbol(string name) : Symbol(name)
{
    public ImmutableArray<FieldSymbol> Fields { get; set; } = [];

    public Dictionary<string, FunctionSymbol> Methods { get; } = [];

    public StructType Type => new(this);

    public FieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

/// <summary>Built-in methods of list, dict and str (SPEC §6).</summary>
public enum BuiltinMethod
{
    ListAppend, ListPop, ListInsert, ListContains,
    DictGet, DictKeys, DictValues, DictRemove,
    StrUpper, StrLower, StrStrip, StrSplit, StrStartsWith, StrEndsWith, StrReplace,
}
