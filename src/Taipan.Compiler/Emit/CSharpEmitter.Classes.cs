using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Emit;

// Special methods, properties, static and class methods, class attributes and abstract classes
// (SPEC §4.8, §8, M28).
public sealed partial class CSharpEmitter
{
    private const string ClassInterface = "global::Taipan.Runtime.IClass";

    // Stands in for self in a static or class method, which has none, so globals are still qualified.
    private static readonly VariableSymbol NoSelf = new("self", TaipanType.Error, VariableKind.Parameter);

    // The class method being emitted, whose cls is the class object, `this`.
    private VariableSymbol? _cls;

    /// <summary>A method's C# name: a property's getter and setter are methods beside each other.</summary>
    private static string MethodName(FunctionSymbol method) => method switch
    {
        { Kind: MemberKind.Property } => $"{method.Name}__get",
        { SetterOf: not null } => $"{method.Name}__set",
        _ => method.Name,
    };

    /// <summary>Whether a class has a class object: it or a base has a static or class method (SPEC §8).</summary>
    private static bool HasClassObject(ClassSymbol symbol) =>
        symbol.Methods.Values.Any(m => m.IsClassLevel) || symbol.Base is { } baseClass && HasClassObject(baseClass);

    /// <summary>The class whose class method calls <c>cls(…)</c>, if this class or a base has one: every class below it can be <c>cls</c>.</summary>
    private static ClassSymbol? ConstructingRoot(ClassSymbol symbol)
    {
        ClassSymbol? root = null;
        for (var c = symbol; c is not null; c = c.Base)
        {
            if (c.Methods.Values.Any(m => m.CallsCls))
                root = c;
        }
        return root;
    }

    private string ClassObjectType(ClassSymbol symbol) => $"{TypePath(symbol)}.@__Class";

    /// <summary>
    /// The explicit <c>IClass</c> implementations for the special methods a class defines (SPEC §8):
    /// display, equality, ordering, hashing and reflected arithmetic, each testing its parameter's
    /// class at run time. A dataclass regenerates <c>__repr__</c> and <c>__eq__</c> unless it defines them.
    /// </summary>
    private void EmitSpecialHooks(ClassSymbol symbol, ClassType type)
    {
        var own = symbol.Methods;
        if (own.ContainsKey("__str__"))
            Line($"string {ClassInterface}.__str__() => this.@__str__();");
        if (own.ContainsKey("__repr__"))
            Line($"string {ClassInterface}.__repr__() => this.@__repr__();");
        else if (symbol.IsDataclass && symbol.Base?.FindMethod("__repr__") is not null)
            Line($"string {ClassInterface}.__repr__() => {Runtime}.Display.StructForm(this);");

        if (own.ContainsKey("__eq__"))
            EmitHook(type, "TryEq(object other, out bool result)", [("__eq__", null)], "result = false;");
        else if (symbol.IsDataclass && symbol.Base?.FindMethod("__eq__") is not null)
            Line($"bool {ClassInterface}.TryEq(object other, out bool result) => {Runtime}.Equality.StructEq(this, other, out result);");

        string[] comparisons = ["__lt__", "__le__", "__gt__", "__ge__"];
        if (comparisons.Any(own.ContainsKey))
            EmitHook(type, "TryCompare(int op, object other, out bool result)",
                [.. comparisons.Select((name, op) => (name, (int?)op)).Where(c => symbol.FindMethod(c.name) is not null)], "result = false;");

        if (own.ContainsKey("__hash__"))
        {
            Line($"bool {ClassInterface}.TryHash(out long hash)");
            Line("{");
            Line("    hash = this.@__hash__();");
            Line("    return true;");
            Line("}");
            // Dicts find keys by C#'s Equals and GetHashCode (SPEC §8).
            Line($"public override bool Equals(object? other) => {Runtime}.Equality.KeyEqual(this, other);");
            Line("public override int GetHashCode() => this.@__hash__().GetHashCode();");
        }

        string[] reflected = ["__radd__", "__rsub__", "__rmul__", "__rtruediv__", "__rfloordiv__", "__rmod__"];
        if (reflected.Any(own.ContainsKey))
        {
            var found = reflected.Select((name, op) => (name, (int?)op)).Where(r => symbol.FindMethod(r.name) is not null).ToList();
            EmitHook(type, "TryReflected(int op, object other, out object? result)", found, "result = null;");
            var owners = string.Join(" ", found.Select(r => $"{r.Item2} => typeof({TypeName(symbol.FindMethod(r.name)!.Owner!.Type)}),"));
            Line($"global::System.Type? {ClassInterface}.ReflectedOwner(int op) => op switch {{ {owners} _ => null }};");
        }
    }

    /// <summary>
    /// A hook that calls one of the class's methods when <c>other</c> is an instance of its
    /// parameter's type; <paramref name="methods"/> pairs each method with its operation code.
    /// </summary>
    private void EmitHook(ClassType type, string signature, IReadOnlyList<(string Name, int? Op)> methods, string none)
    {
        Line($"bool {ClassInterface}.{signature}");
        Line("{");
        _indent++;
        foreach (var (name, op) in methods)
        {
            var method = type.Symbol.FindMethod(name)!;
            var parameter = TypeName(TypeChecker.Substitute(method.Parameters[0].Type, TypeChecker.MapOf(type)));
            var that = $"@{name.Trim('_')}_other";
            var condition = op is { } code ? $"op == {code} && other is {parameter} {that}" : $"other is {parameter} {that}";
            Line($"if ({condition})");
            Line("{");
            Line($"    result = this.@{name}({that});");
            Line("    return true;");
            Line("}");
        }
        Line(none);
        Line("return false;");
        _indent--;
        Line("}");
    }

    /// <summary>A class's attributes, as static properties of the class that declares them (SPEC §8).</summary>
    private void EmitClassAttributes(ClassSymbol symbol)
    {
        foreach (var attribute in symbol.ClassAttributes)
        {
            var value = attribute.Value!;
            _locals = LocalNames.For([], new BoundBlock([new BoundExprStmt(value, value.Span)], value.Span), _globalNames);
            Line($"{MemberAccess(attribute.Name)} static {TypeName(attribute.Type)} @{attribute.Name} {{ get; set; }} = {Expr(value)};");
        }
        if (!symbol.ClassAttributes.IsEmpty)
            Line();
    }

    /// <summary>
    /// A class's class object (SPEC §8): a nested class deriving from its base's, with a singleton,
    /// holding the static and class methods as virtual methods, and, below a class method that calls
    /// <c>cls(…)</c>, a virtual <c>__call__</c> that constructs this class. Each instance reaches its
    /// class's object through <c>__classobj</c>.
    /// </summary>
    private void EmitClassObject(BoundClass classDef, ClassType type)
    {
        var symbol = classDef.Symbol;
        if (!HasClassObject(symbol))
            return;
        var inherited = symbol.Base is { } baseClass && HasClassObject(baseClass);
        Line();
        Line($"{HelperAccess} {(inherited ? "new " : "")}class @__Class{(inherited ? $" : {ClassObjectType(symbol.Base!)}" : "")}");
        Line("{");
        _indent++;
        foreach (var method in classDef.Methods.Where(m => m.Symbol.IsClassLevel))
        {
            Line();
            EmitFunction(method, isMethod: true);
        }
        if (ConstructingRoot(symbol) is { } constructing && !symbol.IsAbstract)
            EmitClsConstruction(symbol, type, constructing);
        _indent--;
        Line("}");
        Line();
        Line($"{HelperAccess} static readonly {(inherited ? "new " : "")}@__Class @__class = new();");
        var root = symbol;
        while (root.Base is { } up && HasClassObject(up))
            root = up;
        var modifier = inherited ? "override " : symbol.HasSubclasses ? "virtual " : "";
        Line($"{HelperAccess} {modifier}{ClassObjectType(root)} @__classobj => @__class;");
    }

    /// <summary>
    /// <c>__call__</c>, which <c>cls(…)</c> calls, with the parameters of <paramref name="root"/>'s
    /// constructor, which every class below it shares (SPEC §4.8), and a helper per default that
    /// evaluates this class's own default.
    /// </summary>
    private void EmitClsConstruction(ClassSymbol symbol, ClassType type, ClassSymbol root)
    {
        var parameters = ClsParameters(root);
        var modifier = symbol == root ? (symbol.HasSubclasses ? "virtual " : "") : "override ";
        var list = string.Join(", ", parameters.Select((p, i) => $"{TypeName(p.Type)} @a{i}"));
        var arguments = string.Join(", ", parameters.Select((_, i) => $"@a{i}"));
        Line();
        var construction = symbol.IsDataclass ? $"new {TypeName(type)}({arguments})" : $"{TypeName(type)}.@__new__({arguments})";
        Line($"public {modifier}{TypeName(root.Type)} @__call__({list}) => {construction};");
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!parameters[i].HasDefault)
                continue;
            string value;
            if (symbol.IsDataclass)
            {
                var field = symbol.AllFields[i];
                var owner = OwnerType(field, type);
                value = $"{TypeName(owner)}.@{FieldDefaultName(ClassName(owner), field)}()";
            }
            else
            {
                value = InitDefault(symbol.Constructor, i, type);
            }
            Line($"public {modifier}{TypeName(parameters[i].Type)} @__call___{i}_default() => {value};");
        }
    }

    private static List<(TaipanType Type, bool HasDefault)> ClsParameters(ClassSymbol root) => root.IsDataclass
        ? [.. root.AllFields.Select(f => (f.Type, f.HasDefault))]
        : [.. root.Constructor.Parameters.Select((p, i) => (p.Type, root.Constructor.DefaultFor(i) is not null))];

    /// <summary>
    /// A static or class method's call (SPEC §8): on <c>this</c> for <c>cls</c>, on an instance's
    /// class object, or on a class's singleton. Its defaults are helpers on the class object.
    /// </summary>
    private string ClassMethodCall(BoundClassMethodCall call)
    {
        var classObject = ClassObjectType(call.Through);
        string receiver;
        string? evaluated = null;
        if (call.ViaCls)
        {
            receiver = "this";
        }
        else if (call.Instance is { } instance)
        {
            var once = call.Shape?.Missing.Any() == true && !IsSimple(instance);
            receiver = once ? $"@{_locals.Fresh("receiver")}" : $"(({classObject}){Operand(instance)}.@__classobj)";
            if (once)
                evaluated = receiver;
        }
        else
        {
            receiver = $"{TypePath(call.Through)}.@__class";
        }
        var invocation = $"{receiver}.@{call.Method.Name}({ShapedArguments(call.Arguments, call.Shape,
            p => $"@{call.Method.Parameters[p].Name}", p => $"{receiver}.@{MethodDefaultName(call.Method, p)}()")})";
        if (evaluated is null)
            return invocation;
        var helper = call.Method.ReturnType is NothingType ? "Do" : "Let";
        return $"{Runtime}.Ops.{helper}(({classObject}){Operand(call.Instance!)}.@__classobj, ({classObject} {evaluated}) => {invocation})";
    }

    /// <summary><c>cls(…)</c>: the class object's <c>__call__</c>, with its defaults from the class <c>cls</c> is (SPEC §8).</summary>
    private string ClsNew(BoundClsNew created)
    {
        switch (created.Construction)
        {
            case BoundNew construct:
                return $"this.@__call__({ShapedArguments(construct.Arguments, construct.Shape, p => $"@a{p}", p => $"this.@__call___{p}_default()")})";
            case BoundConstruct construct:
                var fields = construct.ClassType.Symbol.AllFields;
                var shape = new ArgumentShape([.. construct.Arguments.Select(a => fields.IndexOf(a.Field))], fields.Length);
                var arguments = ShapedArguments([.. construct.Arguments.Select(a => a.Value)], shape.InOrder && shape.Parameters.Length == fields.Length ? null : shape,
                    p => $"@a{p}", p => $"this.@__call___{p}_default()");
                return $"this.@__call__({arguments})";
            default:
                throw new InvalidOperationException($"unexpected construction {created.Construction.GetType().Name}");
        }
    }

    /// <summary>A class attribute: a static property of its class, after evaluating any instance it's reached through.</summary>
    private string ClassAttribute(BoundClassAttribute attribute)
    {
        var value = $"{TypePath(attribute.Attribute.Owner)}.@{attribute.Attribute.Name}";
        return attribute.Instance is { } instance && !IsSimple(instance) ? $"{Runtime}.Ops.After({Expr(instance)}, {value})" : value;
    }

    /// <summary>
    /// An operator that calls a special method (SPEC §4.8, §8). Comparisons on classes that have
    /// subclasses go through the runtime's protocol; arithmetic does when a subclass's reflected
    /// method could take priority. Otherwise the method is called directly.
    /// </summary>
    private string? SpecialBinary(BoundBinary binary)
    {
        var (leftType, rightType) = (Concrete(binary.Left.Type), Concrete(binary.Right.Type));
        if (leftType is not ClassType && rightType is not ClassType || SpecialMethods.Resolve(binary.Op, leftType, rightType) is not var (method, _, result, reflected))
            return null;
        var (l, r) = (Expr(binary.Left), Expr(binary.Right));
        var subclassed = leftType is ClassType { Symbol.HasSubclasses: true } || rightType is ClassType { Symbol.HasSubclasses: true };
        if (SpecialMethods.IsComparison(binary.Op) && subclassed)
        {
            var op = binary.Op switch { BinaryOp.Less => "Lt", BinaryOp.LessEqual => "Le", BinaryOp.Greater => "Gt", _ => "Ge" };
            return $"{Runtime}.Ops.Compare({l}, {r}, {ClassInterface}.{op})";
        }
        var (first, second) = (_locals.Fresh("left"), _locals.Fresh("right"));
        if (reflected)
            return $"{Runtime}.Ops.Reversed({l}, {r}, static (@{second}, @{first}) => @{second}.@{method.Name}(@{first}))";
        if (!SpecialMethods.IsComparison(binary.Op) && MayPrefer(binary.Op, leftType, rightType))
        {
            var code = Array.IndexOf(["__radd__", "__rsub__", "__rmul__", "__rtruediv__", "__rfloordiv__", "__rmod__"], SpecialMethods.Reflected(binary.Op));
            return $"{Runtime}.Ops.Arithmetic<{TypeName(leftType)}, {TypeName(rightType)}, {TypeName(result)}>({l}, {r}, {code}, " +
                $"static (@{first}, @{second}) => @{first}.@{method.Name}(@{second}))";
        }
        return $"{Operand(binary.Left)}.@{method.Name}({r})";
    }

    /// <summary>Whether some class derived from both operands' classes has the reflected method, so it could take priority (SPEC §4.8).</summary>
    private bool MayPrefer(BinaryOp op, TaipanType left, TaipanType right) =>
        left is ClassType { Symbol: var l } && right is ClassType { Symbol: var r }
        && _program.Classes.Any(c => c.Symbol.DerivesFrom(l) && c.Symbol.DerivesFrom(r) && c.Symbol != l && c.Symbol.FindMethod(SpecialMethods.Reflected(op)!) is not null);

    /// <summary>
    /// A library's view of a class's members (SPEC §14): each property a C# property over its
    /// getter and setter, and each static or class method a public static method, whose class
    /// method runs with its own class as <c>cls</c>.
    /// </summary>
    private void EmitLibraryMembers(BoundClass classDef)
    {
        foreach (var method in classDef.Symbol.Methods.Values.Where(m => m.Overrides is null))
        {
            if (method.Kind == MemberKind.Property)
            {
                var setter = method.Setter is null ? "" : $" set => this.@{method.Name}__set(value);";
                Line();
                Line($"{MemberAccess(method.Name)} {TypeName(method.ReturnType)} @{method.Name} {{ get => this.@{method.Name}__get();{setter} }}");
            }
            else if (method.IsClassLevel)
            {
                var parameters = string.Join(", ", method.Parameters.Select(p => $"{TypeName(p.Type)} @{p.Name}"));
                var arguments = string.Join(", ", method.Parameters.Select(p => $"@{p.Name}"));
                Line();
                Line($"{MemberAccess(method.Name)} static {TypeName(method.ReturnType)} @{method.Name}({parameters}) => @__class.@{method.Name}({arguments});");
            }
        }
    }
}
