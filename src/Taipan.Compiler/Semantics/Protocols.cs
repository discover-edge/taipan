namespace Taipan.Compiler.Semantics;

/// <summary>
/// Whether a type satisfies a protocol (SPEC §3.11), for bounds, declarations and, since M40,
/// protocols as types (§3.18), whose assignability is satisfaction.
/// </summary>
public static class Protocols
{
    // Satisfaction checks in progress, assumed to hold, so a protocol whose methods return
    // protocol types (or a class whose methods return itself) doesn't recurse forever.
    [ThreadStatic]
    private static HashSet<(TaipanType, ClassType)>? s_assumed;

    /// <summary>
    /// Why <paramref name="type"/> doesn't satisfy <paramref name="protocol"/>, naming the first of
    /// its fields or methods that's missing or doesn't fit; null if it does.
    /// </summary>
    public static string? Unsatisfied(TaipanType type, ClassType protocol)
    {
        if (type.ContainsError || protocol.ContainsError)
            return null;
        s_assumed ??= [];
        if (!s_assumed.Add((type, protocol)))
            return null;
        try
        {
            var required = Required(protocol, type);
            foreach (var field in protocol.Symbol.Fields)
            {
                var wanted = TypeChecker.Substitute(field.Type, required);
                if (FieldOf(type, field.Name) is not { } actual)
                    return $"it has no field '{field.Name}'";
                if (actual != wanted && !actual.ContainsError)
                    return $"'{field.Name}' must be '{field.Name}: {wanted}'";
            }
            foreach (var method in protocol.Symbol.Methods.Values)
            {
                if (MethodOf(type, method.Name) is not var (actual, map))
                    return $"it has no method '{method.Name}'";
                if (!Fits(method, required, actual, map, type))
                    return $"'{method.Name}' must be '{Signature(method, required)}'";
            }
            return null;
        }
        finally
        {
            s_assumed.Remove((type, protocol));
        }
    }

    /// <summary>The substitution for a protocol's members when <paramref name="self"/> is to satisfy it: its type arguments, and Self.</summary>
    public static Dictionary<TypeParameterSymbol, TaipanType> Required(ClassType protocol, TaipanType self)
    {
        var map = TypeChecker.Zip(protocol.Symbol.TypeParameters, protocol.TypeArguments);
        map[protocol.Symbol.SelfParameter!] = self;
        return map;
    }

    /// <summary>
    /// Whether a protocol can be the type of a value (SPEC §3.18): its methods use Self only as
    /// their whole result. Otherwise the name of the first method that doesn't.
    /// </summary>
    public static string? SelfProblem(ClassSymbol protocol)
    {
        var self = protocol.SelfParameter!;
        foreach (var method in protocol.Methods.Values)
        {
            var result = TypeChecker.Unwrap(method.ReturnType);
            if (method.Parameters.Any(p => Mentions(p.Type, self)) || result is not TypeParameterType && Mentions(result, self))
                return method.Name;
        }
        return null;
    }

    private static bool Mentions(TaipanType type, TypeParameterSymbol parameter) =>
        TypeChecker.Substitute(type, new Dictionary<TypeParameterSymbol, TaipanType> { [parameter] = TaipanType.Error }).ContainsError && !type.ContainsError;

    /// <summary>
    /// A method of a type, with the substitution its signature needs: a class's own or inherited
    /// instance method, a protocol type's method, a bounded type parameter's protocol or class
    /// method, or the built-in <c>__lt__</c> of an ordered type (SPEC §3.11).
    /// </summary>
    public static (FunctionSymbol Method, Dictionary<TypeParameterSymbol, TaipanType> Map)? MethodOf(TaipanType type, string name)
    {
        switch (type)
        {
            case ClassType { Symbol.IsProtocol: false } classType when classType.Symbol.FindMethod(name) is { Kind: MemberKind.Instance } method:
                return (method, TypeChecker.MapOf(classType));
            case ClassType { Symbol.IsProtocol: true } protocolType when protocolType.Symbol.Methods.GetValueOrDefault(name) is { } method:
                return (method, Required(protocolType, protocolType));
            case TypeParameterType { Symbol.Bound: { Symbol.IsProtocol: true } bound } parameter when bound.Symbol.Methods.GetValueOrDefault(name) is { } method:
                return (method, Required(bound, parameter));
            case TypeParameterType { Symbol.ClassBound: { } classBound }:
                return MethodOf(classBound, name);
            case var ordered when ordered.IsOrdered && name == "__lt__":
                var comparable = BuiltinClasses.Comparable;
                return (comparable.Methods[name], new() { [comparable.SelfParameter!] = ordered });
            default:
                return null;
        }
    }

    /// <summary>The type of a type's field <paramref name="name"/>: a class's, a protocol type's, or a bounded type parameter's.</summary>
    public static TaipanType? FieldOf(TaipanType type, string name)
    {
        switch (type)
        {
            case ClassType { Symbol.IsProtocol: false } classType when classType.Symbol.FindField(name) is { } field:
                return TypeChecker.Substitute(field.Type, TypeChecker.MapOf(classType));
            case ClassType { Symbol.IsProtocol: true } protocolType when protocolType.Symbol.Fields.FirstOrDefault(f => f.Name == name) is { } field:
                return TypeChecker.Substitute(field.Type, Required(protocolType, protocolType));
            case TypeParameterType { Symbol.Bound: { Symbol.IsProtocol: true } bound } parameter when bound.Symbol.Fields.FirstOrDefault(f => f.Name == name) is { } field:
                return TypeChecker.Substitute(field.Type, Required(bound, parameter));
            case TypeParameterType { Symbol.ClassBound: { } classBound }:
                return FieldOf(classBound, name);
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a method can stand in for a protocol's: the same parameter names, each taking the
    /// protocol's type or, for a class or protocol, one it's assignable to, and a result that's the
    /// same or, for a class or protocol, assignable to it.
    /// </summary>
    private static bool Fits(FunctionSymbol required, Dictionary<TypeParameterSymbol, TaipanType> self,
        FunctionSymbol method, Dictionary<TypeParameterSymbol, TaipanType> map, TaipanType receiver)
    {
        static bool IsClass(TaipanType type) => TypeChecker.Unwrap(type) is ClassType;
        if (method.Parameters.Length != required.Parameters.Length || !method.Kinds.SequenceEqual(required.Kinds))
            return false;
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var (expected, actual) = (TypeChecker.Substitute(required.Parameters[i].Type, self), TypeChecker.Substitute(method.Parameters[i].Type, map));
            if (method.Parameters[i].Name != required.Parameters[i].Name)
                return false;
            if (expected != actual && !actual.ContainsError && !(IsClass(expected) && IsClass(actual) && TypeChecker.IsAssignable(expected, actual)))
                return false;
        }
        var (result, actualResult) = (TypeChecker.Substitute(required.ReturnType, self), TypeChecker.SubstituteSelf(TypeChecker.Substitute(method.ReturnType, map), receiver));
        if (result is NothingType)
            return actualResult is NothingType;
        return result == actualResult || actualResult.ContainsError
            || IsClass(result) && IsClass(actualResult) && TypeChecker.IsAssignable(actualResult, result);
    }

    /// <summary>A protocol method's signature with <c>Self</c> replaced: <c>def scaled(self, k: float) -> Cube</c>.</summary>
    private static string Signature(FunctionSymbol method, Dictionary<TypeParameterSymbol, TaipanType> self)
    {
        var parameters = string.Concat(method.Parameters.Select(p => $", {p.Name}: {TypeChecker.Substitute(p.Type, self)}"));
        var result = method.ReturnType is NothingType ? "" : $" -> {TypeChecker.Substitute(method.ReturnType, self)}";
        return $"def {method.Name}(self{parameters}){result}";
    }
}
