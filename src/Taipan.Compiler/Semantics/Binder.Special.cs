using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Using special methods, properties, static and class methods and class attributes (SPEC §4.8, M28).
public sealed partial class Binder
{
    /// <summary>The class a member expression's target names, rather than a value: <c>C</c> or <c>m.C</c>.</summary>
    private ClassSymbol? ClassNamed(Expr target)
    {
        switch (target)
        {
            case NameExpr name when _scope.Lookup(name.Name) is ClassSymbol { IsProtocol: false } named:
                Index(name.Span, named);
                return named;
            case MemberExpr { Target: NameExpr module } member when _scope.Lookup(module.Name) is ModuleSymbol symbol
                && symbol.Classes.GetValueOrDefault(member.Member.Text) is { IsProtocol: false } qualified:
                Index(member.Member.Span, qualified);
                return qualified;
            default:
                return null;
        }
    }

    /// <summary>Whether the target is a class method's <c>cls</c> (SPEC §4.8).</summary>
    private bool IsCls(Expr target) => target is NameExpr name && _outerFunction?.Cls is { } cls && _scope.Lookup(name.Name) == cls;

    /// <summary>
    /// <c>C.a</c> or <c>cls.a</c>, read: a class attribute (SPEC §4.8). Anything else on a class
    /// needs an instance, or is a method used without calling it.
    /// </summary>
    private BoundExpr BindClassMember(MemberExpr member, ClassSymbol owner, ClassType? throughType = null, bool viaCls = false)
    {
        var name = member.Member.Text;
        if (owner.FindClassAttribute(name) is { } attribute)
        {
            Index(member.Member.Span, attribute);
            return NarrowedAttribute(new BoundClassAttribute(attribute, null, member.Span));
        }
        switch (owner.FindMethod(name))
        {
            case { Kind: MemberKind.Property }:
                _diagnostics.ReportPropertyThroughClass(member.Member.Span, name);
                break;
            case { IsClassLevel: true } classLevel:
                return BindClassMethodValue(member, owner, null, viaCls, classLevel, throughType ?? (viaCls ? owner.Type : null));
            case not null:
                _diagnostics.ReportNeedsInstance(member.Member.Span, name, owner.Name);
                break;
            case null when owner.FindField(name) is not null:
                _diagnostics.ReportNeedsInstance(member.Member.Span, name, owner.Name);
                break;
            default:
                _diagnostics.ReportNoField(member.Member.Span, owner.Type, name);
                break;
        }
        return new BoundErrorExpr(member.Span);
    }

    /// <summary><c>x.p</c> or <c>x.a</c> on an instance: a property or a class attribute; null if it's neither.</summary>
    private BoundExpr? BindInstanceMember(BoundExpr target, ClassSymbol classSymbol, MemberExpr member)
    {
        var name = member.Member.Text;
        if (classSymbol.FindClassAttribute(name) is { } attribute)
        {
            Index(member.Member.Span, attribute);
            return new BoundClassAttribute(attribute, target, member.Span);
        }
        if (classSymbol.FindMethod(name) is { Kind: MemberKind.Property } property)
        {
            Index(member.Member.Span, property);
            return new BoundPropertyGet(target, property, TypeChecker.SubstituteSelf(TypeChecker.Substitute(property.ReturnType, TypeChecker.MapOf(target.Type)), target.Type), member.Span);
        }
        return null;
    }

    /// <summary>
    /// <c>C.m(…)</c> or <c>cls.m(…)</c>: a static or class method, found from that class upwards
    /// (SPEC §4.8). Instance methods, properties and fields need an instance.
    /// </summary>
    private BoundExpr BindClassMethodCall(CallExpr call, MemberExpr member, ClassSymbol owner, bool viaCls, ClassType? throughType = null)
    {
        var name = member.Member.Text;
        var method = owner.FindMethod(name);
        if (method is { IsClassLevel: true })
            return BindStaticCall(call, member, owner, null, viaCls, method, throughType ?? (viaCls ? owner.Type : null));
        BindArguments(call);
        if (name == "__init__")
            _diagnostics.ReportInitCalled(member.Member.Span);
        else if (SpecialMethods.IsSupported(name))
            _diagnostics.ReportSpecialMethodCalled(member.Member.Span, name, SpecialMethods.Form(name, "x"));
        else if (method is not null || owner.FindField(name) is not null)
            _diagnostics.ReportNeedsInstance(member.Member.Span, name, owner.Name);
        else
            _diagnostics.ReportNoMethod(member.Member.Span, owner.Type, name);
        return new BoundErrorExpr(call.Span);
    }

    /// <summary>A static or class method's call, its arguments matched as for any method (SPEC §4.2).</summary>
    private BoundExpr BindStaticCall(CallExpr call, MemberExpr member, ClassSymbol through, BoundExpr? instance, bool viaCls, FunctionSymbol method,
        ClassType? throughType = null)
    {
        Index(member.Member.Span, method);
        // A generic class's type arguments come from the instance, are written (`Box[int].of`), or are
        // inferred from the arguments, as a constructor's are (SPEC §4.9).
        throughType ??= instance?.Type as ClassType;
        var inferring = !through.TypeParameters.IsEmpty && throughType is null;
        var map = throughType is null ? [] : TypeChecker.MapOf(throughType);
        var result = TypeChecker.Substitute(method.ReturnType, map);
        var (arguments, shape, inferred, valid) = BindCallArguments(call, method, method.Name, member.Member.Span,
            inferring ? through.TypeParameters : [], t => TypeChecker.Substitute(t, map), result, null);
        if (inferring)
        {
            throughType = valid ? new ClassType(through, [.. through.TypeParameters.Select(p => inferred.GetValueOrDefault(p, TaipanType.Error))]) : null;
            result = valid ? TypeChecker.Substitute(method.ReturnType, inferred) : TaipanType.Error;
        }
        // A Self result is of the class or instance it's called through (SPEC §4.9).
        result = TypeChecker.SubstituteSelf(result, throughType ?? through.Type);
        return new BoundClassMethodCall(through, instance, viaCls, method, arguments, result, call.Span)
        {
            Shape = valid ? shape : null,
            ThroughType = through.TypeParameters.IsEmpty ? null : throughType,
        };
    }

    /// <summary>
    /// <c>cls(…)</c> (SPEC §4.8): constructs the class <c>cls</c> is, checked as a construction of
    /// the method's own class, which can't be abstract (TP0234).
    /// </summary>
    private BoundExpr BindClsNew(CallExpr call, TextSpan nameSpan, TaipanType? expected)
    {
        var method = _outerFunction!;
        var owner = method.Owner!;
        method.CallsCls = true;
        if (owner.IsAbstract)
            _diagnostics.ReportClsInAbstract(nameSpan, owner.Name);
        var construction = BindConstruct(call, owner, expected, nameSpan, checkAbstract: false);
        return construction is BoundErrorExpr ? construction : new BoundClsNew(construction, call.Span);
    }

    /// <summary>
    /// <c>x.p = v</c> through a property's setter, or <c>C.a = v</c> on a class attribute through
    /// the class that declares it (SPEC §4.8); null when the member is neither.
    /// </summary>
    private BoundStmt? BindMemberAssign(AssignStmt assign, MemberExpr member, BoundExpr? target, ClassSymbol owner, string? through, BoundExpr? preset)
    {
        var name = member.Member.Text;
        if (owner.FindClassAttribute(name) is { } attribute)
        {
            Index(member.Member.Span, attribute);
            var current = NarrowedAttribute(new BoundClassAttribute(attribute, null, member.Span));
            if (through != attribute.Owner.Name || target is not null)
            {
                _diagnostics.ReportClassAttributeThrough(member.Member.Span, name, through ?? _source.ToString(member.Target.Span), attribute.Owner.Name);
                return OrphanAssign(assign, preset);
            }
            var value = BindAssignedValue(assign, current, attribute.Type, preset);
            EndPathsThrough(attribute, assign.Span);
            return new BoundClassAttributeAssign(attribute, value, assign.Span);
        }
        if (target is null || owner.FindMethod(name) is not { Kind: MemberKind.Property } property)
            return null;
        Index(member.Member.Span, property);
        if (property.Setter is null)
        {
            _diagnostics.ReportCannotAssign(member.Member.Span, name, "read-only property", null);
            return OrphanAssign(assign, preset);
        }
        var type = TypeChecker.Substitute(property.ReturnType, TypeChecker.MapOf(target.Type));
        var assigned = BindAssignedValue(assign, new BoundPropertyGet(target, property, type, member.Span), type, preset);
        EndPaths(_ => true, CallWhy(assign.Span));
        return new BoundPropertySet(target, property, assigned, assign.Op != AssignOp.Assign, assign.Span);
    }

    /// <summary>A unary <c>-</c> on a class value calls its <c>__neg__</c> (SPEC §4.8); null if it has none.</summary>
    private static TaipanType? NegatedType(TaipanType operand) =>
        SpecialMethods.Method(operand, "__neg__") is (_, _, var result) ? result : null;

    /// <summary>
    /// <c>a op b</c> with a class operand (SPEC §4.8): the forward or reflected special method,
    /// with the other operand converted to its parameter. Null when neither applies.
    /// </summary>
    private BoundExpr? BindSpecialBinary(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        if (SpecialMethods.Resolve(op, left.Type, right.Type) is not var (method, parameter, result, reflected))
            return null;
        if (!reflected)
            return new BoundBinary(left, op, Convert(right, parameter), result, span);
        if (!SpecialMethods.IsComparison(op) && AmbiguousForward(op, left.Type, right.Type) is { } other)
            _diagnostics.ReportAmbiguousOperator(span, TypeChecker.Display(op), left.Type, right.Type, $"{other.Owner!.Name}.{other.Name}");
        return new BoundBinary(Convert(left, parameter), op, right, result, span);
    }

    /// <summary>
    /// When <c>a op b</c> uses <c>b</c>'s reflected method, a forward method that a class derived
    /// from <c>a</c>'s has, and that a <c>b</c> could fit at run time, which Python would call first.
    /// </summary>
    private FunctionSymbol? AmbiguousForward(BinaryOp op, TaipanType left, TaipanType right)
    {
        if (left is not ClassType { Symbol: var leftClass } || SpecialMethods.Forward(op) is not { } name)
            return null;
        return _allClasses.Where(c => c.DerivesFrom(leftClass)).Select(c => c.Methods.GetValueOrDefault(name))
            .FirstOrDefault(m => m?.Parameters is [{ Type: var parameter }] && MayFit(right, parameter));
    }

    /// <summary>Whether a value of type <paramref name="value"/> could be an instance of <paramref name="parameter"/> at run time.</summary>
    private static bool MayFit(TaipanType value, TaipanType parameter) =>
        TypeChecker.IsAssignable(value, parameter)
        || value is ClassType { Symbol: var v } && parameter is ClassType { Symbol: var p } && p.DerivesFrom(v);

    /// <summary>
    /// <c>repr(x)</c> and <c>hash(x)</c> (SPEC §6, M28): any value, and any value that can be a dict
    /// key, each giving one result.
    /// </summary>
    private BoundExpr BindReprOrHash(CallExpr call, FunctionSymbol function, BuiltinFunction builtin)
    {
        if (call.Arguments.Length != 1)
        {
            BindArguments(call);
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, "1 argument", call.Arguments.Length);
            return new BoundErrorExpr(call.Span);
        }
        var value = BindValue(call.Arguments[0].Value);
        if (builtin == BuiltinFunction.Hash && !value.Type.ContainsError && !DictOfType.IsValidKey(value.Type))
            _diagnostics.ReportCannotHash(call.Arguments[0].Value.Span, value.Type);
        return new BoundCall(function, [value], function.ReturnType, call.Span);
    }

    /// <summary>
    /// After every class body is bound (SPEC §4.8): a class can't add an abstract method when a base
    /// class has a class method that calls <c>cls(…)</c>, and every class derived from one must be
    /// constructed as it is (TP0228).
    /// </summary>
    private void CheckClsConstruction(IEnumerable<(ClassDef Syntax, ClassSymbol Symbol)> classes, IEnumerable<(MethodDef Syntax, FunctionSymbol Symbol)> methods)
    {
        var methodSyntax = methods.ToDictionary(m => m.Symbol, m => m.Syntax);
        foreach (var (syntax, symbol) in classes)
        {
            foreach (var ancestor in Ancestors(symbol))
            {
                if (ancestor.Methods.Values.FirstOrDefault(m => m.CallsCls) is not { } constructing)
                    continue;
                foreach (var added in symbol.Methods.Values.Where(m => m.IsAbstract && m.Overrides is null))
                    _diagnostics.ReportAbstractBelowCls(methodSyntax[added].Name.Span, symbol.Name, added.Name, ancestor.Name, constructing.Name);
                if (!ConstructedAlike(symbol, ancestor))
                    _diagnostics.ReportClsConstructor(syntax.Name.Span, symbol.Name, ancestor.Name, constructing.Name);
                break;
            }
        }
    }

    /// <summary>Whether a class takes the same constructor arguments as a base: names, types, order, and defaults where it has them.</summary>
    private static bool ConstructedAlike(ClassSymbol symbol, ClassSymbol baseClass)
    {
        var (mine, theirs) = (ConstructorParameters(symbol), ConstructorParameters(baseClass));
        return mine.Count == theirs.Count && mine.Zip(theirs).All(p =>
            p.First.Name == p.Second.Name && p.First.Type == p.Second.Type && (!p.Second.HasDefault || p.First.HasDefault));
    }

    private static List<(string Name, TaipanType Type, bool HasDefault)> ConstructorParameters(ClassSymbol symbol) => symbol.IsDataclass
        ? [.. symbol.AllFields.Select(f => (f.Name, f.Type, f.HasDefault))]
        : [.. symbol.Constructor.Parameters.Select((p, i) => (p.Name, p.Type, symbol.Constructor.DefaultFor(i) is not null))];

    /// <summary><c>C.x = v</c> where <c>x</c> isn't a class attribute: an instance member needs an instance.</summary>
    private BoundStmt NotAnAttribute(AssignStmt assign, MemberExpr member, ClassSymbol owner, BoundExpr? preset)
    {
        var name = member.Member.Text;
        if (owner.FindField(name) is not null || owner.FindMethod(name) is not null)
            _diagnostics.ReportNeedsInstance(member.Member.Span, name, owner.Name);
        else
            _diagnostics.ReportNoField(member.Member.Span, owner.Type, name);
        return OrphanAssign(assign, preset);
    }
}
