using System.Collections.Immutable;

namespace Taipan.Compiler.Semantics;

/// <summary>A Taipan type (SPEC §3). Displayed as written in source.</summary>
public abstract record TaipanType
{
    public static readonly TaipanType Int = new PrimitiveType("int");
    public static readonly TaipanType Float = new PrimitiveType("float");
    public static readonly TaipanType Bool = new PrimitiveType("bool");
    public static readonly TaipanType Str = new PrimitiveType("str");

    /// <summary>The "type" of a call to a function without a return type.</summary>
    public static readonly TaipanType Nothing = new NothingType();

    /// <summary>The type of an expression that already has an error; compatible with everything.</summary>
    public static readonly TaipanType Error = new ErrorType();

    /// <summary>A dynamic Python object (SPEC §10.2).</summary>
    public static readonly TaipanType Py = new PrimitiveType("py");

    /// <summary>The type of <c>none</c>, assignable to every optional (SPEC §3.4).</summary>
    public static readonly TaipanType None = new NoneType();

    public bool IsError => this is ErrorType;

    /// <summary>True for the error type or a collection type built from it, like <c>list[?]</c>.</summary>
    public bool ContainsError => this switch
    {
        ErrorType => true,
        ListOfType list => list.Element.ContainsError,
        DictOfType dict => dict.Key.ContainsError || dict.Value.ContainsError,
        OptionalOf optional => optional.Inner.ContainsError,
        StructType structType => structType.TypeArguments.Any(a => a.ContainsError),
        EnumType enumType => enumType.TypeArguments.Any(a => a.ContainsError),
        _ => false,
    };

    public bool IsNumeric => this == Int || this == Float;

    /// <summary>
    /// Whether values of this type can be passed to Python (SPEC §10.3): int, float, bool, str,
    /// none, py, and lists, dicts and optionals of convertible types.
    /// </summary>
    public bool IsConvertibleToPython => this switch
    {
        ErrorType or NoneType => true,
        PrimitiveType => true,
        ListOfType list => list.Element.IsConvertibleToPython,
        DictOfType dict => dict.Key.IsConvertibleToPython && dict.Value.IsConvertibleToPython,
        OptionalOf optional => optional.Inner.IsConvertibleToPython,
        _ => false,
    };

    /// <summary>Whether <c>as</c> can convert a py value to this type (SPEC §10.4).</summary>
    public bool IsCastTarget => this switch
    {
        ErrorType or PrimitiveType => true,
        ListOfType list => list.Element.IsCastTarget,
        DictOfType dict => dict.Key.IsCastTarget && dict.Value.IsCastTarget,
        OptionalOf optional => optional.Inner.IsCastTarget,
        _ => false,
    };

    /// <summary>True for a list, dict or optional with py somewhere inside (not py itself).</summary>
    public bool ContainsPy => this switch
    {
        ListOfType list => list.Element == Py || list.Element.ContainsPy,
        DictOfType dict => dict.Value == Py || dict.Value.ContainsPy,
        OptionalOf optional => optional.Inner == Py || optional.Inner.ContainsPy,
        StructType s => s.TypeArguments.Any(a => a == Py || a.ContainsPy),
        EnumType e => e.TypeArguments.Any(a => a == Py || a.ContainsPy),
        _ => false,
    };
}

public sealed record PrimitiveType(string Name) : TaipanType
{
    public override string ToString() => Name;
}

public sealed record NothingType : TaipanType
{
    public override string ToString() => "nothing";
}

public sealed record ErrorType : TaipanType
{
    public override string ToString() => "?";
}

public sealed record ListOfType(TaipanType Element) : TaipanType
{
    public override string ToString() => $"list[{Element}]";
}

public sealed record DictOfType(TaipanType Key, TaipanType Value) : TaipanType
{
    public override string ToString() => $"dict[{Key}, {Value}]";

    /// <summary>Dict keys are limited to int, str and bool (SPEC §3.1).</summary>
    public static bool IsValidKey(TaipanType type) =>
        type.IsError || type == Int || type == Str || type == Bool
        || type is TypeParameterType { Symbol.Constraints: [_, ..] constraints } && constraints.All(IsValidKey);
}

/// <summary>A struct, with its type arguments if it's generic (SPEC §3.6). Equal when both match.</summary>
public sealed record StructType(StructSymbol Symbol, ImmutableArray<TaipanType> TypeArguments) : TaipanType
{
    public bool Equals(StructType? other) =>
        other is not null && other.Symbol == Symbol && other.TypeArguments.SequenceEqual(TypeArguments);

    public override int GetHashCode() => Symbol.GetHashCode();

    public override string ToString() => GenericName(Symbol.Name, TypeArguments);

    internal static string GenericName(string name, ImmutableArray<TaipanType> arguments) =>
        arguments.IsEmpty ? name : $"{name}[{string.Join(", ", arguments)}]";
}

public sealed record NoneType : TaipanType
{
    public override string ToString() => "none";
}

/// <summary><c>T?</c>. <see cref="Inner"/> is never itself optional (SPEC §3.1).</summary>
public sealed record OptionalOf(TaipanType Inner) : TaipanType
{
    public override string ToString() => $"{Inner}?";
}

/// <summary>An enum, with its type arguments if it's generic (SPEC §3.6). Equal when both match.</summary>
public sealed record EnumType(EnumSymbol Symbol, ImmutableArray<TaipanType> TypeArguments) : TaipanType
{
    public bool Equals(EnumType? other) =>
        other is not null && other.Symbol == Symbol && other.TypeArguments.SequenceEqual(TypeArguments);

    public override int GetHashCode() => Symbol.GetHashCode();

    public override string ToString() => StructType.GenericName(Symbol.Name, TypeArguments);
}

/// <summary>A type parameter inside a generic definition: an opaque type (SPEC §3.6).</summary>
public sealed record TypeParameterType(TypeParameterSymbol Symbol) : TaipanType
{
    public override string ToString() => Symbol.Name;
}
