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
        _ => false,
    };

    public bool IsNumeric => this == Int || this == Float;
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
        type.IsError || type == Int || type == Str || type == Bool;
}

/// <summary>Equal when the symbols are the same struct.</summary>
public sealed record StructType(StructSymbol Symbol) : TaipanType
{
    public override string ToString() => Symbol.Name;
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

/// <summary>Equal when the symbols are the same enum.</summary>
public sealed record EnumType(EnumSymbol Symbol) : TaipanType
{
    public override string ToString() => Symbol.Name;
}
