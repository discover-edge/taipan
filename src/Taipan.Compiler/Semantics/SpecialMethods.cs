using System.Collections.Immutable;
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
        ["__pow__"] = (1, null),
        ["__radd__"] = (1, null),
        ["__rsub__"] = (1, null),
        ["__rmul__"] = (1, null),
        ["__rtruediv__"] = (1, null),
        ["__rfloordiv__"] = (1, null),
        ["__rmod__"] = (1, null),
        ["__rpow__"] = (1, null),
        ["__neg__"] = (0, null),
        ["__iter__"] = (0, null),
        ["__next__"] = (0, null),
        ["__enter__"] = (0, null),
        ["__post_init__"] = (0, TaipanType.Nothing),
        // M39 (SPEC §4.9); __call__ takes any parameters, checked separately.
        ["__len__"] = (0, TaipanType.Int),
        ["__getitem__"] = (1, null),
        ["__setitem__"] = (2, TaipanType.Nothing),
        ["__contains__"] = (1, TaipanType.Bool),
        ["__call__"] = (-1, null),
        ["__iadd__"] = (1, null),
        ["__isub__"] = (1, null),
        ["__imul__"] = (1, null),
        ["__itruediv__"] = (1, null),
        ["__ifloordiv__"] = (1, null),
        ["__imod__"] = (1, null),
        ["__ipow__"] = (1, null),
        ["__format__"] = (1, TaipanType.Str),
        ["__exit__"] = (3, null),
        // M42 (SPEC §3.20): each but __aiter__ is an async def.
        ["__aiter__"] = (0, null),
        ["__anext__"] = (0, null),
        ["__aenter__"] = (0, null),
        ["__aexit__"] = (3, null),
    };

    private static bool IsAsyncSpecial(string name) => name is "__anext__" or "__aenter__" or "__aexit__";

    /// <summary><c>__exit__</c>'s parameter types (SPEC §3.17, M37).</summary>
    public static ImmutableArray<TaipanType> ExitParameters { get; } =
    [
        new OptionalOf(new ClassOfType(BuiltinClasses.BaseException.Type)),
        new OptionalOf(BuiltinClasses.BaseException.Type),
        new OptionalOf(TaipanType.Traceback),
    ];

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
        // __call__ takes what any method may, defaults and keywords included (SPEC §4.9).
        if (method.Name == "__call__")
            return method.ReturnType is not NothingType || method.ReturnType.ContainsError;
        if (method.Parameters.Length != arity || hasDefaults)
            return false;
        if (IsAsyncSpecial(method.Name) != method.IsAsync)
            return false;
        // An async special method's result is what awaiting it gives (SPEC §3.20).
        var returned = method.IsAsync && method.ReturnType is AwaitableOfType awaitable ? awaitable.Result : method.ReturnType;
        if (method.Name == "__format__" && method.Parameters[0].Type != TaipanType.Str && !method.Parameters[0].Type.ContainsError)
            return false;
        // __exit__ takes exactly Python's three optionals, and suppresses only when it returns bool (SPEC §3.17).
        if (method.Name is "__exit__" or "__aexit__")
            return method.Parameters.Select(p => p.Type).Zip(ExitParameters).All(pair => pair.First.ContainsError || pair.First == pair.Second)
                && (returned.ContainsError || returned is NothingType || returned == TaipanType.Bool);
        if (method.Name == "__aiter__")
            return method.ReturnType.ContainsError || method.ReturnType is AsyncIteratorOfType;
        if (method.ReturnType.ContainsError)
            return true;
        if (result is null ? returned is NothingType : returned != result)
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
        switch (name)
        {
            case "__post_init__":
                return "def __post_init__(self) -> None";
            case "__getitem__":
                return "def __getitem__(self, key: K) -> V";
            case "__setitem__":
                return "def __setitem__(self, key: K, value: V) -> None";
            case "__contains__":
                return "def __contains__(self, item: T) -> bool";
            case "__call__":
                return "def __call__(self, …) -> R";
            case "__format__":
                return "def __format__(self, spec: str) -> str";
        }
        if (name is "__exit__" or "__aexit__")
            return $"{(name == "__aexit__" ? "async " : "")}def {name}(self, exc_type: type[BaseException]?, exc: BaseException?, tb: TracebackType?) -> bool";
        var (arity, result) = Signatures[name];
        var other = name == "__eq__" ? owner.Type.ToString() : "T";
        var parameters = arity == 0 ? "" : $", other: {other}";
        var shown = name switch { "__iter__" => "Iterator[T]", "__aiter__" => "AsyncIterator[T]", "__next__" or "__anext__" or "__aenter__" => "T", _ => result?.ToString() ?? "R" };
        return $"{(IsAsyncSpecial(name) ? "async " : "")}def {name}(self{parameters}) -> {shown}";
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
        "__enter__" or "__exit__" => $"with {target}:",
        "__aiter__" => $"async for … in {target}:",
        "__anext__" => $"anext({target})",
        "__aenter__" or "__aexit__" => $"async with {target}:",
        "__len__" => $"len({target})",
        "__getitem__" => $"{target}[…]",
        "__setitem__" => $"{target}[…] = …",
        "__contains__" => "in",
        "__call__" => $"{target}(…)",
        "__format__" => $"f\"{{{target}:…}}\"",
        _ when InPlaceOperator(name) is { } op => op,
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
        BinaryOp.Power => "__pow__",
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
        BinaryOp.Power => "__rpow__",
        BinaryOp.Less => "__gt__",
        BinaryOp.LessEqual => "__ge__",
        BinaryOp.Greater => "__lt__",
        BinaryOp.GreaterEqual => "__le__",
        _ => null,
    };

    /// <summary>The in-place method <c>x op= y</c> calls first (SPEC §4.9, M39): <c>__iadd__</c> for <c>+=</c>.</summary>
    public static string? InPlace(Syntax.AssignOp op) => op switch
    {
        Syntax.AssignOp.Add => "__iadd__",
        Syntax.AssignOp.Subtract => "__isub__",
        Syntax.AssignOp.Multiply => "__imul__",
        Syntax.AssignOp.Divide => "__itruediv__",
        Syntax.AssignOp.FloorDivide => "__ifloordiv__",
        Syntax.AssignOp.Modulo => "__imod__",
        Syntax.AssignOp.Power => "__ipow__",
        _ => null,
    };

    private static string? InPlaceOperator(string name) => name switch
    {
        "__iadd__" => "+=",
        "__isub__" => "-=",
        "__imul__" => "*=",
        "__itruediv__" => "/=",
        "__ifloordiv__" => "//=",
        "__imod__" => "%=",
        "__ipow__" => "**=",
        _ => null,
    };

    /// <summary>
    /// A class value as a function (SPEC §4.9): its <c>__call__</c>'s parameters and result, when
    /// they're all ordinary positional ones; null otherwise.
    /// </summary>
    public static FunctionType? CallShape(TaipanType type)
    {
        if (Method(type, "__call__") is not var (method, _, result) || method.Kinds.Any(k => k != Syntax.ParamKind.Normal && k != Syntax.ParamKind.PositionalOnly))
            return null;
        var map = TypeChecker.MapOf(type);
        return new FunctionType([.. method.Parameters.Select(p => TypeChecker.Substitute(p.Type, map))], result);
    }

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
        return (method, parameter, TypeChecker.SubstituteSelf(TypeChecker.Substitute(method.ReturnType, map), type));
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
