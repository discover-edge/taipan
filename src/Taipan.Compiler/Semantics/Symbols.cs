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

public sealed class FunctionSymbol(
    string name,
    ImmutableArray<VariableSymbol> parameters,
    TaipanType returnType,
    BuiltinFunction? builtin = null) : Symbol(name)
{
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
