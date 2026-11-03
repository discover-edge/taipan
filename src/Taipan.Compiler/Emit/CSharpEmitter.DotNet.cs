using Microsoft.CodeAnalysis;
using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// .NET interop (SPEC §13, M21): calls by their full names, with arguments and results converted
// at the boundary as §13.3 says.
public sealed partial class CSharpEmitter
{
    private static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private string DotNetCall(BoundDotNetCall call)
    {
        var method = call.Method;
        var receiver = call.Target is null ? FullName(method.ContainingType) : Operand(call.Target);
        // A generic method's type arguments are given, as Taipan inferred them (SPEC §13.8).
        var typeArguments = call.TypeArguments is { } inferred
            ? $"<{string.Join(", ", method.TypeParameters.Select(p => TypeName(Concrete(inferred[p]))))}>"
            : "";
        var code = $"{receiver}.@{method.Name}{typeArguments}({DotNetArguments(call.Arguments, call.TypeArguments)})";
        return method.ReturnsVoid ? code : FromDotNet(code, DotNetTypes.ResultOf(method), call.Type, Who(method), call.TypeArguments);
    }

    // An indexer's arguments in order: C# can't name them.
    private string IndexArguments(BoundDotNetIndex index) => string.Join(", ", index.Arguments.OrderBy(a => a.Parameter.Ordinal).Select(a =>
        ToDotNet(Expr(a.Value), Concrete(a.Value.Type), DotNetTypes.TypeOf(a.Parameter))));

    private string DotNetIndex(BoundDotNetIndex index) =>
        FromDotNet($"{Operand(index.Target)}[{IndexArguments(index)}]", DotNetTypes.TypeOf(index.Indexer), index.Type, $"{new DotNetType(index.Indexer.ContainingType)}[…]");

    private string DotNetIndexAssignment(BoundDotNetIndexAssign assign) =>
        $"{Operand(assign.Index.Target)}[{IndexArguments(assign.Index)}] = {ToDotNet(Expr(assign.Value), Concrete(assign.Value.Type), DotNetTypes.TypeOf(assign.Index.Indexer))}";

    // The operands are cast to the operator's parameter types, so C# picks the one the binder chose.
    private string DotNetOperator(BoundDotNetOperator op)
    {
        var operands = op.Arguments.OrderBy(a => a.Parameter.Ordinal).Select(a =>
            $"({FullName(a.Parameter.Type)})({ToDotNet(Expr(a.Value), Concrete(a.Value.Type), DotNetTypes.TypeOf(a.Parameter))})").ToList();
        var code = operands.Count == 1 ? $"({op.Op}{operands[0]})" : $"({operands[0]} {op.Op} {operands[1]})";
        return FromDotNet(code, DotNetTypes.ResultOf(op.Method), op.Type, Who(op.Method));
    }

    private string DotNetNew(BoundDotNetNew created) => $"new {TypeName(created.Created)}({DotNetArguments(created.Arguments)})";

    private string DotNetMember(BoundDotNetMember member)
    {
        var receiver = member.Target is null ? FullName(member.Member.ContainingType) : Operand(member.Target);
        return FromDotNet($"{receiver}.@{member.Member.Name}", DotNetTypes.TypeOf(member.Member), member.Type, Who(member.Member));
    }

    private string DotNetAssignment(BoundDotNetAssign assign)
    {
        var receiver = assign.Target is null ? FullName(assign.Member.ContainingType) : Operand(assign.Target);
        return $"{receiver}.@{assign.Member.Name} = {ToDotNet(Expr(assign.Value), Concrete(assign.Value.Type), DotNetTypes.TypeOf(assign.Member))}";
    }

    // A caught exception as .NET sees it: a DotNetError raised again by `raise e` is its .NET exception (SPEC §13.6).
    private static string DotNetException(string caught) => $"{Runtime}.Exceptions.DotNet({caught})";

    private static string Who(ISymbol member) => $"{new DotNetType(member.ContainingType)}.{member.Name}";

    // Every argument is named, so a parameter with a default can be left out before a later one.
    private string DotNetArguments(IEnumerable<BoundDotNetArgument> arguments, TypeArguments? typeArguments = null) => string.Join(", ", arguments.Select(a =>
        $"@{a.Parameter.Name}: {ToDotNet(Expr(a.Value), Concrete(a.Value.Type), DotNetTypes.TypeOf(a.Parameter), typeArguments)}"));

    private static readonly SymbolDisplayFormat GenericName = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    /// <summary>A .NET type as C# writes it, with a generic method's type parameters as their Taipan types' C# forms.</summary>
    private string NetName(ITypeSymbol type, TypeArguments? typeArguments) => type switch
    {
        ITypeParameterSymbol parameter when typeArguments?.GetValueOrDefault(parameter) is { } argument => TypeName(Concrete(argument)),
        IArrayTypeSymbol array => $"{NetName(array.ElementType, typeArguments)}[]",
        INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable => $"{NetName(nullable.TypeArguments[0], typeArguments)}?",
        INamedTypeSymbol { IsGenericType: true } named =>
            $"{named.ToDisplayString(GenericName)}<{string.Join(", ", named.TypeArguments.Select(a => NetName(a, typeArguments)))}>",
        _ => FullName(type),
    };

    /// <summary>
    /// A Taipan value as a .NET argument. Anything whose C# type isn't the parameter's exactly is
    /// cast to it, so C# picks the overload the binder chose.
    /// </summary>
    private string ToDotNet(string code, TaipanType argument, ITypeSymbol parameter, TypeArguments? typeArguments = null, string who = "a delegate")
    {
        if (argument is NoneType)
            return $"({NetName(parameter, typeArguments)})null";
        var target = parameter is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : parameter;
        // A type parameter stands for the value's own type (SPEC §13.8).
        if (target is ITypeParameterSymbol)
            return code;
        switch (target.SpecialType)
        {
            case SpecialType.System_Int64 or SpecialType.System_Boolean or SpecialType.System_String:
                return code;
            case SpecialType.System_Double:
                return argument == TaipanType.Float ? code : $"({FullName(parameter)})({code})";
            case SpecialType.System_Single:
                return $"({FullName(parameter)})({code})";
            case SpecialType.System_Char:
                return $"{Runtime}.DotNet.{(argument is OptionalOf ? "CharOrNull" : "Char")}({code})";
            case SpecialType.System_Object:
                return $"(object?)({code})";
            case var _ when DotNetTypes.IsNarrowInteger(target):
                return $"({FullName(parameter)})({code})";
        }

        var unwrapped = TypeChecker.Unwrap(argument);
        var element = unwrapped is ListOfType list ? list.Element : null;
        if (target is IArrayTypeSymbol array && element is not null)
            return ElementConversion(array.ElementType, element, who: null, typeArguments) is { } convert
                ? $"{Runtime}.DotNet.ToArray<{TypeName(element)}, {NetName(array.ElementType, typeArguments)}>({code}, {convert})"
                : $"{Runtime}.DotNet.ToArray({code})";
        if (target is INamedTypeSymbol named && DotNetTypes.IsListLike(named) && element is not null)
        {
            var netElement = named.TypeArguments[0];
            if (ElementConversion(netElement, element, who: null, typeArguments) is { } convert)
                return $"{Runtime}.DotNet.ToList<{TypeName(element)}, {NetName(netElement, typeArguments)}>({code}, {convert})";
            return named.Name == "List" ? code : $"({NetName(parameter, typeArguments)})({code})";
        }
        if (target is INamedTypeSymbol dictionary && DotNetTypes.IsDictionary(dictionary) && unwrapped is DictOfType dict)
        {
            // A Dictionary is a copy; an interface takes the dict itself unless its keys or values convert (SPEC §13.3).
            var (netKey, netValue) = (dictionary.TypeArguments[0], dictionary.TypeArguments[1]);
            var keys = ElementConversion(netKey, dict.Key, who: null, typeArguments);
            var values = ElementConversion(netValue, dict.Value, who: null, typeArguments);
            if (keys is null && values is null)
                return dictionary.Name == "Dictionary" ? $"{Runtime}.DotNet.ToDictionary({code})" : $"({NetName(parameter, typeArguments)})({code})";
            return $"{Runtime}.DotNet.ToDictionary<{TypeName(dict.Key)}, {TypeName(dict.Value)}, {NetName(netKey, typeArguments)}, {NetName(netValue, typeArguments)}>"
                + $"({code}, {keys ?? "static k => k"}, {values ?? "static v => v"})";
        }
        if (target is INamedTypeSymbol task && DotNetTypes.IsTask(task))
            return task.Name == "ValueTask" ? $"new {NetName(task, typeArguments)}({code})" : code;
        if (target is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } callable && unwrapped is FunctionType function)
        {
            // A delegate that calls the function, converting what .NET passes and what it returns (SPEC §13.9).
            var f = $"@{_locals.Fresh("f")}";
            var parameters = invoke.Parameters.Select(p => (Name: $"@{_locals.Fresh("a")}", Parameter: p)).ToList();
            var call = $"{f}.Invoke({string.Join(", ", parameters.Select((p, i) => FromDotNet(p.Name, DotNetTypes.TypeOf(p.Parameter), function.Parameters[i], who, typeArguments)))})";
            var body = invoke.ReturnsVoid ? $"{{ {call}; }}" : ToDotNet(call, function.Result, DotNetTypes.ResultOf(invoke), typeArguments, who);
            var lambda = $"({string.Join(", ", parameters.Select(p => $"{NetName(p.Parameter.Type, typeArguments)} {p.Name}"))}) => {body}";
            return $"{Runtime}.DotNet.With({code}, {f} => new {NetName(callable, typeArguments)}({lambda}))";
        }
        return target.TypeKind == TypeKind.Interface ? $"({NetName(parameter, typeArguments)})({code})" : code;
    }

    /// <summary>A .NET result as a Taipan value: widened, copied into a list or dict, or checked for null (SPEC §13.3).</summary>
    private string FromDotNet(string code, ITypeSymbol type, TaipanType mapped, string who, TypeArguments? typeArguments = null)
    {
        if (type is ITypeParameterSymbol)
            return code;
        if (type.IsReferenceType && mapped is not OptionalOf)
            code = $"{Runtime}.DotNet.NotNull({code}, \"{who}\")";
        var nullable = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
        var target = nullable ? ((INamedTypeSymbol)type).TypeArguments[0] : type;
        var optional = nullable ? "?" : "";
        switch (target.SpecialType)
        {
            case SpecialType.System_Single:
                return $"(double{optional})({code})";
            case SpecialType.System_Char:
                return nullable ? $"{Runtime}.DotNet.Text({code})" : $"({code}).ToString()";
            case var _ when DotNetTypes.IsNarrowInteger(target):
                return $"(long{optional})({code})";
        }

        var unwrapped = TypeChecker.Unwrap(mapped);
        if (target is INamedTypeSymbol task && DotNetTypes.IsTask(task))
        {
            // A ValueTask is awaited as a Task, and a task's result converts when it's done (SPEC §13.11).
            if (task.Name == "ValueTask")
                code = $"({code}).AsTask()";
            if (task.TypeArguments.IsEmpty || unwrapped is not AwaitableOfType awaitable)
                return code;
            return ElementConversion(task.TypeArguments[0], awaitable.Result, who, typeArguments) is { } converted
                ? $"{Runtime}.DotNet.MapTask({code}, {converted})"
                : code;
        }
        if (target is INamedTypeSymbol dictionary && DotNetTypes.IsDictionary(dictionary) && unwrapped is DictOfType dict)
        {
            var keys = ElementConversion(dictionary.TypeArguments[0], dict.Key, who, typeArguments);
            var values = ElementConversion(dictionary.TypeArguments[1], dict.Value, who, typeArguments);
            return keys is null && values is null
                ? $"{Runtime}.DotNet.ToDict({code})"
                : $"{Runtime}.DotNet.ToDict<{NetName(dictionary.TypeArguments[0], typeArguments)}, {NetName(dictionary.TypeArguments[1], typeArguments)}, {TypeName(dict.Key)}, {TypeName(dict.Value)}>"
                    + $"({code}, {keys ?? "static k => k"}, {values ?? "static v => v"})";
        }
        if (target is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke } && unwrapped is FunctionType function)
        {
            // A function value that calls the delegate (SPEC §13.9).
            var d = $"@{_locals.Fresh("d")}";
            var parameters = function.Parameters.Select(p => (Name: $"@{_locals.Fresh("a")}", Type: p)).ToList();
            var call = $"{d}.Invoke({string.Join(", ", parameters.Select((p, i) => ToDotNet(p.Name, p.Type, DotNetTypes.TypeOf(invoke.Parameters[i]), typeArguments)))})";
            var body = invoke.ReturnsVoid ? $"{{ {call}; }}" : FromDotNet(call, DotNetTypes.ResultOf(invoke), function.Result, who, typeArguments);
            var lambda = $"({string.Join(", ", parameters.Select(p => $"{TypeName(p.Type)} {p.Name}"))}) => {body}";
            var created = $"new {TypeName(function)}(\"<delegate>\", {lambda})";
            return mapped is OptionalOf
                ? $"{Runtime}.DotNet.With({code}, {d} => {d} is null ? null : {created})"
                : $"{Runtime}.DotNet.With({code}, {d} => {created})";
        }

        var element = unwrapped is ListOfType list ? list.Element : null;
        var netElement = target switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol named when DotNetTypes.IsListLike(named) || DotNetTypes.IsOrdered(named) => named.TypeArguments[0],
            _ => null,
        };
        if (element is null || netElement is null)
            return code;
        if (ElementConversion(netElement, element, who, typeArguments) is { } convert)
            return $"{Runtime}.DotNet.ToList<{NetName(netElement, typeArguments)}, {TypeName(element)}>({code}, {convert})";
        // A List<T> of a type that maps as is is shared; anything else is copied.
        return target is INamedTypeSymbol { Name: "List" } ? code : $"{Runtime}.DotNet.ToList({code})";
    }

    /// <summary>
    /// A lambda converting one element, to .NET (<paramref name="who"/> null) or from it, or null if
    /// elements pass as they are.
    /// </summary>
    private string? ElementConversion(ITypeSymbol netElement, TaipanType element, string? who, TypeArguments? typeArguments = null)
    {
        var parameter = $"@{_locals.Fresh("e")}";
        var body = who is null ? ToDotNet(parameter, element, netElement, typeArguments) : FromDotNet(parameter, netElement, element, who, typeArguments);
        // A conversion that makes a delegate captures nothing, but isn't static-safe to write as such.
        return body == parameter ? null : $"{(body.Contains("DotNet.With(") ? "" : "static ")}{parameter} => {body}";
    }
}
