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

    /// <summary><c>from dotnet.&lt;namespace&gt; import T, …</c> (SPEC §13.1).</summary>
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
            var types = namespaceSymbol.GetTypeMembers(name.Name.Text).Where(t => t.DeclaredAccessibility == Accessibility.Public).ToList();
            if (types.FirstOrDefault(t => t.Arity == 0) is not { } type)
            {
                if (types.Count > 0)
                    _diagnostics.ReportGenericDotNetImport(name.Name.Span, name.Name.Text);
                else
                    _diagnostics.ReportNoDotNetType(name.Name.Span, ns, name.Name.Text);
                continue;
            }
            var bound = name.Alias ?? name.Name;
            DeclareGlobal(bound, new DotNetTypeSymbol(bound.Text, type), isDefinition: false);
        }
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

    /// <summary>The .NET type an expression names, if it names one: an imported type, or a type nested in one.</summary>
    private INamedTypeSymbol? DotNetTypeNamed(Expr expr) => expr switch
    {
        NameExpr name when !_typeParameters.ContainsKey(name.Name) => (_scope.Lookup(name.Name) as DotNetTypeSymbol)?.Type,
        MemberExpr member when DotNetTypeNamed(member.Target) is { } outer => NestedType(outer, member.Member.Text),
        _ => null,
    };

    private static INamedTypeSymbol? NestedType(INamedTypeSymbol outer, string name) =>
        outer.GetTypeMembers(name).FirstOrDefault(t => t.Arity == 0 && t.DeclaredAccessibility == Accessibility.Public);

    private static string MemberName(INamedTypeSymbol owner, string name) => $"{new DotNetType(owner)}.{name}";

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

    /// <summary><c>T.m</c> or <c>x.m</c> as a value: a property, a field or an enum member (SPEC §13.4).</summary>
    private BoundExpr BindDotNetMember(BoundExpr? target, INamedTypeSymbol owner, MemberExpr member)
    {
        var name = member.Member.Text;
        if (target is null && NestedType(owner, name) is not null)
        {
            _diagnostics.ReportTypeAsValue(member.Span, name);
            return new BoundErrorExpr(member.Span);
        }
        if (PropertyOrField(owner, name, target is null) is not { } found)
        {
            if (Methods(owner, name, target is null).Count > 0)
                _diagnostics.ReportMethodAsValue(member.Member.Span, name);
            else
                _diagnostics.ReportNoField(member.Member.Span, new DotNetType(owner), name);
            return new BoundErrorExpr(member.Span);
        }
        if (DotNetTypes.Map(DotNetTypes.TypeOf(found), result: true, out var unsupported) is not { } type)
        {
            _diagnostics.ReportDotNetUnsupported(member.Member.Span, MemberName(owner, name), DotNetTypes.Display(unsupported!));
            return new BoundErrorExpr(member.Span);
        }
        CheckNative(found, (found as IPropertySymbol)?.GetMethod, member.Member.Span, MemberName(owner, name));
        return new BoundDotNetMember(target, found, type, member.Span);
    }

    /// <summary><c>x.P = v</c> or <c>T.P = v</c>: a settable property or a field that isn't read-only (SPEC §13.4).</summary>
    private BoundStmt BindDotNetAssign(AssignStmt assign, BoundExpr? target, INamedTypeSymbol owner, MemberExpr member, BoundExpr? preset)
    {
        var name = member.Member.Text;
        if (PropertyOrField(owner, name, target is null) is not { } found)
        {
            _diagnostics.ReportNoField(member.Member.Span, new DotNetType(owner), name);
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
        if (DotNetTypes.Map(netType, result: false, out var unsupported) is not { } type)
        {
            _diagnostics.ReportDotNetUnsupported(member.Member.Span, MemberName(owner, name), DotNetTypes.Display(unsupported!));
            return OrphanAssign(assign, preset);
        }
        CheckNative(found, (found as IPropertySymbol)?.SetMethod, member.Member.Span, MemberName(owner, name));
        var current = new BoundDotNetMember(target, found, DotNetTypes.Map(netType, result: true, out _) ?? type, member.Span);
        var value = BindAssignedValue(assign, current, type, preset);
        CheckLiteralFits(value, netType);
        return new BoundDotNetAssign(target, found, value, assign.Op != AssignOp.Assign, assign.Span);
    }

    // ---- Calls and constructors (SPEC §13.4, §13.5) ----

    /// <summary><c>T.m(…)</c> (static, <paramref name="target"/> null) or <c>x.m(…)</c>.</summary>
    private BoundExpr BindDotNetCall(CallExpr call, BoundExpr? target, INamedTypeSymbol owner, Identifier name)
    {
        if (target is null && NestedType(owner, name.Text) is { } nested)
            return BindDotNetConstruct(call, nested, name.Span);
        var methods = Methods(owner, name.Text, target is null);
        if (methods.Count == 0)
        {
            BindArguments(call);
            if (PropertyOrField(owner, name.Text, target is null) is not null)
                _diagnostics.ReportFieldNotMethod(name.Span, name.Text);
            else
                _diagnostics.ReportNoMethod(name.Span, new DotNetType(owner), name.Text);
            return new BoundErrorExpr(call.Span);
        }
        if (ResolveOverload(call, methods, MemberName(owner, name.Text), name.Span) is not var (method, arguments))
            return new BoundErrorExpr(call.Span);
        CheckNative(method, null, name.Span, MemberName(owner, name.Text));
        var result = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _)!;
        return new BoundDotNetCall(target, method, arguments, result, call.Span);
    }

    /// <summary><c>T(…)</c>: a public constructor, or a struct's default value (SPEC §13.4).</summary>
    private BoundExpr BindDotNetConstruct(CallExpr call, INamedTypeSymbol type, TextSpan nameSpan)
    {
        var created = new DotNetType(type);
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
        if (ResolveOverload(call, constructors, created.ToString(), nameSpan) is not var (constructor, arguments))
            return new BoundErrorExpr(call.Span);
        CheckNative(constructor, null, nameSpan, created.ToString());
        return new BoundDotNetNew(created, constructor, arguments, call.Span);
    }

    /// <summary>
    /// Picks the overload a call means (SPEC §13.5) and binds its arguments, reporting why if there's
    /// none: a member Taipan can't use (TP0231), argument errors, no overload, or ambiguity (TP0232).
    /// </summary>
    private (IMethodSymbol Method, ImmutableArray<BoundDotNetArgument> Arguments)? ResolveOverload(
        CallExpr call, List<IMethodSymbol> candidates, string member, TextSpan nameSpan)
    {
        var usable = candidates.Where(m => UnsupportedIn(m) is null).ToList();
        if (usable.Count == 0)
        {
            BindArguments(call);
            _diagnostics.ReportDotNetUnsupported(nameSpan, member, UnsupportedIn(candidates[0])!);
            return null;
        }

        var shaped = usable.Select(m => (Method: m, Shape: ShapeOf(call, m))).Where(c => c.Shape is not null).ToList();
        if (shaped.Count == 1)
        {
            // One overload takes these arguments: they're checked against its parameters, as for a Taipan function.
            var (method, shape) = (shaped[0].Method, shaped[0].Shape!);
            var arguments = call.Arguments.Select((a, i) =>
            {
                var parameter = method.Parameters[shape[i]];
                var value = BindExpected(a.Value, DotNetTypes.Map(DotNetTypes.TypeOf(parameter), result: false, out _)!);
                CheckLiteralFits(value, DotNetTypes.TypeOf(parameter));
                return new BoundDotNetArgument(parameter, value);
            }).ToImmutableArray();
            return (method, arguments);
        }
        if (shaped.Count == 0)
        {
            if (usable.Count == 1)
                ReportShape(call, usable[0], member, nameSpan);
            var types = BindArguments(call);
            if (usable.Count > 1 && !types.Any(t => t.Type.ContainsError))
                _diagnostics.ReportNoOverload(nameSpan, member, types.Select(t => t.Type));
            return null;
        }

        var values = call.Arguments.Select(a => BindValue(a.Value)).ToImmutableArray();
        if (values.Any(v => v.Type.ContainsError))
            return null;
        var applicable = shaped
            .Select(c => (c.Method, Shape: c.Shape!, Fits: c.Shape!.Select((p, i) => DotNetTypes.Fit(values[i].Type, DotNetTypes.TypeOf(c.Method.Parameters[p]))).ToArray()))
            .Where(c => c.Fits.All(f => f != DotNetFit.None))
            .ToList();
        if (applicable.Count == 0)
        {
            _diagnostics.ReportNoOverload(nameSpan, member, values.Select(v => v.Type));
            return null;
        }

        // The chosen overload is the one no other is better than (SPEC §13.5).
        bool Better((IMethodSymbol Method, int[] Shape, DotNetFit[] Fits) a, (IMethodSymbol Method, int[] Shape, DotNetFit[] Fits) b)
        {
            var atLeast = a.Fits.Zip(b.Fits).All(p => p.First >= p.Second);
            var strictly = a.Fits.Zip(b.Fits).Any(p => p.First > p.Second);
            return atLeast && (strictly || a.Method.Parameters.Length - a.Shape.Length < b.Method.Parameters.Length - b.Shape.Length);
        }
        var best = applicable.Where(c => !applicable.Any(o => o != c && Better(o, c))).ToList();
        if (best.Count != 1)
        {
            _diagnostics.ReportAmbiguousDotNetCall(nameSpan, member, Signature(best[0].Method), Signature(best[1].Method));
            return null;
        }
        var chosen = best[0];
        var bound = values.Select((v, i) =>
        {
            var parameter = chosen.Method.Parameters[chosen.Shape[i]];
            CheckLiteralFits(v, DotNetTypes.TypeOf(parameter));
            return new BoundDotNetArgument(parameter, v);
        }).ToImmutableArray();
        return (chosen.Method, bound);
    }

    /// <summary>
    /// The type that keeps Taipan from using an overload (TP0231), or null if it can: a generic
    /// method's type parameter, a <c>ref</c>, <c>out</c> or <c>in</c> parameter, or an unmapped type.
    /// </summary>
    private static string? UnsupportedIn(IMethodSymbol method)
    {
        if (method.IsGenericMethod)
            return method.TypeParameters[0].Name;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
                return $"{(parameter.RefKind == RefKind.RefReadOnlyParameter ? "ref readonly" : parameter.RefKind.ToString().ToLowerInvariant())} {DotNetTypes.Display(parameter.Type)}";
            if (DotNetTypes.Map(DotNetTypes.TypeOf(parameter), result: false, out var unsupported) is null)
                return DotNetTypes.Display(unsupported ?? parameter.Type);
        }
        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
            return $"ref {DotNetTypes.Display(method.ReturnType)}";
        if (method.MethodKind != MethodKind.Constructor && DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out var result) is null)
            return DotNetTypes.Display(result ?? method.ReturnType);
        return null;
    }

    /// <summary>
    /// Which parameter each argument is for, matched as for a Taipan function (SPEC §4.2), or null if
    /// they don't fit: too many, an unknown or repeated keyword, or a parameter without a default left out.
    /// </summary>
    private static int[]? ShapeOf(CallExpr call, IMethodSymbol method)
    {
        var parameters = method.Parameters;
        var positional = call.Arguments.TakeWhile(a => a.Name is null).Count();
        if (positional > parameters.Length)
            return null;
        var shape = new List<int>(Enumerable.Range(0, positional));
        foreach (var argument in call.Arguments.Skip(positional))
        {
            var index = parameters.IndexOf(parameters.FirstOrDefault(p => p.Name == argument.Name!.Text)!);
            if (index < 0 || shape.Contains(index))
                return null;
            shape.Add(index);
        }
        return Enumerable.Range(0, parameters.Length).All(i => shape.Contains(i) || parameters[i].IsOptional) ? [.. shape] : null;
    }

    /// <summary>Why the arguments don't fit the only overload, reported as for a Taipan function.</summary>
    private void ReportShape(CallExpr call, IMethodSymbol method, string member, TextSpan nameSpan)
    {
        var parameters = method.Parameters;
        var required = parameters.Count(p => !p.IsOptional);
        var positional = call.Arguments.TakeWhile(a => a.Name is null).Count();
        if (positional > parameters.Length || (positional == call.Arguments.Length && positional < required))
        {
            var expected = required == parameters.Length ? Arguments(parameters.Length) : $"{required} to {parameters.Length} arguments";
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
