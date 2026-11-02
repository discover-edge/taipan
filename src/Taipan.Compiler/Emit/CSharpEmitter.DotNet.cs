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
        var code = $"{receiver}.@{method.Name}({DotNetArguments(call.Arguments)})";
        return method.ReturnsVoid ? code : FromDotNet(code, DotNetTypes.ResultOf(method), call.Type, Who(method));
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
    private string DotNetArguments(IEnumerable<BoundDotNetArgument> arguments) => string.Join(", ", arguments.Select(a =>
        $"@{a.Parameter.Name}: {ToDotNet(Expr(a.Value), Concrete(a.Value.Type), DotNetTypes.TypeOf(a.Parameter))}"));

    /// <summary>
    /// A Taipan value as a .NET argument. Anything whose C# type isn't the parameter's exactly is
    /// cast to it, so C# picks the overload the binder chose.
    /// </summary>
    private string ToDotNet(string code, TaipanType argument, ITypeSymbol parameter)
    {
        if (argument is NoneType)
            return $"({FullName(parameter)})null";
        var target = parameter is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : parameter;
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

        var element = TypeChecker.Unwrap(argument) is ListOfType list ? list.Element : null;
        if (target is IArrayTypeSymbol array && element is not null)
            return ElementConversion(array.ElementType, element, who: null) is { } convert
                ? $"{Runtime}.DotNet.ToArray<{TypeName(element)}, {FullName(array.ElementType)}>({code}, {convert})"
                : $"{Runtime}.DotNet.ToArray({code})";
        if (target is INamedTypeSymbol named && DotNetTypes.IsListLike(named) && element is not null)
        {
            var netElement = named.TypeArguments[0];
            if (ElementConversion(netElement, element, who: null) is { } convert)
                return $"{Runtime}.DotNet.ToList<{TypeName(element)}, {FullName(netElement)}>({code}, {convert})";
            return named.Name == "List" ? code : $"({FullName(parameter)})({code})";
        }
        return target.TypeKind == TypeKind.Interface ? $"({FullName(parameter)})({code})" : code;
    }

    /// <summary>A .NET result as a Taipan value: widened, copied into a list, or checked for null (SPEC §13.3).</summary>
    private string FromDotNet(string code, ITypeSymbol type, TaipanType mapped, string who)
    {
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

        var element = TypeChecker.Unwrap(mapped) is ListOfType list ? list.Element : null;
        var netElement = target switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol named when DotNetTypes.IsListLike(named) => named.TypeArguments[0],
            _ => null,
        };
        if (element is null || netElement is null)
            return code;
        if (ElementConversion(netElement, element, who) is { } convert)
            return $"{Runtime}.DotNet.ToList<{FullName(netElement)}, {TypeName(element)}>({code}, {convert})";
        // A List<T> of a type that maps as is is shared; anything else is copied.
        return target is INamedTypeSymbol { Name: "List" } ? code : $"{Runtime}.DotNet.ToList({code})";
    }

    /// <summary>
    /// A lambda converting one element, to .NET (<paramref name="who"/> null) or from it, or null if
    /// elements pass as they are.
    /// </summary>
    private string? ElementConversion(ITypeSymbol netElement, TaipanType element, string? who)
    {
        var parameter = $"@{_locals.Fresh("e")}";
        var body = who is null ? ToDotNet(parameter, element, netElement) : FromDotNet(parameter, netElement, element, who);
        return body == parameter ? null : $"static {parameter} => {body}";
    }
}
