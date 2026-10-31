using System.Collections.Immutable;

namespace Taipan.Compiler.Semantics;

// Symbols are classes, not records: two variables with the same name and type are still
// different symbols.

public abstract class Symbol(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary><see cref="Function"/> is a nested <c>def</c>'s name (SPEC §3.7).</summary>
public enum VariableKind { Let, Var, Parameter, LoopVariable, Constant, Function }

public sealed class VariableSymbol(string name, TaipanType type, VariableKind kind) : Symbol(name)
{
    public TaipanType Type { get; } = type;

    public VariableKind Kind { get; } = kind;
}

public enum BuiltinFunction { Print, Len, Str, Int, Float, Range, Sorted, Map, Filter, Enumerate, Zip }

/// <summary>A function, or a method when <see cref="Self"/> is set.</summary>
public sealed class FunctionSymbol(
    string name,
    ImmutableArray<VariableSymbol> parameters,
    TaipanType returnType,
    BuiltinFunction? builtin = null,
    VariableSymbol? self = null,
    ImmutableArray<TypeParameterSymbol> typeParameters = default) : Symbol(name)
{
    /// <summary>A generic function's type parameters (SPEC §3.6); empty otherwise.</summary>
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters.IsDefault ? [] : typeParameters;

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
        new("sorted", [], TaipanType.Error, BuiltinFunction.Sorted),
        new("map", [], TaipanType.Error, BuiltinFunction.Map),
        new("filter", [], TaipanType.Error, BuiltinFunction.Filter),
        new("enumerate", [], TaipanType.Error, BuiltinFunction.Enumerate),
        new("zip", [], TaipanType.Error, BuiltinFunction.Zip),
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
public sealed class StructSymbol(string name, ImmutableArray<TypeParameterSymbol> typeParameters) : Symbol(name)
{
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;

    /// <summary>Field and method types are written in terms of <see cref="TypeParameters"/>.</summary>
    public ImmutableArray<FieldSymbol> Fields { get; set; } = [];

    public Dictionary<string, FunctionSymbol> Methods { get; } = [];

    /// <summary>The struct applied to its own type parameters: the type of <c>self</c>.</summary>
    public StructType Type => new(this, [.. TypeParameters.Select(p => p.Type)]);

    public FieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

/// <summary>Built-in methods of list, dict and str (SPEC §6).</summary>
public enum BuiltinMethod
{
    ListAppend, ListPop, ListInsert, ListContains, ListSort,
    DictGet, DictKeys, DictValues, DictItems, DictRemove,
    StrUpper, StrLower, StrStrip, StrSplit, StrStartsWith, StrEndsWith, StrReplace,
}

/// <summary>An enum (SPEC §4.4). Variants are filled in once every type name is known.</summary>
public sealed class EnumSymbol(string name, ImmutableArray<TypeParameterSymbol> typeParameters) : Symbol(name)
{
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;

    public ImmutableArray<VariantSymbol> Variants { get; set; } = [];

    /// <summary>The enum applied to its own type parameters.</summary>
    public EnumType Type => new(this, [.. TypeParameters.Select(p => p.Type)]);

    public VariantSymbol? FindVariant(string name) => Variants.FirstOrDefault(v => v.Name == name);
}

/// <summary>An enum variant; <see cref="Fields"/> is empty for a variant without a payload.</summary>
public sealed class VariantSymbol(string name, EnumSymbol @enum) : Symbol(name)
{
    public EnumSymbol Enum { get; } = @enum;

    public ImmutableArray<FieldSymbol> Fields { get; set; } = [];
}

/// <summary>A variant name shared by several enums: only the qualified forms are valid (TP0216).</summary>
public sealed class AmbiguousVariantSymbol(string name, ImmutableArray<VariantSymbol> variants) : Symbol(name)
{
    public ImmutableArray<VariantSymbol> Variants { get; } = variants;
}

/// <summary>A type parameter of a generic function, struct or enum (SPEC §3.6).</summary>
public sealed class TypeParameterSymbol(string name) : Symbol(name)
{
    public TypeParameterType Type => new(this);

    /// <summary>The types a constrained parameter can stand for (SPEC §3.6); empty if unconstrained.</summary>
    public ImmutableArray<TaipanType> Constraints { get; set; } = [];
}

/// <summary>
/// A module bound by <c>import m</c> (SPEC §4.7). Its dictionaries hold only the names the module
/// itself defines, which are the names it exports.
/// </summary>
public sealed class ModuleSymbol(string name, IReadOnlyDictionary<string, Symbol> definitions,
    IReadOnlyDictionary<string, StructSymbol> structs, IReadOnlyDictionary<string, EnumSymbol> enums) : Symbol(name)
{
    public IReadOnlyDictionary<string, Symbol> Definitions { get; } = definitions;

    public IReadOnlyDictionary<string, StructSymbol> Structs { get; } = structs;

    public IReadOnlyDictionary<string, EnumSymbol> Enums { get; } = enums;
}
