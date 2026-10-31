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

    public bool IsError => this is ErrorType;

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
