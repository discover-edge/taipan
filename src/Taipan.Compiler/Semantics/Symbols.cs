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

public enum BuiltinFunction { Print, Len, Str, Int, Float, Range, Sorted, Map, Filter, Enumerate, Zip, IsInstance, Super, Repr, Hash, List, Iter, Next }

/// <summary>What kind of method a function is (SPEC §4.8, M28); <see cref="Instance"/> for a function.</summary>
public enum MemberKind { Instance, Property, Static, Class }

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
    public ClassSymbol? Owner => DeclaredIn ?? (Self?.Type as ClassType)?.Symbol;

    /// <summary>The class a static or class method is declared in, since it has no <c>self</c> (SPEC §4.8).</summary>
    public ClassSymbol? DeclaredIn { get; init; }

    public MemberKind Kind { get; init; }

    /// <summary>A class method's <c>cls</c> parameter (SPEC §4.8).</summary>
    public VariableSymbol? Cls { get; init; }

    /// <summary>A property's setter, once bound (SPEC §4.8).</summary>
    public FunctionSymbol? Setter { get; set; }

    /// <summary>For a setter, its property.</summary>
    public FunctionSymbol? SetterOf { get; set; }

    /// <summary>Whether the function's body yields, which makes it a generator (SPEC §3.13, M31).</summary>
    public bool IsGenerator { get; init; }

    /// <summary>Whether the method is marked <c>@abstractmethod</c> (SPEC §4.8).</summary>
    public bool IsAbstract { get; init; }

    /// <summary>How each parameter takes its argument (SPEC §4.2, M30): all ordinary unless the signature says otherwise.</summary>
    public ImmutableArray<Syntax.ParamKind> Kinds
    {
        get => _kinds.IsDefault || _kinds.Length != Parameters.Length ? [.. Parameters.Select(_ => Syntax.ParamKind.Normal)] : _kinds;
        init => _kinds = value;
    }

    private readonly ImmutableArray<Syntax.ParamKind> _kinds;

    /// <summary>The index of the parameter of a kind, or -1.</summary>
    public int IndexOf(Syntax.ParamKind kind) => Kinds.IndexOf(kind);

    /// <summary>Whether a call can give the function a varying number of arguments, or keywords it doesn't name.</summary>
    public bool IsVariadic => Kinds.Contains(Syntax.ParamKind.VarArgs) || Kinds.Contains(Syntax.ParamKind.VarKeywords);

    /// <summary>Whether a class method's body calls <c>cls(…)</c>, so every subclass must be constructed alike (SPEC §4.8).</summary>
    public bool CallsCls { get; set; }

    /// <summary>Whether the method is reached through a class, not an instance: a static or class method.</summary>
    public bool IsClassLevel => Kind is MemberKind.Static or MemberKind.Class;

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
        new("repr", [], TaipanType.Str, BuiltinFunction.Repr),
        new("hash", [], TaipanType.Int, BuiltinFunction.Hash),
        new("list", [], TaipanType.Error, BuiltinFunction.List),
        new("iter", [], TaipanType.Error, BuiltinFunction.Iter),
        new("next", [], TaipanType.Error, BuiltinFunction.Next),
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

/// <summary>A class attribute, <c>count: ClassVar[int] = 0</c> (SPEC §4.8, M28): one value, shared by the class and its subclasses.</summary>
public sealed class ClassAttributeSymbol(string name, TaipanType type, ClassSymbol owner) : Symbol(name)
{
    public TaipanType Type { get; } = type;

    public ClassSymbol Owner { get; } = owner;

    /// <summary>Its value, once bound.</summary>
    public BoundExpr? Value { get; set; }
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

    /// <summary>The methods the class itself declares, apart from <c>__init__</c>; properties and static and class methods too.</summary>
    public Dictionary<string, FunctionSymbol> Methods { get; } = [];

    /// <summary>The class attributes the class itself declares (SPEC §4.8).</summary>
    public ImmutableArray<ClassAttributeSymbol> ClassAttributes { get; set; } = [];

    /// <summary>Whether <c>ABC</c> is in the class's base list (SPEC §4.8).</summary>
    public bool IsAbc { get; set; }

    /// <summary>Whether the class derives from <c>ABC</c>, directly or through its bases, so it may declare abstract methods.</summary>
    public bool DerivesFromAbc => IsAbc || Base?.DerivesFromAbc == true;

    /// <summary>The abstract methods no class from this one up to their own overrides, own and inherited (SPEC §4.8).</summary>
    public IEnumerable<FunctionSymbol> AbstractMethods
    {
        get
        {
            var seen = new HashSet<string>();
            for (var c = this; c is not null; c = c.Base)
            {
                foreach (var method in c.Methods.Values.Where(m => seen.Add(m.Name)).Where(m => m.IsAbstract))
                    yield return method;
            }
        }
    }

    public bool IsAbstract => AbstractMethods.Any();

    /// <summary>
    /// Whether instances can be dict keys (SPEC §4.8): a class whose body defines <c>__eq__</c>
    /// without <c>__hash__</c> isn't, a dataclass is only if its body defines <c>__hash__</c>, and
    /// any other class is as its base is, so a plain class is by identity.
    /// </summary>
    public bool IsHashable =>
        Methods.ContainsKey("__hash__") || !IsDataclass && !Methods.ContainsKey("__eq__") && Base?.IsHashable != false;

    /// <summary>The classes that derive directly from this one.</summary>
    public List<ClassSymbol> Subclasses { get; } = [];

    /// <summary>Whether this class has one of these methods, its own or inherited, or a class derived from it defines one.</summary>
    public bool MayDefine(IReadOnlyCollection<string> methods) =>
        methods.Any(m => FindMethod(m) is not null) || Subclasses.Any(s => s.MayDefine(methods));

    public ClassAttributeSymbol? FindClassAttribute(string name) => ClassAttributes.FirstOrDefault(a => a.Name == name) ?? Base?.FindClassAttribute(name);

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
        return new FunctionSymbol("__init__", parameters, TaipanType.Nothing, self: self) { DefaultsFrom = baseInit, Kinds = baseInit.Kinds };
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

    public static ClassSymbol DotNetError { get; } = CreateDotNetError();

    public static ClassSymbol OverflowError => All.First(c => c.Name == "OverflowError");

    public static ImmutableArray<ClassSymbol> All { get; } = CreateAll();

    /// <summary>Whether only the runtime makes exceptions of this class, so it can't be constructed or derived from.</summary>
    public static bool IsRuntimeOnly(ClassSymbol symbol) => symbol == PythonError || symbol == DotNetError;

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

    private static ClassSymbol CreateDotNetError()
    {
        var error = Builtin("DotNetError", "DotNetError", Exception);
        error.Fields = [new FieldSymbol("type", TaipanType.Str) { Owner = error }];
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
            Builtin("StopIteration", Exception),
            PythonError,
            DotNetError,
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

/// <summary>A .NET type imported with <c>from dotnet.&lt;namespace&gt; import</c> (SPEC §13.1), under the name it's bound to.</summary>
public sealed class DotNetTypeSymbol(string name, Microsoft.CodeAnalysis.INamedTypeSymbol type) : Symbol(name)
{
    public Microsoft.CodeAnalysis.INamedTypeSymbol Type { get; } = type;
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
