using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

/// <summary>
/// The special methods a class may define (SPEC §4.8, M28), and how operators resolve to them.
/// Used by the binder to check and bind, and by the emitter to call the same methods.
/// </summary>
public static class SpecialMethods
{
    /// <summary>The methods that take one argument and give a fixed result, or any result when it's null.</summary>
    private static readonly Dictionary<string, (int Arity, TaipanType? Result)> Signatures = new()
    {
        ["__str__"] = (0, TaipanType.Str),
        ["__repr__"] = (0, TaipanType.Str),
        ["__eq__"] = (1, TaipanType.Bool),
        ["__hash__"] = (0, TaipanType.Int),
        ["__lt__"] = (1, TaipanType.Bool),
        ["__le__"] = (1, TaipanType.Bool),
        ["__gt__"] = (1, TaipanType.Bool),
        ["__ge__"] = (1, TaipanType.Bool),
        ["__add__"] = (1, null),
        ["__sub__"] = (1, null),
        ["__mul__"] = (1, null),
        ["__truediv__"] = (1, null),
        ["__floordiv__"] = (1, null),
        ["__mod__"] = (1, null),
        ["__radd__"] = (1, null),
        ["__rsub__"] = (1, null),
        ["__rmul__"] = (1, null),
        ["__rtruediv__"] = (1, null),
        ["__rfloordiv__"] = (1, null),
        ["__rmod__"] = (1, null),
        ["__neg__"] = (0, null),
        ["__iter__"] = (0, null),
        ["__next__"] = (0, null),
    };

    public static bool IsSupported(string name) => Signatures.ContainsKey(name);

    public static bool IsSpecialName(string name) =>
        name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);

    /// <summary>
    /// Whether a method has its special method's signature: the arity, no defaults, and the result,
    /// with <c>__eq__</c> taking the class or a base. Error types are let through.
    /// </summary>
    public static bool HasSignature(FunctionSymbol method, ClassSymbol owner, bool hasDefaults)
    {
        var (arity, result) = Signatures[method.Name];
        if (method.Parameters.Length != arity || hasDefaults)
            return false;
        if (method.ReturnType.ContainsError)
            return true;
        if (result is null ? method.ReturnType is NothingType : method.ReturnType != result)
            return false;
        // __iter__ gives an iterator (SPEC §3.13).
        if (method.Name == "__iter__" && method.ReturnType is not IteratorOfType)
            return false;
        return method.Name != "__eq__" || method.Parameters[0].Type is var other
            && (other.ContainsError || other is ClassType { Symbol: var c } && (other == owner.Type || owner.DerivesFrom(c) && c.TypeParameters.IsEmpty));
    }

    /// <summary>The signature a message shows: <c>def __eq__(self, other: Money) -> bool</c>.</summary>
    public static string Signature(string name, ClassSymbol owner)
    {
        var (arity, result) = Signatures[name];
        var other = name == "__eq__" ? owner.Type.ToString() : "T";
        var parameters = arity == 0 ? "" : $", other: {other}";
        var shown = name switch { "__iter__" => "Iterator[T]", "__next__" => "T", _ => result?.ToString() ?? "R" };
        return $"def {name}(self{parameters}) -> {shown}";
    }

    /// <summary>How a special method is reached, for the help when it's called directly.</summary>
    public static string Form(string name, string target) => name switch
    {
        "__str__" => $"str({target})",
        "__repr__" => $"repr({target})",
        "__hash__" => $"hash({target})",
        "__neg__" => "-",
        "__eq__" => "==",
        "__iter__" => $"iter({target})",
        "__next__" => $"next({target})",
        _ => Operator(name) ?? name,
    };

    private static string? Operator(string name)
    {
        var ops = Enum.GetValues<BinaryOp>();
        var op = ops.Where(o => Forward(o) == name).Concat(ops.Where(o => Reflected(o) == name && !IsComparison(o))).Cast<BinaryOp?>().FirstOrDefault();
        return op is { } found ? TypeChecker.Display(found) : null;
    }

    /// <summary>The method <c>a op b</c> calls on <c>a</c>: <c>__add__</c> for <c>+</c>, <c>__lt__</c> for <c>&lt;</c>.</summary>
    public static string? Forward(BinaryOp op) => op switch
    {
        BinaryOp.Add => "__add__",
        BinaryOp.Subtract => "__sub__",
        BinaryOp.Multiply => "__mul__",
        BinaryOp.Divide => "__truediv__",
        BinaryOp.FloorDivide => "__floordiv__",
        BinaryOp.Modulo => "__mod__",
        BinaryOp.Less => "__lt__",
        BinaryOp.LessEqual => "__le__",
        BinaryOp.Greater => "__gt__",
        BinaryOp.GreaterEqual => "__ge__",
        _ => null,
    };

    /// <summary>The method <c>a op b</c> calls on <c>b</c> instead: <c>__radd__</c> for <c>+</c>, <c>__gt__</c> for <c>&lt;</c>.</summary>
    public static string? Reflected(BinaryOp op) => op switch
    {
        BinaryOp.Add => "__radd__",
        BinaryOp.Subtract => "__rsub__",
        BinaryOp.Multiply => "__rmul__",
        BinaryOp.Divide => "__rtruediv__",
        BinaryOp.FloorDivide => "__rfloordiv__",
        BinaryOp.Modulo => "__rmod__",
        BinaryOp.Less => "__gt__",
        BinaryOp.LessEqual => "__ge__",
        BinaryOp.Greater => "__lt__",
        BinaryOp.GreaterEqual => "__le__",
        _ => null,
    };

    public static bool IsComparison(BinaryOp op) => op is BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual;

    /// <summary>
    /// A class's special method <paramref name="name"/>, own or inherited, with its parameter and
    /// result types as seen through <paramref name="type"/>'s type arguments.
    /// </summary>
    public static (FunctionSymbol Method, TaipanType? Parameter, TaipanType Result)? Method(TaipanType type, string? name)
    {
        if (name is null || type is not ClassType { Symbol: { IsProtocol: false } symbol } classType || symbol.FindMethod(name) is not { } method)
            return null;
        var map = TypeChecker.MapOf(classType);
        var parameter = method.Parameters.IsEmpty ? null : TypeChecker.Substitute(method.Parameters[0].Type, map);
        return (method, parameter, TypeChecker.Substitute(method.ReturnType, map));
    }

    /// <summary>
    /// The method <c>left op right</c> calls (SPEC §4.8): <c>left</c>'s forward method when it takes
    /// <c>right</c>, else <c>right</c>'s reflected one when it takes <c>left</c>.
    /// </summary>
    public static (FunctionSymbol Method, TaipanType Parameter, TaipanType Result, bool IsReflected)? Resolve(BinaryOp op, TaipanType left, TaipanType right)
    {
        if (Method(left, Forward(op)) is ({ } forward, { } parameter, var result) && Takes(parameter, right))
            return (forward, parameter, result, false);
        if (Method(right, Reflected(op)) is ({ } reflected, { } reflectedParameter, var reflectedResult) && Takes(reflectedParameter, left))
            return (reflected, reflectedParameter, reflectedResult, true);
        return null;
    }

    public static bool Takes(TaipanType parameter, TaipanType argument) =>
        argument.ContainsError || parameter.ContainsError || TypeChecker.IsAssignable(argument, parameter);
}
