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

public enum BuiltinFunction { Print, Len, Str, Int, Float, Range, Sorted, Map, Filter, Enumerate, Zip, IsInstance, Super }

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

    /// <summary>For a method, its implicit <c>self</c> parameter, typed as the class.</summary>
    public VariableSymbol? Self { get; } = self;

    public ImmutableArray<VariableSymbol> Parameters { get; } = parameters;

    /// <summary><see cref="TaipanType.Nothing"/> for a function without a return type.</summary>
    public TaipanType ReturnType { get; } = returnType;

    /// <summary>Set for built-in functions (SPEC §6), whose calls are checked specially.</summary>
    public BuiltinFunction? Builtin { get; } = builtin;

    /// <summary>
    /// Each parameter's default value (SPEC §4.2, M14), null where there's none; empty until bound.
    /// It's bound once, where the function is defined, and evaluated at each call that omits it.
    /// </summary>
    public ImmutableArray<BoundExpr?> Defaults
    {
        get => DefaultsFrom?.Defaults ?? _defaults;
        set => _defaults = value;
    }

    private ImmutableArray<BoundExpr?> _defaults = [];

    /// <summary>
    /// For the <c>__init__</c> of a class that doesn't write one, the base <c>__init__</c> it passes
    /// its arguments on to, whose defaults it shares (SPEC §4.3).
    /// </summary>
    public FunctionSymbol? DefaultsFrom { get; init; }

    /// <summary>Whether the method is marked <c>@override</c> (SPEC §4.3).</summary>
    public bool IsMarkedOverride { get; init; }

    /// <summary>The base class method this method overrides, once checked.</summary>
    public FunctionSymbol? Overrides { get; set; }

    /// <summary>For a method, the class it's declared in.</summary>
    public ClassSymbol? Owner => (Self?.Type as ClassType)?.Symbol;

    public BoundExpr? DefaultFor(int parameter) => parameter < Defaults.Length ? Defaults[parameter] : null;

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
        new("isinstance", [], TaipanType.Bool, BuiltinFunction.IsInstance),
        new("super", [], TaipanType.Error, BuiltinFunction.Super),
    ];
}

/// <summary>A class field or an enum variant's payload field.</summary>
public sealed class FieldSymbol(string name, TaipanType type) : Symbol(name)
{
    public TaipanType Type { get; } = type;

    /// <summary>The class that declares the field; null for a variant's field.</summary>
    public ClassSymbol? Owner { get; init; }

    /// <summary>Whether the field has a default (SPEC §4.3), which is bound into <see cref="Default"/> later.</summary>
    public bool HasDefault { get; init; }

    public BoundExpr? Default { get; set; }
}

/// <summary>
/// A class (SPEC §4.3). It is also the class's constructor in the value namespace (§4.6). The
/// base, fields and methods are filled in after every class name is known, since they may refer
/// to each other.
/// </summary>
public sealed class ClassSymbol(string name, ImmutableArray<TypeParameterSymbol> typeParameters, bool isDataclass) : Symbol(name)
{
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;

    public bool IsDataclass { get; } = isDataclass;

    /// <summary>For a built-in class (SPEC §3.10), the C# class in <c>Taipan.Runtime</c> that implements it.</summary>
    public string? RuntimeName { get; init; }

    /// <summary>
    /// For a protocol (SPEC §3.11), the type <c>Self</c> stands for in its methods' signatures;
    /// null for a class. A protocol has methods and nothing else.
    /// </summary>
    public TypeParameterSymbol? SelfParameter { get; init; }

    public bool IsProtocol => SelfParameter is not null;

    /// <summary>The protocols the class declares in its base list (SPEC §3.11), checked at its definition.</summary>
    public ImmutableArray<ClassSymbol> Protocols { get; set; } = [];

    /// <summary>The base class, if any; never part of a cycle once the declarations are checked.</summary>
    public ClassSymbol? Base { get; set; }

    /// <summary>Whether any class derives from this one.</summary>
    public bool HasSubclasses { get; set; }

    /// <summary>The fields the class itself declares. Their types are written in terms of <see cref="TypeParameters"/>.</summary>
    public ImmutableArray<FieldSymbol> Fields { get; set; } = [];

    /// <summary>Every field, inherited ones first: a dataclass's constructor parameters (SPEC §4.3).</summary>
    public ImmutableArray<FieldSymbol> AllFields => [.. Base?.AllFields ?? [], .. Fields];

    /// <summary>The methods the class itself declares, apart from <c>__init__</c>.</summary>
    public Dictionary<string, FunctionSymbol> Methods { get; } = [];

    /// <summary>A plain class's own <c>__init__</c>, if it writes one.</summary>
    public FunctionSymbol? Init { get; set; }

    /// <summary>
    /// The <c>__init__</c> construction calls (SPEC §4.3): the class's own, or else one that
    /// takes its base's parameters and passes them on, or takes none.
    /// </summary>
    public FunctionSymbol Constructor => Init ?? (_inherited ??= InheritedConstructor());

    private FunctionSymbol? _inherited;

    private FunctionSymbol InheritedConstructor()
    {
        var self = new VariableSymbol("self", Type, VariableKind.Parameter);
        if (Base?.Constructor is not { } baseInit)
            return new FunctionSymbol("__init__", [], TaipanType.Nothing, self: self);
        var parameters = baseInit.Parameters.Select(p => new VariableSymbol(p.Name, p.Type, VariableKind.Parameter)).ToImmutableArray();
        return new FunctionSymbol("__init__", parameters, TaipanType.Nothing, self: self) { DefaultsFrom = baseInit };
    }

    /// <summary>The class applied to its own type parameters: the type of <c>self</c>.</summary>
    public ClassType Type => new(this, [.. TypeParameters.Select(p => p.Type)]);

    public FieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name) ?? Base?.FindField(name);

    /// <summary>The method of that name, the class's own or inherited.</summary>
    public FunctionSymbol? FindMethod(string name) => Methods.GetValueOrDefault(name) ?? Base?.FindMethod(name);

    /// <summary>Whether this class is <paramref name="other"/> or derives from it.</summary>
    public bool DerivesFrom(ClassSymbol other) => this == other || Base?.DerivesFrom(other) == true;

    /// <summary>The nearest class both derive from, if any (SPEC §3.2).</summary>
    public ClassSymbol? CommonBase(ClassSymbol other)
    {
        for (var candidate = this; candidate is not null; candidate = candidate.Base)
        {
            if (other.DerivesFrom(candidate))
                return candidate;
        }
        return null;
    }
}

/// <summary>
/// The built-in exception classes (SPEC §3.10), in Python's hierarchy. They're shared by every
/// compilation and never change once built: they're always open to subclasses, and only
/// <c>Exception</c> declares fields and an <c>__init__</c>.
/// </summary>
public static class BuiltinClasses
{
    public static ClassSymbol Exception { get; } = CreateException();

    public static ClassSymbol PythonError { get; } = CreatePythonError();

    public static ImmutableArray<ClassSymbol> All { get; } = CreateAll();

    private static ClassSymbol CreateException()
    {
        var exception = Builtin("Exception", "TaipanException", null);
        var message = new FieldSymbol("message", TaipanType.Str) { Owner = exception };
        exception.Fields = [message];
        var self = new VariableSymbol("self", exception.Type, VariableKind.Parameter);
        exception.Init = new FunctionSymbol("__init__", [new VariableSymbol("message", TaipanType.Str, VariableKind.Parameter)], TaipanType.Nothing, self: self)
        {
            Defaults = [new BoundLiteral("", TaipanType.Str, default)],
        };
        return exception;
    }

    private static ClassSymbol CreatePythonError()
    {
        var error = Builtin("PythonError", "PythonError", Exception);
        error.Fields =
        [
            new FieldSymbol("type", TaipanType.Str) { Owner = error },
            new FieldSymbol("value", TaipanType.Py) { Owner = error },
        ];
        return error;
    }

    private static ImmutableArray<ClassSymbol> CreateAll()
    {
        var arithmetic = Builtin("ArithmeticError", Exception);
        var lookup = Builtin("LookupError", Exception);
        return
        [
            Exception,
            arithmetic,
            Builtin("ZeroDivisionError", arithmetic),
            Builtin("OverflowError", arithmetic),
            lookup,
            Builtin("IndexError", lookup),
            Builtin("KeyError", lookup),
            Builtin("ValueError", Exception),
            Builtin("TypeError", Exception),
            Builtin("RuntimeError", Exception),
            PythonError,
        ];
    }

    private static ClassSymbol Builtin(string name, ClassSymbol? baseClass) => Builtin(name, name, baseClass);

    private static ClassSymbol Builtin(string name, string runtimeName, ClassSymbol? baseClass) =>
        new(name, [], isDataclass: false) { RuntimeName = $"Taipan.Runtime.{runtimeName}", Base = baseClass, HasSubclasses = true };

    /// <summary>Whether <paramref name="symbol"/> is <c>Exception</c> or derives from it.</summary>
    public static bool IsException(ClassSymbol symbol) => symbol.DerivesFrom(Exception);

    /// <summary>The built-in protocol <c>Comparable</c>: <c>def __lt__(self, other: Self) -> bool</c> (SPEC §3.11).</summary>
    public static ClassSymbol Comparable { get; } = CreateComparable();

    private static ClassSymbol CreateComparable()
    {
        var comparable = Protocol("Comparable");
        var self = new VariableSymbol("self", comparable.Type, VariableKind.Parameter);
        var other = new VariableSymbol("other", comparable.SelfParameter!.Type, VariableKind.Parameter);
        comparable.Methods["__lt__"] = new FunctionSymbol("__lt__", [other], TaipanType.Bool, self: self);
        return comparable;
    }

    /// <summary>A protocol with no methods yet, and its own <c>Self</c>.</summary>
    public static ClassSymbol Protocol(string name) => new(name, [], isDataclass: false) { SelfParameter = new TypeParameterSymbol("Self") };
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

/// <summary>A type parameter of a generic function, class or enum (SPEC §3.6).</summary>
public sealed class TypeParameterSymbol(string name) : Symbol(name)
{
    public TypeParameterType Type => new(this);

    /// <summary>The types a constrained parameter can stand for (SPEC §3.6); empty if unconstrained.</summary>
    public ImmutableArray<TaipanType> Constraints { get; set; } = [];

    /// <summary>The protocol a bounded parameter's type arguments must satisfy (SPEC §3.11); null if unbounded.</summary>
    public ClassSymbol? Bound { get; set; }
}

/// <summary>
/// A type alias (SPEC §3.12, M18). <see cref="Target"/> is its right-hand side in terms of its own
/// type parameters, set once it's bound; it's an error type for an alias in a cycle.
/// </summary>
public sealed class TypeAliasSymbol(string name, ImmutableArray<TypeParameterSymbol> typeParameters) : Symbol(name)
{
    public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;

    public TaipanType? Target { get; set; }

    /// <summary>The class or enum the alias names, for help pointing at it, or null if it names another type.</summary>
    public string? TargetName => Target switch
    {
        ClassType classType => classType.Symbol.Name,
        EnumType enumType => enumType.Symbol.Name,
        _ => null,
    };
}

/// <summary>
/// A module bound by <c>import m</c> (SPEC §4.7). Its dictionaries hold only the names the module
/// itself defines, which are the names it exports.
/// </summary>
public sealed class ModuleSymbol(string name, IReadOnlyDictionary<string, Symbol> definitions,
    IReadOnlyDictionary<string, ClassSymbol> classes, IReadOnlyDictionary<string, EnumSymbol> enums) : Symbol(name)
{
    public IReadOnlyDictionary<string, Symbol> Definitions { get; } = definitions;

    public IReadOnlyDictionary<string, ClassSymbol> Classes { get; } = classes;

    public IReadOnlyDictionary<string, EnumSymbol> Enums { get; } = enums;
}
