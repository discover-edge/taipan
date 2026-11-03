using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// A program bound as a CPython extension module (SPEC §17, M25): what its module file exports.
public sealed partial class Binder
{
    private readonly bool _asExtension;

    private BoundExtension BindExtension(Module main)
    {
        Enter(main);
        var functions = new List<FunctionSymbol>();
        var constants = new List<BoundConstant>();
        var exceptions = new List<ClassSymbol>();
        foreach (var item in main.File.Unit.Items)
        {
            switch (item)
            {
                case FuncDef funcDef when !funcDef.Name.Text.StartsWith('_'):
                    var function = main.Functions.First(f => f.Syntax == funcDef).Symbol;
                    var unconvertibleType = function.Parameters.Select(p => Unconvertible(p.Type, isResult: false))
                        .Append(Unconvertible(function.ReturnType, isResult: true)).FirstOrDefault(t => t is not null);
                    var reason = !function.TypeParameters.IsEmpty ? "it's generic"
                        : function.IsAsync ? "it's async"
                        : unconvertibleType is not null ? $"'{unconvertibleType}' can't be passed to Python"
                        : null;
                    if (reason is null)
                        functions.Add(function);
                    else
                        _diagnostics.ReportNotExported(funcDef.Name.Span, funcDef.Name.Text, reason);
                    break;
                case ConstDef constDef when !constDef.Name.Text.StartsWith('_'):
                    var constant = main.Constants.First(c => c.Symbol.Name == constDef.Name.Text);
                    if (Unconvertible(constant.Symbol.Type, isResult: false) is { } unconvertible)
                        _diagnostics.ReportNotExported(constDef.Name.Span, constDef.Name.Text, $"'{unconvertible}' can't be passed to Python");
                    else
                        constants.Add(constant);
                    break;
                case ClassDef classDef when !classDef.Name.Text.StartsWith('_'):
                    var symbol = main.ClassDefs.First(c => c.Syntax == classDef).Symbol;
                    if (!symbol.IsProtocol && BuiltinClasses.IsException(symbol))
                        exceptions.Add(symbol);
                    break;
            }
        }
        // Each class after its bases, so its Python base exists when it's created.
        var ordered = exceptions.OrderBy(Depth).ToImmutableArray();
        return new BoundExtension(main.File.Name, [.. functions], [.. constants], ordered);
    }

    private static int Depth(ClassSymbol symbol) => symbol.Base is { } baseClass ? Depth(baseClass) + 1 : 0;

    /// <summary>
    /// The part of a type that can't be passed to or from Python (SPEC §17.2), or null if it's
    /// convertible (§10.3): no result, for a result, but not <c>py</c>.
    /// </summary>
    private static TaipanType? Unconvertible(TaipanType type, bool isResult) => type switch
    {
        // An error is already reported.
        { ContainsError: true } => null,
        NothingType when isResult => null,
        PrimitiveType { Name: "int" or "float" or "bool" or "str" } => null,
        OptionalOf optional => Unconvertible(optional.Inner, isResult: false),
        ListOfType list => Unconvertible(list.Element, isResult: false),
        DictOfType dict => Unconvertible(dict.Key, isResult: false) ?? Unconvertible(dict.Value, isResult: false),
        TupleOfType tuple => tuple.Elements.Select(e => Unconvertible(e, isResult: false)).FirstOrDefault(e => e is not null),
        _ => type,
    };
}
