using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Classes III (SPEC §4.9, M39): the comparison methods `order=True` and `@total_ordering` generate.
public sealed partial class Binder
{
    private static readonly string[] Comparisons = ["__lt__", "__le__", "__gt__", "__ge__"];

    /// <summary>
    /// Adds the methods the compiler writes to each class that asks for them, bases first, so a
    /// class sees what its base generated (SPEC §4.9).
    /// </summary>
    private void GenerateMethods(List<Module> modules)
    {
        static int Depth(ClassSymbol symbol) => symbol.Base is { } baseClass ? 1 + Depth(baseClass) : 0;
        var classes = modules.SelectMany(m => m.ClassDefs.Select(c => (Module: m, c.Syntax, c.Symbol)))
            .Where(c => !c.Symbol.IsProtocol)
            .OrderBy(c => Depth(c.Symbol));
        foreach (var (module, syntax, symbol) in classes)
        {
            Enter(module);
            if (symbol.IsOrdered && symbol.IsDataclass)
                GenerateOrder(syntax, symbol);
            if (symbol.TotalOrdering)
                GenerateTotalOrdering(syntax, symbol);
        }
    }

    /// <summary>
    /// <c>order=True</c>: <c>__lt__</c> and the others, comparing the fields as a tuple. Every field
    /// must be ordered, and the body can't define them itself (TP0225).
    /// </summary>
    private void GenerateOrder(ClassDef syntax, ClassSymbol symbol)
    {
        foreach (var method in syntax.Methods.Where(m => Comparisons.Contains(m.Name.Text)))
            _diagnostics.ReportOrderGenerates(method.Name.Span, method.Name.Text);
        foreach (var name in Comparisons)
            symbol.Methods[name] = GeneratedComparison(symbol, name, symbol.Type, "order");
        foreach (var field in symbol.AllFields.Where(f => !f.Type.ContainsError && !IsSortable(f.Type)))
        {
            var at = syntax.Fields.FirstOrDefault(f => f.Name.Text == field.Name)?.Name.Span ?? syntax.Name.Span;
            _diagnostics.ReportUnorderedField(at, field.Name, field.Type);
        }
    }

    /// <summary>
    /// <c>@total_ordering</c>: the comparisons the class doesn't have, from the first of
    /// <c>__lt__</c>, <c>__le__</c>, <c>__gt__</c> and <c>__ge__</c> it has, as Python's (TP0225 without one).
    /// </summary>
    private void GenerateTotalOrdering(ClassDef syntax, ClassSymbol symbol)
    {
        if (Comparisons.Select(symbol.FindMethod).FirstOrDefault(m => m is not null) is not { } root)
        {
            _diagnostics.ReportTotalOrderingRoot(syntax.Name.Span);
            return;
        }
        var parameter = root.Parameters[0].Type;
        foreach (var name in Comparisons.Where(n => symbol.FindMethod(n) is null))
            symbol.Methods[name] = GeneratedComparison(symbol, name, parameter, root.Name);
    }

    private static FunctionSymbol GeneratedComparison(ClassSymbol owner, string name, TaipanType parameter, string from) =>
        new(name, [new VariableSymbol("other", parameter, VariableKind.Parameter)], TaipanType.Bool,
            self: new VariableSymbol("self", owner.Type, VariableKind.Parameter))
        {
            DeclaredIn = owner,
            Generated = from,
            Defaults = [null],
        };

    /// <summary>A call of a class's special method, with arguments already checked against it (SPEC §4.9).</summary>
    private static BoundMethodCall SpecialCall(BoundExpr target, string name, ImmutableArray<BoundExpr> arguments, TextSpan span)
    {
        var (method, _, result) = SpecialMethods.Method(target.Type, name)!.Value;
        return new BoundMethodCall(target, method, arguments, result, span);
    }

    /// <summary><c>x(…)</c> on a value of a class with <c>__call__</c>: a call of it, with arguments matched as a method's (SPEC §4.9).</summary>
    private BoundExpr BindObjectCall(CallExpr call, BoundExpr target, TextSpan calleeSpan)
    {
        var (method, _, returnType) = SpecialMethods.Method(target.Type, "__call__")!.Value;
        var map = TypeChecker.MapOf(target.Type);
        var (arguments, shape, _, valid) = BindCallArguments(call, method, "__call__", calleeSpan, [], t => TypeChecker.Substitute(t, map), returnType, null);
        return new BoundMethodCall(target, method, arguments, returnType, call.Span) { Shape = valid ? shape : null };
    }

    private static bool IsCallableObject(TaipanType type) => type is ClassType && SpecialMethods.Method(type, "__call__") is not null;

    /// <summary>A class value with <c>__call__</c> as a function value, if it is one (SPEC §4.9); otherwise the value itself.</summary>
    private static BoundExpr AsFunctionValue(BoundExpr value) =>
        value.Type is ClassType && SpecialMethods.CallShape(value.Type) is { } shape ? new BoundCallableObject(value, shape, value.Span) : value;

    /// <summary>
    /// <c>x op= y</c> through the class's in-place method, when it has one that takes <c>y</c> (SPEC §4.9);
    /// null to fall back to <c>x = x op y</c>.
    /// </summary>
    private BoundMethodCall? BindInPlace(BoundExpr current, AssignOp op, BoundExpr operand, TextSpan span)
    {
        if (current.Type is not ClassType || SpecialMethods.InPlace(op) is not { } name
            || SpecialMethods.Method(current.Type, name) is not var (method, parameter, result) || !SpecialMethods.Takes(parameter!, operand.Type))
            return null;
        return new BoundMethodCall(current, method, [Convert(operand, parameter!)], result, span);
    }

    private static string OpText(AssignOp op) => TypeChecker.Display(TypeChecker.ToBinaryOp(op)) + "=";

    /// <summary>
    /// Whether a value is a <c>Self</c> in a method's body (SPEC §4.9): <c>self</c>, <c>cls(…)</c>, or
    /// what a method returning <c>Self</c> gives when called on one.
    /// </summary>
    private bool IsSelfValued(BoundExpr value) => value switch
    {
        BoundVariable variable => variable.Symbol == _outerFunction?.Self,
        BoundNarrowed narrowed => IsSelfValued(narrowed.Value),
        BoundClsNew => true,
        BoundMethodCall call => call.Method.ReturnType is SelfType && IsSelfValued(call.Target),
        BoundPropertyGet get => get.Property.ReturnType is SelfType && IsSelfValued(get.Target),
        BoundClassMethodCall call => call.Method.ReturnType is SelfType && (call.ViaCls || call.Instance is { } instance && IsSelfValued(instance)),
        BoundSuperCall call => call.Method.ReturnType is SelfType,
        _ => false,
    };

    /// <summary>
    /// A static or class method as a function value (SPEC §4.9): a lambda that calls it through the
    /// class, instance or <c>cls</c> it was reached through, which a class method keeps as <c>cls</c>.
    /// </summary>
    private BoundExpr BindClassMethodValue(MemberExpr member, ClassSymbol through, BoundExpr? instance, bool viaCls, FunctionSymbol method, ClassType? throughType)
    {
        Index(member.Member.Span, method);
        throughType ??= instance?.Type as ClassType;
        if (!through.TypeParameters.IsEmpty && throughType is null)
        {
            _diagnostics.ReportGenericAsValue(member.Member.Span, method.Name);
            return new BoundErrorExpr(member.Span);
        }
        if (method.Kinds.Any(k => k is not (ParamKind.Normal or ParamKind.PositionalOnly)))
        {
            _diagnostics.ReportVariadicAsValue(member.Member.Span, method.Name);
            return new BoundErrorExpr(member.Span);
        }
        var map = throughType is null ? [] : TypeChecker.MapOf(throughType);
        var parameters = method.Parameters.Select(p => new VariableSymbol(p.Name, TypeChecker.Substitute(p.Type, map), VariableKind.Parameter)).ToImmutableArray();
        var result = TypeChecker.SubstituteSelf(TypeChecker.Substitute(method.ReturnType, map), throughType ?? through.Type);
        var call = new BoundClassMethodCall(through, instance, viaCls, method, [.. parameters.Select(p => (BoundExpr)new BoundVariable(p, member.Span))], result, member.Span)
        {
            ThroughType = through.TypeParameters.IsEmpty ? null : throughType,
        };
        return new BoundLambda(parameters, call, new FunctionType([.. parameters.Select(p => p.Type)], result), member.Span);
    }

    /// <summary><c>Box[int]</c> as a receiver: a generic class with type arguments written as an expression (SPEC §4.9), or null.</summary>
    private ClassType? GenericClassReceiver(Expr target)
    {
        if (target is not IndexExpr { Target: NameExpr name } index
            || _scope.Lookup(name.Name) is not ClassSymbol { IsProtocol: false, TypeParameters.IsEmpty: false } symbol)
            return null;
        var written = (index.Index is TupleExpr tuple ? tuple.Elements : [index.Index]).Select(TypeFromExpr).ToList();
        if (written.Any(t => t is null))
            return null;
        Index(name.Span, symbol);
        return BindType(new GenericType(new Identifier(name.Name, name.Span), [.. written!], index.Span)) as ClassType;
    }

    // A type written where an expression is parsed, as `int` and `list[str]` in `Box[int]`.
    private static TypeSyntax? TypeFromExpr(Expr expr) => expr switch
    {
        NameExpr name => new NamedType(new Identifier(name.Name, name.Span), name.Span),
        IndexExpr { Target: NameExpr { Name: "list" } } list => TypeFromExpr(list.Index) is { } element ? new ListType(element, list.Span) : null,
        IndexExpr { Target: NameExpr { Name: "dict" }, Index: TupleExpr { Elements: [var key, var value] } } dict =>
            TypeFromExpr(key) is { } k && TypeFromExpr(value) is { } v ? new DictType(k, v, dict.Span) : null,
        IndexExpr { Target: NameExpr name } generic =>
            (generic.Index is TupleExpr tuple ? tuple.Elements : [generic.Index]).Select(TypeFromExpr).ToList() is var arguments && arguments.All(a => a is not null)
                ? new GenericType(new Identifier(name.Name, name.Span), [.. arguments!], generic.Span)
                : null,
        _ => null,
    };
}
