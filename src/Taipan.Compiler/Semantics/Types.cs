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

    /// <summary>The type of <c>None</c>, assignable to every optional (SPEC §3.4).</summary>
    public static readonly TaipanType None = new NoneType();

    /// <summary>An open text file or standard stream (SPEC §6.1, M37).</summary>
    public static readonly TaipanType TextIO = new OpaqueType("TextIO", "Taipan.Runtime.TextFile");

    /// <summary><c>Self</c> in a class method's result (SPEC §4.9, M39): the type of what it's called on.</summary>
    public static readonly TaipanType Self = new SelfType();

    /// <summary><c>types.TracebackType</c> (SPEC §3.17, M37).</summary>
    public static readonly TaipanType Traceback = new OpaqueType("TracebackType", "Taipan.Runtime.Traceback");

    /// <summary><c>asyncio.Timeout</c>, <c>asyncio.timeout</c>'s async context manager (SPEC §3.20, M42).</summary>
    public static readonly TaipanType Timeout = new OpaqueType("Timeout", "Taipan.Runtime.Timeout");

    /// <summary><c>object</c>: any value, which only <c>StopIteration.value</c> gives (SPEC §3.20, M42).</summary>
    public static readonly TaipanType Object = new ObjectType();

    /// <summary>
    /// The alias this type was written with, as diagnostics show it (SPEC §3.18, M40): <c>Handler</c>,
    /// <c>Pair[int]</c>. It isn't part of the type, so equality ignores it.
    /// </summary>
    public string? Alias { get; init; }

    public virtual bool Equals(TaipanType? other) => other is not null && EqualityContract == other.EqualityContract;

    public override int GetHashCode() => EqualityContract.GetHashCode();

    public sealed override string ToString() => Alias ?? Describe();

    /// <summary>The type as written without an alias.</summary>
    protected abstract string Describe();

    public bool IsError => this is ErrorType;

    /// <summary>True for the error type or a collection type built from it, like <c>list[?]</c>.</summary>
    public bool ContainsError => this switch
    {
        ErrorType => true,
        ListOfType list => list.Element.ContainsError,
        VarTupleOfType varTuple => varTuple.Element.ContainsError,
        SetOfType set => set.Element.ContainsError,
        IteratorOfType iterator => iterator.Element.ContainsError,
        IterableOfType iterable => iterable.Element.ContainsError,
        GeneratorOfType generator => generator.Yielded.ContainsError || generator.Sent.ContainsError || generator.Returned.ContainsError,
        AsyncIteratorOfType iterator => iterator.Element.ContainsError,
        AsyncIterableOfType iterable => iterable.Element.ContainsError,
        AsyncGeneratorOfType generator => generator.Yielded.ContainsError || generator.Sent.ContainsError,
        AwaitableOfType awaitable => awaitable.Result.ContainsError,
        DictOfType dict => dict.Key.ContainsError || dict.Value.ContainsError,
        OptionalOf optional => optional.Inner.ContainsError,
        ClassType classType => classType.TypeArguments.Any(a => a.ContainsError),
        EnumType enumType => enumType.TypeArguments.Any(a => a.ContainsError),
        FunctionType function => function.Parameters.Any(p => p.ContainsError) || function.Result.ContainsError,
        TupleOfType tuple => tuple.Elements.Any(e => e.ContainsError),
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
        VarTupleOfType varTuple => varTuple.Element.IsConvertibleToPython,
        DictOfType dict => dict.Key.IsConvertibleToPython && dict.Value.IsConvertibleToPython,
        OptionalOf optional => optional.Inner.IsConvertibleToPython,
        TupleOfType tuple => tuple.Elements.All(e => e.IsConvertibleToPython),
        _ => false,
    };

    /// <summary>Whether <c>as</c> can convert a py value to this type (SPEC §10.4).</summary>
    public bool IsCastTarget => this switch
    {
        ErrorType or PrimitiveType => true,
        ListOfType list => list.Element.IsCastTarget,
        VarTupleOfType varTuple => varTuple.Element.IsCastTarget,
        DictOfType dict => dict.Key.IsCastTarget && dict.Value.IsCastTarget,
        OptionalOf optional => optional.Inner.IsCastTarget,
        TupleOfType tuple => tuple.Elements.All(e => e.IsCastTarget),
        _ => false,
    };

    /// <summary>True for a list, dict or optional with py somewhere inside (not py itself).</summary>
    public bool ContainsPy => this switch
    {
        ListOfType list => list.Element == Py || list.Element.ContainsPy,
        DictOfType dict => dict.Value == Py || dict.Value.ContainsPy,
        OptionalOf optional => optional.Inner == Py || optional.Inner.ContainsPy,
        ClassType s => s.TypeArguments.Any(a => a == Py || a.ContainsPy),
        EnumType e => e.TypeArguments.Any(a => a == Py || a.ContainsPy),
        TupleOfType tuple => tuple.Elements.Any(e => e == Py || e.ContainsPy),
        _ => false,
    };

    /// <summary>Whether <c>&lt;</c> orders values of this type: int, float, str, and tuples of those (SPEC §5.3).</summary>
    public bool IsOrdered => this == Int || this == Float || this == Str || this is TupleOfType { Elements: var elements } && elements.All(e => e.IsOrdered)
        || this is VarTupleOfType { Element.IsOrdered: true };
}

public sealed record PrimitiveType(string Name) : TaipanType
{
    protected override string Describe() => Name;
}

public sealed record NothingType : TaipanType
{
    protected override string Describe() => "nothing";
}

/// <summary>A runtime object with members the binder knows: <c>TextIO</c>, <c>TracebackType</c> (M37).</summary>
public sealed record OpaqueType(string Name, string RuntimeName) : TaipanType
{
    protected override string Describe() => Name;
}

/// <summary><c>type[C]</c>: a class as a value, which only <c>__exit__</c> receives (SPEC §3.17, M37).</summary>
public sealed record ClassOfType(ClassType Class) : TaipanType
{
    protected override string Describe() => $"type[{Class}]";
}

/// <summary>A class method's <c>Self</c> result (M39); replaced by the receiver's type at each call.</summary>
public sealed record SelfType : TaipanType
{
    protected override string Describe() => "Self";
}

public sealed record ErrorType : TaipanType
{
    protected override string Describe() => "?";
}

/// <summary><c>Iterator[T]</c>: a single-pass stream of values (SPEC §3.13, M31).</summary>
public sealed record IteratorOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"Iterator[{Element}]";
}

/// <summary>
/// <c>Generator[Y, S, R]</c> (SPEC §3.20, M42): a generator that yields <c>Y</c>s, receives <c>S</c>s
/// from <c>send</c>, and returns an <c>R</c>. Each may be <c>None</c>.
/// </summary>
public sealed record GeneratorOfType(TaipanType Yielded, TaipanType Sent, TaipanType Returned) : TaipanType
{
    protected override string Describe() => $"Generator[{Yielded}, {Sent}, {Returned}]";
}

/// <summary><c>AsyncIterator[T]</c>: what <c>async for</c> takes values from (SPEC §3.20, M42).</summary>
public sealed record AsyncIteratorOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"AsyncIterator[{Element}]";
}

/// <summary><c>AsyncIterable[T]</c>: anything <c>async for</c> goes over (SPEC §3.20, M42).</summary>
public sealed record AsyncIterableOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"AsyncIterable[{Element}]";
}

/// <summary><c>AsyncGenerator[Y, S]</c>: an async generator that yields <c>Y</c>s and receives <c>S</c>s (SPEC §3.20, M42).</summary>
public sealed record AsyncGeneratorOfType(TaipanType Yielded, TaipanType Sent) : TaipanType
{
    protected override string Describe() => $"AsyncGenerator[{Yielded}, {Sent}]";
}

/// <summary><c>object</c> (SPEC §3.20, M42): holds a value of any type, converted back with <c>as</c>.</summary>
public sealed record ObjectType : TaipanType
{
    protected override string Describe() => "object";
}

/// <summary><c>Awaitable[T]</c>: a value that will give a <c>T</c>, or nothing for <c>Awaitable[None]</c> (SPEC §3.14, M33).</summary>
public sealed record AwaitableOfType(TaipanType Result) : TaipanType
{
    protected override string Describe() => $"Awaitable[{(Result is NothingType ? "None" : Result)}]";
}

/// <summary><c>Iterable[T]</c>: anything a <c>for</c> loop goes over (SPEC §3.13, M31).</summary>
public sealed record IterableOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"Iterable[{Element}]";
}

public sealed record ListOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"list[{Element}]";
}

/// <summary><c>set[T]</c>: distinct values in insertion order (SPEC §3.15, M35).</summary>
public sealed record SetOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"set[{Element}]";
}

public sealed record DictOfType(TaipanType Key, TaipanType Value) : TaipanType
{
    protected override string Describe() => $"dict[{Key}, {Value}]";

    /// <summary>Dict keys are int, str, bool, tuples of keys, and hashable classes (SPEC §3.1, §4.8).</summary>
    public static bool IsValidKey(TaipanType type) =>
        type.IsError || type == Int || type == Str || type == Bool
        || type is ClassType { Symbol.IsHashable: true }
        || type is TupleOfType tuple && tuple.Elements.All(IsValidKey)
        || type is VarTupleOfType varTuple && IsValidKey(varTuple.Element)
        || type is TypeParameterType { Symbol.Constraints: [_, ..] constraints } && constraints.All(IsValidKey);
}

/// <summary>A class, with its type arguments if it's generic (SPEC §3.6). Equal when both match.</summary>
public sealed record ClassType(ClassSymbol Symbol, ImmutableArray<TaipanType> TypeArguments) : TaipanType
{
    /// <summary>A class that isn't generic, as named in <c>isinstance</c>, <c>as</c> and class patterns.</summary>
    public ClassType(ClassSymbol symbol) : this(symbol, [])
    {
    }

    public bool Equals(ClassType? other) =>
        other is not null && other.Symbol == Symbol && other.TypeArguments.SequenceEqual(TypeArguments);

    public override int GetHashCode() => Symbol.GetHashCode();

    protected override string Describe() => GenericName(Symbol.Name, TypeArguments);

    internal static string GenericName(string name, ImmutableArray<TaipanType> arguments) =>
        arguments.IsEmpty ? name : $"{name}[{string.Join(", ", arguments)}]";
}

public sealed record NoneType : TaipanType
{
    protected override string Describe() => "None";
}

/// <summary><c>T?</c>. <see cref="Inner"/> is never itself optional (SPEC §3.1).</summary>
public sealed record OptionalOf(TaipanType Inner) : TaipanType
{
    protected override string Describe() => $"{Inner}?";
}

/// <summary>An enum, with its type arguments if it's generic (SPEC §3.6). Equal when both match.</summary>
public sealed record EnumType(EnumSymbol Symbol, ImmutableArray<TaipanType> TypeArguments) : TaipanType
{
    public bool Equals(EnumType? other) =>
        other is not null && other.Symbol == Symbol && other.TypeArguments.SequenceEqual(TypeArguments);

    public override int GetHashCode() => Symbol.GetHashCode();

    protected override string Describe() => ClassType.GenericName(Symbol.Name, TypeArguments);
}

/// <summary>
/// <c>Callable[[A, B], R]</c> (SPEC §3.7). <see cref="Result"/> is <see cref="TaipanType.Nothing"/>
/// for a function that returns nothing, written <c>None</c>. Equal when every type matches.
/// </summary>
public sealed record FunctionType(ImmutableArray<TaipanType> Parameters, TaipanType Result) : TaipanType
{
    public bool Equals(FunctionType? other) =>
        other is not null && other.Parameters.SequenceEqual(Parameters) && other.Result == Result;

    public override int GetHashCode() => HashCode.Combine(Parameters.Length, Result);

    protected override string Describe() =>
        $"Callable[[{string.Join(", ", Parameters)}], {(Result is NothingType ? "None" : Result.ToString())}]";
}

/// <summary><c>tuple[A, B, …]</c>, with at least two elements (SPEC §3.8). Equal when every element matches.</summary>
public sealed record TupleOfType(ImmutableArray<TaipanType> Elements) : TaipanType
{
    public bool Equals(TupleOfType? other) => other is not null && other.Elements.SequenceEqual(Elements);

    public override int GetHashCode() => Elements.Length;

    protected override string Describe() => $"tuple[{string.Join(", ", Elements)}]";
}

/// <summary><c>tuple[T, ...]</c>: any number of <c>T</c>s, immutable (SPEC §3.19, M41).</summary>
public sealed record VarTupleOfType(TaipanType Element) : TaipanType
{
    protected override string Describe() => $"tuple[{Element}, ...]";
}

/// <summary>A type parameter inside a generic definition: an opaque type (SPEC §3.6).</summary>
public sealed record TypeParameterType(TypeParameterSymbol Symbol) : TaipanType
{
    protected override string Describe() => Symbol.Name;
}
