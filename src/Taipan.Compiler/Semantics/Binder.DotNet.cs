using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// .NET interop (SPEC §13, M21): imported types, their members, and overload resolution.
public sealed partial class Binder
{
    // Set once any file imports from dotnet, so a program without .NET never reads its metadata.
    private DotNetLibrary? _dotnet;

    private readonly DotNetLibrary _library;

    private DotNetLibrary DotNet => _dotnet ??= _library;

    // ---- Imports and type names ----

    /// <summary><c>from dotnet.&lt;namespace&gt; import T, …</c> (SPEC §13.1): each name with every arity it has.</summary>
    private void ImportDotNet(FromImportStmt from)
    {
        if (from.Module.Text == Syntax.ModuleLoader.DotNetModule)
        {
            _diagnostics.ReportDotNetNotModule(from.Module.Span);
            return;
        }
        var ns = from.Module.Text[(Syntax.ModuleLoader.DotNetModule.Length + 1)..];
        if (DotNet.Namespace(ns) is not { } namespaceSymbol)
        {
            _diagnostics.ReportDotNetNamespaceNotFound(from.Module.Span, ns);
            return;
        }
        foreach (var name in from.Names)
        {
            ImmutableArray<INamedTypeSymbol> types = [.. namespaceSymbol.GetTypeMembers(name.Name.Text)
                .Where(t => t.DeclaredAccessibility == Accessibility.Public).OrderBy(t => t.Arity)];
            if (types.IsEmpty)
            {
                _diagnostics.ReportNoDotNetType(name.Name.Span, ns, name.Name.Text);
                continue;
            }
            var bound = name.Alias ?? name.Name;
            DeclareGlobal(bound, new DotNetTypeSymbol(bound.Text, types), isDefinition: false);
        }
    }

    /// <summary><c>import dotnet.&lt;namespace&gt; as n</c> (SPEC §13.1, M43).</summary>
    private void ImportDotNetNamespace(ImportStmt import)
    {
        var ns = import.Module.Text[(Syntax.ModuleLoader.DotNetModule.Length + 1)..];
        if (DotNet.Namespace(ns) is not { } namespaceSymbol)
        {
            _diagnostics.ReportDotNetNamespaceNotFound(import.Module.Span, ns);
            return;
        }
        if (import.Alias is not { } alias)
        {
            _diagnostics.ReportNamespaceImportNeedsName(import.Module.Span, ns);
            return;
        }
        DeclareGlobal(alias, new DotNetNamespaceSymbol(alias.Text, namespaceSymbol), isDefinition: false);
    }

    /// <summary>A .NET type named in an annotation: anything but a static class (SPEC §13.3).</summary>
    private TaipanType DotNetTypeOf(INamedTypeSymbol type, TextSpan span)
    {
        if (type.IsStatic)
        {
            _diagnostics.ReportStaticClass(span, type.Name);
            return TaipanType.Error;
        }
        return new DotNetType(type);
    }

    /// <summary>
    /// A .NET type written with <paramref name="argumentSyntax"/> as type arguments, picking the arity
    /// among <paramref name="types"/> (SPEC §13.13): <c>HashSet[str]</c>, or a non-generic type without any.
    /// </summary>
    private TaipanType BindDotNetType(ImmutableArray<INamedTypeSymbol> types, string name, ImmutableArray<TypeSyntax> argumentSyntax, TextSpan span,
        ImmutableArray<TaipanType> outer = default)
    {
        var arguments = argumentSyntax.Select(BindType).ToImmutableArray();
        return BindDotNetType(types, name, arguments, span, outer);
    }

    private TaipanType BindDotNetType(ImmutableArray<INamedTypeSymbol> types, string name, ImmutableArray<TaipanType> arguments, TextSpan span,
        ImmutableArray<TaipanType> outer = default)
    {
        if (types.FirstOrDefault(t => t.Arity == arguments.Length) is not { } type)
        {
            _diagnostics.ReportWrongTypeArgumentCount(span, name, types.First(t => t.Arity != arguments.Length).Arity, arguments.Length);
            return TaipanType.Error;
        }
        if (arguments.IsEmpty && (outer.IsDefaultOrEmpty))
            return DotNetTypeOf(type, span);
        if (arguments.Any(a => a.ContainsError))
            return TaipanType.Error;
        ImmutableArray<TaipanType> all = [.. outer.IsDefault ? [] : outer, .. arguments];
        var created = new DotNetType(type, all);
        // Types Taipan maps itself are written as Taipan's (SPEC §13.13).
        if (!arguments.IsEmpty && (DotNetTypes.IsListLike(type) || DotNetTypes.IsDictionary(type) || DotNetTypes.IsTask(type) || DotNetTypes.IsOrdered(type)
            || type.SpecialType == SpecialType.System_Nullable_T || type.TypeKind == TypeKind.Delegate)
            && DotNetTypes.Map(type, result: false, out _, created.Map) is { } mapped)
        {
            _diagnostics.ReportMappedGeneric(span, $"{name}[{string.Join(", ", arguments)}]", mapped);
            return TaipanType.Error;
        }
        if (!SatisfiesConstraints(DotNetTypes.AllTypeParameters(type).Skip(all.Length - arguments.Length), created.Map!, name, span))
            return TaipanType.Error;
        return created;
    }

    /// <summary>
    /// The .NET type an expression names, if it names one: an imported type, a type in an imported
    /// namespace, a type nested in one, or a generic type with its type arguments (<c>HashSet[str]</c>).
    /// A generic type without them is its definition, which <see cref="CheckConstructed"/> reports,
    /// and one whose type arguments have errors is the error type.
    /// </summary>
    private TaipanType? DotNetTypeNamed(Expr expr)
    {
        switch (expr)
        {
            case NameExpr name when !_typeParameters.ContainsKey(name.Name) && _scope.Lookup(name.Name) is DotNetTypeSymbol symbol:
                return new DotNetType(symbol.Type);
            case MemberExpr { Target: NameExpr ns } member when _scope.Lookup(ns.Name) is DotNetNamespaceSymbol namespaceSymbol
                && namespaceSymbol.TypesNamed(member.Member.Text) is { IsEmpty: false } inNamespace:
                return new DotNetType(inNamespace.FirstOrDefault(t => t.Arity == 0) ?? inNamespace[0]);
            case MemberExpr member when DotNetTypeNamed(member.Target) is DotNetType outer && NestedType(outer.Symbol, member.Member.Text) is { } nested:
                return new DotNetType(nested, outer.TypeArguments);
            case IndexExpr index when GenericDotNetTypes(index.Target) is { } generic:
                var written = (index.Index is TupleExpr tuple ? tuple.Elements : [index.Index]).Select(TypeFromExpr).ToList();
                if (written.Any(t => t is null))
                    return null;
                return BindDotNetType(generic.Types, generic.Name, [.. written!], index.Span) is DotNetType created ? created : TaipanType.Error;
            default:
                return null;
        }
    }

    /// <summary>The .NET types, of every arity, a name or a namespace member names.</summary>
    private (ImmutableArray<INamedTypeSymbol> Types, string Name)? GenericDotNetTypes(Expr expr) => expr switch
    {
        NameExpr name when !_typeParameters.ContainsKey(name.Name) && _scope.Lookup(name.Name) is DotNetTypeSymbol symbol => (symbol.Types, name.Name),
        MemberExpr { Target: NameExpr ns } member when _scope.Lookup(ns.Name) is DotNetNamespaceSymbol namespaceSymbol
            && namespaceSymbol.TypesNamed(member.Member.Text) is { IsEmpty: false } types => (types, member.Member.Text),
        _ => null,
    };

    /// <summary>A generic .NET type used without type arguments is TP0209 (SPEC §13.13); a type whose arguments had errors was reported.</summary>
    private bool CheckConstructed(DotNetType owner, TextSpan span)
    {
        if (DotNetTypes.AllTypeParameters(owner.Symbol).Count() == owner.TypeArguments.Length)
            return true;
        _diagnostics.ReportWrongTypeArgumentCount(span, owner.Symbol.Name, owner.Symbol.Arity, 0);
        return false;
    }

    private static INamedTypeSymbol? NestedType(INamedTypeSymbol outer, string name) =>
        outer.GetTypeMembers(name).FirstOrDefault(t => t.Arity == 0 && t.DeclaredAccessibility == Accessibility.Public);

    private static string MemberName(INamedTypeSymbol owner, string name) => $"{new DotNetType(owner)}.{name}";

    private static string MemberName(DotNetType owner, string name) => $"{(owner.IsGeneric ? owner.Symbol.Name : owner.ToString())}.{name}";

    // ---- Native programs (SPEC §15.2) ----

    private static readonly string[] NativeUnsafe = ["RequiresDynamicCodeAttribute", "RequiresUnreferencedCodeAttribute"];

    /// <summary>
    /// For a native build, a member .NET marks as needing run-time code generation or reflection,
    /// on itself, the accessor used or a containing type, is TP0233.
    /// </summary>
    private void CheckNative(ISymbol member, IMethodSymbol? accessor, TextSpan span, string name)
    {
        if (!_asNative)
            return;
        IEnumerable<ISymbol> holders = [member, .. accessor is null ? [] : (ISymbol[])[accessor]];
        for (var type = member.ContainingType; type is not null; type = type.ContainingType)
            holders = holders.Append(type);
        var attribute = holders.SelectMany(h => h.GetAttributes()).FirstOrDefault(a =>
            NativeUnsafe.Contains(a.AttributeClass?.Name) && a.AttributeClass?.ContainingNamespace?.ToDisplayString() == "System.Diagnostics.CodeAnalysis");
        if (attribute is not null)
            _diagnostics.ReportNativeDotNet(span, name, attribute.ConstructorArguments.FirstOrDefault().Value as string ?? attribute.AttributeClass!.Name);
    }

    // ---- Exceptions (SPEC §13.6) ----

    private static bool IsDotNetException(INamedTypeSymbol type) => Bases(type).Any(t => IsSystemType(t, "Exception"));

    private static bool IsSystemType(INamedTypeSymbol type, string name) =>
        type.Name == name && type.ContainingNamespace?.ToDisplayString() == "System";

    private static bool IsOverflow(INamedTypeSymbol type) => Bases(type).Any(t => IsSystemType(t, "OverflowException"));

    // .NET's OverflowException is Taipan's OverflowError, not a DotNetError, so a clause for it,
    // a class deriving from it, or one of its bases can see what an earlier DotNetError clause can't.
    private static bool MaySeeOverflow(INamedTypeSymbol type) =>
        IsOverflow(type) || ((string[])["ArithmeticException", "SystemException", "Exception"]).Any(name => IsSystemType(type, name));

    /// <summary>The earlier clause's class that already catches a .NET class's exceptions (TP0300), or null.</summary>
    private static string? CaughtBy(INamedTypeSymbol type, List<ClassSymbol> caught, List<INamedTypeSymbol> caughtDotNet)
    {
        if (caughtDotNet.FirstOrDefault(c => SymbolEqualityComparer.Default.Equals(c, type) || DotNetTypes.Derives(type, c)) is { } earlier)
            return new DotNetType(earlier).ToString();
        if (caught.Contains(BuiltinClasses.Exception))
            return BuiltinClasses.Exception.Name;
        if (IsOverflow(type))
            return caught.FirstOrDefault(c => BuiltinClasses.OverflowError.DerivesFrom(c))?.Name;
        return caught.Contains(BuiltinClasses.DotNetError) && !MaySeeOverflow(type) ? BuiltinClasses.DotNetError.Name : null;
    }

    private static INamedTypeSymbol DotNetCommonBase(INamedTypeSymbol a, INamedTypeSymbol b) =>
        Bases(a).First(candidate => SymbolEqualityComparer.Default.Equals(candidate, b) || DotNetTypes.Derives(b, candidate));

    // ---- Deriving from .NET classes, and implementing .NET interfaces (SPEC §13.16, M43) ----

    /// <summary>Whether a base names a .NET type: <c>Random</c>, <c>IComparable[Version]</c>, <c>io.Stream</c>.</summary>
    private bool IsDotNetBase(TypeSyntax syntax) => syntax switch
    {
        NamedType named => _global.Lookup(named.Name.Text) is DotNetTypeSymbol,
        GenericType generic => _global.Lookup(generic.Name.Text) is DotNetTypeSymbol,
        QualifiedType qualified => _global.Lookup(qualified.Module.Text) is DotNetTypeSymbol or DotNetNamespaceSymbol,
        _ => false,
    };

    /// <summary>A .NET base: an interface, or a class Taipan can derive from; null, after TP0225, otherwise.</summary>
    private DotNetType? BindDotNetBase(TypeSyntax syntax, ClassSymbol symbol, TextSpan nameSpan)
    {
        // A static class isn't a type, so it's reported as a base here rather than as a type.
        if (syntax is NamedType named && _global.Lookup(named.Name.Text) is DotNetTypeSymbol { Type.IsStatic: true } staticClass)
        {
            _diagnostics.ReportCannotDeriveDotNet(syntax.Span, staticClass.Type.Name, "static");
            return null;
        }
        if (BindType(syntax) is not DotNetType type)
            return null;
        if (!symbol.TypeParameters.IsEmpty)
        {
            _diagnostics.ReportGenericImplementsDotNet(nameSpan);
            return null;
        }
        var net = type.Symbol;
        if (net.TypeKind == TypeKind.Interface)
            return type;
        string? reason = net switch
        {
            { IsStatic: true } => "static",
            { TypeKind: TypeKind.Struct } => "a struct",
            { TypeKind: not TypeKind.Class } or { IsSealed: true } => "sealed",
            _ when IsDotNetException(net) => "an exception class",
            _ when !net.InstanceConstructors.Any(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
                => "without a constructor Taipan can call",
            _ => null,
        };
        if (reason is not null)
        {
            _diagnostics.ReportCannotDeriveDotNet(syntax.Span, type.ToString(), reason);
            return null;
        }
        if (symbol.IsDataclass)
        {
            _diagnostics.ReportMixedBase(syntax.Span, isDataclass: true);
            return null;
        }
        return type;
    }

    /// <summary>The constructors a Taipan class deriving from a .NET class may call (SPEC §13.16).</summary>
    private static List<IMethodSymbol> BaseConstructors(DotNetType type) =>
        [.. type.Symbol.InstanceConstructors.Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)];

    /// <summary>The public virtual or abstract methods with a name, of a .NET class and the classes above it.</summary>
    private static List<IMethodSymbol> VirtualMethods(DotNetType type, string name) =>
        [.. Bases(type.Symbol).SelectMany(t => t.GetMembers(name)).OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic && !m.IsSealed && m.DeclaredAccessibility == Accessibility.Public
                && (m.IsVirtual || m.IsAbstract || m.IsOverride))];

    /// <summary>
    /// The parameter and result types a Taipan method implementing or overriding a .NET method has:
    /// what .NET passes it, mapped as results, and what it gives back, mapped as an argument. Null
    /// when Taipan can't implement it (ref or out parameters, or a type it can't use).
    /// </summary>
    private static (ImmutableArray<TaipanType> Parameters, TaipanType Result)? ExpectedSignature(IMethodSymbol method, TypeArguments? map)
    {
        var parameters = ImmutableArray.CreateBuilder<TaipanType>();
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None || DotNetTypes.Map(DotNetTypes.TypeOf(parameter), result: true, out _, map) is not { } type)
                return null;
            parameters.Add(type);
        }
        if (method.ReturnsByRef || method.ReturnsByRefReadonly || DotNetTypes.Map(DotNetTypes.ResultOf(method), result: false, out _, map) is not { } result)
            return null;
        return (parameters.ToImmutable(), result);
    }

    private static bool Matches(FunctionSymbol method, (ImmutableArray<TaipanType> Parameters, TaipanType Result) expected) =>
        method.Parameters.Length == expected.Parameters.Length
        && method.Parameters.Select(p => p.Type).SequenceEqual(expected.Parameters)
        && method.ReturnType == expected.Result;

    private static string SignatureText(IMethodSymbol method, (ImmutableArray<TaipanType> Parameters, TaipanType Result) expected) =>
        $"def {method.Name}(self{string.Concat(method.Parameters.Select((p, i) => $", {p.Name}: {expected.Parameters[i]}"))}) -> {(expected.Result is NothingType ? "None" : expected.Result)}";

    // A .NET interface's members a class implements, and why it can't, as TP0231 says it.
    private static string? CannotImplement(ISymbol member, List<ISymbol> others) => member switch
    {
        IPropertySymbol { IsIndexer: true } => "an indexer",
        IPropertySymbol property => $"the property '{property.Name}'",
        IEventSymbol dotnetEvent => $"the event '{dotnetEvent.Name}'",
        IMethodSymbol { IsGenericMethod: true } method => $"the generic method '{method.Name}'",
        IMethodSymbol method when others.OfType<IMethodSymbol>().Count(m => m.Name == method.Name) > 1 => $"two methods named '{method.Name}'",
        _ => null,
    };

    /// <summary>
    /// A class's .NET bases (SPEC §13.16): each interface method it must define, each virtual method
    /// it overrides, the abstract ones it must, and how its <c>__init__</c> constructs the .NET base.
    /// </summary>
    private void CheckDotNetBases(ClassDef classDef, ClassSymbol symbol, IEnumerable<(MethodDef Syntax, FunctionSymbol Symbol)> methods)
    {
        var nameSpan = classDef.Name.Span;
        if (symbol.Base is { } taipanBase && DotNetBaseOf(taipanBase) is { } inherited)
            _diagnostics.ReportDerivesFromDotNetClass(classDef.Bases.First(b => !IsAbcMarker(b)).Span, taipanBase.Name, inherited.ToString());
        if (symbol.DotNetBases.IsEmpty)
            return;
        var own = methods.Where(m => symbol.Methods.GetValueOrDefault(m.Symbol.Name) == m.Symbol && m.Symbol.Kind == MemberKind.Instance && !m.Symbol.IsClassLevel).ToList();

        foreach (var contract in symbol.DotNetBases.Where(b => b.Symbol.TypeKind == TypeKind.Interface))
        {
            var members = new[] { contract.Symbol }.Concat(contract.Symbol.AllInterfaces)
                .SelectMany(t => t.GetMembers()).Where(m => m.IsAbstract && !m.IsStatic).ToList();
            var why = members.Select(m => CannotImplement(m, members)).FirstOrDefault(w => w is not null);
            if (why is not null)
            {
                _diagnostics.ReportCannotImplement(nameSpan, contract.ToString(), why);
                continue;
            }
            foreach (var required in members.OfType<IMethodSymbol>())
            {
                var owner = DotNetTypes.Map(required.ContainingType, result: false, out _, contract.Map) as DotNetType ?? contract;
                if (symbol.FindMethod(required.Name) is not { Kind: MemberKind.Instance, IsClassLevel: false } method)
                {
                    _diagnostics.ReportMissingImplementation(nameSpan, symbol.Name, required.Name, owner.ToString());
                    continue;
                }
                if (ExpectedSignature(required, contract.Map) is not { } expected)
                {
                    _diagnostics.ReportCannotImplement(nameSpan, owner.ToString(), $"the method '{required.Name}'");
                    continue;
                }
                var span = own.FirstOrDefault(m => m.Symbol == method).Syntax?.Name.Span ?? nameSpan;
                if (!Matches(method, expected))
                {
                    _diagnostics.ReportDotNetSignature(span, required.Name, SignatureText(required, expected), owner.ToString());
                    continue;
                }
                method.DotNetImplements.Add((owner, required));
            }
        }

        if (symbol.DotNetBaseClass is not { } baseClass)
            return;
        foreach (var abstractMember in Bases(baseClass.Symbol).SelectMany(t => t.GetMembers()).Where(m => m.IsAbstract && m is IPropertySymbol or IEventSymbol))
        {
            _diagnostics.ReportCannotImplement(nameSpan, baseClass.ToString(), CannotImplement(abstractMember, [])!);
            return;
        }
        foreach (var (syntax, method) in own)
        {
            if (method.Name is "ToString" or "Equals" or "GetHashCode")
            {
                _diagnostics.ReportDefinedBySpecial(syntax.Name.Span, method.Name, method.Name switch { "ToString" => "__str__", "Equals" => "__eq__", _ => "__hash__" });
                continue;
            }
            if (symbol.Base?.FindMethod(method.Name) is not null)
                continue;
            var virtuals = VirtualMethods(baseClass, method.Name);
            if (virtuals.Count == 0)
                continue;
            var matching = virtuals.FirstOrDefault(v => ExpectedSignature(v, baseClass.Map) is { } expected && Matches(method, expected));
            if (matching is null)
            {
                var closest = virtuals.FirstOrDefault(v => v.Parameters.Length == method.Parameters.Length) ?? virtuals[0];
                var owner = new DotNetType(closest.ContainingType).ToString();
                if (ExpectedSignature(closest, baseClass.Map) is { } shown)
                    _diagnostics.ReportDotNetSignature(syntax.Name.Span, method.Name, SignatureText(closest, shown), owner);
                else
                    _diagnostics.ReportDotNetUnsupported(syntax.Name.Span, $"{owner}.{method.Name}", Signature(closest));
                continue;
            }
            if (!method.IsMarkedOverride)
                _diagnostics.ReportMissingOverride(syntax.Name.Span, method.Name, new DotNetType(matching.ContainingType).ToString());
            method.DotNetOverride = matching;
        }
        // Every abstract method of the .NET bases needs an override (SPEC §13.16).
        foreach (var abstractMethod in Bases(baseClass.Symbol).SelectMany(t => t.GetMembers()).OfType<IMethodSymbol>().Where(m => m.IsAbstract && m.MethodKind == MethodKind.Ordinary))
        {
            var overriddenInDotNet = Bases(baseClass.Symbol).SelectMany(t => t.GetMembers(abstractMethod.Name)).OfType<IMethodSymbol>()
                .Any(m => m.IsOverride && !m.IsAbstract && SameParameters(m, abstractMethod));
            var overriddenHere = own.Any(m => SymbolEqualityComparer.Default.Equals(m.Symbol.DotNetOverride?.OriginalDefinition, abstractMethod.OriginalDefinition)
                || m.Symbol.DotNetOverride is { } o && o.Name == abstractMethod.Name && SameParameters(o, abstractMethod));
            if (!overriddenInDotNet && !overriddenHere)
                _diagnostics.ReportMustOverride(nameSpan, symbol.Name, $"{new DotNetType(abstractMethod.ContainingType)}.{abstractMethod.Name}");
        }
        // The .NET base is constructed first: by super().__init__(…), __init__'s first statement (SPEC §13.16).
        var init = classDef.Methods.FirstOrDefault(m => m.Name.Text == "__init__");
        if (init is null)
        {
            if (!BaseConstructors(baseClass).Any(c => c.Parameters.All(p => p.IsOptional || p.IsParams)))
                _diagnostics.ReportCannotDeriveDotNet(classDef.Bases.First(IsDotNetBase).Span, baseClass.ToString(), "without a constructor Taipan can call");
            return;
        }
        if (init.Body.Statements.FirstOrDefault() is not ExprStmt { Expr: CallExpr { Callee: MemberExpr { Member.Text: "__init__", Target: CallExpr { Callee: NameExpr { Name: "super" } } } } })
        {
            var misplaced = FindSuperInit(init.Body);
            _diagnostics.ReportDotNetSuperFirst(misplaced ?? init.Name.Span, baseClass.ToString());
        }
    }

    // A super().__init__(…) call anywhere in a block, for TP0226's position.
    private static TextSpan? FindSuperInit(Block block)
    {
        foreach (var statement in block.Statements)
        {
            if (statement is ExprStmt { Expr: CallExpr { Callee: MemberExpr { Member.Text: "__init__", Target: CallExpr { Callee: NameExpr { Name: "super" } } superCall } } })
                return superCall.Callee.Span;
        }
        return null;
    }

    /// <summary><c>super().__init__(…)</c> in a class with a .NET base: one of its constructors, chosen as overloads are (SPEC §13.16).</summary>
    private BoundExpr BindDotNetSuperInit(CallExpr call, DotNetType baseClass, TextSpan nameSpan)
    {
        var constructors = BaseConstructors(baseClass);
        if (ResolveOverload(call, constructors, baseClass.ToString(), nameSpan, baseClass.Map, null, null) is not var (constructor, arguments, _))
            return new BoundErrorExpr(call.Span);
        return new BoundDotNetSuperInit(baseClass, constructor, arguments, call.Span);
    }

    // ---- Type tests (SPEC §13.17, M43) ----

    /// <summary>
    /// <c>isinstance(x, T)</c> where <c>x</c> is an <c>object</c> or a .NET value, or <c>T</c> a .NET
    /// type: <c>T</c> may be a .NET type that isn't generic, a class, or for an object a built-in type.
    /// </summary>
    private BoundExpr BindTypeTest(BoundExpr operand, CallExpr call)
    {
        var syntax = call.Arguments[1].Value;
        var tested = TypeChecker.Unwrap(operand.Type);
        TaipanType? target = null;
        if (DotNetTypeNamed(syntax) is { } named)
        {
            if (named is DotNetType { IsGeneric: true } or DotNetType { Symbol.Arity: > 0 })
                _diagnostics.ReportGenericClassTest(syntax.Span, ((DotNetType)named).Symbol.Name);
            else if (named is DotNetType net)
                target = net;
        }
        else if (tested is ObjectType && syntax is NameExpr { Name: "int" or "float" or "str" or "bool" } builtin && _scope.Lookup(builtin.Name) is not VariableSymbol)
        {
            target = builtin.Name switch { "int" => TaipanType.Int, "float" => TaipanType.Float, "str" => TaipanType.Str, _ => TaipanType.Bool };
        }
        else if (ResolveClass(syntax, tested is ClassType ? tested : TaipanType.Error) is { } classType)
        {
            target = classType;
        }
        if (target is null || operand.Type.ContainsError)
            return new BoundErrorExpr(call.Span);
        if (tested is not (ObjectType or DotNetType or ClassType))
        {
            _diagnostics.ReportIsInstanceNeedsClass(call.Arguments[0].Value.Span, operand.Type);
            return new BoundErrorExpr(call.Span);
        }
        if (!CheckDotNetRelated(tested, target, syntax.Span))
            return new BoundErrorExpr(call.Span);
        return new BoundTypeTest(operand, target, call.Span);
    }

    /// <summary>
    /// Whether a value of type <paramref name="tested"/> could be a <paramref name="target"/>, as a
    /// type test needs (TP0200 otherwise): an object could be anything; otherwise one derives from the
    /// other, or one is an interface that a class or struct that isn't sealed might implement.
    /// </summary>
    private bool CheckDotNetRelated(TaipanType tested, TaipanType target, TextSpan span)
    {
        var related = (tested, target) switch
        {
            (ObjectType, _) => true,
            (DotNetType from, DotNetType to) => DotNetTypes.IsBase(from, to) || DotNetTypes.IsBase(to, from) || MightImplement(from, to) || MightImplement(to, from),
            (DotNetType from, ClassType to) => DotNetTypes.ClassIsBase(to, from) || from.Symbol.TypeKind == TypeKind.Interface,
            (ClassType from, DotNetType to) => DotNetTypes.ClassIsBase(from, to) || to.Symbol.TypeKind == TypeKind.Interface,
            (ClassType from, ClassType to) => TypeChecker.AreRelated(from.Symbol, to.Symbol),
            _ => false,
        };
        if (!related)
            _diagnostics.ReportNeverInstance(span, tested, target.ToString());
        return related;
    }

    // An interface a value of a class or struct could still implement: it isn't sealed, or it's a struct, which can't be derived from.
    private static bool MightImplement(DotNetType value, DotNetType contract) =>
        contract.Symbol.TypeKind == TypeKind.Interface && (value.Symbol.TypeKind == TypeKind.Interface || !value.Symbol.IsSealed && !value.Symbol.IsValueType);

    /// <summary>Whether a class pattern names a .NET type: <c>StringBuilder()</c>, <c>io.FileInfo()</c>.</summary>
    private bool NamesDotNetType(VariantPattern pattern) => PatternDotNetType(pattern) is not null;

    private INamedTypeSymbol? PatternDotNetType(VariantPattern pattern) => (pattern.Module, pattern.Enum) switch
    {
        (null, null) => (_scope.Lookup(pattern.Variant.Text) as DotNetTypeSymbol)?.Types.FirstOrDefault(t => t.Arity == 0),
        (null, { } ns) => (_scope.Lookup(ns.Text) as DotNetNamespaceSymbol)?.TypesNamed(pattern.Variant.Text).FirstOrDefault(t => t.Arity == 0),
        _ => null,
    };

    /// <summary>
    /// A class pattern on an <c>object</c>, a .NET value or a class with .NET bases (SPEC §13.17):
    /// a .NET type with sub-patterns for its properties and fields, by name, or on an object a Taipan class.
    /// </summary>
    private BoundPattern BindDotNetPattern(VariantPattern pattern, TaipanType type, PatternBindings bindings)
    {
        BoundPattern Invalid()
        {
            bindings.Valid = false;
            BindSubPatterns(pattern, bindings);
            return new BoundWildcardPattern();
        }

        var name = _source.ToString(new TextSpan(pattern.Span.Start, pattern.Variant.Span.End - pattern.Span.Start));
        if (PatternDotNetType(pattern) is not { } symbol)
        {
            // A Taipan class on an object matches its instances, as on its own type.
            if (type is ObjectType && pattern.Module is null && (pattern.Enum is null ? _scope.Lookup(pattern.Variant.Text) : null) is ClassSymbol { IsProtocol: false } classSymbol)
                return BindClassPattern(pattern, classSymbol.TypeParameters.IsEmpty ? classSymbol.Type : new ClassType(classSymbol), bindings);
            if (type is ObjectType && pattern.Enum is not null && _scope.Lookup(pattern.Enum.Text) is ModuleSymbol module
                && module.Classes.GetValueOrDefault(pattern.Variant.Text) is { IsProtocol: false } moduleClass)
                return BindClassPattern(pattern, moduleClass.Type, bindings);
            if (_scope.Lookup(pattern.Variant.Text) is DotNetTypeSymbol)
                _diagnostics.ReportGenericClassTest(pattern.Variant.Span, pattern.Variant.Text);
            else
                _diagnostics.ReportNotAClass(pattern.Span, name);
            return Invalid();
        }
        if (pattern.Bindings is not { } arguments)
        {
            _diagnostics.ReportBareClassPattern(pattern.Span, name);
            return Invalid();
        }
        var target = new DotNetType(symbol);
        if (!CheckDotNetRelated(TypeChecker.Unwrap(type), target, pattern.Span))
            return Invalid();
        if (arguments.Any(a => a.Field is null))
        {
            _diagnostics.ReportPositionalOnPlainClass(pattern.Span, target.ToString());
            return Invalid();
        }
        var members = ImmutableArray.CreateBuilder<(ISymbol, TaipanType, BoundPattern)>();
        var given = new HashSet<string>();
        var valid = true;
        foreach (var argument in arguments)
        {
            var field = argument.Field!;
            if (PropertyOrField(symbol, field.Text, isStatic: false) is not { } member)
            {
                _diagnostics.ReportNoField(field.Span, target, field.Text);
                valid = false;
                BindPattern(argument.Pattern, TaipanType.Error, bindings);
                continue;
            }
            if (!given.Add(field.Text))
            {
                _diagnostics.ReportRepeatedField(field.Span, field.Text);
                valid = false;
                BindPattern(argument.Pattern, TaipanType.Error, bindings);
                continue;
            }
            if (DotNetTypes.Map(DotNetTypes.TypeOf(member), result: true, out var unsupported) is not { } memberType)
            {
                _diagnostics.ReportDotNetUnsupported(field.Span, MemberName(symbol, field.Text), DotNetTypes.Display(unsupported!));
                valid = false;
                BindPattern(argument.Pattern, TaipanType.Error, bindings);
                continue;
            }
            Index(field.Span, member);
            members.Add((member, memberType, BindPattern(argument.Pattern, memberType, bindings)));
        }
        if (!valid)
        {
            bindings.Valid = false;
            return new BoundWildcardPattern();
        }
        return new BoundDotNetPattern(target, members.ToImmutable());
    }

    // ---- Members (SPEC §13.4) ----

    /// <summary>Public members of a type and the types it derives from, most derived first.</summary>
    private static IEnumerable<ISymbol> MembersNamed(INamedTypeSymbol owner, string name)
    {
        IEnumerable<INamedTypeSymbol> types = owner.TypeKind == TypeKind.Interface
            ? [owner, .. owner.AllInterfaces]
            : Bases(owner);
        // Interfaces have object's members too, as every value does.
        if (owner.TypeKind == TypeKind.Interface && Bases(owner).LastOrDefault() is { } root)
            types = types.Append(root);
        return types.SelectMany(t => t.GetMembers(name)).Where(m => m.DeclaredAccessibility == Accessibility.Public);
    }

    private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
    }

    /// <summary>A readable property (not an indexer) or field, static or instance as asked.</summary>
    private static ISymbol? PropertyOrField(INamedTypeSymbol owner, string name, bool isStatic) =>
        MembersNamed(owner, name).FirstOrDefault(m => m.IsStatic == isStatic && m switch
        {
            IPropertySymbol { IsIndexer: false } property => property.GetMethod is { DeclaredAccessibility: Accessibility.Public },
            IFieldSymbol => true,
            _ => false,
        });

    /// <summary>A public event, static or instance as asked (SPEC §13.18).</summary>
    private static IEventSymbol? EventNamed(INamedTypeSymbol owner, string name, bool isStatic) =>
        MembersNamed(owner, name).OfType<IEventSymbol>().FirstOrDefault(e => e.IsStatic == isStatic);

    /// <summary>Whether a .NET type has a member of that name, which keeps extension methods out (SPEC §13.15).</summary>
    private static bool HasMember(INamedTypeSymbol owner, string name, bool isStatic) =>
        Methods(owner, name, isStatic).Count > 0 || PropertyOrField(owner, name, isStatic) is not null || EventNamed(owner, name, isStatic) is not null;

    /// <summary>The overloads of a method, without those a more derived type overrides or hides.</summary>
    private static List<IMethodSymbol> Methods(INamedTypeSymbol owner, string name, bool isStatic)
    {
        var methods = new List<IMethodSymbol>();
        foreach (var method in MembersNamed(owner, name).OfType<IMethodSymbol>())
        {
            if (method.MethodKind == MethodKind.Ordinary && method.IsStatic == isStatic && !methods.Any(m => SameParameters(m, method)))
                methods.Add(method);
        }
        return methods;
    }

    private static bool SameParameters(IMethodSymbol a, IMethodSymbol b) =>
        a.Parameters.Length == b.Parameters.Length
        && a.Parameters.Zip(b.Parameters).All(p => p.First.RefKind == p.Second.RefKind && SymbolEqualityComparer.Default.Equals(p.First.Type, p.Second.Type));

    /// <summary>The <c>List&lt;T&gt;</c> a list is, whose members a list has too (SPEC §13.4, M43).</summary>
    private DotNetType ListAsDotNet(ListOfType list) => new(DotNet.Named("System.Collections.Generic.List`1"), [list.Element]);

    /// <summary><c>T.m</c> or <c>x.m</c> as a value: a property, a field or an enum member (SPEC §13.4).</summary>
    private BoundExpr BindDotNetMember(BoundExpr? target, DotNetType owner, MemberExpr member)
    {
        var name = member.Member.Text;
        if (!CheckConstructed(owner, member.Target.Span))
            return new BoundErrorExpr(member.Span);
        if (target is null && NestedType(owner.Symbol, name) is not null)
        {
            _diagnostics.ReportTypeAsValue(member.Span, name);
            return new BoundErrorExpr(member.Span);
        }
        if (EventNamed(owner.Symbol, name, target is null) is not null)
        {
            _diagnostics.ReportEventMisuse(member.Member.Span, name);
            return new BoundErrorExpr(member.Span);
        }
        if (PropertyOrField(owner.Symbol, name, target is null) is not { } found)
        {
            if (Methods(owner.Symbol, name, target is null).Count > 0)
                _diagnostics.ReportMethodAsValue(member.Member.Span, name);
            else
                _diagnostics.ReportNoField(member.Member.Span, owner, name);
            return new BoundErrorExpr(member.Span);
        }
        if (DotNetTypes.Map(DotNetTypes.TypeOf(found), result: true, out var unsupported, owner.Map) is not { } type)
        {
            _diagnostics.ReportDotNetUnsupported(member.Member.Span, MemberName(owner, name), DotNetTypes.Display(unsupported!));
            return new BoundErrorExpr(member.Span);
        }
        CheckNative(found, (found as IPropertySymbol)?.GetMethod, member.Member.Span, MemberName(owner, name));
        Index(member.Member.Span, found);
        return new BoundDotNetMember(target, found, type, member.Span) { Owner = owner };
    }

    /// <summary>
    /// <c>x.P = v</c> or <c>T.P = v</c>: a settable property or a field that isn't read-only (SPEC
    /// §13.4); <c>x.E += f</c> and <c>x.E -= f</c> on an event (§13.18).
    /// </summary>
    private BoundStmt BindDotNetAssign(AssignStmt assign, BoundExpr? target, DotNetType owner, MemberExpr member, BoundExpr? preset)
    {
        var name = member.Member.Text;
        if (!CheckConstructed(owner, member.Target.Span))
            return OrphanAssign(assign, preset);
        if (EventNamed(owner.Symbol, name, target is null) is { } dotnetEvent)
        {
            if (assign.Op is not (AssignOp.Add or AssignOp.Subtract) || preset is not null)
            {
                _diagnostics.ReportEventMisuse(member.Member.Span, name);
                return OrphanAssign(assign, preset);
            }
            if (DotNetTypes.Map(dotnetEvent.Type, result: false, out var unsupportedHandler, owner.Map) is not { } handlerType)
            {
                _diagnostics.ReportDotNetUnsupported(member.Member.Span, MemberName(owner, name), DotNetTypes.Display(unsupportedHandler!));
                BindValue(assign.Value);
                return new BoundExprStmt(new BoundErrorExpr(assign.Span), assign.Span);
            }
            Index(member.Member.Span, dotnetEvent);
            var handler = BindExpected(assign.Value, handlerType);
            return new BoundDotNetEvent(target, dotnetEvent, handler, assign.Op == AssignOp.Add, assign.Span) { Owner = owner };
        }
        if (PropertyOrField(owner.Symbol, name, target is null) is not { } found)
        {
            _diagnostics.ReportNoField(member.Member.Span, owner, name);
            return OrphanAssign(assign, preset);
        }
        var what = found switch
        {
            IPropertySymbol { SetMethod.DeclaredAccessibility: Accessibility.Public, SetMethod.IsInitOnly: false } => null,
            IPropertySymbol => "read-only property",
            IFieldSymbol { IsConst: true } => "constant",
            IFieldSymbol { IsReadOnly: true } => "read-only field",
            _ => null,
        };
        if (what is not null)
        {
            _diagnostics.ReportCannotAssign(member.Member.Span, name, what, null);
            return OrphanAssign(assign, preset);
        }
        var netType = DotNetTypes.TypeOf(found);
        if (DotNetTypes.Map(netType, result: false, out var unsupported, owner.Map) is not { } type)
        {
            _diagnostics.ReportDotNetUnsupported(member.Member.Span, MemberName(owner, name), DotNetTypes.Display(unsupported!));
            return OrphanAssign(assign, preset);
        }
        CheckNative(found, (found as IPropertySymbol)?.SetMethod, member.Member.Span, MemberName(owner, name));
        Index(member.Member.Span, found);
        var current = new BoundDotNetMember(target, found, DotNetTypes.Map(netType, result: true, out _, owner.Map) ?? type, member.Span) { Owner = owner };
        var value = BindAssignedValue(assign, current, type, preset);
        CheckLiteralFits(value, netType);
        return new BoundDotNetAssign(target, found, value, assign.Op != AssignOp.Assign, assign.Span) { Owner = owner };
    }

    // ---- Indexers and operators (SPEC §13.10, M33) ----

    /// <summary>The public indexers of a type and its bases that can be read.</summary>
    private static List<IPropertySymbol> Indexers(INamedTypeSymbol owner) =>
        [.. MembersNamed(owner, "this[]").OfType<IPropertySymbol>().Where(p => p.IsIndexer && !p.IsStatic && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })];

    // `x[a, b]` passes a and b to an indexer with two parameters.
    private static ImmutableArray<Expr> IndexArguments(IndexExpr index) => index.Index is TupleExpr tuple ? tuple.Elements : [index.Index];

    /// <summary><c>x[i]</c> on a .NET value: its indexer, chosen among overloads as a method is (SPEC §13.10).</summary>
    private BoundExpr BindDotNetIndex(BoundExpr target, DotNetType owner, IndexExpr index)
    {
        var syntax = IndexArguments(index);
        var indexers = Indexers(owner.Symbol);
        if (indexers.Count == 0)
        {
            foreach (var argument in syntax)
                BindValue(argument);
            _diagnostics.ReportCannotIndex(index.Target.Span, target.Type);
            return new BoundErrorExpr(index.Span);
        }
        var name = $"{owner}[…]";
        var map = owner.Map;
        var shaped = indexers.Select(p => p.GetMethod!).Where(m => m.Parameters.Length == syntax.Length && UnsupportedIn(m, map) is null)
            .Select(m => new Overload(m, Enumerable.Range(0, syntax.Length).ToArray(), false)).ToList();
        var values = syntax.Select(a => a is LambdaExpr ? null : BindValue(a)).ToArray();
        if (values.Any(v => v is not null && v.Type.ContainsError))
            return new BoundErrorExpr(index.Span);
        if (shaped.Count == 0)
        {
            _diagnostics.ReportNoOverload(index.Target.Span, name, values.Select(v => v?.Type ?? TaipanType.Error));
            return new BoundErrorExpr(index.Span);
        }
        if (ChooseOverload(syntax, shaped, values, name, index.Target.Span, map, null) is not var (getter, arguments, _))
            return new BoundErrorExpr(index.Span);
        var indexer = indexers.First(p => SymbolEqualityComparer.Default.Equals(p.GetMethod, getter));
        var type = DotNetTypes.Map(DotNetTypes.TypeOf(indexer), result: true, out _, map)!;
        return new BoundDotNetIndex(target, indexer, arguments, type, index.Span) { TypeArguments = map };
    }

    /// <summary><c>x[i] = v</c> on a .NET value: an indexer with a public setter (SPEC §13.10).</summary>
    private BoundStmt BindDotNetIndexAssign(AssignStmt assign, BoundDotNetIndex read, BoundExpr? preset)
    {
        if (read.Indexer.SetMethod is not { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false })
        {
            _diagnostics.ReportCannotAssign(read.Span, $"{read.Target.Type}[…]", "read-only indexer", null);
            return OrphanAssign(assign, preset);
        }
        var netType = DotNetTypes.TypeOf(read.Indexer);
        var type = DotNetTypes.Map(netType, result: false, out _, read.TypeArguments)!;
        var value = BindAssignedValue(assign, read, type, preset);
        CheckLiteralFits(value, netType);
        return new BoundDotNetIndexAssign(read, value, assign.Op != AssignOp.Assign, assign.Span);
    }

    private static string? OperatorName(BinaryOp op) => op switch
    {
        BinaryOp.Add => "op_Addition",
        BinaryOp.Subtract => "op_Subtraction",
        BinaryOp.Multiply => "op_Multiply",
        BinaryOp.Divide => "op_Division",
        BinaryOp.Modulo => "op_Modulus",
        BinaryOp.Less => "op_LessThan",
        BinaryOp.LessEqual => "op_LessThanOrEqual",
        BinaryOp.Greater => "op_GreaterThan",
        BinaryOp.GreaterEqual => "op_GreaterThanOrEqual",
        _ => null,
    };

    // The user-defined operators with a name, on each .NET type among the operands.
    private static List<IMethodSymbol> Operators(string name, params TaipanType[] operands) =>
        [.. operands.OfType<DotNetType>().Select(t => t.Symbol).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .SelectMany(t => t.GetMembers(name)).OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.UserDefinedOperator && m.DeclaredAccessibility == Accessibility.Public)];

    // The type arguments of the operands' generic types, for their operators' parameter types.
    private static TypeArguments? OperandArguments(params TaipanType[] operands) =>
        operands.OfType<DotNetType>().Select(t => t.Map).Aggregate((TypeArguments?)null, (a, b) => a is null ? b : b is null ? a
            : new TypeArguments(a.Concat(b.Where(p => !a.ContainsKey(p.Key)))));

    /// <summary>
    /// <c>a op b</c> where either is a .NET value: the operator their types define, chosen as an
    /// overload is (SPEC §13.10). Null when neither defines one with that name.
    /// </summary>
    private BoundExpr? BindDotNetOperator(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        // The operands are bound already; their syntax only places messages.
        Expr leftSyntax = new ErrorExpr(left.Span), rightSyntax = new ErrorExpr(right.Span);
        if (OperatorName(op) is not { } name || Operators(name, left.Type, right.Type) is not { Count: > 0 } operators)
            return null;
        var map = OperandArguments(left.Type, right.Type);
        var shaped = operators.Where(m => m.Parameters.Length == 2 && UnsupportedIn(m, map) is null).Select(m => new Overload(m, [0, 1], false)).ToList();
        var plans = shaped.Select(c => Plan([leftSyntax, rightSyntax], c, [left, right], name, span, report: false, map, null)).OfType<OverloadPlan>().ToList();
        if (plans.Count == 0)
            return null;
        if (ChooseOverload([leftSyntax, rightSyntax], [.. plans.Select(p => p.Overload)], [left, right], TypeChecker.Display(op), span, map, null) is not var (method, arguments, _))
            return new BoundErrorExpr(span);
        var type = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _, map)!;
        return new BoundDotNetOperator(TypeChecker.Display(op), method, arguments, type, span);
    }

    /// <summary><c>-x</c> on a .NET value: its type's unary negation (SPEC §13.10).</summary>
    private BoundExpr? BindDotNetNegation(BoundExpr operand, TextSpan span)
    {
        Expr operandSyntax = new ErrorExpr(operand.Span);
        var map = OperandArguments(operand.Type);
        var operators = Operators("op_UnaryNegation", operand.Type).Where(m => m.Parameters.Length == 1 && UnsupportedIn(m, map) is null).ToList();
        if (operators.Count == 0)
            return null;
        if (ChooseOverload([operandSyntax], [.. operators.Select(m => new Overload(m, [0], false))], [operand], "-", span, map, null) is not var (method, arguments, _))
            return new BoundErrorExpr(span);
        var type = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _, map)!;
        return new BoundDotNetOperator("-", method, arguments, type, span);
    }

    /// <summary>
    /// <c>a | b</c>, <c>a &amp; b</c>, <c>a ^ b</c> and <c>a in b</c> on a flags enum (SPEC §13.19, M43);
    /// null when the operands aren't one flags enum's values.
    /// </summary>
    private BoundExpr? BindFlags(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        if (left.Type is not DotNetType { Symbol.TypeKind: TypeKind.Enum } type || right.Type != left.Type
            || !type.Symbol.GetAttributes().Any(a => a.AttributeClass is { Name: "FlagsAttribute" } flags && flags.ContainingNamespace?.ToDisplayString() == "System"))
            return null;
        return op switch
        {
            BinaryOp.Union => new BoundDotNetFlags("|", left, right, type, span),
            BinaryOp.Intersection => new BoundDotNetFlags("&", left, right, type, span),
            BinaryOp.SymmetricDifference => new BoundDotNetFlags("^", left, right, type, span),
            BinaryOp.In => new BoundDotNetFlags("in", left, right, TaipanType.Bool, span),
            BinaryOp.NotIn => new BoundDotNetFlags("not in", left, right, TaipanType.Bool, span),
            _ => null,
        };
    }

    // ---- Calls and constructors (SPEC §13.4, §13.5) ----

    /// <summary>
    /// <c>T.m(…)</c> (static, <paramref name="target"/> null) or <c>x.m(…)</c>, with explicit type
    /// arguments if they're given (SPEC §13.8). A value whose type has no member named <c>m</c> may
    /// have an extension method (§13.15).
    /// </summary>
    private BoundExpr BindDotNetCall(CallExpr call, BoundExpr? target, DotNetType owner, Identifier name, ImmutableArray<TaipanType>? typeArguments = null)
    {
        if (!CheckConstructed(owner, ((MemberExpr)(call.Callee is IndexExpr explicitCallee ? explicitCallee.Target : call.Callee)).Target.Span))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        if (target is null && typeArguments is null && NestedType(owner.Symbol, name.Text) is { } nested)
            return BindDotNetConstruct(call, new DotNetType(nested, owner.TypeArguments), name.Span);
        var methods = Methods(owner.Symbol, name.Text, target is null);
        if (methods.Count == 0)
        {
            if (target is not null && !HasMember(owner.Symbol, name.Text, isStatic: false) && BindExtensionCall(call, target, name, typeArguments) is { } extension)
                return extension;
            BindArguments(call);
            if (EventNamed(owner.Symbol, name.Text, target is null) is not null)
                _diagnostics.ReportEventMisuse(name.Span, name.Text);
            else if (PropertyOrField(owner.Symbol, name.Text, target is null) is not null)
                _diagnostics.ReportFieldNotMethod(name.Span, name.Text);
            else
                _diagnostics.ReportNoMethod(name.Span, owner, name.Text);
            return new BoundErrorExpr(call.Span);
        }
        if (ResolveOverload(call, methods, MemberName(owner, name.Text), name.Span, owner.Map, null, typeArguments) is not var (method, arguments, map))
            return new BoundErrorExpr(call.Span);
        CheckNative(method, null, name.Span, MemberName(owner, name.Text));
        Index(name.Span, method);
        return new BoundDotNetCall(target, method, arguments, CallResult(method, map), call.Span) { TypeArguments = map, Owner = owner };
    }

    /// <summary>The .NET base class of a Taipan class or of a class above it (SPEC §13.16).</summary>
    private static DotNetType? DotNetBaseOf(ClassSymbol type)
    {
        for (var current = type; current is not null; current = current.Base)
        {
            if (current.DotNetBaseClass is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// <c>T[A](…)</c>, constructing a generic .NET type (SPEC §13.13), or <c>T.m[A](…)</c> and
    /// <c>x.m[A](…)</c>, calling a .NET method with explicit type arguments (§13.8). Null when the
    /// callee is neither, such as an element of a list of functions.
    /// </summary>
    private BoundExpr? BindExplicitlyTyped(CallExpr call, IndexExpr callee)
    {
        if (DotNetTypeNamed(callee) is { } created)
        {
            if (created is DotNetType type)
                return BindDotNetConstruct(call, type, callee.Target.Span);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        if (callee.Target is not MemberExpr member)
            return null;
        var written = (callee.Index is TupleExpr tuple ? tuple.Elements : [callee.Index]).Select(TypeFromExpr).ToList();
        if (written.Any(t => t is null))
            return null;
        var name = member.Member;
        if (DotNetTypeNamed(member.Target) is { } owner)
        {
            if (owner is not DotNetType staticOwner)
            {
                BindArguments(call);
                return new BoundErrorExpr(call.Span);
            }
            return BindDotNetCall(call, null, staticOwner, name, [.. written.Select(t => BindType(t!))]);
        }
        // An instance method: only when the receiver's type, or an extension, has a .NET method of that name.
        var speculated = SpeculateType(member.Target);
        var receiverOwner = speculated switch
        {
            DotNetType net => net,
            ListOfType list => ListAsDotNet(list),
            _ => null,
        };
        var hasMethod = receiverOwner is not null && Methods(receiverOwner.Symbol, name.Text, isStatic: false).Count > 0
            || ExtensionClasses().Any(c => c.GetMembers(name.Text).OfType<IMethodSymbol>().Any(m => m.IsExtensionMethod));
        if (!hasMethod)
            return null;
        var target = Upcast(BindValue(member.Target));
        var typeArguments = written.Select(t => BindType(t!)).ToImmutableArray();
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        if (target.Type is DotNetType dotnet)
            return BindDotNetCall(call, target, dotnet, name, typeArguments);
        if (target.Type is ListOfType listType && Methods(ListAsDotNet(listType).Symbol, name.Text, isStatic: false).Count > 0)
            return BindDotNetCall(call, target, ListAsDotNet(listType), name, typeArguments);
        if (BindExtensionCall(call, target, name, typeArguments) is { } extension)
            return extension;
        BindArguments(call);
        _diagnostics.ReportNoMethod(name.Span, target.Type, name.Text);
        return new BoundErrorExpr(call.Span);
    }

    /// <summary>An expression's type, bound without keeping anything, to decide how to bind what it's in.</summary>
    private TaipanType SpeculateType(Expr expr)
    {
        var diagnostics = _diagnostics;
        _diagnostics = new Diagnostics.DiagnosticBag { Source = _source, FileOrder = diagnostics.FileOrder };
        _speculating++;
        try
        {
            return BindValue(expr).Type;
        }
        finally
        {
            _speculating--;
            _diagnostics = diagnostics;
        }
    }

    /// <summary>The static classes the file imports by name, whose extension methods apply (SPEC §13.15).</summary>
    private IEnumerable<INamedTypeSymbol> ExtensionClasses() =>
        _global.Symbols.Select(p => p.Value).OfType<DotNetTypeSymbol>().SelectMany(s => s.Types).Where(t => t.IsStatic && t.MightContainExtensionMethods)
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);

    /// <summary>
    /// <c>x.m(…)</c> as an extension method of an imported static class, with <c>x</c> as its first
    /// argument (SPEC §13.15); null when no extension method named <c>m</c> takes <c>x</c>.
    /// </summary>
    private BoundExpr? BindExtensionCall(CallExpr call, BoundExpr receiver, Identifier name, ImmutableArray<TaipanType>? typeArguments)
    {
        var candidates = ExtensionClasses()
            .SelectMany(c => c.GetMembers(name.Text).OfType<IMethodSymbol>())
            .Where(m => m.IsExtensionMethod && m.DeclaredAccessibility == Accessibility.Public && !m.Parameters.IsEmpty && ReceiverFits(receiver, m))
            .ToList();
        if (candidates.Count == 0)
            return null;
        var member = $"{candidates[0].ContainingType.Name}.{name.Text}";
        if (ResolveOverload(call, candidates, member, name.Span, null, receiver, typeArguments) is not var (method, arguments, map))
            return new BoundErrorExpr(call.Span);
        CheckNative(method, null, name.Span, member);
        Index(name.Span, method);
        return new BoundDotNetCall(null, method, arguments, CallResult(method, map), call.Span)
        {
            TypeArguments = map,
            Owner = new DotNetType(method.ContainingType),
        };
    }

    /// <summary>Whether an extension method's first parameter takes the receiver, inferring what it can from it.</summary>
    private bool ReceiverFits(BoundExpr receiver, IMethodSymbol method)
    {
        if (UnsupportedIn(method, null) is not null)
            return false;
        var parameter = DotNetTypes.TypeOf(method.Parameters[0]);
        if (!method.IsGenericMethod)
            return DotNetTypes.Fit(receiver.Type, parameter) != DotNetFit.None;
        var (placeholders, symbolic) = Placeholders(method);
        var map = new Dictionary<TypeParameterSymbol, TaipanType>();
        var inference = new TypeInference(placeholders, map, new Diagnostics.DiagnosticBag { Source = _source });
        if (DotNetTypes.Map(parameter, result: false, out _, symbolic) is not { } mapped)
            return false;
        inference.Unify(mapped, InferenceView(receiver.Type, parameter), receiver.Span);
        var arguments = new TypeArguments(method.TypeParameters.Select((p, k) => KeyValuePair.Create(p, map.GetValueOrDefault(placeholders[k]) ?? placeholders[k].Type)));
        return DotNetTypes.Fit(receiver.Type, parameter, arguments) != DotNetFit.None;
    }

    /// <summary>
    /// What an argument's type is matched against a parameter as, inferring type arguments: a .NET
    /// value as the base or interface the parameter names, so a <c>HashSet[str]</c> gives an
    /// <c>IEnumerable&lt;TSource&gt;</c> its <c>str</c> (SPEC §13.13).
    /// </summary>
    private static TaipanType InferenceView(TaipanType argument, ITypeSymbol parameter)
    {
        if (argument is DotNetType value && parameter is INamedTypeSymbol { IsGenericType: true } named
            && DotNetTypes.BaseNamed(value.Symbol, named) is { } found
            && DotNetTypes.Map(found, result: false, out _, value.Map) is { } seen)
            return seen;
        return argument;
    }

    /// <summary>The value of a call (SPEC §13.14): the result, with the <c>out</c> and <c>ref</c> parameters' values after it in a tuple.</summary>
    private static TaipanType CallResult(IMethodSymbol method, TypeArguments? map)
    {
        var result = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _, map)!;
        var outs = method.Parameters.Where(p => p.RefKind is RefKind.Out or RefKind.Ref)
            .Select(p => DotNetTypes.Map(DotNetTypes.TypeOf(p), result: true, out _, map)!).ToList();
        if (outs.Count == 0)
            return result;
        ImmutableArray<TaipanType> elements = result is NothingType ? [.. outs] : [result, .. outs];
        return elements.Length == 1 ? elements[0] : new TupleOfType(elements);
    }

    /// <summary><c>T(…)</c>: a public constructor, or a struct's default value (SPEC §13.4).</summary>
    private BoundExpr BindDotNetConstruct(CallExpr call, DotNetType created, TextSpan nameSpan)
    {
        if (!CheckConstructed(created, nameSpan))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        var type = created.Symbol;
        var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        if (type.IsStatic || type.IsAbstract || type.TypeKind is TypeKind.Interface or TypeKind.Enum || (constructors.Count == 0 && !type.IsValueType))
        {
            BindArguments(call);
            if (type.IsStatic)
                _diagnostics.ReportStaticClass(nameSpan, type.Name);
            else
                _diagnostics.ReportCannotConstruct(nameSpan, created.ToString());
            return new BoundErrorExpr(call.Span);
        }
        if (type.IsValueType && call.Arguments.IsEmpty && !constructors.Any(c => c.Parameters.IsEmpty))
            return new BoundDotNetNew(created, null, [], call.Span);
        if (ResolveOverload(call, constructors, created.ToString(), nameSpan, created.Map, null, null) is not var (constructor, arguments, _))
            return new BoundErrorExpr(call.Span);
        CheckNative(constructor, null, nameSpan, created.ToString());
        return new BoundDotNetNew(created, constructor, arguments, call.Span);
    }

    /// <summary>An overload with which parameter each argument is for, and whether its <c>params</c> parameter is expanded (SPEC §13.14).</summary>
    private sealed record Overload(IMethodSymbol Method, int[] Shape, bool Expanded)
    {
        /// <summary>The <c>CancellationToken</c> parameters left out, which are given the async function's token (SPEC §13.20).</summary>
        public int[] Tokens { get; init; } = [];
    }

    // Whether a call here may leave out a CancellationToken, which the async function's own fills (SPEC §13.20, M43).
    private bool PassesToken => _lambdaDepth == 0 && (_function is { IsAsync: true } || _function is null && _inTopLevel);

    private static bool IsCancellationToken(IParameterSymbol parameter) =>
        parameter.RefKind == RefKind.None && parameter.Type is INamedTypeSymbol { Name: "CancellationToken", Arity: 0 } token
        && token.ContainingNamespace?.ToDisplayString() == "System.Threading";

    // Arguments needing their parameter's function type to be bound: lambdas, and generic functions, built-ins and constructors as values (SPEC §3.18).
    private bool Deferred(Expr argument) => argument is LambdaExpr || CallableValueTarget(argument) is { } target && NeedsExpectedType(target);

    /// <summary>
    /// Picks the overload a call means (SPEC §13.5) and binds its arguments, reporting why if there's
    /// none: a member Taipan can't use (TP0231), argument errors, no overload, or ambiguity (TP0232).
    /// A generic method's type arguments are inferred (SPEC §13.8) unless they're given, and a lambda
    /// is bound with each candidate's parameter types, then for real with the chosen one's.
    /// <paramref name="owner"/> holds a generic type's type arguments, and <paramref name="receiver"/>
    /// is an extension method's first argument (§13.15).
    /// </summary>
    private (IMethodSymbol Method, ImmutableArray<BoundDotNetArgument> Arguments, TypeArguments? TypeArguments)? ResolveOverload(
        CallExpr call, List<IMethodSymbol> candidates, string member, TextSpan nameSpan, TypeArguments? owner, BoundExpr? receiver,
        ImmutableArray<TaipanType>? typeArguments)
    {
        if (typeArguments is { } given && candidates.Where(m => m.Arity == given.Length).ToList() is var withArity && withArity.Count != candidates.Count)
        {
            if (withArity.Count == 0)
            {
                BindArguments(call);
                _diagnostics.ReportMethodTypeArgumentCount(nameSpan, member, candidates[0].Arity, given.Length);
                return null;
            }
            candidates = withArity;
        }
        var usable = candidates.Where(m => UnsupportedIn(m, owner) is null).ToList();
        if (usable.Count == 0)
        {
            BindArguments(call);
            _diagnostics.ReportDotNetUnsupported(nameSpan, member, UnsupportedIn(candidates[0], owner)!);
            return null;
        }

        var shaped = usable.SelectMany(m => Shapes(call, m, receiver is not null, PassesToken)).ToList();
        if (receiver is null && typeArguments is null && shaped is [{ Method.IsGenericMethod: false, Expanded: false, Tokens: [] } only])
        {
            // One overload takes these arguments: they're checked against its parameters, as for a Taipan function.
            var (method, shape) = (only.Method, only.Shape);
            var arguments = call.Arguments.Select((a, i) =>
            {
                var parameter = method.Parameters[shape[i]];
                var value = BindExpected(a.Value, DotNetTypes.Map(DotNetTypes.TypeOf(parameter), result: false, out _, owner)!);
                CheckLiteralFits(value, DotNetTypes.TypeOf(parameter));
                return new BoundDotNetArgument(parameter, value);
            }).ToImmutableArray();
            return (method, arguments, owner);
        }
        if (shaped.Count == 0)
        {
            if (usable.Count == 1 && receiver is null)
                ReportShape(call, usable[0], member, nameSpan);
            var types = BindArguments(call);
            if ((usable.Count > 1 || receiver is not null) && !types.Any(t => t.Type.ContainsError))
                _diagnostics.ReportNoOverload(nameSpan, member, types.Select(t => t.Type));
            return null;
        }

        // Deferred arguments are bound for each candidate; every other argument once.
        IEnumerable<Expr> syntax = call.Arguments.Select(a => a.Value);
        IEnumerable<BoundExpr?> values = call.Arguments.Select(a => Deferred(a.Value) ? null : BindValue(a.Value));
        if (receiver is not null)
        {
            syntax = syntax.Prepend(new ErrorExpr(receiver.Span));
            values = values.Prepend(receiver);
        }
        var bound = values.ToArray();
        if (bound.Any(v => v is not null && v.Type.ContainsError))
            return null;
        return ChooseOverload([.. syntax], shaped, bound, member, nameSpan, owner, typeArguments);
    }

    /// <summary>The ways a call's arguments can fill an overload's parameters: its normal form, and its expanded one for a <c>params</c> parameter.</summary>
    private static IEnumerable<Overload> Shapes(CallExpr call, IMethodSymbol method, bool receiver, bool passToken)
    {
        int[] Tokens(int[] shape) => passToken
            ? [.. Enumerable.Range(0, method.Parameters.Length).Where(i => !shape.Contains(i) && IsCancellationToken(method.Parameters[i]))]
            : [];
        if (ShapeOf(call, method, receiver, expanded: false, passToken) is { } normal)
            yield return new Overload(method, normal, false) { Tokens = Tokens(normal) };
        if (IsParamsList(method) && ShapeOf(call, method, receiver, expanded: true, passToken) is { } expanded)
            yield return new Overload(method, expanded, true) { Tokens = Tokens(expanded) };
    }

    // A params parameter Taipan takes: one whose type maps to a list (SPEC §13.14).
    private static bool IsParamsList(IMethodSymbol method) =>
        method.Parameters.LastOrDefault() is { IsParams: true } last
        && (last.Type is IArrayTypeSymbol { Rank: 1 } || last.Type is INamedTypeSymbol named && DotNetTypes.IsListLike(named));

    /// <summary>The .NET type an argument in a parameter's place converts to: the element type for an expanded <c>params</c> argument.</summary>
    private static ITypeSymbol SlotType(IMethodSymbol method, int parameter, bool expanded)
    {
        var type = DotNetTypes.TypeOf(method.Parameters[parameter]);
        if (!expanded || parameter != method.Parameters.Length - 1 || !method.Parameters[parameter].IsParams)
            return type;
        return type is IArrayTypeSymbol array ? array.ElementType : ((INamedTypeSymbol)type).TypeArguments[0];
    }

    /// <summary>
    /// The overload no other is better than, among those that take <paramref name="values"/>
    /// (null for each deferred argument in <paramref name="syntax"/>), with its arguments bound (SPEC §13.5).
    /// </summary>
    private (IMethodSymbol Method, ImmutableArray<BoundDotNetArgument> Arguments, TypeArguments? TypeArguments)? ChooseOverload(
        IReadOnlyList<Expr> syntax, List<Overload> shaped, BoundExpr?[] values, string member, TextSpan nameSpan, TypeArguments? owner,
        ImmutableArray<TaipanType>? typeArguments)
    {
        var single = shaped.Count == 1;
        var plans = shaped.Select(c => Plan(syntax, c, values, member, nameSpan, report: single, owner, typeArguments)).OfType<OverloadPlan>().ToList();
        // A params overload's expanded form applies only when its normal form doesn't, as in C# (SPEC §13.14).
        plans = [.. plans.Where(p => !p.Overload.Expanded || !plans.Any(o => !o.Overload.Expanded && SymbolEqualityComparer.Default.Equals(o.Overload.Method, p.Overload.Method)))];
        if (plans.Count == 0)
        {
            if (!single)
                _diagnostics.ReportNoOverload(nameSpan, member, values.Select(v => v?.Type ?? TaipanType.Error));
            return null;
        }

        // The chosen overload is the one no other is better than (SPEC §13.5, §13.8).
        var best = plans.Where(c => !plans.Any(o => o != c && Better(o, c))).ToList();
        if (best.Count != 1)
        {
            _diagnostics.ReportAmbiguousDotNetCall(nameSpan, member, Signature(best[0].Overload.Method), Signature(best[1].Overload.Method));
            return null;
        }
        return Finish(syntax, best[0], values, member, nameSpan);
    }

    /// <summary>A candidate that takes a call's arguments: its type arguments, each argument's fit, and each argument's parameter type.</summary>
    private sealed record OverloadPlan(Overload Overload, TypeArguments? TypeArguments, DotNetFit[] Fits, TaipanType?[] Parameters);

    private static bool Better(OverloadPlan a, OverloadPlan b)
    {
        var atLeast = a.Fits.Zip(b.Fits).All(p => p.First >= p.Second);
        if (!atLeast)
            return false;
        if (a.Fits.Zip(b.Fits).Any(p => p.First > p.Second))
            return true;
        var (omittedA, omittedB) = (Omitted(a.Overload), Omitted(b.Overload));
        if (omittedA != omittedB)
            return omittedA < omittedB;
        // Fitting the same, one that takes the async function's token is better (SPEC §13.20).
        if (a.Overload.Tokens.Length != b.Overload.Tokens.Length)
            return a.Overload.Tokens.Length > b.Overload.Tokens.Length;
        // Fitting the same, a normal form beats an expanded one (SPEC §13.14).
        if (a.Overload.Expanded != b.Overload.Expanded)
            return !a.Overload.Expanded;
        // A non-generic overload is better, and of generic ones the more specific (SPEC §13.5).
        return GenericRank(a.Overload.Method) < GenericRank(b.Overload.Method);
    }

    // Parameters with defaults left out; out parameters, and an expanded params parameter, don't count (SPEC §13.5, §13.14).
    private static int Omitted(Overload candidate) =>
        candidate.Method.Parameters.Where((p, i) => p.RefKind != RefKind.Out && !candidate.Shape.Contains(i) && !candidate.Tokens.Contains(i)
            && !(candidate.Expanded && p.IsParams)).Count();

    private static int GenericRank(IMethodSymbol method) =>
        method.IsGenericMethod ? 1 + method.OriginalDefinition.Parameters.Sum(p => Mentions(p.Type)) : 0;

    private static int Mentions(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => 1,
        IArrayTypeSymbol array => Mentions(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Sum(Mentions),
        _ => 0,
    };

    /// <summary>
    /// Whether a candidate takes the arguments, inferring its type arguments (SPEC §13.8) unless
    /// they're given: from each argument that isn't deferred, then from deferred ones whose parameter
    /// types are known. With <paramref name="report"/>, the only candidate's problems are reported.
    /// </summary>
    private OverloadPlan? Plan(IReadOnlyList<Expr> syntax, Overload candidate, BoundExpr?[] values, string member, TextSpan nameSpan, bool report,
        TypeArguments? owner, ImmutableArray<TaipanType>? typeArguments)
    {
        var (method, shape, expanded) = candidate;
        var (placeholders, symbolic) = typeArguments is { } given
            ? ([], new TypeArguments(method.TypeParameters.Zip(given, KeyValuePair.Create)))
            : Placeholders(method);
        var parameterMap = TypeArguments.Merge(owner, symbolic);
        var map = new Dictionary<TypeParameterSymbol, TaipanType>();
        var diagnostics = report ? _diagnostics : new Diagnostics.DiagnosticBag { Source = _source };
        var reported = diagnostics.ToImmutable().Count(d => !d.IsWarning);
        var inference = new TypeInference(placeholders, map, diagnostics);
        var slots = shape.Select(p => SlotType(method, p, expanded)).ToArray();
        var parameterTypes = slots.Select(t => DotNetTypes.Map(t, result: false, out _, parameterMap)!).ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value)
                inference.Unify(parameterTypes[i], InferenceView(value.Type, slots[i]), syntax[i].Span);
        }

        var deferredResults = new TaipanType?[values.Length];
        var pending = Enumerable.Range(0, values.Length).Where(i => values[i] is null).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(i => TypeChecker.Unwrap(parameterTypes[i]) is not FunctionType callable || callable.Parameters.All(inference.IsKnown), -1);
            if (ready < 0)
                break;
            pending.Remove(ready);
            if (TypeChecker.Unwrap(parameterTypes[ready]) is not FunctionType function
                || syntax[ready] is LambdaExpr lambda && function.Parameters.Length != lambda.Parameters.Length)
            {
                if (report)
                    _diagnostics.ReportTypeMismatch(syntax[ready].Span, parameterTypes[ready], syntax[ready] is LambdaExpr ? "lambda" : "function");
                return null;
            }
            var parameters = function.Parameters.Select(p => TypeChecker.Substitute(p, map)).ToImmutableArray();
            var result = SpeculateDeferred(syntax[ready], parameters, function.Result is NothingType);
            // A function value that can't be called with these parameters rules the candidate out.
            if (syntax[ready] is not LambdaExpr && result.ContainsError && !report)
                return null;
            deferredResults[ready] = result;
            if (!inference.IsKnown(function.Result))
                inference.Unify(function.Result, result, syntax[ready] is LambdaExpr bodied ? bodied.Body.Span : syntax[ready].Span);
        }
        if (pending.Count > 0)
        {
            if (report)
            {
                if (syntax[pending[0]] is LambdaExpr { Parameters: [var parameter, ..] })
                    _diagnostics.ReportCannotInferLambdaParameter(parameter.Span, parameter.Text);
                else if (syntax[pending[0]] is not LambdaExpr)
                    _diagnostics.ReportCannotInferValue(syntax[pending[0]].Span, _source.ToString(syntax[pending[0]].Span));
            }
            return null;
        }
        foreach (var parameter in placeholders.Where(p => !map.ContainsKey(p)))
        {
            if (report)
                _diagnostics.ReportCannotInferTypeArgument(nameSpan, parameter.Name, member);
            return null;
        }
        // A type argument inferred two ways was reported, or rules this candidate out.
        if (diagnostics.ToImmutable().Count(d => !d.IsWarning) > reported)
            return null;

        var methodArguments = !method.IsGenericMethod ? null
            : typeArguments is not null ? symbolic
            : new TypeArguments(method.TypeParameters.Select((p, k) => KeyValuePair.Create(p, map[placeholders[k]])));
        var all = TypeArguments.Merge(owner, methodArguments);
        var fits = new DotNetFit[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            fits[i] = values[i] is { } value ? DotNetTypes.Fit(value.Type, slots[i], all)
                : DeferredFit(deferredResults[i]!, (INamedTypeSymbol)slots[i], all);
            // The only candidate's arguments are checked against it when they're bound.
            if (fits[i] == DotNetFit.None && !report)
                return null;
        }
        var concrete = parameterTypes.Select(t => TypeChecker.Substitute(t, map)).ToArray();
        return new OverloadPlan(candidate, all, fits, concrete);
    }

    /// <summary>A lambda's or function value's fit is its result's, to the delegate's result type (SPEC §13.5).</summary>
    private static DotNetFit DeferredFit(TaipanType result, INamedTypeSymbol callable, TypeArguments? typeArguments) =>
        callable.DelegateInvokeMethod is not { } invoke || invoke.ReturnsVoid ? DotNetFit.Exact
            : DotNetTypes.Fit(result, DotNetTypes.ResultOf(invoke), typeArguments);

    /// <summary>Binds the chosen overload's arguments: deferred ones with its parameter types, and checks its constraints.</summary>
    private (IMethodSymbol, ImmutableArray<BoundDotNetArgument>, TypeArguments?)? Finish(
        IReadOnlyList<Expr> syntax, OverloadPlan plan, BoundExpr?[] values, string member, TextSpan nameSpan)
    {
        var (method, shape, expanded) = plan.Overload;
        var arguments = ImmutableArray.CreateBuilder<BoundDotNetArgument>();
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = method.Parameters[shape[i]];
            var isExpanded = expanded && parameter.IsParams;
            var expected = plan.Parameters[i]!;
            BoundExpr value;
            if (values[i] is { } bound)
            {
                value = bound;
                if (plan.Fits[i] == DotNetFit.None && !expected.ContainsError)
                    _diagnostics.ReportTypeMismatch(bound.Span, expected, bound.Type);
                if (!method.IsGenericMethod)
                    CheckLiteralFits(value, SlotType(method, shape[i], expanded));
            }
            else
            {
                var function = (FunctionType)TypeChecker.Unwrap(expected);
                value = syntax[i] is LambdaExpr lambda
                    ? BindLambdaAs(lambda, function.Parameters, function.Result)
                    : BindCallableAs(syntax[i], CallableValueTarget(syntax[i])!, syntax[i].Span, function.Parameters, function.Result);
            }
            arguments.Add(new BoundDotNetArgument(parameter, value, isExpanded));
        }
        foreach (var token in plan.Overload.Tokens)
            arguments.Add(new BoundDotNetArgument(method.Parameters[token], new BoundCancellationToken(new DotNetType((INamedTypeSymbol)method.Parameters[token].Type), nameSpan)));
        if (method.IsGenericMethod && plan.TypeArguments is { } typeArguments && !SatisfiesConstraints(method.TypeParameters, typeArguments, member, nameSpan))
            return null;
        return (method, arguments.ToImmutable(), plan.TypeArguments);
    }

    // Taipan type parameters standing for a generic method's own, while its type arguments are inferred.
    private readonly Dictionary<IMethodSymbol, (ImmutableArray<TypeParameterSymbol>, TypeArguments)> _placeholders = new(SymbolEqualityComparer.Default);

    private (ImmutableArray<TypeParameterSymbol> Placeholders, TypeArguments Symbolic) Placeholders(IMethodSymbol method)
    {
        if (_placeholders.TryGetValue(method, out var found))
            return found;
        ImmutableArray<TypeParameterSymbol> placeholders = [.. method.TypeParameters.Select(p => new TypeParameterSymbol(p.Name))];
        var symbolic = new TypeArguments(method.TypeParameters.Select((p, k) => KeyValuePair.Create(p, (TaipanType)placeholders[k].Type)));
        return _placeholders[method] = (placeholders, symbolic);
    }

    // Binding lambdas for candidates that may not be chosen: their diagnostics and names aren't kept.
    private int _speculating;

    /// <summary>A lambda's or function value's result type with these parameter types, binding it without keeping anything.</summary>
    private TaipanType SpeculateDeferred(Expr argument, ImmutableArray<TaipanType> parameters, bool discardsResult)
    {
        var diagnostics = _diagnostics;
        _diagnostics = new Diagnostics.DiagnosticBag { Source = _source, FileOrder = diagnostics.FileOrder };
        _speculating++;
        try
        {
            var result = discardsResult ? TaipanType.Nothing : null;
            var bound = argument is LambdaExpr lambda
                ? BindLambdaAs(lambda, parameters, result)
                : BindCallableAs(argument, CallableValueTarget(argument)!, argument.Span, parameters, result);
            return bound is BoundLambda { FunctionType.Result: var type } && !_diagnostics.ToImmutable().Any(d => !d.IsWarning) ? type : TaipanType.Error;
        }
        finally
        {
            _speculating--;
            _diagnostics = diagnostics;
        }
    }

    /// <summary>
    /// Type parameters' constraints, checked against each type argument's .NET form (SPEC §13.8,
    /// §13.13): <c>class</c>, <c>struct</c>, <c>new()</c>, and base classes or interfaces.
    /// </summary>
    private bool SatisfiesConstraints(IEnumerable<ITypeParameterSymbol> parameters, TypeArguments typeArguments, string member, TextSpan nameSpan)
    {
        var symbols = new Dictionary<ITypeParameterSymbol, ITypeSymbol?>(SymbolEqualityComparer.Default);
        foreach (var (parameter, argument) in typeArguments)
            symbols[parameter] = NetSymbolOf(argument);
        foreach (var parameter in parameters)
        {
            var argument = typeArguments[parameter];
            if (argument.ContainsError)
                continue;
            var symbol = symbols[parameter];
            var isValue = argument is PrimitiveType { Name: "int" or "float" or "bool" } or TupleOfType || symbol is { IsValueType: true };
            string? unmet = null;
            if (parameter.HasReferenceTypeConstraint && isValue)
                unmet = "class";
            else if ((parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint) && (!isValue || argument is OptionalOf))
                unmet = parameter.HasUnmanagedTypeConstraint ? "unmanaged" : "struct";
            else if (parameter.HasConstructorConstraint && !isValue
                && !(symbol is INamedTypeSymbol { IsAbstract: false } type && type.InstanceConstructors.Any(c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public)))
                unmet = "new()";
            else
            {
                foreach (var constraint in parameter.ConstraintTypes)
                {
                    if (!MeetsConstraint(argument, symbol, constraint, symbols, typeArguments))
                    {
                        unmet = DotNetTypes.Display(constraint);
                        break;
                    }
                }
            }
            if (unmet is not null)
            {
                _diagnostics.ReportUnmetConstraint(nameSpan, argument, unmet, parameter.Name, member);
                return false;
            }
        }
        return true;
    }

    // A base class or interface constraint, met by a .NET symbol, a generic .NET type, or a Taipan class implementing it (SPEC §13.16).
    private static bool MeetsConstraint(TaipanType argument, ITypeSymbol? symbol, ITypeSymbol constraint,
        Dictionary<ITypeParameterSymbol, ITypeSymbol?> symbols, TypeArguments typeArguments)
    {
        if (constraint is INamedTypeSymbol named && DotNetTypes.Map(named, result: false, out _, typeArguments) is DotNetType required)
        {
            if (argument is DotNetType net)
                return DotNetTypes.IsBase(net, required);
            if (argument is ClassType classType)
                return DotNetTypes.ClassIsBase(classType, required);
        }
        return symbol is not null && Construct(constraint, symbols) is { } constructed
            && (SymbolEqualityComparer.Default.Equals(symbol, constructed) || symbol is INamedTypeSymbol from && constructed is INamedTypeSymbol target && DotNetTypes.Derives(from, target));
    }

    /// <summary>A Taipan type's .NET type, for one that has a symbol in the metadata: built-in types and .NET types that aren't generic.</summary>
    private ITypeSymbol? NetSymbolOf(TaipanType type) => type switch
    {
        PrimitiveType { Name: "int" } => DotNet.Special(SpecialType.System_Int64),
        PrimitiveType { Name: "float" } => DotNet.Special(SpecialType.System_Double),
        PrimitiveType { Name: "bool" } => DotNet.Special(SpecialType.System_Boolean),
        PrimitiveType { Name: "str" } => DotNet.Special(SpecialType.System_String),
        DotNetType { IsGeneric: false } net => net.Symbol,
        _ => null,
    };

    // A constraint with the type arguments put in, when each has a .NET symbol.
    private static ITypeSymbol? Construct(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol?> symbols) => type switch
    {
        ITypeParameterSymbol parameter => symbols.GetValueOrDefault(parameter),
        INamedTypeSymbol { IsGenericType: true } named => named.TypeArguments.Select(a => Construct(a, symbols)).ToList() is var arguments && arguments.All(a => a is not null)
            ? named.OriginalDefinition.Construct([.. arguments!])
            : null,
        _ => type,
    };

    /// <summary>
    /// The type that keeps Taipan from using an overload (TP0231), or null if it can: an unmapped
    /// type, a <c>ref</c> return, or since M43 a constructor's <c>ref</c> or <c>out</c> parameter.
    /// A generic method's type parameters stand for whatever is inferred (SPEC §13.8), and a generic
    /// type's for its type arguments (§13.13).
    /// </summary>
    private string? UnsupportedIn(IMethodSymbol method, TypeArguments? owner)
    {
        var symbolic = TypeArguments.Merge(owner, method.IsGenericMethod ? Placeholders(method).Symbolic : null);
        foreach (var parameter in method.Parameters)
        {
            var refKind = parameter.RefKind == RefKind.RefReadOnlyParameter ? "ref readonly" : parameter.RefKind.ToString().ToLowerInvariant();
            if (parameter.RefKind is RefKind.Out or RefKind.Ref && method.MethodKind != MethodKind.Ordinary)
                return $"{refKind} {DotNetTypes.Display(parameter.Type)}";
            var type = DotNetTypes.TypeOf(parameter);
            // What .NET gives back in an out or ref parameter is a result (SPEC §13.14).
            if (parameter.RefKind is RefKind.Out or RefKind.Ref && DotNetTypes.Map(type, result: true, out var given, symbolic) is null)
                return DotNetTypes.Display(given ?? parameter.Type);
            if (parameter.RefKind != RefKind.Out && DotNetTypes.Map(type, result: false, out var unsupported, symbolic) is null)
                return DotNetTypes.Display(unsupported ?? parameter.Type);
        }
        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            return $"ref {DotNetTypes.Display(method.ReturnType)}";
        if (method.MethodKind != MethodKind.Constructor && DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out var result, symbolic) is null)
            return DotNetTypes.Display(result ?? method.ReturnType);
        return null;
    }

    /// <summary>
    /// Which parameter each argument is for, matched as for a Taipan function (SPEC §4.2), or null if
    /// they don't fit: too many, an unknown or repeated keyword, or a parameter without a default left
    /// out. <c>out</c> parameters aren't given, an extension method's first parameter is the
    /// receiver's, and an expanded <c>params</c> parameter takes every positional argument from its place on (SPEC §13.14, §13.15).
    /// </summary>
    private static int[]? ShapeOf(CallExpr call, IMethodSymbol method, bool receiver, bool expanded, bool passToken)
    {
        var parameters = method.Parameters;
        var paramsIndex = expanded ? parameters.Length - 1 : -1;
        var shape = new List<int>();
        if (receiver)
            shape.Add(0);
        var next = receiver ? 1 : 0;
        foreach (var argument in call.Arguments.TakeWhile(a => a.Name is null))
        {
            while (next < parameters.Length && next != paramsIndex && parameters[next].RefKind == RefKind.Out)
                next++;
            if (next >= parameters.Length)
                return null;
            shape.Add(next);
            if (next != paramsIndex)
                next++;
        }
        foreach (var argument in call.Arguments.SkipWhile(a => a.Name is null))
        {
            var index = parameters.IndexOf(parameters.FirstOrDefault(p => p.Name == argument.Name!.Text)!);
            if (index < 0 || index == paramsIndex || (receiver && index == 0) || parameters[index].RefKind == RefKind.Out || shape.Contains(index))
                return null;
            shape.Add(index);
        }
        return Enumerable.Range(0, parameters.Length).All(i => shape.Contains(i) || parameters[i].IsOptional || parameters[i].RefKind == RefKind.Out || i == paramsIndex
                || passToken && IsCancellationToken(parameters[i]))
            ? [.. shape]
            : null;
    }

    /// <summary>Why the arguments don't fit the only overload, reported as for a Taipan function.</summary>
    private void ReportShape(CallExpr call, IMethodSymbol method, string member, TextSpan nameSpan)
    {
        var parameters = method.Parameters.Where(p => p.RefKind != RefKind.Out).ToList();
        var required = parameters.Count(p => !p.IsOptional);
        var positional = call.Arguments.TakeWhile(a => a.Name is null).Count();
        if (positional > parameters.Count || (positional == call.Arguments.Length && positional < required))
        {
            var expected = required == parameters.Count ? Arguments(parameters.Count) : $"{required} to {parameters.Count} arguments";
            _diagnostics.ReportWrongArgumentCount(nameSpan, member, expected, positional);
            return;
        }
        var given = new HashSet<string>(parameters.Take(positional).Select(p => p.Name));
        foreach (var argument in call.Arguments.Skip(positional))
        {
            var keyword = argument.Name!;
            if (parameters.All(p => p.Name != keyword.Text))
            {
                _diagnostics.ReportNoKeywordArgument(keyword.Span, member, keyword.Text);
                return;
            }
            if (!given.Add(keyword.Text))
            {
                _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                return;
            }
        }
        foreach (var missing in parameters.Where(p => !p.IsOptional && !given.Contains(p.Name)))
            _diagnostics.ReportMissingArgument(nameSpan, missing.Name);
    }

    private static string Signature(IMethodSymbol method) =>
        $"{(method.MethodKind == MethodKind.Constructor ? method.ContainingType.Name : method.Name)}({string.Join(", ", method.Parameters.Select(p => DotNetTypes.Display(DotNetTypes.TypeOf(p))))})";

    /// <summary>A literal argument for a narrower integer or a char must fit it (SPEC §13.3, TP0200).</summary>
    private void CheckLiteralFits(BoundExpr value, ITypeSymbol parameter)
    {
        if (parameter is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            parameter = nullable.TypeArguments[0];
        var constant = value switch
        {
            BoundLiteral { Value: long l } => l,
            BoundUnary { Op: UnaryOp.Negate, Operand: BoundLiteral { Value: long n } } => -n,
            _ => (long?)null,
        };
        if (constant is { } number && DotNetTypes.IsNarrowInteger(parameter) && !InRange(parameter.SpecialType, number))
            _diagnostics.ReportOutOfRangeFor(value.Span, number.ToString(System.Globalization.CultureInfo.InvariantCulture), parameter.Name);
        if (value is BoundLiteral { Value: string text } && parameter.SpecialType == SpecialType.System_Char && text.Length != 1)
            _diagnostics.ReportOutOfRangeFor(value.Span, Runtime.Display.Repr(text), parameter.Name);
    }

    private static bool InRange(SpecialType type, long value) => type switch
    {
        SpecialType.System_Int32 => value is >= int.MinValue and <= int.MaxValue,
        SpecialType.System_Int16 => value is >= short.MinValue and <= short.MaxValue,
        SpecialType.System_SByte => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        SpecialType.System_Byte => value is >= 0 and <= byte.MaxValue,
        SpecialType.System_UInt16 => value is >= 0 and <= ushort.MaxValue,
        SpecialType.System_UInt32 => value is >= 0 and <= uint.MaxValue,
        SpecialType.System_UInt64 => value >= 0,
        _ => true,
    };
}
