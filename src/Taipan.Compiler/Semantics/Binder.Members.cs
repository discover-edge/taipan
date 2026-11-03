using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// Declaring special methods, properties, static and class methods, class attributes and abstract
// methods (SPEC §4.8, M28).
public sealed partial class Binder
{
    private static readonly string[] MethodDecorators = ["override", "property", "staticmethod", "classmethod", "abstractmethod"];

    // Methods whose '...' body was meant for '@abstractmethod', which was rejected, and so isn't bound.
    private readonly HashSet<FunctionSymbol> _stubs = [];

    /// <summary>
    /// The decorators on a method, once checked: which are in effect, which were written (some
    /// left out for clashing with another, which shouldn't lead to more errors), and a setter's.
    /// </summary>
    private sealed record MethodMarks(HashSet<string> Names, HashSet<string> Written, Decorator? Setter)
    {
        public bool Has(string name) => Names.Contains(name);
    }

    /// <summary>
    /// A method's decorators (SPEC §2, §4.8): the method decorators, and <c>@p.setter</c>. Any other,
    /// a repeated one, or one that can't go with another is TP0224 and left out.
    /// </summary>
    private MethodMarks CheckMethodDecorators(ClassSymbol owner, MethodDef method)
    {
        var names = new HashSet<string>();
        var written = new HashSet<string>();
        Decorator? setter = null;
        foreach (var decorator in method.Decorators)
        {
            var name = decorator.Name.Text;
            if (decorator.Member is null && MethodDecorators.Contains(name))
                written.Add(name);
            if (decorator.Member is { } member)
            {
                if (member.Text != "setter")
                    _diagnostics.ReportUnknownDecorator(decorator.Span, $"{name}.{member.Text}");
                else if (setter is not null)
                    _diagnostics.ReportDuplicateDecorator(decorator.Span, $"{name}.setter");
                else if (names.FirstOrDefault() is { } other)
                    _diagnostics.ReportDecoratorCombination(decorator.Span, $"{name}.setter", other);
                else
                    setter = decorator;
                continue;
            }
            if (names.Contains(name))
                _diagnostics.ReportDuplicateDecorator(decorator.Span, name);
            else if (name == "dataclass")
                _diagnostics.ReportMisplacedDecorator(decorator.Span, name);
            else if (!MethodDecorators.Contains(name))
                _diagnostics.ReportUnknownDecorator(decorator.Span, name);
            else if (setter is not null)
                _diagnostics.ReportDecoratorCombination(decorator.Span, name, $"{setter.Name.Text}.setter");
            else if (Conflict(name, names) is { } other)
                _diagnostics.ReportDecoratorCombination(decorator.Span, name, other);
            else if (name == "abstractmethod" && !owner.DerivesFromAbc)
                _diagnostics.ReportAbstractNeedsAbc(decorator.Span);
            else if (name != "override" && (method.Name.Text == "__init__" || SpecialMethods.IsSpecialName(method.Name.Text) && name != "abstractmethod"))
                _diagnostics.ReportMisplacedDecorator(decorator.Span, name, help: null);
            else
                names.Add(name);
        }
        return new MethodMarks(names, written, setter);
    }

    // Each method has one kind, and an abstract one is neither an override nor static (SPEC §4.8).
    private static string? Conflict(string name, HashSet<string> present)
    {
        string[] kinds = ["property", "staticmethod", "classmethod"];
        if (kinds.Contains(name))
            return present.FirstOrDefault(p => kinds.Contains(p) || p == "abstractmethod" && name != "property");
        if (name == "abstractmethod")
            return present.FirstOrDefault(p => p is "override" or "staticmethod" or "classmethod");
        return name == "override" ? present.FirstOrDefault(p => p == "abstractmethod") : null;
    }

    /// <summary>
    /// Declares a method in its class, returning its symbol, or null when it's left out: a setter
    /// joins its property instead, and an unsupported or malformed special method is reported.
    /// </summary>
    private FunctionSymbol? DeclareMethod(ClassSymbol owner, MethodDef method, MethodDef? previous, HashSet<string> names)
    {
        var name = method.Name.Text;
        var marks = CheckMethodDecorators(owner, method);
        var kind = marks.Has("property") ? MemberKind.Property
            : marks.Has("staticmethod") ? MemberKind.Static
            : marks.Has("classmethod") ? MemberKind.Class
            : MemberKind.Instance;
        var receiver = kind switch { MemberKind.Static => Receiver.None, MemberKind.Class => Receiver.Cls, _ => Receiver.Self };
        // A kind that was left out for clashing says nothing about the receiver.
        var kindLeftOut = marks.Written.Any(w => w is "property" or "staticmethod" or "classmethod" && !marks.Has(w));
        if (method.Receiver != receiver && !kindLeftOut)
            _diagnostics.ReportReceiver(method.Name.Span, name, receiver switch { Receiver.Self => "self", Receiver.Cls => "cls", _ => null });
        if (kind is MemberKind.Static or MemberKind.Class && !owner.TypeParameters.IsEmpty)
            _diagnostics.ReportGenericClassMembers(method.Name.Span, kind == MemberKind.Static ? "static methods" : "class methods");

        var returnType = BindReturnType(method.ReturnType);
        if (name == "__init__" && method.ReturnType is { } initReturn)
        {
            _diagnostics.ReportInitReturnType(initReturn.Span);
            returnType = TaipanType.Nothing;
        }
        var isAbstract = marks.Has("abstractmethod");
        var stub = method.Body.Statements is [ExprStmt { Expr: EllipsisExpr }];
        if (isAbstract && !stub)
            _diagnostics.ReportAbstractBody(method.Body.Statements.FirstOrDefault()?.Span ?? method.Name.Span);
        var symbol = new FunctionSymbol(
            name, BindParams(method.Params), returnType,
            self: kind is MemberKind.Instance or MemberKind.Property ? new VariableSymbol("self", owner.Type, VariableKind.Parameter) : null)
        {
            Defaults = PendingDefaults(method.Params),
            Kinds = KindsOf(method.Params),
            IsGenerator = Yields(method.Body),
            IsMarkedOverride = marks.Written.Contains("override"),
            DeclaredIn = owner,
            Kind = kind,
            Cls = kind == MemberKind.Class ? new VariableSymbol("cls", owner.Type, VariableKind.Parameter) : null,
            IsAbstract = isAbstract,
        };
        Index(method.Name.Span, symbol, isDefinition: true);
        CheckGeneratorReturn(symbol, method.Name);
        // An '@abstractmethod' that was rejected still explains a '...' body.
        if (!isAbstract && stub && marks.Written.Contains("abstractmethod"))
            _stubs.Add(symbol);

        if (marks.Setter is { } setter)
            return DeclareSetter(owner, method, previous, setter, symbol) ? symbol : null;
        if (!names.Add(name))
        {
            _diagnostics.ReportDuplicateDefinition(method.Name.Span, name);
            return symbol;
        }
        if (kind == MemberKind.Property && (!method.Params.IsEmpty || method.ReturnType is null))
        {
            _diagnostics.ReportPropertySignature(method.Name.Span, hasParameters: !method.Params.IsEmpty);
            return symbol;
        }
        if (name == "__init__")
        {
            if (owner.IsDataclass)
                _diagnostics.ReportDataclassInit(method.Name.Span);
            else
                owner.Init = symbol;
            if (symbol.IsMarkedOverride)
                _diagnostics.ReportInitOverride(method.Name.Span);
            return symbol;
        }
        if (SpecialMethods.IsSpecialName(name))
        {
            // Other special methods are deferred, and reported rather than ignored (SPEC §4.8).
            if (!SpecialMethods.IsSupported(name))
                _diagnostics.ReportSpecialMethod(method.Name.Span, name);
            else if (!SpecialMethods.HasSignature(symbol, owner, method.Params.Any(p => p.Default is not null)))
                _diagnostics.ReportSpecialSignature(method.Name.Span, name, SpecialMethods.Signature(name, owner));
            else
                owner.Methods[name] = symbol;
            return symbol;
        }
        owner.Methods[name] = symbol;
        return symbol;
    }

    /// <summary>
    /// <c>@p.setter</c> on a method named <c>p</c> right after the property <c>p</c>, taking one
    /// parameter of its type and returning nothing (SPEC §4.8). Returns whether it was attached.
    /// </summary>
    private bool DeclareSetter(ClassSymbol owner, MethodDef method, MethodDef? previous, Decorator decorator, FunctionSymbol setter)
    {
        var name = decorator.Name.Text;
        var property = owner.Methods.GetValueOrDefault(name) is { Kind: MemberKind.Property } p ? p : null;
        if (property is null || method.Name.Text != name)
        {
            _diagnostics.ReportSetterWithoutProperty(decorator.Span, name);
            return false;
        }
        if (previous?.Name.Text != name || property.Setter is not null)
        {
            _diagnostics.ReportSetterPlacement(method.Name.Span);
            return false;
        }
        if (setter.Parameters is not [var value] || value.Type != property.ReturnType && !value.Type.ContainsError
            || setter.ReturnType is not NothingType || method.Params.Any(a => a.Default is not null))
        {
            _diagnostics.ReportSetterSignature(method.Name.Span, property.ReturnType);
            return false;
        }
        property.Setter = setter;
        setter.SetterOf = property;
        return true;
    }

    /// <summary>A class attribute (SPEC §4.8): its type, and a value bound later, once every class is known.</summary>
    private ClassAttributeSymbol? DeclareClassAttribute(ClassSymbol owner, FieldDef field, GenericType classVar)
    {
        if (classVar.Arguments is not [var typeSyntax])
        {
            _diagnostics.ReportWrongTypeArgumentCount(classVar.Span, "ClassVar", 1, classVar.Arguments.Length);
            return null;
        }
        if (!owner.TypeParameters.IsEmpty)
            _diagnostics.ReportGenericClassMembers(field.Name.Span, "class attributes");
        if (field.Default is null)
            _diagnostics.ReportClassAttributeValue(field.Name.Span, field.Name.Text);
        var attribute = new ClassAttributeSymbol(field.Name.Text, BindType(typeSyntax), owner);
        Index(field.Name.Span, attribute, isDefinition: true);
        return attribute;
    }

    private static string KindName(MemberKind kind) => kind switch
    {
        MemberKind.Property => "property",
        MemberKind.Static => "static method",
        MemberKind.Class => "class method",
        _ => "method",
    };

    /// <summary>
    /// What a class's special methods require of it against its bases (SPEC §4.8): a subclass of a
    /// hashable class stays hashable (TP0225), and a reflected arithmetic method that can run in place
    /// of a base's forward one returns what it does (TP0228).
    /// </summary>
    private void CheckSpecialInheritance(ClassDef classDef, ClassSymbol symbol, IEnumerable<(MethodDef Syntax, FunctionSymbol Symbol)> methods)
    {
        if (symbol.Base is { } baseClass && baseClass.IsHashable && !symbol.IsHashable)
            _diagnostics.ReportUnhashableSubclass(classDef.Name.Span, symbol.Name, baseClass.Name);
        // A class that's both iterable and an iterator gives one element type (SPEC §3.13).
        if (symbol.FindMethod("__iter__") is { ReturnType: IteratorOfType iterated } iter && symbol.FindMethod("__next__") is { } next
            && iterated.Element != next.ReturnType && !iterated.ContainsError && !next.ReturnType.ContainsError
            && methods.FirstOrDefault(m => m.Symbol == iter || m.Symbol == next) is { Syntax: { } mismatched })
            _diagnostics.ReportSpecialSignature(mismatched.Name.Span, "__iter__", $"def __iter__(self) -> Iterator[{next.ReturnType}]");
        foreach (var (syntax, method) in methods)
        {
            var op = Enum.GetValues<BinaryOp>().Cast<BinaryOp?>().FirstOrDefault(o => SpecialMethods.Reflected(o!.Value) == method.Name && !SpecialMethods.IsComparison(o.Value));
            if (symbol.Methods.GetValueOrDefault(method.Name) != method || op is null || method.Parameters is not [var other])
                continue;
            foreach (var ancestor in Ancestors(symbol))
            {
                if (SpecialMethods.Method(ancestor.Type, SpecialMethods.Forward(op.Value)) is not ({ } forward, { } parameter, var result)
                    || !SpecialMethods.Takes(parameter, symbol.Type) || !SpecialMethods.Takes(other.Type, ancestor.Type)
                    || method.ReturnType.ContainsError || TypeChecker.IsAssignable(method.ReturnType, result))
                    continue;
                _diagnostics.ReportReflectedReturn(
                    syntax.ReturnType?.Span ?? syntax.Name.Span, symbol.Name, method.Name, result, forward.Owner!.Name, forward.Name);
                break;
            }
        }
    }
}

