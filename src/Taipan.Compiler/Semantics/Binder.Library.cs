using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// A program bound as a .NET library (SPEC §14, M22): which definitions it exports, and which
// generic definitions can be .NET generics.
public sealed partial class Binder
{
    private readonly bool _asLibrary;

    /// <summary>A definition a library might export: where its name is, and the module it's in.</summary>
    private sealed record Candidate(Symbol Symbol, TextSpan Name, Module Module);

    // A library's namespace, when taipan.json or an SDK project sets it (SPEC §18.3).
    private string? _libraryNamespace;

    private BoundLibrary BindLibrary(List<Module> modules, Dictionary<string, Module> byName, List<BoundFunction> functions, List<BoundClass> classes)
    {
        var candidates = new List<Candidate>();
        foreach (var module in modules)
        {
            foreach (var item in module.File.Unit.Items)
            {
                Symbol? symbol = item switch
                {
                    FuncDef funcDef => module.Functions.First(f => f.Syntax == funcDef).Symbol,
                    ClassDef classDef => module.ClassDefs.First(c => c.Syntax == classDef).Symbol is { IsProtocol: false } classSymbol ? classSymbol : null,
                    EnumDef enumDef => module.EnumDefs.First(e => e.Syntax == enumDef).Symbol,
                    ConstDef constDef => module.Constants.First(c => c.Symbol.Name == constDef.Name.Text).Symbol,
                    _ => null,
                };
                var name = item switch
                {
                    FuncDef funcDef => funcDef.Name,
                    ClassDef classDef => classDef.Name,
                    EnumDef enumDef => enumDef.Name,
                    ConstDef constDef => constDef.Name,
                    _ => null,
                };
                if (symbol is not null && name is not null)
                    candidates.Add(new Candidate(symbol, name.Span, module));
            }
        }

        var bodies = functions.ToLookup(f => (Symbol)f.Symbol);
        var methods = classes.ToDictionary(c => (Symbol)c.Symbol, c => c.Methods);
        var generic = NetGenerics(candidates, bodies, methods, out var genericReasons);

        // What each definition shows callers; anything it uses must be exported too.
        var reasons = new Dictionary<Symbol, string>();
        foreach (var candidate in candidates)
        {
            if (candidate.Symbol.Name.StartsWith('_'))
                reasons[candidate.Symbol] = "";
            else if (genericReasons.TryGetValue(candidate.Symbol, out var reason))
                reasons[candidate.Symbol] = reason;
            else if (candidate.Symbol.Name == candidate.Module.File.Name)
                reasons[candidate.Symbol] = "it has its module's name";
        }
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var candidate in candidates.Where(c => !reasons.ContainsKey(c.Symbol)))
            {
                if (Surface(candidate.Symbol).SelectMany(Uses).FirstOrDefault(used => !IsExported(used, reasons)) is { } hidden)
                {
                    reasons[candidate.Symbol] = $"it uses '{hidden.Name}', which isn't exported";
                    changed = true;
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (reasons.TryGetValue(candidate.Symbol, out var reason) && reason.Length > 0)
            {
                _diagnostics.Source = candidate.Module.File.Source;
                _diagnostics.ReportNotExported(candidate.Name, candidate.Symbol.Name, reason);
            }
        }

        var main = modules[0].File.Name;
        // The namespace is the main file's name in PascalCase, unless it's set (SPEC §14.1, §18.3).
        return new BoundLibrary(
            _libraryNamespace ?? string.Concat(main.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])),
            [.. modules.Select(module => new BoundModule(
                module.File.Name,
                module.File.Source,
                [.. ModuleLoader.Imports(module.File.Unit).Select(i => i.Text).Where(byName.ContainsKey).Distinct()],
                [.. module.Functions.Select(f => f.Symbol)],
                [.. module.ClassDefs.Select(c => c.Symbol).Where(c => !c.IsProtocol)],
                [.. module.EnumDefs.Select(e => e.Symbol)],
                [.. module.Constants],
                [.. module.PythonImports]))],
            [.. candidates.Select(c => c.Symbol).Where(s => !reasons.ContainsKey(s))],
            generic);
    }

    // The library's own definitions are exported unless they have a reason not to be; the built-in
    // exception classes are the runtime's, which is public.
    private static bool IsExported(Symbol symbol, Dictionary<Symbol, string> reasons) =>
        !reasons.ContainsKey(symbol) && !(symbol is ClassSymbol { RuntimeName: null } && symbol.Name.StartsWith('_'));

    /// <summary>The types a definition shows callers: its signature, or a class's base and public members.</summary>
    private static IEnumerable<TaipanType> Surface(Symbol symbol) => symbol switch
    {
        FunctionSymbol function => Signature(function),
        VariableSymbol constant => [constant.Type],
        ClassSymbol classSymbol =>
        [
            .. classSymbol.Base is { } baseClass ? [baseClass.Type] : Array.Empty<TaipanType>(),
            .. classSymbol.Fields.Where(f => !f.Name.StartsWith('_')).Select(f => f.Type),
            .. classSymbol.Constructor.Parameters.Select(p => p.Type),
            .. classSymbol.Methods.Values.Where(m => !m.Name.StartsWith('_')).SelectMany(Signature),
        ],
        EnumSymbol enumSymbol => enumSymbol.Variants.SelectMany(v => v.Fields).Select(f => f.Type),
        _ => [],
    };

    private static IEnumerable<TaipanType> Signature(FunctionSymbol function) => [.. function.Parameters.Select(p => p.Type), function.ReturnType];

    /// <summary>The classes and enums a type names, at any depth.</summary>
    private static IEnumerable<Symbol> Uses(TaipanType type) => type switch
    {
        ClassType classType => [classType.Symbol, .. classType.TypeArguments.SelectMany(Uses)],
        EnumType enumType => [enumType.Symbol, .. enumType.TypeArguments.SelectMany(Uses)],
        ListOfType list => Uses(list.Element),
        DictOfType dict => [.. Uses(dict.Key), .. Uses(dict.Value)],
        OptionalOf optional => Uses(optional.Inner),
        TupleOfType tuple => tuple.Elements.SelectMany(Uses),
        FunctionType function => [.. function.Parameters.SelectMany(Uses), .. Uses(function.Result)],
        _ => [],
    };

    /// <summary>
    /// The generic definitions that can be .NET generics (SPEC §14.2): no bounds, no optional of a
    /// type parameter, and only such definitions used with their type parameters. The others get
    /// the reason they can't.
    /// </summary>
    private static ImmutableHashSet<Symbol> NetGenerics(
        List<Candidate> candidates, ILookup<Symbol, BoundFunction> bodies, Dictionary<Symbol, ImmutableArray<BoundFunction>> methods,
        out Dictionary<Symbol, string> reasonsOut)
    {
        var reasons = reasonsOut = [];
        var uses = new Dictionary<Symbol, List<Symbol>>();
        foreach (var candidate in candidates)
        {
            var symbol = candidate.Symbol;
            var typeParameters = symbol switch
            {
                FunctionSymbol function => function.TypeParameters,
                ClassSymbol classSymbol => classSymbol.TypeParameters,
                EnumSymbol enumSymbol => enumSymbol.TypeParameters,
                _ => [],
            };
            if (typeParameters.IsEmpty)
                continue;
            if (typeParameters.FirstOrDefault(p => p.Bound is not null || !p.Constraints.IsEmpty) is { } bounded)
            {
                reasons[symbol] = $"its type parameter '{bounded.Name}' has a bound";
                continue;
            }

            var functions = symbol switch
            {
                FunctionSymbol => bodies[symbol],
                ClassSymbol => methods.GetValueOrDefault(symbol, []),
                _ => [],
            };
            var types = Surface(symbol)
                .Concat(symbol is ClassSymbol c ? c.Fields.Select(f => f.Type) : [])
                .Concat(functions.SelectMany(f => TypesIn(f.Body)))
                .Concat(DefaultsOf(symbol).SelectMany(TypesIn))
                .ToList();
            if (types.SelectMany(OptionalTypeParameters).FirstOrDefault() is { } optional)
            {
                reasons[symbol] = $"'{optional.Name}?' has no .NET generic form";
                continue;
            }
            uses[symbol] = [.. types.SelectMany(GenericUses).Concat(functions.SelectMany(f => CalledGenerics(f.Body))).Distinct().Where(u => u != symbol)];
        }

        // A definition stays a .NET generic only if everything generic it uses with its type parameters is one.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (symbol, used) in uses)
            {
                if (reasons.ContainsKey(symbol))
                    continue;
                if (used.FirstOrDefault(u => reasons.ContainsKey(u) || !uses.ContainsKey(u)) is { } other)
                {
                    reasons[symbol] = $"it uses '{other.Name}', which isn't a .NET generic";
                    changed = true;
                }
            }
        }
        return [.. uses.Keys.Where(s => !reasons.ContainsKey(s))];
    }

    private static IEnumerable<BoundExpr> DefaultsOf(Symbol symbol) => symbol switch
    {
        FunctionSymbol function => function.Defaults.OfType<BoundExpr>(),
        ClassSymbol classSymbol =>
        [
            .. classSymbol.Fields.Select(f => f.Default).OfType<BoundExpr>(),
            .. classSymbol.Methods.Values.SelectMany(m => m.Defaults.OfType<BoundExpr>()),
        ],
        _ => [],
    };

    /// <summary>The type parameters a type has as optionals, as in <c>T?</c> or <c>list[T?]</c>.</summary>
    private static IEnumerable<TypeParameterSymbol> OptionalTypeParameters(TaipanType type) => type switch
    {
        OptionalOf { Inner: TypeParameterType parameter } => [parameter.Symbol],
        OptionalOf optional => OptionalTypeParameters(optional.Inner),
        ClassType classType => classType.TypeArguments.SelectMany(OptionalTypeParameters),
        EnumType enumType => enumType.TypeArguments.SelectMany(OptionalTypeParameters),
        ListOfType list => OptionalTypeParameters(list.Element),
        DictOfType dict => [.. OptionalTypeParameters(dict.Key), .. OptionalTypeParameters(dict.Value)],
        TupleOfType tuple => tuple.Elements.SelectMany(OptionalTypeParameters),
        FunctionType function => [.. function.Parameters.SelectMany(OptionalTypeParameters), .. OptionalTypeParameters(function.Result)],
        _ => [],
    };

    // Generic classes and enums a type names with type parameters among their arguments.
    private static IEnumerable<Symbol> GenericUses(TaipanType type) => type switch
    {
        ClassType classType => [.. HasTypeParameter(classType.TypeArguments) ? [classType.Symbol] : Array.Empty<Symbol>(), .. classType.TypeArguments.SelectMany(GenericUses)],
        EnumType enumType => [.. HasTypeParameter(enumType.TypeArguments) ? [enumType.Symbol] : Array.Empty<Symbol>(), .. enumType.TypeArguments.SelectMany(GenericUses)],
        ListOfType list => GenericUses(list.Element),
        DictOfType dict => [.. GenericUses(dict.Key), .. GenericUses(dict.Value)],
        OptionalOf optional => GenericUses(optional.Inner),
        TupleOfType tuple => tuple.Elements.SelectMany(GenericUses),
        FunctionType function => [.. function.Parameters.SelectMany(GenericUses), .. GenericUses(function.Result)],
        _ => [],
    };

    private static bool HasTypeParameter(IEnumerable<TaipanType> types) => types.Any(t => t is TypeParameterType || GenericUsesTypeParameter(t));

    private static bool GenericUsesTypeParameter(TaipanType type) => type switch
    {
        TypeParameterType => true,
        ClassType classType => HasTypeParameter(classType.TypeArguments),
        EnumType enumType => HasTypeParameter(enumType.TypeArguments),
        ListOfType list => GenericUsesTypeParameter(list.Element),
        DictOfType dict => GenericUsesTypeParameter(dict.Key) || GenericUsesTypeParameter(dict.Value),
        OptionalOf optional => GenericUsesTypeParameter(optional.Inner),
        TupleOfType tuple => HasTypeParameter(tuple.Elements),
        FunctionType function => HasTypeParameter(function.Parameters) || GenericUsesTypeParameter(function.Result),
        _ => false,
    };

    // Generic functions a body calls with type parameters among their type arguments.
    private static IEnumerable<Symbol> CalledGenerics(BoundBlock body) =>
        AllExpressions(body).OfType<BoundCall>().Where(c => !c.Function.TypeParameters.IsEmpty && HasTypeParameter(c.TypeArguments)).Select(c => c.Function);

    /// <summary>The type of every expression and local in a body, nested functions and lambdas included.</summary>
    private static IEnumerable<TaipanType> TypesIn(BoundStmt body) =>
        AllExpressions(body).Select(e => e.Type).Concat(AllStatements(body).SelectMany(s => s switch
        {
            BoundVariableDecl decl => [decl.Symbol.Type],
            BoundLocalFunction function => [function.Symbol.Type],
            _ => Array.Empty<TaipanType>(),
        }));

    private static IEnumerable<TaipanType> TypesIn(BoundExpr expr) => AllExpressions(expr).Select(e => e.Type);

    private static IEnumerable<BoundStmt> AllStatements(BoundStmt stmt) => [stmt, .. BoundTree.Statements(stmt).SelectMany(AllStatements)];

    private static IEnumerable<BoundExpr> AllExpressions(BoundStmt stmt) => AllStatements(stmt).SelectMany(BoundTree.Expressions).SelectMany(AllExpressions);

    private static IEnumerable<BoundExpr> AllExpressions(BoundExpr expr) => [expr, .. BoundTree.Children(expr).SelectMany(AllExpressions)];
}
