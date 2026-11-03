using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Types II (SPEC §3.18, M40): generic functions, built-ins and constructors as function values.
public sealed partial class Binder
{
    /// <summary>
    /// The function, built-in, class or variant a name or qualified name refers to, when it can be
    /// a value only as a lambda that calls it: anything callable but a plain top-level function.
    /// </summary>
    private Symbol? CallableValueTarget(Expr expr)
    {
        var symbol = expr switch
        {
            NameExpr name when !_typeParameters.ContainsKey(name.Name) => _scope.Lookup(name.Name),
            MemberExpr { Target: NameExpr target } member => _scope.Lookup(target.Name) switch
            {
                ModuleSymbol module => module.Definitions.GetValueOrDefault(member.Member.Text),
                EnumSymbol enumSymbol => enumSymbol.FindVariant(member.Member.Text),
                _ => null,
            },
            _ => null,
        };
        return symbol switch
        {
            FunctionSymbol { Builtin: not null } or FunctionSymbol { TypeParameters.IsEmpty: false } => symbol,
            ClassSymbol { IsProtocol: false } => symbol,
            VariantSymbol { Fields.IsEmpty: false } => symbol,
            _ => null,
        };
    }

    /// <summary>Whether a callable value's type comes only from an expected function type (SPEC §3.18).</summary>
    private static bool NeedsExpectedType(Symbol symbol) => symbol switch
    {
        FunctionSymbol => true,
        ClassSymbol classSymbol => !classSymbol.TypeParameters.IsEmpty,
        VariantSymbol variant => !variant.Enum.TypeParameters.IsEmpty,
        _ => false,
    };

    /// <summary>
    /// A generic function, built-in, constructor or variant as a value (SPEC §3.18): a lambda that
    /// calls it with the expected function type's parameters, or, for a constructor or variant that
    /// isn't generic, with its own.
    /// </summary>
    private BoundExpr BindCallableValue(Expr syntax, Symbol symbol, string name, TextSpan span, TaipanType? expected)
    {
        switch (symbol)
        {
            case FunctionSymbol { Builtin: BuiltinFunction.Range or BuiltinFunction.Super or BuiltinFunction.IsInstance or BuiltinFunction.Open }:
                _diagnostics.ReportBuiltinAsValue(span, name);
                return new BoundErrorExpr(span);
            case ClassSymbol { IsAbstract: true } abstractClass:
                _diagnostics.ReportAbstractInstantiation(span, abstractClass.Name, abstractClass.AbstractMethods.Select(m => $"{m.Owner!.Name}.{m.Name}"));
                return new BoundErrorExpr(span);
            case ClassSymbol runtimeOnly when BuiltinClasses.IsRuntimeOnly(runtimeOnly):
                _diagnostics.ReportCannotConstruct(span, runtimeOnly.Name);
                return new BoundErrorExpr(span);
        }
        if (TypeChecker.Unwrap(expected ?? TaipanType.Error) is FunctionType function)
            return BindCallableAs(syntax, symbol, span, function.Parameters, function.Result);
        if (NeedsExpectedType(symbol))
        {
            if (expected is null || !expected.ContainsError)
                _diagnostics.ReportCannotInferValue(span, name);
            return new BoundErrorExpr(span);
        }
        var (parameters, kinds) = symbol switch
        {
            ClassSymbol { IsDataclass: true } dataclass => (
                dataclass.AllFields.Select(f => TypeChecker.Substitute(f.Type, TypeChecker.MapOf(dataclass.Type))).ToImmutableArray(), ImmutableArray<ParamKind>.Empty),
            ClassSymbol classSymbol => (classSymbol.Constructor.Parameters.Select(p => p.Type).ToImmutableArray(), classSymbol.Constructor.Kinds),
            VariantSymbol variant => (variant.Fields.Select(f => f.Type).ToImmutableArray(), ImmutableArray<ParamKind>.Empty),
            _ => ([], []),
        };
        if (kinds.Any(k => k is not (ParamKind.Normal or ParamKind.PositionalOnly)))
        {
            _diagnostics.ReportVariadicAsValue(span, name);
            return new BoundErrorExpr(span);
        }
        return BindCallableAs(syntax, symbol, span, parameters, result: null);
    }

    /// <summary>
    /// The lambda for a callable value: a call of <paramref name="syntax"/> with one parameter of each
    /// type, checked as that call would be, and against <paramref name="result"/> when it's known.
    /// </summary>
    private BoundExpr BindCallableAs(Expr syntax, Symbol symbol, TextSpan span, ImmutableArray<TaipanType> parameterTypes, TaipanType? result)
    {
        var outer = _scope;
        _scope = new Scope(outer);
        var parameters = parameterTypes.Select((t, i) => new VariableSymbol($"__a{i}", t, VariableKind.Parameter)).ToImmutableArray();
        foreach (var parameter in parameters)
            _scope.Declare(parameter);
        var call = new CallExpr(syntax, [.. parameters.Select(p => new Argument(null, new NameExpr(p.Name, span), span))], span);
        BoundExpr body = result switch
        {
            null => BindValue(call),
            NothingType => BindExpr(call),
            _ => BindExpected(call, result),
        };
        _scope = outer;
        if (body.Type.ContainsError)
            return new BoundErrorExpr(span);
        return new BoundLambda(parameters, body, new FunctionType(parameterTypes, result ?? body.Type), span) { Shown = symbol };
    }
}
