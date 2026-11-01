using System.Collections.Immutable;
using System.Globalization;
using Taipan.Compiler.Diagnostics;
using System.Text;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Emit;

/// <param name="EntryClass">The class holding <c>Main</c>, to pass to the C# compiler.</param>
/// <param name="Diagnostics">Errors found while instantiating generics (TP0219); the code is unusable if there are any.</param>
public sealed record EmittedProgram(string Code, string EntryClass, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Emits one C# compilation unit for a bound program (SPEC §8). Every statement is preceded by a
/// <c>#line</c> directive so stack traces, and therefore panics, report <c>.tpn</c> lines.
/// </summary>
public sealed class CSharpEmitter
{
    private const string Runtime = "global::Taipan.Runtime";
    private const string Collections = "global::System.Collections.Generic";
    private const string Interop = "global::Taipan.Runtime.Interop";
    private const string PyObject = "global::Python.Runtime.PyObject";

    // Generic instantiations nested deeper than this are polymorphic recursion (SPEC §3.6, TP0219).
    private const int MaxInstantiationDepth = 32;

    private readonly SourceText _source;

    // The file of the code being emitted, for #line directives (SPEC §4.7).
    private SourceText _lineSource;

    // C# names for functions, constants, classes and enums. Definitions with the same name in
    // different modules get numeric suffixes (SPEC §8).
    private readonly Dictionary<Symbol, string> _names = [];
    private readonly BoundProgram _program;
    private readonly StringBuilder _functionsOut = new();
    private readonly StringBuilder _typesOut = new();
    private StringBuilder _out;
    private readonly HashSet<string> _globalNames;
    private readonly string _programClass;
    private readonly string _topLevelMethod;
    private readonly string _entryClass;

    // Per method being emitted.
    private LocalNames _locals = null!;
    private VariableSymbol? _self;
    private readonly Dictionary<BoundExpr, string> _hoisted = new(ReferenceEqualityComparer.Instance);
    private Stack<LoopContext> _loops = new();
    private int _indent;

    // The caught C# exception of each enclosing except block, innermost on top, for a bare raise.
    private readonly Stack<string> _caught = new();

    // Monomorphization (SPEC §8): the substitution for the instance being emitted, and the
    // instances still to emit. Each one is emitted once, under a name derived from its type arguments.
    private Dictionary<TypeParameterSymbol, TaipanType> _map = [];
    private readonly Dictionary<string, string> _instanceNames = [];
    private readonly Queue<Instance> _pending = new();
    private readonly Dictionary<FunctionSymbol, List<BoundFunction>> _genericFunctions = [];
    private readonly Dictionary<ClassSymbol, BoundClass> _classes = [];
    private readonly DiagnosticBag _diagnostics = new();
    private int _depth;
    private TextSpan _rootSpan;
    private SourceText? _rootSource;
    private TextSpan _statementSpan;
    private bool _tooDeep;

    // Top-level functions used as values: each has one cached Function object, in a static field (SPEC §8).
    private readonly Dictionary<FunctionSymbol, (string Field, FunctionType Type)> _functionValues = [];

    // The helper method that evaluates each default of a top-level function (or instance), by C# name and parameter.
    private readonly Dictionary<(string Function, int Parameter), string> _defaultNames = [];

    /// <summary>A generic definition to emit with concrete type arguments.</summary>
    private sealed record Instance(Symbol Definition, ImmutableArray<TaipanType> TypeArguments, string Name, int Depth, TextSpan RootSpan, SourceText RootSource);

    /// <summary>
    /// A loop being emitted. Inside a <c>switch</c>, a C# <c>break</c> would leave the switch, so
    /// a Taipan <c>break</c> there jumps to <see cref="BreakLabel"/> after the loop instead.
    /// </summary>
    private sealed class LoopContext
    {
        public int SwitchDepth { get; set; }

        public string? BreakLabel { get; set; }
    }

    private CSharpEmitter(SourceText source, BoundProgram program)
    {
        _source = source;
        _lineSource = source;
        _program = program;
        _globalNames = [.. program.Enums.SelectMany(e => e.Variants.Select(v => v.Name))];
        IEnumerable<Symbol> definitions =
        [
            .. program.PythonImports.Select(p => p.Symbol),
            .. program.Constants.Select(c => c.Symbol),
            .. program.Functions.Select(f => f.Symbol).Distinct(),
            .. program.Classes.Select(s => s.Symbol),
            .. program.Enums,
        ];
        foreach (var definition in definitions)
        {
            var name = UniqueGlobal(definition.Name);
            _globalNames.Add(name);
            _names[definition] = name;
        }
        // The built-in exception classes are runtime classes (SPEC §8).
        foreach (var builtin in BuiltinClasses.All)
            _names[builtin] = builtin.RuntimeName!;
        _out = _functionsOut;
        foreach (var function in program.Functions.Where(f => !f.Symbol.TypeParameters.IsEmpty))
        {
            if (!_genericFunctions.TryGetValue(function.Symbol, out var bodies))
                _genericFunctions[function.Symbol] = bodies = [];
            bodies.Add(function);
        }
        foreach (var classDef in program.Classes)
            _classes[classDef.Symbol] = classDef;
        _programClass = UniqueGlobal("TaipanProgram");
        _topLevelMethod = UniqueGlobal("TopLevel");
        _entryClass = UniqueGlobal("TaipanEntry");
    }

    public static EmittedProgram Emit(SourceText source, BoundProgram program)
    {
        var emitter = new CSharpEmitter(source, program);
        var code = emitter.EmitProgram();
        return new EmittedProgram(code, emitter._entryClass, emitter._diagnostics.ToImmutable());
    }

    private string UniqueGlobal(string name)
    {
        var unique = name;
        for (var i = 2; _globalNames.Contains(unique); i++)
            unique = $"{name}{i}";
        return unique;
    }

    // ---- Program structure ----

    private string EmitProgram()
    {
        _indent = 1;
        foreach (var import in _program.PythonImports)
            Line($"public static {PyObject} @{_names[import.Symbol]} = default!;");
        foreach (var constant in _program.Constants)
            Line($"public static {TypeName(constant.Symbol.Type)} @{_names[constant.Symbol]} = default!;");
        if (!_program.Constants.IsEmpty || !_program.PythonImports.IsEmpty)
            Line();

        EmitTopLevel();
        foreach (var function in _program.Functions.Where(f => f.Symbol.TypeParameters.IsEmpty))
        {
            Line();
            EmitFunction(function, isMethod: false, _names[function.Symbol]);
        }

        _out = _typesOut;
        _indent = 0;
        foreach (var classDef in _program.Classes.Where(s => s.Symbol.TypeParameters.IsEmpty))
        {
            Line();
            EmitClass(classDef, _names[classDef.Symbol]);
        }
        foreach (var enumSymbol in _program.Enums.Where(e => e.TypeParameters.IsEmpty))
        {
            Line();
            EmitEnum(enumSymbol, _names[enumSymbol]);
        }

        while (_pending.TryDequeue(out var instance))
            EmitInstance(instance);

        _out = _functionsOut;
        _indent = 1;
        if (_functionValues.Count > 0)
            Line();
        foreach (var (function, (field, type)) in _functionValues)
            Line($"public static readonly {TypeName(type)} @{field} = new(\"{function.Name}\", @{_names[function]});");

        var code = new StringBuilder();
        code.Append($"// Generated by taipan from {_source.Path}. Do not edit.\n");
        code.Append("#nullable enable\n#line hidden\n\n");
        code.Append($"public static class {_programClass}\n{{\n");
        code.Append(_functionsOut);
        code.Append("}\n");
        code.Append(_typesOut);
        code.Append($"\npublic static class {_entryClass}\n{{\n");
        code.Append($"    public static int Main() => {Runtime}.Panic.Run({_programClass}.{_topLevelMethod});\n");
        code.Append("}\n");
        return code.ToString();
    }

    private void EmitInstance(Instance instance)
    {
        (_depth, _rootSpan, _rootSource) = (instance.Depth, instance.RootSpan, instance.RootSource);
        switch (instance.Definition)
        {
            case FunctionSymbol function:
                _map = TypeChecker.Zip(function.TypeParameters, instance.TypeArguments);
                _out = _functionsOut;
                _indent = 1;
                Line();
                EmitFunction(BodyFor(function, instance.TypeArguments), isMethod: false, instance.Name);
                break;
            case ClassSymbol classSymbol:
                _map = TypeChecker.Zip(classSymbol.TypeParameters, instance.TypeArguments);
                _out = _typesOut;
                _indent = 0;
                Line();
                EmitClass(_classes[classSymbol], instance.Name);
                break;
            case EnumSymbol enumSymbol:
                _map = TypeChecker.Zip(enumSymbol.TypeParameters, instance.TypeArguments);
                _out = _typesOut;
                _indent = 0;
                Line();
                EmitEnum(enumSymbol, instance.Name);
                break;
        }
        _map = [];
    }

    /// <summary>
    /// The body to emit for a generic function's instance: for constrained type parameters, the
    /// one checked for the instance's combination of their types (SPEC §3.6).
    /// </summary>
    private BoundFunction BodyFor(FunctionSymbol function, ImmutableArray<TaipanType> typeArguments) =>
        _genericFunctions[function].First(body => body.Specialization.IsEmpty
            || body.Specialization.Zip(typeArguments).All(pair => pair.First is TypeParameterType || pair.First == pair.Second));

    /// <summary>
    /// The C# name of a generic definition at concrete type arguments, queueing it for emission
    /// the first time. Instances requested from inside an instance are one level deeper; past
    /// <see cref="MaxInstantiationDepth"/> that is TP0219, reported at the root of the chain.
    /// </summary>
    private string InstanceName(Symbol definition, ImmutableArray<TaipanType> typeArguments)
    {
        var key = $"{definition.GetType().Name} {definition.Name}[{string.Join(", ", typeArguments)}]";
        if (_instanceNames.TryGetValue(key, out var existing))
            return existing;

        var root = _depth == 0 ? _statementSpan : _rootSpan;
        var rootSource = _depth == 0 ? _lineSource : _rootSource!;
        if (_depth + 1 > MaxInstantiationDepth)
        {
            _diagnostics.Source = rootSource;
            if (!_tooDeep)
                _diagnostics.ReportInstantiationTooDeep(root, definition.Name, $"{definition.Name}[{string.Join(", ", typeArguments.Select(a => Abbreviate(a, 3)))}]");
            _tooDeep = true;
            return definition.Name;
        }

        var name = UniqueGlobal($"{definition.Name}_{string.Join("_", typeArguments.Select(Mangle))}");
        _globalNames.Add(name);
        _instanceNames[key] = name;
        _pending.Enqueue(new Instance(definition, typeArguments, name, _depth + 1, root, rootSource));
        return name;
    }

    // A type for a message, with anything nested deeper than `depth` shown as "...".
    private static string Abbreviate(TaipanType type, int depth) => depth == 0 ? "..." : type switch
    {
        ListOfType list => $"list[{Abbreviate(list.Element, depth - 1)}]",
        DictOfType dict => $"dict[{Abbreviate(dict.Key, depth - 1)}, {Abbreviate(dict.Value, depth - 1)}]",
        OptionalOf optional => $"{Abbreviate(optional.Inner, depth)}?",
        ClassType { TypeArguments.IsEmpty: false } s => $"{s.Symbol.Name}[{string.Join(", ", s.TypeArguments.Select(a => Abbreviate(a, depth - 1)))}]",
        EnumType { TypeArguments.IsEmpty: false } e => $"{e.Symbol.Name}[{string.Join(", ", e.TypeArguments.Select(a => Abbreviate(a, depth - 1)))}]",
        _ => type.ToString(),
    };

    // Members C# generates or reserves on our classes and records, which a field can't reuse.
    private static readonly HashSet<string> ReservedMembers =
        ["ToString", "Equals", "GetHashCode", "GetType", "Deconstruct", "PrintMembers", "EqualityContract", "MemberwiseClone", "Finalize"];

    /// <summary>
    /// A field's C# name in a class named <paramref name="className"/>: its own name, unless C#
    /// would reject it (the class's own name, or a reserved member), which gets a trailing <c>_</c>.
    /// </summary>
    private static string FieldName(string className, FieldSymbol field) =>
        field.Name == className || ReservedMembers.Contains(field.Name) ? $"{field.Name}_" : field.Name;

    /// <summary>The C# class holding a class's fields.</summary>
    private string ClassName(TaipanType owner) => Concrete(owner) switch
    {
        ClassType { TypeArguments.IsEmpty: true } s => _names[s.Symbol],
        ClassType s => InstanceName(s.Symbol, s.TypeArguments),
        _ => throw new InvalidOperationException($"no class for {owner}"),
    };

    private static string Mangle(TaipanType type) => type switch
    {
        PrimitiveType primitive => primitive.Name,
        ListOfType list => $"list_{Mangle(list.Element)}",
        DictOfType dict => $"dict_{Mangle(dict.Key)}_{Mangle(dict.Value)}",
        OptionalOf optional => $"{Mangle(optional.Inner)}_opt",
        ClassType classType => string.Join("_", [classType.Symbol.Name, .. classType.TypeArguments.Select(Mangle)]),
        EnumType enumType => string.Join("_", [enumType.Symbol.Name, .. enumType.TypeArguments.Select(Mangle)]),
        FunctionType function => string.Join("_", ["fn", .. function.Parameters.Select(Mangle), function.Result is NothingType ? "none" : Mangle(function.Result)]),
        TupleOfType tuple => string.Join("_", ["tuple", .. tuple.Elements.Select(Mangle)]),
        _ => "error",
    };

    /// <summary>A type with the current instance's type arguments substituted.</summary>
    private TaipanType Concrete(TaipanType type) => TypeChecker.Substitute(type, _map);

    private void EmitTopLevel()
    {
        _locals = LocalNames.For([], _program.TopLevel, _globalNames);
        Line($"public static void {_topLevelMethod}()");
        Line("{");
        _indent++;
        // Python modules are imported first, then constants are initialized (SPEC §10.1).
        foreach (var import in _program.PythonImports)
        {
            _lineSource = import.Source;
            LineDirective(import.Span);
            Line($"@{_names[import.Symbol]} = {Interop}.Import(\"{import.Module}\");");
        }
        foreach (var constant in _program.Constants)
        {
            _lineSource = constant.Source ?? _source;
            LineDirective(constant.Span);
            Line($"@{_names[constant.Symbol]} = {Expr(constant.Value)};");
        }
        _lineSource = _source;
        EmitStatements(_program.TopLevel.Statements);
        HiddenDirective();
        _indent--;
        Line("}");
    }

    private void EmitFunction(BoundFunction function, bool isMethod, string? name = null)
    {
        var symbol = function.Symbol;
        _lineSource = function.Source ?? _source;
        _locals = LocalNames.For(function.Parameters, function.Body, _globalNames);
        _self = symbol.Self;
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{TypeName(p.Type)} @{_locals[p]}"));
        var owner = symbol.Owner;
        var isInit = owner is not null && symbol == owner.Init;
        // Methods dispatch on the runtime class (SPEC §4.3); each class's __init__ is its own.
        var modifier = owner is null ? "static "
            : isInit ? (owner.Base is null ? "" : "new ")
            : symbol.Overrides is not null ? "override "
            : owner.HasSubclasses ? "virtual " : "";
        Line($"public {modifier}{TypeName(symbol.ReturnType)} @{name ?? symbol.Name}({parameters})");
        Line("{");
        _indent++;
        if (isInit)
            EmitFieldDefaults(owner!);
        EmitStatements(function.Body.Statements);
        HiddenDirective();
        _indent--;
        Line("}");

        for (var i = 0; i < symbol.Parameters.Length; i++)
        {
            if (symbol.DefaultFor(i) is not { } value)
                continue;
            if (!isMethod)
                EmitDefault(value, symbol.Parameters[i].Type, DefaultName(name ?? symbol.Name, symbol, i), "static ");
            else
                EmitDefault(value, symbol.Parameters[i].Type, MethodDefaultName(symbol, i), DefaultModifier(symbol, i));
        }
        _self = null;
    }

    /// <summary>
    /// A method's default helpers are overridden with it, so a call evaluates the defaults of the
    /// method that runs (SPEC §4.3); <c>__init__</c>'s are static, since construction has no object yet.
    /// </summary>
    private static string DefaultModifier(FunctionSymbol method, int parameter)
    {
        var owner = method.Owner!;
        if (method == owner.Init)
            return "static ";
        if (method.Overrides?.DefaultFor(parameter) is not null)
            return "override ";
        return owner.HasSubclasses ? "virtual " : "";
    }

    // A plain class's __init__ first evaluates its own fields' defaults (SPEC §4.3).
    private void EmitFieldDefaults(ClassSymbol owner)
    {
        foreach (var field in owner.Fields.Where(f => f.Default is not null))
            Line($"this.@{FieldName(ClassName(owner.Type), field)} = @{FieldDefaultName(ClassName(owner.Type), field)}();");
    }

    // A default is a helper next to its function, called by each call that leaves it out, so it's
    // evaluated every time, in the scope where it was defined (SPEC §4.2, §8). In a class, _self
    // stays set so that globals are qualified; a default can't use self.
    private void EmitDefault(BoundExpr value, TaipanType type, string name, string modifier)
    {
        _locals = LocalNames.For([], new BoundBlock([new BoundExprStmt(value, value.Span)], value.Span), _globalNames);
        Line();
        Line($"public {modifier}{TypeName(type)} @{name}()");
        Line("{");
        _indent++;
        LineDirective(value.Span);
        Line($"return {Expr(value)};");
        HiddenDirective();
        _indent--;
        Line("}");
    }

    /// <summary>The helper for a top-level function's default, by the function's C# name.</summary>
    private string DefaultName(string function, FunctionSymbol symbol, int parameter)
    {
        if (!_defaultNames.TryGetValue((function, parameter), out var name))
        {
            name = UniqueGlobal($"{function}_{symbol.Parameters[parameter].Name}");
            _globalNames.Add(name);
            _defaultNames[(function, parameter)] = name;
        }
        return name;
    }

    // Inside the method's class, beside its methods and fields.
    private static string MethodDefaultName(FunctionSymbol method, int parameter) =>
        $"{method.Name}_{method.Parameters[parameter].Name}_default";

    private static string FieldDefaultName(string className, FieldSymbol field) => $"{FieldName(className, field)}_default";

    /// <summary>A field's C# property, in the class that declares it (one the field is reached through, or a base).</summary>
    private string Member(FieldSymbol field, TaipanType through) => FieldName(ClassName(OwnerType(field, through)), field);

    // Generic classes don't derive, so a field of a generic class is reached through that class (SPEC §4.3).
    private TaipanType OwnerType(FieldSymbol field, TaipanType through) =>
        Concrete(through) is ClassType classType && classType.Symbol == field.Owner ? classType : field.Owner!.Type;

    // A class's base list: its base class, or nothing, then the runtime interface it implements,
    // listed again in a subclass to give it its own TypeName (SPEC §8).
    private string BaseList(ClassSymbol symbol, string runtimeInterface) =>
        symbol.Base is { } baseClass ? $"global::@{_names[baseClass]}, {Runtime}.{runtimeInterface}" : $"{Runtime}.{runtimeInterface}";

    private void EmitClass(BoundClass classDef, string className)
    {
        var symbol = classDef.Symbol;
        // Field defaults and methods sit in the class, where globals are qualified; they can't use self.
        _self = new VariableSymbol("self", symbol.Type, VariableKind.Parameter);
        if (symbol.IsDataclass)
            EmitDataclass(classDef, className);
        else
            EmitPlainClass(classDef, className);
        _self = null;
    }

    // A dataclass has mutable properties and a positional constructor over every field, passing
    // the inherited ones to its base's (SPEC §8).
    private void EmitDataclass(BoundClass classDef, string className)
    {
        var symbol = classDef.Symbol;
        Line($"public {(symbol.HasSubclasses ? "" : "sealed ")}class @{className} : {BaseList(symbol, "IStruct")}");
        Line("{");
        _indent++;
        foreach (var field in symbol.Fields)
            Line($"public {TypeName(field.Type)} @{FieldName(className, field)} {{ get; set; }}");
        Line();

        var all = symbol.AllFields;
        var parameters = string.Join(", ", all.Select(f => $"{TypeName(f.Type)} @{Member(f, symbol.Type)}"));
        var inherited = symbol.Base?.AllFields ?? [];
        var baseCall = inherited.IsEmpty ? "" : $" : base({string.Join(", ", inherited.Select(f => $"@{Member(f, symbol.Type)}"))})";
        Line($"public @{className}({parameters}){baseCall}");
        Line("{");
        foreach (var field in symbol.Fields)
            Line($"    this.@{FieldName(className, field)} = @{FieldName(className, field)};");
        Line("}");
        Line();

        var fields = string.Join(", ", all.Select(f => $"(\"{f.Name}\", (object?)this.@{Member(f, symbol.Type)})"));
        Line($"string {Runtime}.IStruct.TypeName => \"{symbol.Name}\";");
        Line($"{Collections}.IReadOnlyList<(string Name, object? Value)> {Runtime}.IStruct.Fields => [{fields}];");
        Line($"public override string ToString() => {Runtime}.Display.Format(this);");
        EmitFieldDefaultHelpers(symbol, className);

        foreach (var method in classDef.Methods)
        {
            Line();
            EmitFunction(method, isMethod: true);
        }
        _indent--;
        Line("}");
    }

    // A plain class is constructed by a static __new__, which makes the object with a hidden
    // constructor and runs __init__ on it, so __init__ can set the subclass's fields before calling
    // the base's (SPEC §4.3, §8). It compares by reference, since Equals isn't overridden.
    private void EmitPlainClass(BoundClass classDef, string className)
    {
        var symbol = classDef.Symbol;
        Line($"public {(symbol.HasSubclasses ? "" : "sealed ")}class @{className} : {BaseList(symbol, "IObject")}");
        Line("{");
        _indent++;
        foreach (var field in symbol.Fields)
            Line($"public {TypeName(field.Type)} @{FieldName(className, field)} {{ get; set; }} = default!;");
        if (!symbol.Fields.IsEmpty)
            Line();
        Line($"{(symbol.HasSubclasses ? "protected" : "private")} @{className}() {{ }}");
        Line();

        var constructor = symbol.Constructor;
        var parameters = string.Join(", ", constructor.Parameters.Select(p => $"{TypeName(p.Type)} @{p.Name}"));
        var arguments = string.Join(", ", constructor.Parameters.Select(p => $"@{p.Name}"));
        Line($"public static @{className} @__new__({parameters})");
        Line("{");
        Line($"    var @__self = new @{className}();");
        Line($"    @__self.@__init__({arguments});");
        Line("    return @__self;");
        Line("}");
        Line();
        Line($"string {Runtime}.IObject.TypeName => \"{symbol.Name}\";");
        Line($"public override string ToString() => {Runtime}.Display.Format(this);");
        EmitFieldDefaultHelpers(symbol, className);

        if (symbol.Init is null)
        {
            // Without its own __init__, a class sets its defaults and passes its arguments on (SPEC §4.3).
            Line();
            Line($"public {(symbol.Base is null ? "" : "new ")}void @__init__({parameters})");
            Line("{");
            _indent++;
            EmitFieldDefaults(symbol);
            if (symbol.Base is not null)
                Line($"base.@__init__({arguments});");
            _indent--;
            Line("}");
        }
        foreach (var method in classDef.Methods)
        {
            Line();
            EmitFunction(method, isMethod: true);
        }
        _indent--;
        Line("}");
    }

    // Each field default is a static helper, evaluated anew for each object (SPEC §4.3).
    private void EmitFieldDefaultHelpers(ClassSymbol symbol, string className)
    {
        var self = _self;
        foreach (var field in symbol.Fields)
        {
            if (field.Default is { } value)
                EmitDefault(value, field.Type, FieldDefaultName(className, field), "static ");
        }
        _self = self;
    }

    // An enum is an abstract record with a private constructor; each variant is a sealed record
    // nested inside it (SPEC §8). Variants implement IVariant for display and equality.
    private void EmitEnum(EnumSymbol symbol, string className)
    {
        var variantInterface = $"{Runtime}.IVariant";
        Line($"public abstract record @{className}");
        Line("{");
        _indent++;
        Line($"private @{className}() {{ }}");
        Line();
        Line($"public sealed override string ToString() => {Runtime}.Display.Format(this);");
        foreach (var variant in symbol.Variants)
        {
            Line();
            var parameters = string.Join(", ", variant.Fields.Select(f => $"{TypeName(f.Type)} @{FieldName(variant.Name, f)}"));
            Line($"public sealed record @{variant.Name}({parameters}) : @{className}, {variantInterface}");
            Line("{");
            _indent++;
            var fields = string.Join(", ", variant.Fields.Select(f => $"(\"{f.Name}\", (object?)this.@{FieldName(variant.Name, f)})"));
            Line($"string {Runtime}.IStruct.TypeName => \"{variant.Name}\";");
            Line($"{Collections}.IReadOnlyList<(string Name, object? Value)> {Runtime}.IStruct.Fields => [{fields}];");
            _indent--;
            Line("}");
        }
        _indent--;
        Line("}");
    }

    // ---- Statements ----

    private void EmitStatements(IEnumerable<BoundStmt> statements)
    {
        foreach (var stmt in statements)
            EmitStmt(stmt);
    }

    private void EmitBody(BoundBlock block, string? prologue = null)
    {
        Line("{");
        _indent++;
        if (prologue is not null)
            Line(prologue);
        EmitStatements(block.Statements);
        _indent--;
        Line("}");
    }

    private void EmitStmt(BoundStmt stmt)
    {
        switch (stmt)
        {
            case BoundBlock { Statements.IsEmpty: true }:
                break; // pass
            case BoundBlock block:
                EmitStatements(block.Statements);
                break;
            case BoundVariableDecl decl:
                LineDirective(decl.Span);
                Line($"{TypeName(decl.Symbol.Type)} @{_locals[decl.Symbol]} = {Expr(decl.Value)};");
                break;
            case BoundAssign assign:
                LineDirective(assign.Span);
                Line($"@{_locals[assign.Target]} = {Expr(assign.Value)};");
                break;
            case BoundFieldAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target],
                    () => Line($"{Operand(assign.Target)}.@{Member(assign.Field, assign.Target.Type)} = {Expr(assign.Value)};"));
                break;
            case BoundIndexAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target, assign.Index],
                    () => Line($"{Runtime}.Ops.SetIndex({Expr(assign.Target)}, {Expr(assign.Index)}, {Expr(assign.Value)});"));
                break;
            case BoundPySetAttr assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target],
                    () => Line($"{Interop}.SetAttr({Expr(assign.Target)}, \"{assign.Name}\", {Expr(assign.Value)});"));
                break;
            case BoundPySetItem assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target, assign.Index],
                    () => Line($"{Interop}.SetItem({Expr(assign.Target)}, {Expr(assign.Index)}, {Expr(assign.Value)});"));
                break;
            case BoundReturn ret:
                LineDirective(ret.Span);
                Line(ret.Value is null ? "return;" : $"return {Expr(ret.Value)};");
                break;
            case BoundBreak brk:
                LineDirective(brk.Span);
                if (_loops.TryPeek(out var loop) && loop.SwitchDepth > 0)
                {
                    loop.BreakLabel ??= _locals.Fresh("break_loop");
                    Line($"goto {loop.BreakLabel};");
                }
                else
                {
                    Line("break;");
                }
                break;
            case BoundContinue cont:
                LineDirective(cont.Span);
                Line("continue;");
                break;
            case BoundExprStmt { Expr: BoundCall or BoundMethodCall or BoundBuiltinMethodCall or BoundPyCall or BoundInvoke or BoundSort or BoundSuperCall } exprStmt:
                LineDirective(exprStmt.Span);
                Line($"{Expr(exprStmt.Expr)};");
                break;
            case BoundExprStmt exprStmt:
                LineDirective(exprStmt.Span);
                Line($"_ = {Expr(exprStmt.Expr)};");
                break;
            case BoundIf ifStmt:
                EmitIf(ifStmt);
                break;
            case BoundWhile whileStmt:
                LineDirective(whileStmt.Span);
                Line($"while ({Expr(whileStmt.Condition)})");
                EmitLoopBody(whileStmt.Body);
                break;
            case BoundForRange forStmt:
                EmitForRange(forStmt);
                break;
            case BoundForEach forEach:
                LineDirective(forEach.Span);
                var iterate = Concrete(forEach.Iterable.Type) == TaipanType.Py ? $"{Interop}.Iterate" : $"{Runtime}.Iter.Over";
                Line($"foreach ({TypeName(forEach.Variable.Type)} @{_locals[forEach.Variable]} in {iterate}({Expr(forEach.Iterable)}))");
                EmitLoopBody(forEach.Body);
                break;
            case BoundMatch match:
                EmitMatch(match);
                break;
            case BoundRaise raise:
                LineDirective(raise.Span);
                Line(raise.Value is null ? $"throw {Runtime}.Exceptions.Rethrow({_caught.Peek()});" : $"throw {Expr(raise.Value)};");
                break;
            case BoundTry tryStmt:
                EmitTry(tryStmt);
                break;
            case BoundLocalFunction function:
                EmitLocalFunction(function);
                break;
            case BoundUnpack unpack:
                LineDirective(unpack.Span);
                Line($"{TypeName(unpack.Temp.Type)} @{_locals[unpack.Temp]} = {Expr(unpack.Value)};");
                EmitStatements(unpack.Stores);
                break;
            default:
                throw new InvalidOperationException($"unexpected bound statement {stmt.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits an assignment. For a compound one, sub-expressions with side effects are first stored
    /// in temporaries, since the bound value reads them again (SPEC §4.6: evaluated once).
    /// </summary>
    private void EmitCompound(bool isCompound, BoundExpr[] parts, Action emitAssignment)
    {
        var hoist = isCompound ? parts.Where(p => !IsSimple(p)).ToList() : [];
        if (hoist.Count == 0)
        {
            emitAssignment();
            return;
        }

        Line("{");
        _indent++;
        foreach (var part in hoist)
        {
            var name = $"@{_locals.Fresh("tmp")}";
            Line($"var {name} = {Expr(part)};");
            _hoisted[part] = name;
        }
        emitAssignment();
        foreach (var part in hoist)
            _hoisted.Remove(part);
        _indent--;
        Line("}");
    }

    // Expressions that are safe to evaluate twice: no calls, so no side effects.
    private static bool IsSimple(BoundExpr expr) => expr switch
    {
        BoundVariable or BoundLiteral => true,
        BoundNarrowed narrowed => IsSimple(narrowed.Value),
        BoundFieldAccess access => IsSimple(access.Target),
        BoundIndex index => IsSimple(index.Target) && IsSimple(index.Index),
        BoundUnary unary => IsSimple(unary.Operand),
        BoundBinary binary => IsSimple(binary.Left) && IsSimple(binary.Right),
        _ => false,
    };

    private void EmitIf(BoundIf ifStmt)
    {
        for (var i = 0; i < ifStmt.Clauses.Length; i++)
        {
            var clause = ifStmt.Clauses[i];
            LineDirective(clause.Condition.Span);
            Line($"{(i == 0 ? "if" : "else if")} ({Expr(clause.Condition)})");
            EmitBody(clause.Body);
        }
        if (ifStmt.Else is not null)
        {
            Line("else");
            EmitBody(ifStmt.Else);
        }
    }

    // range(a, b) evaluates a, then b, once (SPEC §8). When the body makes closures, each iteration
    // copies the counter into its own variable, which C# captures per iteration (SPEC §3.7).
    /// <summary>
    /// SPEC §8: a C# try whose one catch turns the exception into its Taipan value and tests the
    /// except clauses in order, rethrowing it unchanged if none matches. The else block follows
    /// the try/catch, so its exceptions aren't caught there; it needs a flag only when an except
    /// block can finish normally. A finally block wraps the lot.
    /// </summary>
    private void EmitTry(BoundTry tryStmt)
    {
        var handlers = tryStmt.Handlers.Where(h => h.IsReachable).ToList();
        string? completed = null;
        if (tryStmt.Else is not null && handlers.Any(h => !TypeChecker.Terminates(h.Body)))
        {
            completed = $"@{_locals.Fresh("completed")}";
            Line($"var {completed} = false;");
        }
        if (tryStmt.Finally is not null)
        {
            Line("try");
            Line("{");
            _indent++;
        }

        if (handlers.Count == 0)
        {
            EmitStatements(tryStmt.Body.Statements);
        }
        else
        {
            Line("try");
            Line("{");
            _indent++;
            EmitStatements(tryStmt.Body.Statements);
            if (completed is not null)
                Line($"{completed} = true;");
            _indent--;
            Line("}");

            var caught = $"@{_locals.Fresh("caught")}";
            var exception = $"@{_locals.Fresh("exception")}";
            LineDirective(tryStmt.Span);
            Line($"catch (global::System.Exception {caught}) when ({Runtime}.Exceptions.IsCatchable({caught}))");
            Line("{");
            _indent++;
            Line($"var {exception} = {Runtime}.Exceptions.From({caught});");
            _caught.Push(caught);
            for (var i = 0; i < handlers.Count; i++)
            {
                var handler = handlers[i];
                Line($"{(i == 0 ? "" : "else ")}if ({HandlerTest(handler, exception)})");
                var binding = handler.Binding is { } symbol
                    ? $"{TypeName(symbol.Type)} @{_locals[symbol]} = ({TypeName(symbol.Type)}){exception};"
                    : null;
                EmitBody(handler.Body, binding);
            }
            _caught.Pop();
            Line("else");
            Line("    throw;");
            HiddenDirective();
            _indent--;
            Line("}");
        }

        if (tryStmt.Else is { } elseBlock)
        {
            if (completed is not null)
                Line($"if ({completed})");
            EmitBody(elseBlock);
        }
        if (tryStmt.Finally is { } finallyBlock)
        {
            _indent--;
            Line("}");
            Line("finally");
            EmitBody(finallyBlock);
        }
    }

    // An except clause's test: its classes, then its Python classes, tested with Python's isinstance.
    private string HandlerTest(BoundHandler handler, string exception)
    {
        var tests = new List<string>();
        if (!handler.Classes.IsEmpty)
            tests.Add($"{exception} is {string.Join(" or ", handler.Classes.Select(c => TypeName(c.Type)))}");
        var python = $"{Runtime}.PythonError";
        foreach (var pythonClass in handler.PythonClasses)
            tests.Add($"({exception} is {python} && {Interop}.IsInstance((({python}){exception}).@value, {Expr(pythonClass)}))");
        return string.Join(" || ", tests);
    }

    private void EmitForRange(BoundForRange forStmt)
    {
        var variable = $"@{_locals[forStmt.Variable]}";
        var counter = BoundTree.MakesClosures(forStmt.Body) ? $"@{_locals.Fresh(forStmt.Variable.Name)}" : variable;
        var end = $"@{_locals.Fresh("end")}";
        var start = forStmt.Start is null ? "0L" : Expr(forStmt.Start);
        LineDirective(forStmt.Span);
        Line($"for (long {counter} = {start}, {end} = {Expr(forStmt.Stop)}; {counter} < {end}; {counter}++)");
        EmitLoopBody(forStmt.Body, counter == variable ? null : $"long {variable} = {counter};");
    }

    private void EmitLoopBody(BoundBlock body, string? prologue = null)
    {
        var loop = new LoopContext();
        _loops.Push(loop);
        EmitBody(body, prologue);
        _loops.Pop();
        if (loop.BreakLabel is { } label)
            Line($"{label}: ;");
    }

    // A nested def is a Function wrapping a C# lambda. Its variable is declared first, so the
    // body can call it recursively (SPEC §3.7). Loops outside it aren't its loops.
    private void EmitLocalFunction(BoundLocalFunction function)
    {
        var type = TypeName(function.Symbol.Type);
        var name = $"@{_locals[function.Symbol]}";
        LineDirective(function.Span);
        Line($"{type} {name} = null!;");
        Line($"{name} = new {type}(\"{function.Function.Name}\", ({LambdaParameters(function.Parameters)}) =>");
        var outerLoops = _loops;
        _loops = new Stack<LoopContext>();
        Line("{");
        _indent++;
        EmitStatements(function.Body.Statements);
        _indent--;
        Line("});");
        _loops = outerLoops;
    }

    private string LambdaParameters(IEnumerable<VariableSymbol> parameters) =>
        string.Join(", ", parameters.Select(p => $"{TypeName(p.Type)} @{_locals[p]}"));

    // A lambda is a Function wrapping a C# lambda. One whose result is none gets a statement body.
    private string Lambda(BoundLambda lambda)
    {
        var type = TypeName(lambda.Type);
        var body = lambda.FunctionType.Result is not NothingType ? Expr(lambda.Body)
            : lambda.Body is BoundCall or BoundMethodCall or BoundBuiltinMethodCall or BoundPyCall or BoundInvoke or BoundSort or BoundSuperCall ? $"{{ {Expr(lambda.Body)}; }}"
            : $"{{ _ = {Expr(lambda.Body)}; }}";
        return $"new {type}(\"<lambda>\", ({LambdaParameters(lambda.Parameters)}) => {body})";
    }

    private string FunctionValue(BoundFunctionValue value)
    {
        if (!_functionValues.TryGetValue(value.Function, out var cached))
        {
            cached = (UniqueGlobal($"{_names[value.Function]}_value"), value.FunctionType);
            _globalNames.Add(cached.Field);
            _functionValues[value.Function] = cached;
        }
        return $"global::{_programClass}.@{cached.Field}";
    }

    // A value tuple with explicit element types, so each value converts to its element's type.
    // Past seven elements, C# nests the rest in an eighth.
    private string NewTuple(ImmutableArray<TaipanType> types, IReadOnlyList<string> values)
    {
        var names = types.Select(TypeName).ToList();
        if (types.Length <= 7)
            return $"global::System.ValueTuple.Create<{string.Join(", ", names)}>({string.Join(", ", values)})";
        var rest = $"({string.Join(", ", names.Skip(7))})";
        var restValue = types.Length == 8 ? $"global::System.ValueTuple.Create<{names[7]}>({values[7]})" : NewTuple([.. types.Skip(7)], [.. values.Skip(7)]);
        return $"new global::System.ValueTuple<{string.Join(", ", names.Take(7))}, {(types.Length == 8 ? $"global::System.ValueTuple<{names[7]}>" : rest)}>({string.Join(", ", values.Take(7))}, {restValue})";
    }

    private string Slice(BoundSlice slice)
    {
        var parts = string.Join(", ", ((BoundExpr?[])[slice.Start, slice.Stop, slice.Step]).Select(p => p is null ? "null" : Expr(p)));
        var owner = Concrete(slice.Target.Type) == TaipanType.Py ? Interop : $"{Runtime}.Ops";
        return $"{owner}.Slice({Expr(slice.Target)}, {parts})";
    }

    // sorted(xs, …) and xs.sort(…): key and reverse are C# named arguments, kept in source order.
    // Classes sort by their __lt__, passed as `less` (SPEC §3.11).
    private string Sort(BoundSort sort)
    {
        var key = sort.Keywords.FirstOrDefault(k => k.Name == "key").Value;
        var method = (sort.InPlace ? "Sort" : "Sorted") + (key is not null ? "By" : "");
        var arguments = sort.Keywords.Select(k => $"{k.Name}: {Expr(k.Value)}").Prepend(Expr(sort.List));
        var sorted = key is not null ? ((FunctionType)TypeChecker.Unwrap(Concrete(key.Type))).Result : ((ListOfType)Concrete(sort.List.Type)).Element;
        if (sorted is ClassType)
        {
            var (a, b) = (_locals.Fresh("a"), _locals.Fresh("b"));
            arguments = arguments.Append($"less: static (@{a}, @{b}) => @{a}.@__lt__(@{b})");
        }
        return $"{Runtime}.Ops.{method}({string.Join(", ", arguments)})";
    }

    // A switch statement; each arm's body gets its own braces so their locals don't clash (SPEC §8).
    private void EmitMatch(BoundMatch match)
    {
        LineDirective(match.Span);
        Line($"switch ({Expr(match.Scrutinee)})");
        Line("{");
        _indent++;
        if (_loops.TryPeek(out var loop))
            loop.SwitchDepth++;

        var hasDefault = false;
        foreach (var arm in match.Arms.Where(a => a.IsReachable))
        {
            hasDefault |= arm.Pattern is BoundWildcardPattern;
            Line(CaseLabel(arm.Pattern, Concrete(match.Scrutinee.Type)));
            Line("{");
            _indent++;
            EmitStatements(arm.Body.Statements);
            var jumpsAway = TypeChecker.Terminates(arm.Body) || arm.Body.Statements.LastOrDefault() is BoundBreak or BoundContinue;
            if (!jumpsAway)
                Line("break;");
            _indent--;
            Line("}");
        }
        if (!hasDefault)
        {
            Line("default:");
            Line("    throw new global::System.Diagnostics.UnreachableException();");
        }

        if (loop is not null)
            loop.SwitchDepth--;
        _indent--;
        Line("}");
    }

    private string CaseLabel(BoundPattern pattern, TaipanType scrutineeType) => pattern switch
    {
        BoundWildcardPattern => "default:",
        BoundLiteralPattern literal => $"case {Literal(literal.Value)}:",
        BoundVariantPattern { Bindings.IsEmpty: true } variant => $"case {VariantTypeName(variant.Variant, scrutineeType)}:",
        BoundVariantPattern variant =>
            $"case {VariantTypeName(variant.Variant, scrutineeType)}({string.Join(", ", variant.Bindings.Select(b => b is null ? "_" : $"var @{_locals[b]}"))}):",
        BoundClassPattern { Bindings.IsEmpty: true } classPattern => $"case {TypeName(classPattern.Class)}:",
        BoundClassPattern classPattern =>
            $"case {TypeName(classPattern.Class)} {{ {string.Join(", ", classPattern.Bindings.Select(b => $"@{Member(b.Field, classPattern.Class)}: var @{_locals[b.Binding]}"))} }}:",
        _ => throw new InvalidOperationException($"unexpected pattern {pattern.GetType().Name}"),
    };

    /// <summary>A variant's nested record, inside its enum's class (or the enum instance's, if generic).</summary>
    private string VariantTypeName(VariantSymbol variant, TaipanType enumType) => $"{TypeName(enumType)}.@{variant.Name}";

    // ---- Expressions ----

    private string Expr(BoundExpr expr)
    {
        if (_hoisted.TryGetValue(expr, out var temporary))
            return temporary;

        return expr switch
        {
            BoundLiteral literal => Literal(literal.Value),
            BoundVariable variable when variable.Symbol == _self => "this",
            BoundVariable { Symbol.Kind: VariableKind.Constant } variable => Global(_names[variable.Symbol]),
            BoundVariable variable => $"@{_locals[variable.Symbol]}",
            BoundUnary { Op: UnaryOp.Negate, Type: PrimitiveType { Name: "py" } } unary => $"{Interop}.Negate({Expr(unary.Operand)})",
            BoundUnary { Op: UnaryOp.Negate } unary when OverflowsAtCompileTime(unary) =>
                $"{Runtime}.Ops.Negate({Expr(unary.Operand)})",
            BoundUnary unary => $"{(unary.Op == UnaryOp.Negate ? "-" : "!")}{Operand(unary.Operand)}",
            BoundBinary binary => Binary(binary),
            BoundCall call => Call(call),
            BoundFString fstring => FString(fstring),
            BoundListLiteral list => ListLiteral(list),
            BoundDictLiteral dict => DictLiteral(dict),
            BoundConstruct construct => Construct(construct),
            BoundConstructVariant construct => ConstructVariant(construct),
            BoundFieldAccess access => $"{Operand(access.Target)}.@{Member(access.Field, access.Target.Type)}",
            BoundIndex index => $"{Runtime}.Ops.Index({Expr(index.Target)}, {Expr(index.Index)})",
            BoundMethodCall call => MethodCall(call),
            BoundBuiltinMethodCall call => BuiltinMethodCall(call),
            BoundNone => "null",
            BoundPyGetAttr get => $"{Interop}.GetAttr({Expr(get.Target)}, \"{get.Name}\")",
            BoundPyGetItem get => $"{Interop}.GetItem({Expr(get.Target)}, {Expr(get.Index)})",
            BoundPyCall call => PyCall(call),
            BoundToPy convert => $"{Interop}.From({Expr(convert.Value)})",
            BoundCast cast => Cast(cast),
            BoundIsSome isSome => $"{Operand(isSome.Operand)} is {{}} @{_locals[isSome.Binding]}",
            BoundIsNone isNone => $"{Operand(isNone.Operand)} is {(isNone.Negated ? "not " : "")}null",
            BoundNew construct => New(construct),
            BoundSuperCall call => SuperCall(call),
            BoundIsInstance isInstance => $"{Operand(isInstance.Operand)} is {TypeName(isInstance.Class)}",
            BoundNarrowed narrowed => Narrowed(narrowed),
            BoundClassCast cast => ClassCast(cast),
            BoundConvertFunction convert => ConvertFunction(convert),
            BoundLambda lambda => Lambda(lambda),
            BoundFunctionValue value => FunctionValue(value),
            BoundInvoke invoke => $"{Operand(invoke.Callee)}.Invoke({ShapedArguments(invoke.Arguments, invoke.Shape,
                p => $"arg{p + 1}", p => Expr(invoke.Function!.Defaults[p]!))})",
            BoundSort sort => Sort(sort),
            BoundTupleLiteral tuple => NewTuple(((TupleOfType)Concrete(tuple.Type)).Elements, [.. tuple.Elements.Select(Expr)]),
            BoundTupleElement element => $"{Operand(element.Tuple)}.Item{element.Index + 1}",
            BoundSlice slice => Slice(slice),
            _ => throw new InvalidOperationException($"unexpected bound expression {expr.GetType().Name}"),
        };
    }

    /// <summary>
    /// A method call (SPEC §4.3). The defaults it leaves out are virtual helpers on the object, so
    /// an object that isn't a simple expression is evaluated once, into a lambda's parameter.
    /// </summary>
    private string MethodCall(BoundMethodCall call)
    {
        var once = call.Shape?.Missing.Any() == true && !IsSimple(call.Target);
        var receiver = once ? $"@{_locals.Fresh("receiver")}" : Operand(call.Target);
        var invocation = $"{receiver}.@{call.Method.Name}({ShapedArguments(call.Arguments, call.Shape,
            p => $"@{call.Method.Parameters[p].Name}", p => $"{receiver}.@{MethodDefaultName(call.Method, p)}()")})";
        if (!once)
            return invocation;
        var helper = call.Method.ReturnType is NothingType ? "Do" : "Let";
        return $"{Runtime}.Ops.{helper}({Expr(call.Target)}, ({TypeName(call.Target.Type)} {receiver}) => {invocation})";
    }

    /// <summary>A plain class's construction, through its static <c>__new__</c> (SPEC §8).</summary>
    private string New(BoundNew construct) =>
        $"{TypeName(construct.ClassType)}.@__new__({ShapedArguments(construct.Arguments, construct.Shape,
            p => $"@{construct.Constructor.Parameters[p].Name}", p => InitDefault(construct.Constructor, p, construct.ClassType))})";

    /// <summary>
    /// An <c>__init__</c> default: a static helper of the class whose <c>__init__</c> declares it.
    /// A class without its own <c>__init__</c> uses its base's defaults (SPEC §4.3).
    /// </summary>
    private string InitDefault(FunctionSymbol constructor, int parameter, ClassType through)
    {
        var origin = constructor;
        while (origin.DefaultsFrom is { } inherited)
            origin = inherited;
        var owner = origin.Owner!;
        var ownerType = Concrete(through) is ClassType classType && classType.Symbol == owner ? classType : owner.Type;
        return $"{TypeName(ownerType)}.@{MethodDefaultName(origin, parameter)}()";
    }

    // `super().m(…)` runs the base's method without dispatch: a C# base call (SPEC §8).
    private string SuperCall(BoundSuperCall call)
    {
        var baseType = call.Method.Owner!.Type;
        string Default(int p) => call.IsInit ? InitDefault(call.Method, p, baseType) : $"base.@{MethodDefaultName(call.Method, p)}()";
        return $"base.@{call.Method.Name}({ShapedArguments(call.Arguments, call.Shape, p => $"@{call.Method.Parameters[p].Name}", Default)})";
    }

    // A narrowed name (SPEC §3.9): a cast to its class, or its value without the optional.
    private string Narrowed(BoundNarrowed narrowed)
    {
        var value = Expr(narrowed.Value);
        var original = Concrete(narrowed.Value.Type);
        var type = Concrete(narrowed.Type);
        if (type is ClassType && TypeChecker.Unwrap(original) != type)
            return $"(({TypeName(type)}){value})";
        return IsValueType(type) ? $"{value}.Value" : $"{value}!";
    }

    // `x as C` between classes (SPEC §4.3): to a base it always succeeds; to a subclass it panics
    // when x isn't one, or for `C?` gives None.
    private string ClassCast(BoundClassCast cast)
    {
        var value = Expr(cast.Value);
        var target = Concrete(cast.Type);
        if (target is OptionalOf optional)
            return $"({value} as {TypeName(optional.Inner)})";
        if (TypeChecker.Unwrap(Concrete(cast.Value.Type)) is ClassType from && from.Symbol.DerivesFrom(((ClassType)target).Symbol))
            return $"(({TypeName(target)}){value})";
        return $"{Runtime}.Ops.Cast<{TypeName(target)}>({value}, \"{target}\")";
    }

    // A function value as another function type (SPEC §3.3): a new delegate that calls it, with C#'s
    // implicit conversions on the arguments and result. It keeps the original's identity.
    private string ConvertFunction(BoundConvertFunction convert)
    {
        var d = $"@{_locals.Fresh("d")}";
        var parameters = convert.FunctionType.Parameters.Select(_ => $"@{_locals.Fresh("a")}").ToList();
        var list = string.Join(", ", parameters);
        return $"{TypeName(convert.FunctionType)}.Convert({Expr(convert.Value)}, {d} => ({list}) => {d}({list}))";
    }

    /// <summary>A function or constant: unqualified in the program class, qualified from a class's members.</summary>
    private string Global(string name) => _self is null ? $"@{name}" : $"global::{_programClass}.@{name}";

    private string FunctionReference(BoundCall call) => Global(FunctionName(call));

    private string FunctionName(BoundCall call) =>
        call.TypeArguments.IsEmpty ? _names[call.Function] : InstanceName(call.Function, [.. call.TypeArguments.Select(Concrete)]);

    /// <summary>
    /// A call's arguments (SPEC §4.2): positional while each is its own parameter, then named, so
    /// they still evaluate in source order; then each missing parameter's <paramref name="default"/>.
    /// <paramref name="parameterName"/> gives the C# name to pass a parameter by.
    /// </summary>
    private string ShapedArguments(
        ImmutableArray<BoundExpr> arguments, ArgumentShape? shape, Func<int, string> parameterName, Func<int, string> @default)
    {
        if (shape is null)
            return Arguments(arguments);
        IEnumerable<(int Parameter, string Value)> all =
            [.. arguments.Select((a, i) => (shape.Parameters[i], Expr(a))), .. shape.Missing.Select(p => (p, @default(p)))];
        var positional = true;
        return string.Join(", ", all.Select((a, i) =>
        {
            positional = positional && a.Parameter == i;
            return positional ? a.Value : $"{parameterName(a.Parameter)}: {a.Value}";
        }));
    }

    private string Arguments(IEnumerable<BoundExpr> arguments) => string.Join(", ", arguments.Select(Expr));

    /// <summary>An operand of an operator or member access, parenthesized when it's an operator expression or a C# <c>is</c> test.</summary>
    private string Operand(BoundExpr expr) =>
        expr is BoundBinary or BoundUnary or BoundIsNone or BoundIsSome or BoundIsInstance && !_hoisted.ContainsKey(expr) ? $"({Expr(expr)})" : Expr(expr);

    /// <summary>
    /// Whether C# would fold this int expression of literals at compile time and overflow, which
    /// C# reports as an error. Such expressions go through <c>Ops</c> so they panic at run time.
    /// </summary>
    private static bool OverflowsAtCompileTime(BoundExpr expr)
    {
        try
        {
            if (expr.Type == TaipanType.Int)
                Fold(expr);
            return false;
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    // The value of an int expression made only of literals, + - * and unary minus, or null if it
    // has any other part (C# doesn't fold those). Throws OverflowException where C# folding would.
    private static long? Fold(BoundExpr expr)
    {
        switch (expr)
        {
            case BoundLiteral { Value: long value }:
                return value;
            case BoundUnary { Op: UnaryOp.Negate } unary:
                return Fold(unary.Operand) is { } operand ? checked(-operand) : null;
            case BoundBinary { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply } binary:
                if (Fold(binary.Left) is not { } left || Fold(binary.Right) is not { } right)
                    return null;
                return binary.Op switch
                {
                    BinaryOp.Add => checked(left + right),
                    BinaryOp.Subtract => checked(left - right),
                    _ => checked(left * right),
                };
            default:
                return null;
        }
    }

    private string Binary(BoundBinary binary)
    {
        if (binary.Op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply && OverflowsAtCompileTime(binary))
            return $"{Runtime}.Ops.{binary.Op}({Expr(binary.Left)}, {Expr(binary.Right)})";

        var leftType = Concrete(binary.Left.Type);
        var (l, r) = (Expr(binary.Left), Expr(binary.Right));
        if (leftType == TaipanType.Py || Concrete(binary.Right.Type) == TaipanType.Py)
            return binary.Op is BinaryOp.And or BinaryOp.Or
                ? $"{Operand(binary.Left)} {(binary.Op == BinaryOp.And ? "&&" : "||")} {Operand(binary.Right)}"
                : $"{Interop}.Binary(\"{TypeChecker.Display(binary.Op)}\", {l}, {r})";
        var left = Operand(binary.Left);
        var right = Operand(binary.Right);
        var compared = TypeChecker.Unwrap(leftType) is NoneType ? TypeChecker.Unwrap(Concrete(binary.Right.Type)) : TypeChecker.Unwrap(leftType);
        var structural = compared is ListOfType or DictOfType or ClassType or EnumType or TupleOfType or FunctionType;
        var tuple = leftType is TupleOfType;
        // A class's __lt__ (SPEC §3.11): 'a > b' evaluates a, then b, and calls b.__lt__(a).
        if (binary.Op is BinaryOp.Less && leftType is ClassType)
            return $"{left}.@__lt__({r})";
        if (binary.Op is BinaryOp.Greater && leftType is ClassType)
        {
            var (first, second) = (_locals.Fresh("left"), _locals.Fresh("right"));
            return $"{Runtime}.Ops.Reversed({l}, {r}, static (@{second}, @{first}) => @{second}.@__lt__(@{first}))";
        }
        return binary.Op switch
        {
            BinaryOp.Add when leftType is ListOfType => $"{Runtime}.Ops.Concat({l}, {r})",
            BinaryOp.Add => $"{left} + {right}",
            BinaryOp.Subtract => $"{left} - {right}",
            BinaryOp.Multiply => $"{left} * {right}",
            BinaryOp.Divide => $"{Runtime}.Ops.Div({l}, {r})",
            BinaryOp.FloorDivide => $"{Runtime}.Ops.FloorDiv({l}, {r})",
            BinaryOp.Modulo => $"{Runtime}.Ops.Mod({l}, {r})",
            BinaryOp.Equal when structural => $"{Runtime}.Equality.Equal({l}, {r})",
            BinaryOp.NotEqual when structural => $"!{Runtime}.Equality.Equal({l}, {r})",
            BinaryOp.Equal => $"{left} == {right}",
            BinaryOp.NotEqual => $"{left} != {right}",
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual when tuple => $"{Runtime}.Ops.{binary.Op}({l}, {r})",
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual when leftType == TaipanType.Str =>
                $"string.CompareOrdinal({l}, {r}) {TypeChecker.Display(binary.Op)} 0",
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual =>
                $"{left} {TypeChecker.Display(binary.Op)} {right}",
            BinaryOp.In => $"{Runtime}.Ops.In({l}, {r})",
            BinaryOp.NotIn => $"!{Runtime}.Ops.In({l}, {r})",
            BinaryOp.And => $"{left} && {right}",
            BinaryOp.Or => $"{left} || {right}",
            BinaryOp.Coalesce => $"{left} ?? {right}",
            _ => throw new InvalidOperationException($"unexpected operator {binary.Op}"),
        };
    }

    private string Call(BoundCall call)
    {
        // A bare `null` passed to print would bind to its params array itself, not be an element.
        var arguments = string.Join(", ", call.Arguments.Select(a =>
            a is BoundNone && call.Function.Builtin == BuiltinFunction.Print ? "(object?)null" : Expr(a)));
        var argumentType = call.Arguments.IsEmpty ? TaipanType.Error : Concrete(call.Arguments[0].Type);
        return call.Function.Builtin switch
        {
            null when call.Shape is not null => $"{FunctionReference(call)}({ShapedArguments(call.Arguments, call.Shape,
                p => $"@{call.Function.Parameters[p].Name}", p => $"{Global(DefaultName(FunctionName(call), call.Function, p))}()")})",
            null => $"{FunctionReference(call)}({arguments})",
            BuiltinFunction.Print => $"{Runtime}.Builtins.Print({arguments})",
            BuiltinFunction.Len when argumentType == TaipanType.Py => $"{Interop}.Len({arguments})",
            BuiltinFunction.Len => $"{Runtime}.Builtins.Len({arguments})",
            BuiltinFunction.Str => $"{Runtime}.Display.Format({arguments})",
            BuiltinFunction.Int when argumentType == TaipanType.Str => $"{Runtime}.Ops.ParseInt({arguments})",
            BuiltinFunction.Float when argumentType == TaipanType.Str => $"{Runtime}.Ops.ParseFloat({arguments})",
            BuiltinFunction.Int when argumentType == TaipanType.Float => $"{Runtime}.Ops.ToInt({arguments})",
            BuiltinFunction.Int => arguments,
            BuiltinFunction.Float when argumentType == TaipanType.Int => $"(double){Operand(call.Arguments[0])}",
            BuiltinFunction.Float => arguments,
            BuiltinFunction.Map => $"{Runtime}.Ops.Map({arguments})",
            BuiltinFunction.Filter => $"{Runtime}.Ops.Filter({arguments})",
            BuiltinFunction.Enumerate => $"{Runtime}.Ops.Enumerate({arguments})",
            BuiltinFunction.Zip => $"{Runtime}.Ops.Zip({arguments})",
            _ => throw new InvalidOperationException($"unexpected builtin {call.Function.Builtin}"),
        };
    }

    private string BuiltinMethodCall(BoundBuiltinMethodCall call)
    {
        var target = Operand(call.Target);
        var args = call.Arguments.Select(Expr).ToList();
        var ordinal = "global::System.StringComparison.Ordinal";
        return call.Method switch
        {
            BuiltinMethod.ListAppend => $"{target}.Add({args[0]})",
            BuiltinMethod.ListPop => $"{Runtime}.Ops.Pop({Expr(call.Target)})",
            BuiltinMethod.ListInsert => $"{Runtime}.Ops.Insert({Expr(call.Target)}, {args[0]}, {args[1]})",
            BuiltinMethod.ListContains => $"{Runtime}.Ops.Contains({Expr(call.Target)}, {args[0]})",
            BuiltinMethod.DictGet when ((DictOfType)Concrete(call.Target.Type)).Value is var value && IsValueType(value) =>
                $"{Runtime}.Ops.Get({Expr(call.Target)}, {args[0]})",
            // Already-optional values: a missing key gives that optional's none (SPEC §3.4).
            BuiltinMethod.DictGet when ((DictOfType)Concrete(call.Target.Type)).Value is OptionalOf =>
                $"{Runtime}.Ops.GetOrDefault({Expr(call.Target)}, {args[0]})",
            BuiltinMethod.DictGet => $"{Runtime}.Ops.GetOrNull({Expr(call.Target)}, {args[0]})",
            BuiltinMethod.DictKeys => $"{Runtime}.Ops.Keys({Expr(call.Target)})",
            BuiltinMethod.DictValues => $"{Runtime}.Ops.Values({Expr(call.Target)})",
            BuiltinMethod.DictItems => $"{Runtime}.Ops.Items({Expr(call.Target)})",
            BuiltinMethod.DictRemove => $"{target}.Remove({args[0]})",
            BuiltinMethod.StrUpper => $"{target}.ToUpperInvariant()",
            BuiltinMethod.StrLower => $"{target}.ToLowerInvariant()",
            BuiltinMethod.StrStrip => $"{target}.Trim()",
            BuiltinMethod.StrSplit => $"{Runtime}.Ops.Split({Expr(call.Target)}, {args[0]})",
            BuiltinMethod.StrStartsWith => $"{target}.StartsWith({args[0]}, {ordinal})",
            BuiltinMethod.StrEndsWith => $"{target}.EndsWith({args[0]}, {ordinal})",
            BuiltinMethod.StrReplace => $"{Runtime}.Ops.Replace({Expr(call.Target)}, {args[0]}, {args[1]})",
            _ => throw new InvalidOperationException($"unexpected builtin method {call.Method}"),
        };
    }

    // Positional arguments as an object?[], keywords as (name, value) pairs (SPEC §10.2).
    private string PyCall(BoundPyCall call)
    {
        var positional = string.Join(", ", call.Arguments.Select(Expr));
        var keywords = string.Join(", ", call.Keywords.Select(k => $"(\"{k.Name}\", {Expr(k.Value)})"));
        return $"{Interop}.Call({Expr(call.Callee)}, [{positional}], [{keywords}])";
    }

    // `x as T` (SPEC §10.4): a converter lambda built from T. An optional target gives none on a
    // mismatch; any other panics, naming T.
    private string Cast(BoundCast cast)
    {
        var target = Concrete(cast.Type);
        var value = Expr(cast.Value);
        var v = $"@{_locals.Fresh("v")}";
        if (target is OptionalOf optional)
        {
            var method = IsValueType(optional.Inner) ? "AsOptionalValue" : "AsOptionalRef";
            return $"{Interop}.{method}({value}, {v} => {Converter(optional.Inner, v)})";
        }
        return $"{Interop}.As({value}, {v} => {Converter(target, v)}, \"{target}\")";
    }

    private string Converter(TaipanType type, string value)
    {
        switch (type)
        {
            case PrimitiveType { Name: "int" }:
                return $"{Interop}.ToInt({value})";
            case PrimitiveType { Name: "float" }:
                return $"{Interop}.ToFloat({value})";
            case PrimitiveType { Name: "bool" }:
                return $"{Interop}.ToBool({value})";
            case PrimitiveType { Name: "str" }:
                return $"{Interop}.ToStr({value})";
            case PrimitiveType { Name: "py" }:
                return value;
            case ListOfType list:
            {
                var e = $"@{_locals.Fresh("e")}";
                return $"{Interop}.ToList({value}, {e} => {Converter(list.Element, e)})";
            }
            case DictOfType dict:
            {
                var (k, e) = ($"@{_locals.Fresh("k")}", $"@{_locals.Fresh("e")}");
                return $"{Interop}.ToDict({value}, {k} => {Converter(dict.Key, k)}, {e} => {Converter(dict.Value, e)})";
            }
            case TupleOfType tuple:
            {
                var items = $"@{_locals.Fresh("items")}";
                var elements = tuple.Elements.Select((element, i) => Converter(element, $"{items}[{i}]"));
                return $"{Interop}.ToTuple({value}, {tuple.Elements.Length}, {items} => {NewTuple(tuple.Elements, [.. elements])})";
            }
            case OptionalOf optional:
            {
                var e = $"@{_locals.Fresh("e")}";
                var method = IsValueType(optional.Inner) ? "NoneOrValue" : "NoneOrRef";
                return $"{Interop}.{method}({value}, {e} => {Converter(optional.Inner, e)})";
            }
            default:
                throw new InvalidOperationException($"no Python conversion to {type}");
        }
    }

    private string ListLiteral(BoundListLiteral list)
    {
        var type = TypeName(list.ListType);
        return list.Elements.IsEmpty ? $"new {type}()" : $"new {type} {{ {Arguments(list.Elements)} }}";
    }

    private string DictLiteral(BoundDictLiteral dict)
    {
        var type = TypeName(dict.DictType);
        var entries = string.Join(", ", dict.Entries.Select(e => $"[{Expr(e.Key)}] = {Expr(e.Value)}"));
        return dict.Entries.IsEmpty ? $"new {type}()" : $"new {type} {{ {entries} }}";
    }

    // Keyword construction uses C# named arguments, which C# evaluates in source order; then each
    // field left out takes its default, in field order (SPEC §4.6).
    private string Construct(BoundConstruct construct)
    {
        var fields = construct.ClassType.Symbol.AllFields;
        var shape = new ArgumentShape([.. construct.Arguments.Select(a => fields.IndexOf(a.Field))], fields.Length);
        var arguments = ShapedArguments([.. construct.Arguments.Select(a => a.Value)], shape.InOrder && shape.Parameters.Length == fields.Length ? null : shape,
            p => $"@{Member(fields[p], construct.ClassType)}",
            p => $"{TypeName(OwnerType(fields[p], construct.ClassType))}.@{FieldDefaultName(ClassName(OwnerType(fields[p], construct.ClassType)), fields[p])}()");
        return $"new {TypeName(construct.ClassType)}({arguments})";
    }

    private string ConstructVariant(BoundConstructVariant construct)
    {
        var arguments = construct.Arguments.Select(a =>
            construct.IsKeyword ? $"@{FieldName(construct.Variant.Name, a.Field)}: {Expr(a.Value)}" : Expr(a.Value));
        return $"new {VariantTypeName(construct.Variant, construct.EnumType)}({string.Join(", ", arguments)})";
    }

    private string FString(BoundFString fstring)
    {
        var text = new StringBuilder("$\"");
        foreach (var part in fstring.Parts)
        {
            if (part is BoundLiteral { Value: string literal })
                text.Append(Escape(literal).Replace("{", "{{").Replace("}", "}}"));
            else
                text.Append($"{{({Runtime}.Display.Format({Expr(part)}))}}");
        }
        return text.Append('"').ToString();
    }

    private static string Literal(object value) => value switch
    {
        long.MinValue => "long.MinValue",
        long l => $"{l.ToString(CultureInfo.InvariantCulture)}L",
        double d when double.IsPositiveInfinity(d) => "double.PositiveInfinity",
        double d => FloatLiteral(d),
        bool b => b ? "true" : "false",
        string s => $"\"{Escape(s)}\"",
        _ => throw new InvalidOperationException($"unexpected literal {value.GetType().Name}"),
    };

    private static string FloatLiteral(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static string Escape(string value)
    {
        var escaped = new StringBuilder();
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\t' => @"\t",
                '\r' => @"\r",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return escaped.ToString();
    }

    private string TypeName(TaipanType type) => Concrete(type) switch
    {
        PrimitiveType { Name: "int" } => "long",
        PrimitiveType { Name: "float" } => "double",
        PrimitiveType { Name: "bool" } => "bool",
        PrimitiveType { Name: "str" } => "string",
        PrimitiveType { Name: "py" } => PyObject,
        NothingType => "void",
        ListOfType list => $"{Collections}.List<{TypeName(list.Element)}>",
        DictOfType dict => $"{Collections}.OrderedDictionary<{TypeName(dict.Key)}, {TypeName(dict.Value)}>",
        ClassType { TypeArguments.IsEmpty: true } classType => $"global::@{_names[classType.Symbol]}",
        ClassType classType => $"global::@{InstanceName(classType.Symbol, classType.TypeArguments)}",
        EnumType { TypeArguments.IsEmpty: true } enumType => $"global::@{_names[enumType.Symbol]}",
        EnumType enumType => $"global::@{InstanceName(enumType.Symbol, enumType.TypeArguments)}",
        OptionalOf optional => $"{TypeName(optional.Inner)}?",
        FunctionType function => $"{Runtime}.Function<{DelegateName(function)}>",
        TupleOfType tuple => $"({string.Join(", ", tuple.Elements.Select(TypeName))})",
        var other => throw new InvalidOperationException($"no C# type for {other}"),
    };

    private string DelegateName(FunctionType function)
    {
        IEnumerable<string> types = function.Parameters.Select(TypeName);
        if (function.Result is NothingType)
            return function.Parameters.IsEmpty ? "global::System.Action" : $"global::System.Action<{string.Join(", ", types)}>";
        return $"global::System.Func<{string.Join(", ", types.Append(TypeName(function.Result)))}>";
    }

    /// <summary>Whether the C# type is a value type, whose optional is <c>Nullable&lt;T&gt;</c>.</summary>
    private static bool IsValueType(TaipanType type) =>
        type == TaipanType.Int || type == TaipanType.Float || type == TaipanType.Bool || type is TupleOfType;

    // ---- Output ----

    private void LineDirective(TextSpan span)
    {
        _statementSpan = span;
        var line = _lineSource.GetLinePosition(span.Start).Line;
        _out.Append($"#line {line} \"{_lineSource.Path}\"\n");
    }

    private void HiddenDirective() => _out.Append("#line hidden\n");

    private void Line(string text = "")
    {
        if (text.Length > 0)
            _out.Append(' ', _indent * 4);
        _out.Append(text).Append('\n');
    }
}
