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
        Index(member.Member.Span, found);
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
        Index(member.Member.Span, found);
        var current = new BoundDotNetMember(target, found, DotNetTypes.Map(netType, result: true, out _) ?? type, member.Span);
        var value = BindAssignedValue(assign, current, type, preset);
        CheckLiteralFits(value, netType);
        return new BoundDotNetAssign(target, found, value, assign.Op != AssignOp.Assign, assign.Span);
    }

    // ---- Indexers and operators (SPEC §13.10, M33) ----

    /// <summary>The public indexers of a type and its bases that can be read.</summary>
    private static List<IPropertySymbol> Indexers(INamedTypeSymbol owner) =>
        [.. MembersNamed(owner, "this[]").OfType<IPropertySymbol>().Where(p => p.IsIndexer && !p.IsStatic && p.GetMethod is { DeclaredAccessibility: Accessibility.Public })];

    // `x[a, b]` passes a and b to an indexer with two parameters.
    private static ImmutableArray<Expr> IndexArguments(IndexExpr index) => index.Index is TupleExpr tuple ? tuple.Elements : [index.Index];

    /// <summary><c>x[i]</c> on a .NET value: its indexer, chosen among overloads as a method is (SPEC §13.10).</summary>
    private BoundExpr BindDotNetIndex(BoundExpr target, INamedTypeSymbol owner, IndexExpr index)
    {
        var syntax = IndexArguments(index);
        var indexers = Indexers(owner);
        if (indexers.Count == 0)
        {
            foreach (var argument in syntax)
                BindValue(argument);
            _diagnostics.ReportCannotIndex(index.Target.Span, target.Type);
            return new BoundErrorExpr(index.Span);
        }
        var name = $"{new DotNetType(owner)}[…]";
        var shaped = indexers.Select(p => p.GetMethod!).Where(m => m.Parameters.Length == syntax.Length && UnsupportedIn(m) is null)
            .Select(m => (m, Enumerable.Range(0, syntax.Length).ToArray())).ToList();
        var values = syntax.Select(a => a is LambdaExpr ? null : BindValue(a)).ToArray();
        if (values.Any(v => v is not null && v.Type.ContainsError))
            return new BoundErrorExpr(index.Span);
        if (shaped.Count == 0)
        {
            _diagnostics.ReportNoOverload(index.Target.Span, name, values.Select(v => v?.Type ?? TaipanType.Error));
            return new BoundErrorExpr(index.Span);
        }
        if (ChooseOverload(syntax, shaped, values, name, index.Target.Span) is not var (getter, arguments, _))
            return new BoundErrorExpr(index.Span);
        var indexer = indexers.First(p => SymbolEqualityComparer.Default.Equals(p.GetMethod, getter));
        var type = DotNetTypes.Map(DotNetTypes.TypeOf(indexer), result: true, out _)!;
        return new BoundDotNetIndex(target, indexer, arguments, type, index.Span);
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
        var type = DotNetTypes.Map(netType, result: false, out _)!;
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
        var shaped = operators.Where(m => m.Parameters.Length == 2 && UnsupportedIn(m) is null).Select(m => (m, new[] { 0, 1 })).ToList();
        var plans = shaped.Select(c => Plan([leftSyntax, rightSyntax], c.m, c.Item2, [left, right], name, span, report: false)).OfType<OverloadPlan>().ToList();
        if (plans.Count == 0)
            return null;
        if (ChooseOverload([leftSyntax, rightSyntax], [.. plans.Select(p => (p.Method, p.Shape))], [left, right], TypeChecker.Display(op), span) is not var (method, arguments, _))
            return new BoundErrorExpr(span);
        var type = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _)!;
        return new BoundDotNetOperator(TypeChecker.Display(op), method, arguments, type, span);
    }

    /// <summary><c>-x</c> on a .NET value: its type's unary negation (SPEC §13.10).</summary>
    private BoundExpr? BindDotNetNegation(BoundExpr operand, TextSpan span)
    {
        Expr operandSyntax = new ErrorExpr(operand.Span);
        var operators = Operators("op_UnaryNegation", operand.Type).Where(m => m.Parameters.Length == 1 && UnsupportedIn(m) is null).ToList();
        if (operators.Count == 0)
            return null;
        if (ChooseOverload([operandSyntax], [.. operators.Select(m => (m, new[] { 0 }))], [operand], "-", span) is not var (method, arguments, _))
            return new BoundErrorExpr(span);
        var type = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _)!;
        return new BoundDotNetOperator("-", method, arguments, type, span);
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
        if (ResolveOverload(call, methods, MemberName(owner, name.Text), name.Span) is not var (method, arguments, typeArguments))
            return new BoundErrorExpr(call.Span);
        CheckNative(method, null, name.Span, MemberName(owner, name.Text));
        Index(name.Span, method);
        var result = DotNetTypes.Map(DotNetTypes.ResultOf(method), result: true, out _, typeArguments)!;
        return new BoundDotNetCall(target, method, arguments, result, call.Span) { TypeArguments = typeArguments };
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
        if (ResolveOverload(call, constructors, created.ToString(), nameSpan) is not var (constructor, arguments, _))
            return new BoundErrorExpr(call.Span);
        CheckNative(constructor, null, nameSpan, created.ToString());
        return new BoundDotNetNew(created, constructor, arguments, call.Span);
    }

    /// <summary>
    /// Picks the overload a call means (SPEC §13.5) and binds its arguments, reporting why if there's
    /// none: a member Taipan can't use (TP0231), argument errors, no overload, or ambiguity (TP0232).
    /// A generic method's type arguments are inferred (SPEC §13.8), and a lambda is bound with each
    /// candidate's parameter types, then for real with the chosen one's.
    /// </summary>
    private (IMethodSymbol Method, ImmutableArray<BoundDotNetArgument> Arguments, TypeArguments? TypeArguments)? ResolveOverload(
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
        if (shaped is [{ Method.IsGenericMethod: false } only])
        {
            // One overload takes these arguments: they're checked against its parameters, as for a Taipan function.
            var (method, shape) = (only.Method, only.Shape!);
            var arguments = call.Arguments.Select((a, i) =>
            {
                var parameter = method.Parameters[shape[i]];
                var value = BindExpected(a.Value, DotNetTypes.Map(DotNetTypes.TypeOf(parameter), result: false, out _)!);
                CheckLiteralFits(value, DotNetTypes.TypeOf(parameter));
                return new BoundDotNetArgument(parameter, value);
            }).ToImmutableArray();
            return (method, arguments, null);
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

        // Lambdas are bound for each candidate; every other argument once.
        var values = call.Arguments.Select(a => a.Value is LambdaExpr ? null : BindValue(a.Value)).ToArray();
        if (values.Any(v => v is not null && v.Type.ContainsError))
            return null;
        return ChooseOverload([.. call.Arguments.Select(a => a.Value)], [.. shaped.Select(c => (c.Method, c.Shape!))], values, member, nameSpan);
    }

    /// <summary>
    /// The overload no other is better than, among those that take <paramref name="values"/>
    /// (null for each lambda in <paramref name="syntax"/>), with its arguments bound (SPEC §13.5).
    /// </summary>
    private (IMethodSymbol Method, ImmutableArray<BoundDotNetArgument> Arguments, TypeArguments? TypeArguments)? ChooseOverload(
        IReadOnlyList<Expr> syntax, List<(IMethodSymbol Method, int[] Shape)> shaped, BoundExpr?[] values, string member, TextSpan nameSpan)
    {
        var single = shaped.Count == 1;
        var plans = shaped.Select(c => Plan(syntax, c.Method, c.Shape, values, member, nameSpan, report: single)).OfType<OverloadPlan>().ToList();
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
            _diagnostics.ReportAmbiguousDotNetCall(nameSpan, member, Signature(best[0].Method), Signature(best[1].Method));
            return null;
        }
        return Finish(syntax, best[0], values, member, nameSpan);
    }

    /// <summary>A candidate that takes a call's arguments: its type arguments, each argument's fit, and each lambda's parameter type.</summary>
    private sealed record OverloadPlan(IMethodSymbol Method, int[] Shape, TypeArguments? TypeArguments, DotNetFit[] Fits, TaipanType?[] Parameters);

    private static bool Better(OverloadPlan a, OverloadPlan b)
    {
        var atLeast = a.Fits.Zip(b.Fits).All(p => p.First >= p.Second);
        if (!atLeast)
            return false;
        if (a.Fits.Zip(b.Fits).Any(p => p.First > p.Second))
            return true;
        var (omittedA, omittedB) = (a.Method.Parameters.Length - a.Shape.Length, b.Method.Parameters.Length - b.Shape.Length);
        if (omittedA != omittedB)
            return omittedA < omittedB;
        // Fitting the same, a non-generic overload is better, and of generic ones the more specific (SPEC §13.5).
        return GenericRank(a.Method) < GenericRank(b.Method);
    }

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
    /// Whether a candidate takes the arguments, inferring its type arguments (SPEC §13.8): from
    /// each argument that isn't a lambda, then from lambdas whose parameter types are known.
    /// With <paramref name="report"/>, the only candidate's problems are reported.
    /// </summary>
    private OverloadPlan? Plan(IReadOnlyList<Expr> syntax, IMethodSymbol method, int[] shape, BoundExpr?[] values, string member, TextSpan nameSpan, bool report)
    {
        var (placeholders, symbolic) = Placeholders(method);
        var map = new Dictionary<TypeParameterSymbol, TaipanType>();
        var diagnostics = report ? _diagnostics : new Diagnostics.DiagnosticBag { Source = _source };
        var reported = diagnostics.ToImmutable().Count(d => !d.IsWarning);
        var inference = new TypeInference(placeholders, map, diagnostics);
        var parameterTypes = shape.Select(p => DotNetTypes.Map(DotNetTypes.TypeOf(method.Parameters[p]), result: false, out _, symbolic)!).ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } value)
                inference.Unify(parameterTypes[i], value.Type, syntax[i].Span);
        }

        var lambdaResults = new TaipanType?[values.Length];
        var pending = Enumerable.Range(0, values.Length).Where(i => values[i] is null).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(i => TypeChecker.Unwrap(parameterTypes[i]) is not FunctionType callable || callable.Parameters.All(inference.IsKnown), -1);
            if (ready < 0)
                break;
            pending.Remove(ready);
            var lambda = (LambdaExpr)syntax[ready];
            if (TypeChecker.Unwrap(parameterTypes[ready]) is not FunctionType function || function.Parameters.Length != lambda.Parameters.Length)
            {
                if (report)
                    _diagnostics.ReportTypeMismatch(lambda.Span, parameterTypes[ready], "lambda");
                return null;
            }
            var result = SpeculateLambda(lambda, [.. function.Parameters.Select(p => TypeChecker.Substitute(p, map))], function.Result is NothingType);
            lambdaResults[ready] = result;
            if (!inference.IsKnown(function.Result))
                inference.Unify(function.Result, result, lambda.Body.Span);
        }
        if (pending.Count > 0)
        {
            if (report)
            {
                var parameter = ((LambdaExpr)syntax[pending[0]]).Parameters.FirstOrDefault();
                if (parameter is not null)
                    _diagnostics.ReportCannotInferLambdaParameter(parameter.Span, parameter.Text);
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

        var typeArguments = method.IsGenericMethod
            ? new TypeArguments(method.TypeParameters.Select((p, k) => KeyValuePair.Create(p, map[placeholders[k]])))
            : null;
        var fits = new DotNetFit[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = DotNetTypes.TypeOf(method.Parameters[shape[i]]);
            fits[i] = values[i] is { } value ? DotNetTypes.Fit(value.Type, parameter, typeArguments)
                : LambdaFit(lambdaResults[i]!, (INamedTypeSymbol)parameter, typeArguments);
            // The only candidate's arguments are checked against it when they're bound.
            if (fits[i] == DotNetFit.None && !report)
                return null;
        }
        var concrete = parameterTypes.Select(t => TypeChecker.Substitute(t, map)).ToArray();
        return new OverloadPlan(method, shape, typeArguments, fits, concrete);
    }

    /// <summary>A lambda's fit is its result's, to the delegate's result type (SPEC §13.5).</summary>
    private static DotNetFit LambdaFit(TaipanType result, INamedTypeSymbol callable, TypeArguments? typeArguments) =>
        callable.DelegateInvokeMethod is not { } invoke || invoke.ReturnsVoid ? DotNetFit.Exact
            : DotNetTypes.Fit(result, DotNetTypes.ResultOf(invoke), typeArguments);

    /// <summary>Binds the chosen overload's arguments: lambdas with its parameter types, and checks its constraints.</summary>
    private (IMethodSymbol, ImmutableArray<BoundDotNetArgument>, TypeArguments?)? Finish(
        IReadOnlyList<Expr> syntax, OverloadPlan plan, BoundExpr?[] values, string member, TextSpan nameSpan)
    {
        var arguments = ImmutableArray.CreateBuilder<BoundDotNetArgument>();
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = plan.Method.Parameters[plan.Shape[i]];
            var expected = plan.Parameters[i]!;
            BoundExpr value;
            if (values[i] is { } bound)
            {
                value = bound;
                if (plan.Fits[i] == DotNetFit.None && !expected.ContainsError)
                    _diagnostics.ReportTypeMismatch(bound.Span, expected, bound.Type);
                if (!plan.Method.IsGenericMethod)
                    CheckLiteralFits(value, DotNetTypes.TypeOf(parameter));
            }
            else
            {
                var function = (FunctionType)TypeChecker.Unwrap(expected);
                value = BindLambdaAs((LambdaExpr)syntax[i], function.Parameters, function.Result);
            }
            arguments.Add(new BoundDotNetArgument(parameter, value));
        }
        if (plan.TypeArguments is { } typeArguments && !SatisfiesConstraints(plan.Method, typeArguments, member, nameSpan))
            return null;
        return (plan.Method, arguments.ToImmutable(), plan.TypeArguments);
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

    /// <summary>A lambda's result type with these parameter types, binding it without keeping anything.</summary>
    private TaipanType SpeculateLambda(LambdaExpr lambda, ImmutableArray<TaipanType> parameters, bool discardsResult)
    {
        var diagnostics = _diagnostics;
        _diagnostics = new Diagnostics.DiagnosticBag { Source = _source, FileOrder = diagnostics.FileOrder };
        _speculating++;
        try
        {
            var bound = BindLambdaAs(lambda, parameters, discardsResult ? TaipanType.Nothing : null);
            return bound is BoundLambda { FunctionType.Result: var result } && !_diagnostics.ToImmutable().Any(d => !d.IsWarning) ? result : TaipanType.Error;
        }
        finally
        {
            _speculating--;
            _diagnostics = diagnostics;
        }
    }

    /// <summary>
    /// A generic method's constraints, checked against each type argument's .NET form (SPEC §13.8):
    /// <c>class</c>, <c>struct</c>, <c>new()</c>, and base classes or interfaces.
    /// </summary>
    private bool SatisfiesConstraints(IMethodSymbol method, TypeArguments typeArguments, string member, TextSpan nameSpan)
    {
        var symbols = new Dictionary<ITypeParameterSymbol, ITypeSymbol?>(SymbolEqualityComparer.Default);
        foreach (var (parameter, argument) in typeArguments)
            symbols[parameter] = NetSymbolOf(argument);
        foreach (var parameter in method.TypeParameters)
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
                    if (symbol is null || Construct(constraint, symbols) is not { } required
                        || !(SymbolEqualityComparer.Default.Equals(symbol, required) || symbol is INamedTypeSymbol named && required is INamedTypeSymbol target && DotNetTypes.Derives(named, target)))
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

    /// <summary>A Taipan type's .NET type, for one that has a symbol in the metadata: built-in types and .NET types.</summary>
    private ITypeSymbol? NetSymbolOf(TaipanType type) => type switch
    {
        PrimitiveType { Name: "int" } => DotNet.Special(SpecialType.System_Int64),
        PrimitiveType { Name: "float" } => DotNet.Special(SpecialType.System_Double),
        PrimitiveType { Name: "bool" } => DotNet.Special(SpecialType.System_Boolean),
        PrimitiveType { Name: "str" } => DotNet.Special(SpecialType.System_String),
        DotNetType net => net.Symbol,
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
    /// The type that keeps Taipan from using an overload (TP0231), or null if it can: a <c>ref</c>,
    /// <c>out</c> or <c>in</c> parameter, or an unmapped type. A generic method's type parameters
    /// stand for whatever is inferred (SPEC §13.8).
    /// </summary>
    private string? UnsupportedIn(IMethodSymbol method)
    {
        var symbolic = method.IsGenericMethod ? Placeholders(method).Symbolic : null;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
                return $"{(parameter.RefKind == RefKind.RefReadOnlyParameter ? "ref readonly" : parameter.RefKind.ToString().ToLowerInvariant())} {DotNetTypes.Display(parameter.Type)}";
            if (DotNetTypes.Map(DotNetTypes.TypeOf(parameter), result: false, out var unsupported, symbolic) is null)
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
