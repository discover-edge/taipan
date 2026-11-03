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
/// <param name="References">The .NET assemblies it compiles and runs against beyond the defaults (SPEC §13.2).</param>
public sealed record EmittedProgram(string Code, string? EntryClass, ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> References);

/// <summary>
/// Emits one C# compilation unit for a bound program (SPEC §8). Every statement is preceded by a
/// <c>#line</c> directive so stack traces, and therefore panics, report <c>.tpn</c> lines.
/// </summary>
public sealed partial class CSharpEmitter
{
    private const string Runtime = "global::Taipan.Runtime";
    private const string Collections = "global::System.Collections.Generic";
    private const string Tasks = "global::System.Threading.Tasks.Task";
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

    // The program class, fully qualified.
    private readonly string _programPath;
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
    private readonly Dictionary<(Symbol, string), string> _cachedLambdas = [];
    private string? _currentClassName;
    private readonly List<(string Field, string Type, string Value)> _cachedLambdaFields = [];

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

        /// <summary>A for loop's line, which a native program's line is set back to before the next element (SPEC §15.3).</summary>
        public int? Line { get; init; }
    }

    private CSharpEmitter(SourceText source, BoundProgram program)
    {
        _source = source;
        _lineSource = source;
        _program = program;
        _library = program.Library;
        _globalNames = [.. program.Enums.SelectMany(e => e.Variants.Select(v => v.Name))];
        // A library's module classes share its namespace with its internal types (SPEC §14.1).
        if (_library is not null)
            _globalNames.UnionWith(_library.Modules.Select(m => m.Name));
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
        _programClass = UniqueGlobal(_library is null ? "TaipanProgram" : "TaipanLibrary");
        _programPath = _library is null ? $"global::{_programClass}" : $"global::{_library.Namespace}.{_programClass}";
        _topLevelMethod = UniqueGlobal("TopLevel");
        _entryClass = UniqueGlobal("TaipanEntry");
        if (_library is not null)
            SetUpLibrary(_library);
    }

    /// <summary>Emits a program, which tracks its own lines for tracebacks, native or not (SPEC §15.3).</summary>
    public static EmittedProgram Emit(SourceText source, BoundProgram program)
    {
        var emitter = new CSharpEmitter(source, program);
        var code = program.Library is null ? emitter.EmitProgram() : emitter.EmitLibrary();
        // A library and an extension module have no entry point (SPEC §14.4, §17.1).
        var entry = program.Library is null && program.Extension is null ? emitter._entryClass : null;
        return new EmittedProgram(code, entry, emitter._diagnostics.ToImmutable(), program.References);
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
        foreach (var classDef in _program.Classes.Where(s => !s.Symbol.TypeParameters.IsEmpty && !s.Symbol.ClassAttributes.IsEmpty))
        {
            Line();
            Line($"{_typeAccess} static class @{_names[classDef.Symbol]}__ClassVars");
            Line("{");
            _indent++;
            EmitClassAttributes(classDef.Symbol, holder: true);
            _indent--;
            Line("}");
        }

        while (_pending.TryDequeue(out var instance))
            EmitInstance(instance);
        EmitProtocols();
        PatchProtocols(_typesOut);

        _out = _functionsOut;
        _indent = 1;
        if (_functionValues.Count > 0 || _cachedLambdaFields.Count > 0)
            Line();
        foreach (var (function, (field, type)) in _functionValues)
            Line($"public static readonly {TypeName(type)} @{field} = new(\"{function.Name}\", @{_names[function]});");
        foreach (var (field, type, value) in _cachedLambdaFields)
            Line($"public static readonly {type} @{field} = {value};");

        var code = new StringBuilder();
        code.Append($"// Generated by taipan from {_source.Path}. Do not edit.\n");
        code.Append("#nullable enable\n#line hidden\n\n");
        code.Append($"public static class {_programClass}\n{{\n");
        code.Append(_functionsOut);
        code.Append("}\n");
        code.Append(_typesOut);
        if (_program.Extension is { } extension)
        {
            code.Append(ExtensionClass(extension));
            return code.ToString();
        }
        code.Append($"\npublic static class {_entryClass}\n{{\n");
        // An awaiting top level runs to its end before the program exits (SPEC §3.14).
        code.Append(_program.IsAsync
            ? $"    public static int Main(string[] args) => {Runtime}.Panic.Run(() => {_programClass}.{_topLevelMethod}().GetAwaiter().GetResult(), args);\n"
            : $"    public static int Main(string[] args) => {Runtime}.Panic.Run({_programClass}.{_topLevelMethod}, args);\n");
        code.Append("}\n");
        return code.ToString();
    }

    private void EmitInstance(Instance instance)
    {
        (_depth, _rootSpan, _rootSource) = (instance.Depth, instance.RootSpan, instance.RootSource);
        // In a library, instances are internal (SPEC §14.2).
        if (_library is not null)
        {
            _currentModule = _moduleOf[instance.Definition];
            _typeAccess = "internal";
        }
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
        _typeAccess = "public";
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
        ClassType { Symbol.IsProtocol: true } protocol => ProtocolName(protocol),
        ClassType { TypeArguments.IsEmpty: true } s => _names[s.Symbol],
        ClassType s when IsNetGeneric(s.Symbol) => _names[s.Symbol],
        ClassType s => InstanceName(s.Symbol, s.TypeArguments),
        _ => throw new InvalidOperationException($"no class for {owner}"),
    };

    private static string Mangle(TaipanType type) => type switch
    {
        PrimitiveType primitive => primitive.Name,
        ListOfType list => $"list_{Mangle(list.Element)}",
        SetOfType set => $"set_{Mangle(set.Element)}",
        VarTupleOfType varTuple => $"tuple_{Mangle(varTuple.Element)}_etc",
        IteratorOfType iterator => $"iterator_{Mangle(iterator.Element)}",
        AwaitableOfType awaitable => $"awaitable_{Mangle(awaitable.Result)}",
        IterableOfType iterable => $"iterable_{Mangle(iterable.Element)}",
        DictOfType dict => $"dict_{Mangle(dict.Key)}_{Mangle(dict.Value)}",
        OptionalOf optional => $"{Mangle(optional.Inner)}_opt",
        ClassType classType => string.Join("_", [classType.Symbol.Name, .. classType.TypeArguments.Select(Mangle)]),
        DotNetType net => net.Symbol.Name,
        EnumType enumType => string.Join("_", [enumType.Symbol.Name, .. enumType.TypeArguments.Select(Mangle)]),
        FunctionType function => string.Join("_", ["fn", .. function.Parameters.Select(Mangle), function.Result is NothingType ? "none" : Mangle(function.Result)]),
        TupleOfType tuple => string.Join("_", ["tuple", .. tuple.Elements.Select(Mangle)]),
        _ => "error",
    };

    /// <summary>A type with the current instance's type arguments substituted.</summary>
    private TaipanType Concrete(TaipanType type) => TypeChecker.Unaliased(TypeChecker.Substitute(type, _map));

    private void EmitTopLevel()
    {
        _locals = LocalNames.For([], _program.TopLevel, _globalNames);
        Line($"public static {(_program.IsAsync ? $"async {Tasks}" : "void")} {_topLevelMethod}()");
        Line("{");
        _indent++;
        EmitTracked(_program.TopLevel.Span, "<module>", () =>
        {
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
        });
        _indent--;
        Line("}");
    }

    private void EmitFunction(BoundFunction function, bool isMethod, string? name = null)
    {
        var symbol = function.Symbol;
        _lineSource = function.Source ?? _source;
        _locals = LocalNames.For(function.Parameters, function.Body, _globalNames);
        var owner = symbol.Owner;
        _self = symbol.Self ?? (owner is null ? null : NoSelf);
        _cls = symbol.Cls;
        // In a library, a method's *args is params when it's last, and its **kwargs may be null (SPEC §14.2).
        var library = _library is not null && owner is not null;
        var last = function.Parameters.Length - 1;
        var parameters = string.Join(", ", function.Parameters.Select((p, i) =>
            $"{(library && i == last && symbol.Kinds[i] == ParamKind.VarArgs ? "params " : "")}{TypeName(p.Type)}{(library && symbol.Kinds[i] == ParamKind.VarKeywords ? "?" : "")} @{_locals[p]}"));
        var isInit = owner is not null && symbol == owner.Init;
        // Methods dispatch on the runtime class (SPEC §4.3); each class's __init__ is its own.
        var modifier = owner is null ? "static "
            : isInit ? (owner.Base is null ? "" : "new ")
            : symbol.IsAbstract ? "abstract "
            : symbol.Overrides is not null || symbol.SetterOf?.Overrides?.Setter is not null ? "override "
            : owner.HasSubclasses ? "virtual " : "";
        var typeParameters = owner is null && IsNetGeneric(symbol) ? TypeParameterList(symbol.TypeParameters) : "";
        name ??= owner is null ? symbol.Name : MethodName(symbol);
        // In a library, a property's getter and setter are behind a C# property (SPEC §14).
        var access = symbol.Kind == MemberKind.Property || symbol.SetterOf is not null ? HelperAccess : MemberAccess(symbol.Name);
        var async = symbol.IsAsync && !symbol.IsAbstract ? "async " : "";
        var signature = $"{access} {modifier}{async}{TypeName(symbol.ReturnType)} @{name}{typeParameters}({parameters})";
        if (symbol.IsAbstract)
        {
            // An abstract method has no body (SPEC §8); its defaults are still helpers.
            Line($"{signature};");
            EmitMethodDefaults(symbol);
            (_self, _cls) = (null, null);
            return;
        }
        Line(signature);
        Line("{");
        _indent++;
        // A property's getter and setter are frames named for the property (SPEC §3.19).
        EmitTracked(function.Span, symbol.SetterOf?.Name ?? symbol.Name, () =>
        {
            if (library)
            {
                foreach (var keywords in function.Parameters.Where((_, i) => symbol.Kinds[i] == ParamKind.VarKeywords))
                    Line($"@{_locals[keywords]} ??= new();");
            }
            if (isInit)
                EmitFieldDefaults(owner!);
            if (symbol.IsGenerator)
                EmitGeneratorBody(symbol, function.Body.Statements);
            else
                EmitStatements(function.Body.Statements);
            HiddenDirective();
        });
        _indent--;
        Line("}");

        if (!isMethod)
        {
            for (var i = 0; i < symbol.Parameters.Length; i++)
            {
                if (symbol.DefaultFor(i) is { } value)
                    EmitDefault(value, symbol.Parameters[i].Type, DefaultName(name, symbol, i) + typeParameters, "static ");
            }
        }
        else
        {
            EmitMethodDefaults(symbol);
        }
        if (_library is not null && isMethod && !isInit && symbol.Overrides is null && !symbol.IsClassLevel && symbol.Kind != MemberKind.Property && symbol.SetterOf is null)
            EmitMethodOverloads(symbol);
        (_self, _cls) = (null, null);
    }

    private void EmitMethodDefaults(FunctionSymbol symbol)
    {
        for (var i = 0; i < symbol.Parameters.Length; i++)
        {
            if (symbol.DefaultFor(i) is { } value)
                EmitDefault(value, symbol.Parameters[i].Type, MethodDefaultName(symbol, i), DefaultModifier(symbol, i));
        }
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
        Line($"{HelperAccess} {modifier}{TypeName(type)} @{name}()");
        Line("{");
        _indent++;
        // A library's helpers can run first in a module, in a constructor's arguments (SPEC §14.3).
        if (_library is not null)
            Line($"{InitCall(_currentModule!)};");
        // A default has no frame of its own; the call's frame reports it (SPEC §3.19).
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

    // A field of a generic class is in the instantiation the class it's reached through derives from (SPEC §3.18).
    private TaipanType OwnerType(FieldSymbol field, TaipanType through) =>
        Concrete(through) is ClassType classType && TypeChecker.AsBase(classType, field.Owner!) is { } owner ? owner : field.Owner!.Type;

    // A class's base list: its base class, or nothing, then the runtime interface it implements,
    // listed again in a subclass to give it its own TypeName (SPEC §8).
    private string BaseList(ClassSymbol symbol, string runtimeInterface) =>
        (symbol.BaseType is { } baseType ? $"{TypeName(baseType)}, {Runtime}.{runtimeInterface}" : $"{Runtime}.{runtimeInterface}")
        + InterfacesMarker(_currentClassName!);

    private void EmitClass(BoundClass classDef, string className)
    {
        var symbol = classDef.Symbol;
        // The protocols it satisfies are filled in at the end (SPEC §3.18).
        _currentClassName = className;
        _emittedClasses.Add((ClassTypeOf(classDef), className));
        // Self in the class's signatures is the class itself in C# (SPEC §8, M39).
        _selfClass = ClassTypeOf(classDef);
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
        Line($"{_typeAccess} {ClassModifier(symbol)}class @{className}{TypeParameterList(symbol)} : {BaseList(symbol, "IDataclass")}");
        Line("{");
        _indent++;
        foreach (var field in symbol.Fields)
            Line($"{MemberAccess(field.Name)} {TypeName(field.Type)} @{FieldName(className, field)} {{ get; set; }}");
        Line();

        var all = symbol.AllFields;
        var parameters = string.Join(", ", all.Select(f => $"{TypeName(TypeChecker.Substitute(f.Type, TypeChecker.MapOf(symbol.Type)))} @{Member(f, symbol.Type)}"));
        var inherited = symbol.Base is { IsDataclass: true } dataclassBase ? dataclassBase.AllFields : [];
        var baseCall = inherited.IsEmpty ? "" : $" : base({string.Join(", ", inherited.Select(f => $"@{Member(f, symbol.Type)}"))})";
        Line($"public @{className}({parameters}){baseCall}");
        Line("{");
        if (_library is not null)
            Line($"    {InitCall(_currentModule!)};");
        foreach (var field in symbol.Fields)
            Line($"    this.@{FieldName(className, field)} = @{FieldName(className, field)};");
        // A dataclass exception's message and args come from its fields' values (SPEC §3.19).
        if (BuiltinClasses.IsException(symbol))
            Line($"    if (GetType() == typeof(@{className})) {Runtime}.Exceptions.InitData(this{string.Concat(all.Select(f => $", @{Member(f, symbol.Type)}"))});");
        // Only the most derived constructor runs __post_init__, once every field is set (SPEC §4.9).
        if (symbol.FindMethod("__post_init__") is not null)
            Line($"    if (GetType() == typeof(@{className})) this.@__post_init__();");
        Line("}");
        if (_library is not null)
            EmitDataclassOverloads(symbol, className);
        Line();

        var fields = string.Join(", ", all.Select(f => $"(\"{f.Name}\", (object?)this.@{Member(f, symbol.Type)})"));
        Line($"string {Runtime}.IStruct.TypeName => \"{symbol.Name}\";");
        Line($"{Collections}.IReadOnlyList<(string Name, object? Value)> {Runtime}.IStruct.Fields => [{fields}];");
        Line($"public override string ToString() => {Runtime}.Display.Format(this);");
        EmitSpecialHooks(symbol, ClassTypeOf(classDef));
        EmitFieldDefaultHelpers(symbol, className);
        EmitMembers(classDef);
        _indent--;
        _out.Append(ImplementationsMarker(className));
        Line("}");
    }

    // An abstract class can't be sealed, and a class nothing derives from is (SPEC §8).
    private static string ClassModifier(ClassSymbol symbol) =>
        symbol.IsAbstract ? "abstract " : symbol.HasSubclasses ? "" : "sealed ";

    // The class being emitted, as a type: a generic class's instance, by the current substitution.
    private ClassType ClassTypeOf(BoundClass classDef) =>
        classDef.Symbol.TypeParameters.IsEmpty ? classDef.Symbol.Type : (ClassType)TypeChecker.Substitute(classDef.Symbol.Type, _map);

    // Class attributes, instance methods, properties and setters, then the class object (SPEC §8).
    private void EmitMembers(BoundClass classDef)
    {
        var self = _self;
        EmitClassAttributes(classDef.Symbol);
        foreach (var generated in classDef.Symbol.Methods.Values.Where(m => m.Generated is not null))
            EmitGeneratedComparison(generated, ClassTypeOf(classDef));
        foreach (var method in classDef.Methods.Where(m => !m.Symbol.IsClassLevel))
        {
            Line();
            EmitFunction(method, isMethod: true);
        }
        if (_library is not null)
            EmitLibraryMembers(classDef);
        EmitClassObject(classDef, ClassTypeOf(classDef));
        _self = self;
    }

    // A plain class is constructed by a static __new__, which makes the object with a hidden
    // constructor and runs __init__ on it, so __init__ can set the subclass's fields before calling
    // the base's (SPEC §4.3, §8). It compares by reference, since Equals isn't overridden.
    private void EmitPlainClass(BoundClass classDef, string className)
    {
        var symbol = classDef.Symbol;
        var self = $"@{className}{TypeParameterList(symbol)}";
        Line($"{_typeAccess} {ClassModifier(symbol)}class {self} : {BaseList(symbol, "IObject")}");
        Line("{");
        _indent++;
        foreach (var field in symbol.Fields)
            Line($"{MemberAccess(field.Name)} {TypeName(field.Type)} @{FieldName(className, field)} {{ get; set; }} = default!;");
        if (!symbol.Fields.IsEmpty)
            Line();
        var constructor = symbol.Constructor;
        var parameters = string.Join(", ", constructor.Parameters.Select(p => $"{TypeName(p.Type)} @{p.Name}"));
        var arguments = string.Join(", ", constructor.Parameters.Select(p => $"@{p.Name}"));
        if (_library is null)
        {
            Line($"{(symbol.HasSubclasses ? "protected" : "private")} @{className}() {{ }}");
        }
        else
        {
            EmitHiddenConstructor(symbol, className);
            if (!symbol.IsAbstract)
                EmitPublicConstructors(symbol, className, parameters, arguments);
        }
        Line();

        // Nothing constructs an abstract class (SPEC §8).
        if (!symbol.IsAbstract)
        {
            Line($"{HelperAccess} static {self} @__new__({parameters})");
            Line("{");
            Line($"    var @__self = new {self}({(_library is null ? "" : HiddenArgument)});");
            Line($"    @__self.@__init__({arguments});");
            Line("    return @__self;");
            Line("}");
            Line();
        }
        Line($"string {Runtime}.IObject.TypeName => \"{symbol.Name}\";");
        if (symbol.Module is { } module)
            Line($"string {Runtime}.IObject.ModuleName => \"{module}\";");
        Line($"public override string ToString() => {Runtime}.Display.Format(this);");
        EmitSpecialHooks(symbol, ClassTypeOf(classDef));
        EmitFieldDefaultHelpers(symbol, className);

        if (symbol.Init is null)
        {
            // Without its own __init__, a class sets its defaults and passes its arguments on (SPEC §4.3).
            Line();
            Line($"{MemberAccess("__init__")} {(symbol.Base is null ? "" : "new ")}void @__init__({parameters})");
            Line("{");
            _indent++;
            EmitFieldDefaults(symbol);
            if (symbol.Base is not null)
                Line($"base.@__init__({arguments});");
            _indent--;
            Line("}");
        }
        EmitMembers(classDef);
        _indent--;
        _out.Append(ImplementationsMarker(className));
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
        var self = $"@{className}{TypeParameterList(symbol.TypeParameters, IsNetGeneric(symbol))}";
        Line($"{_typeAccess} abstract record {self}");
        Line("{");
        _indent++;
        Line($"private @{className}() {{ }}");
        Line();
        Line($"public sealed override string ToString() => {Runtime}.Display.Format(this);");
        foreach (var variant in symbol.Variants)
        {
            Line();
            var parameters = string.Join(", ", variant.Fields.Select(f => $"{TypeName(f.Type)} @{FieldName(variant.Name, f)}"));
            Line($"public sealed record @{variant.Name}({parameters}) : {self}, {variantInterface}");
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

    private void EmitBody(BoundBlock block, string? prologue = null, string? epilogue = null)
    {
        Line("{");
        _indent++;
        if (prologue is not null)
            Line(prologue);
        EmitStatements(block.Statements);
        if (epilogue is not null)
            Line(epilogue);
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
            case BoundPropertySet assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target],
                    () => Line($"{Operand(assign.Target)}.@{assign.Property.Name}__set({Expr(assign.Value)});"));
                break;
            case BoundClassAttributeAssign assign:
                LineDirective(assign.Span);
                Line($"{AttributeOwner(assign.Attribute.Owner)}.@{assign.Attribute.Name} = {Expr(assign.Value)};");
                break;
            case BoundDotNetAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, assign.Target is null ? [] : [assign.Target], () => Line($"{DotNetAssignment(assign)};"));
                break;
            case BoundDotNetIndexAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Index.Target, .. assign.Index.Arguments.Select(a => a.Value)], () => Line($"{DotNetIndexAssignment(assign)};"));
                break;
            case BoundIndexAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target, assign.Index],
                    () => Line($"{Runtime}.Ops.SetIndex({Expr(assign.Target)}, {Expr(assign.Index)}, {Expr(assign.Value)});"));
                break;
            case BoundClassIndexAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target, assign.Index],
                    () => Line($"{Operand(assign.Target)}.@__setitem__({Expr(assign.Index)}, {Expr(assign.Value)});"));
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
            case BoundSliceAssign assign:
                LineDirective(assign.Span);
                Line($"{SliceOwner(assign.Slice)}.SetSlice({Expr(assign.Slice.Target)}, {SliceParts(assign.Slice)}, {Expr(assign.Value)});");
                break;
            case BoundDelete delete:
                LineDirective(delete.Span);
                foreach (var target in delete.Targets)
                    Line($"{Deletion(target)};");
                break;
            case BoundReturn ret:
                LineDirective(ret.Span);
                // A generator's return ends it (SPEC §3.13).
                Line(_inGenerator ? "yield break;" : ret.Value is null ? "return;" : $"return {Expr(ret.Value)};");
                break;
            case BoundYield yield when yield.From:
                LineDirective(yield.Span);
                var yielded = $"@{_locals.Fresh("yielded")}";
                Line($"foreach (var {yielded} in {Expr(yield.Value)}) yield return {yielded};");
                break;
            case BoundYield yield:
                LineDirective(yield.Span);
                Line($"yield return {Expr(yield.Value)};");
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
                // A for loop's next element is fetched on the loop's line (SPEC §15.3).
                if (_lineVariable is not null && _loops.Peek().Line is { } loopLine)
                    Line($"@{_lineVariable} = {loopLine};");
                Line("continue;");
                break;
            // sys.exit raises SystemExit (SPEC §6.1).
            case BoundExprStmt { Expr: BoundCall { Function.Builtin: BuiltinFunction.Exit } exit } exprStmt:
                LineDirective(exprStmt.Span);
                Line($"throw {Exit(exit)};");
                break;
            case BoundExprStmt { Expr: BoundCall or BoundPrint or BoundInPlaceCheck or BoundMethodCall or BoundBuiltinMethodCall or BoundPyCall or BoundInvoke or BoundSort or BoundSuperCall or BoundClassMethodCall or BoundRuntimeMethodCall or BoundDotNetCall { Method.ReturnsVoid: true } } exprStmt:
                LineDirective(exprStmt.Span);
                Line($"{Expr(exprStmt.Expr)};");
                break;
            case BoundExprStmt { Expr: BoundAwait awaited } exprStmt:
                LineDirective(exprStmt.Span);
                Line($"{(awaited.Type is NothingType ? "" : "_ = ")}await {Operand(awaited.Operand)};");
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
                Line($"while ({Condition(whileStmt.Span, Expr(whileStmt.Condition))})");
                EmitLoopBody(whileStmt.Body);
                break;
            case BoundForRange forStmt:
                EmitForRange(forStmt);
                break;
            case BoundForEach forEach:
                LineDirective(forEach.Span);
                var iterate = Concrete(forEach.Iterable.Type) switch
                {
                    PrimitiveType { Name: "py" } => $"{Interop}.Iterate",
                    // An iterator or iterable is a .NET sequence already (SPEC §3.13).
                    IteratorOfType or IterableOfType or OpaqueType => "",
                    _ => $"{Runtime}.Iter.Over",
                };
                Line($"foreach ({TypeName(forEach.Variable.Type)} @{_locals[forEach.Variable]} in {iterate}({Expr(forEach.Iterable)}))");
                EmitLoopBody(forEach.Body, line: LineOf(forEach.Span));
                break;
            case BoundMatch match:
                EmitMatch(match);
                break;
            case BoundRaise raise:
                LineDirective(raise.Span);
                Line($"{Throw(raise)};");
                break;
            case BoundTry tryStmt:
                EmitTry(tryStmt);
                break;
            case BoundWith with:
                EmitWith(with);
                break;
            case BoundLocalFunction function:
                EmitLocalFunction(function);
                break;
            case BoundUnpack unpack:
                LineDirective(unpack.Span);
                Line($"{TypeName(unpack.Temp.Type)} @{_locals[unpack.Temp]} = {Expr(unpack.Value)};");
                // A list's length is known only now (SPEC §3.8, M29).
                if (unpack.Length is var (count, starred))
                {
                    LineDirective(unpack.Span);
                    Line($"{Runtime}.Ops.CheckUnpack(@{_locals[unpack.Temp]}.Count, {count}, {(starred ? "true" : "false")});");
                }
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
        BoundUnary unary => IsSimple(unary.Operand) && unary.Operand.Type is not ClassType,
        // An operator on a class value calls a special method (SPEC §4.8).
        BoundBinary binary => IsSimple(binary.Left) && IsSimple(binary.Right) && binary.Left.Type is not ClassType && binary.Right.Type is not ClassType,
        _ => false,
    };

    private void EmitIf(BoundIf ifStmt)
    {
        for (var i = 0; i < ifStmt.Clauses.Length; i++)
        {
            var clause = ifStmt.Clauses[i];
            LineDirective(clause.Condition.Span, statement: i == 0);
            Line($"{(i == 0 ? "if" : "else if")} ({(i == 0 ? Expr(clause.Condition) : Condition(clause.Condition.Span, Expr(clause.Condition)))})");
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
    /// except clauses in order, rethrowing it unchanged if none matches. Its filter adds the frame
    /// to the exception's traceback, and each except block handles the exception, for the context
    /// of any raised in it (SPEC §3.19). The else block follows the try/catch, so its exceptions
    /// aren't caught there; it needs a flag only when an except block can finish normally. A
    /// finally block wraps the lot, handling the exception in flight, which a filter captures.
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
        // A generator's try can't have a catch around a yield, so its finally doesn't chain.
        string? inFlight = null;
        if (tryStmt.Finally is not null && !_inGenerator)
        {
            inFlight = $"@{_locals.Fresh("inFlight")}";
            Line($"{Runtime}.TaipanBaseException? {inFlight} = null;");
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
            LineDirective(tryStmt.Span, statement: false);
            Line($"catch (global::System.Exception {caught}) when ({Runtime}.Tracebacks.Catch({caught}, {FrameArguments()}))");
            Line("{");
            _indent++;
            Line($"var {exception} = {Runtime}.Exceptions.From({caught});");
            _caught.Push(caught);
            for (var i = 0; i < handlers.Count; i++)
            {
                var handler = handlers[i];
                Line($"{(i == 0 ? "" : "else ")}if ({HandlerTest(handler, exception, caught)})");
                Line("{");
                _indent++;
                if (handler.Binding is { } symbol)
                    Line($"{TypeName(symbol.Type)} @{_locals[symbol]} = ({TypeName(symbol.Type)}){(symbol.Type is DotNetType ? DotNetException(caught) : exception)};");
                Line($"{Runtime}.Tracebacks.Handle({exception});");
                Line("try");
                EmitBody(handler.Body);
                Line("finally");
                Line("{");
                Line($"    {Runtime}.Tracebacks.Unhandle();");
                Line("}");
                _indent--;
                Line("}");
            }
            _caught.Pop();
            Line("else");
            Line("{");
            Line($"    {Runtime}.Tracebacks.Again({caught});");
            Line("    throw;");
            Line("}");
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
            if (inFlight is null)
            {
                Line("finally");
                EmitBody(finallyBlock);
                return;
            }
            var propagating = $"@{_locals.Fresh("propagating")}";
            Line($"catch (global::System.Exception {propagating}) when ({Runtime}.Tracebacks.InFlight({propagating}, ref {inFlight}))");
            Line("{");
            Line("    throw;");
            Line("}");
            Line("finally");
            Line("{");
            _indent++;
            Line($"{Runtime}.Tracebacks.Handle({inFlight});");
            Line("try");
            EmitBody(finallyBlock);
            Line("finally");
            Line("{");
            Line($"    {Runtime}.Tracebacks.Unhandle();");
            Line("}");
            _indent--;
            Line("}");
        }
    }

    // An except clause's test: its classes, its .NET classes, then its Python classes, tested with
    // Python's isinstance.
    private string HandlerTest(BoundHandler handler, string exception, string caught)
    {
        var tests = new List<string>();
        if (!handler.Classes.IsEmpty)
            tests.Add($"{exception} is {string.Join(" or ", handler.Classes.Select(c => TypeName(c.Type)))}");
        if (!handler.DotNetClasses.IsEmpty)
            tests.Add($"{DotNetException(caught)} is {string.Join(" or ", handler.DotNetClasses.Select(TypeName))}");
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
        if (forStmt.Step is { } step)
        {
            // A step counts down when it's negative, and may not be 0 (SPEC §6).
            var by = $"@{_locals.Fresh("step")}";
            Line($"for (long {counter} = {start}, {end} = {Expr(forStmt.Stop)}, {by} = {Runtime}.Ops.RangeStep({Expr(step)}); {by} > 0 ? {counter} < {end} : {counter} > {end}; {counter} += {by})");
        }
        else
        {
            Line($"for (long {counter} = {start}, {end} = {Expr(forStmt.Stop)}; {counter} < {end}; {counter}++)");
        }
        EmitLoopBody(forStmt.Body, counter == variable ? null : $"long {variable} = {counter};");
    }

    private void EmitLoopBody(BoundBlock body, string? prologue = null, int? line = null)
    {
        var loop = new LoopContext { Line = line };
        _loops.Push(loop);
        EmitBody(body, prologue, _lineVariable is not null && line is not null ? $"@{_lineVariable} = {line};" : null);
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
        Line($"{name} = new {type}(\"{function.Function.Name}\", {(function.Function.IsAsync ? "async " : "")}({LambdaParameters(function.Parameters)}) =>");
        var outerLoops = _loops;
        _loops = new Stack<LoopContext>();
        Line("{");
        _indent++;
        var outerGenerator = _inGenerator;
        _inGenerator = false;
        EmitTracked(function.Span, function.Function.Name, () =>
        {
            if (function.Function.IsGenerator)
                EmitGeneratorBody(function.Function, function.Body.Statements);
            else
                EmitStatements(function.Body.Statements);
        });
        _inGenerator = outerGenerator;
        _indent--;
        Line("});");
        _loops = outerLoops;
    }

    private string LambdaParameters(IEnumerable<VariableSymbol> parameters) =>
        string.Join(", ", parameters.Select(p => $"{TypeName(p.Type)} @{_locals[p]}"));

    // A lambda is a Function wrapping a C# lambda. One whose result is none gets a statement body.
    private string Lambda(BoundLambda lambda)
    {
        if (lambda.Shown is { } shown)
            return CachedLambda(lambda, shown);
        var type = TypeName(lambda.Type);
        var returns = lambda.FunctionType.Result is not NothingType;
        var statement = returns ? $"return {Expr(lambda.Body)};"
            : lambda.Body is BoundCall or BoundMethodCall or BoundBuiltinMethodCall or BoundPyCall or BoundInvoke or BoundSort or BoundSuperCall or BoundRuntimeMethodCall or BoundDotNetCall { Method.ReturnsVoid: true } ? $"{Expr(lambda.Body)};"
            : $"_ = {Expr(lambda.Body)};";
        var body = _tracking ? $"{{ {TrackedStatement(lambda.Span, statement, "<lambda>")} }}"
            : returns ? Expr(lambda.Body)
            : $"{{ {statement} }}";
        return $"new {type}(\"<lambda>\", ({LambdaParameters(lambda.Parameters)}) => {body})";
    }

    /// <summary>
    /// A generic function, built-in or constructor as a value (SPEC §3.18): one static field per
    /// function and type, displayed as Python displays it.
    /// </summary>
    private string CachedLambda(BoundLambda lambda, Symbol shown)
    {
        var type = TypeName(lambda.Type);
        var key = (shown, type);
        if (!_cachedLambdas.TryGetValue(key, out var field))
        {
            var (name, display) = shown switch
            {
                FunctionSymbol { Builtin: BuiltinFunction.Str or BuiltinFunction.Int or BuiltinFunction.Float or BuiltinFunction.List or BuiltinFunction.Set or BuiltinFunction.Tuple } builtin
                    => (builtin.Name, $"<class '{builtin.Name}'>"),
                FunctionSymbol { Builtin: not null } builtin => (builtin.Name, $"<built-in function {builtin.Name}>"),
                FunctionSymbol function => (function.Name, $"<function {function.Name}>"),
                ClassSymbol classSymbol => (classSymbol.Name, $"<class '{classSymbol.Module ?? "__main__"}.{classSymbol.Name}'>"),
                VariantSymbol variant => ($"{variant.Enum.Name}.{variant.Name}", $"<function {variant.Enum.Name}.{variant.Name}>"),
                _ => (shown.Name, $"<function {shown.Name}>"),
            };
            // A function value is no frame of its own; its caller's reports it (SPEC §3.19).
            var tracking = _tracking;
            _tracking = false;
            var body = Lambda(lambda with { Shown = null });
            _tracking = tracking;
            var prefix = $"new {type}(\"<lambda>\", ";
            field = UniqueGlobal($"{name.Replace('.', '_')}_value");
            _globalNames.Add(field);
            _cachedLambdas[key] = field;
            _cachedLambdaFields.Add((field, type, $"new {type}(\"{name}\", {body[prefix.Length..^1]}, \"{display}\")"));
        }
        return $"{_programPath}.@{field}";
    }

    private string FunctionValue(BoundFunctionValue value)
    {
        if (!_functionValues.TryGetValue(value.Function, out var cached))
        {
            cached = (UniqueGlobal($"{_names[value.Function]}_value"), value.FunctionType);
            _globalNames.Add(cached.Field);
            _functionValues[value.Function] = cached;
        }
        return $"{_programPath}.@{cached.Field}";
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

    private string Slice(BoundSlice slice) => $"{SliceOwner(slice)}.Slice({Expr(slice.Target)}, {SliceParts(slice)})";

    // A slice's start, stop and step, with null for an omitted part (SPEC §8).
    private string SliceParts(BoundSlice slice) =>
        string.Join(", ", ((BoundExpr?[])[slice.Start, slice.Stop, slice.Step]).Select(p => p is null ? "null" : Expr(p)));

    private string SliceOwner(BoundSlice slice) => Concrete(slice.Target.Type) == TaipanType.Py ? Interop : $"{Runtime}.Ops";

    // `del` of one target, as bound for reading it (SPEC §5.5).
    private string Deletion(BoundExpr target) => target switch
    {
        BoundIndex index => $"{Runtime}.Ops.DelItem({Expr(index.Target)}, {Expr(index.Index)})",
        BoundPyGetItem item => $"{Interop}.DelItem({Expr(item.Target)}, {Expr(item.Index)})",
        BoundSlice slice => $"{SliceOwner(slice)}.DelSlice({Expr(slice.Target)}, {SliceParts(slice)})",
        _ => throw new InvalidOperationException($"cannot delete {target.GetType().Name}"),
    };

    // sorted(xs, …) and xs.sort(…): key and reverse are C# named arguments, kept in source order.
    // Classes sort by their __lt__, passed as `less` (SPEC §3.11).
    private string Sort(BoundSort sort)
    {
        var key = sort.Keywords.FirstOrDefault(k => k.Name == "key").Value;
        var method = (sort.InPlace ? "Sort" : "Sorted") + (key is not null ? "By" : "");
        var arguments = sort.Keywords.Select(k => $"{k.Name}: {Expr(k.Value)}").Prepend(Expr(sort.List));
        var sorted = key is not null ? ((FunctionType)TypeChecker.Unwrap(Concrete(key.Type))).Result : ((ListOfType)Concrete(sort.List.Type)).Element;
        // Python's sort uses '<', so with subclasses it follows the comparison protocol (SPEC §4.8).
        if (LessThan(sorted) is { } less)
            arguments = arguments.Append($"less: {less}");
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

        // An arm whose or-pattern binds names is one section per alternative (SPEC §8, M38).
        foreach (var arm in match.Arms.Where(a => a.IsReachable))
        {
            foreach (var label in CaseLabels(arm.Pattern, arm.Guard))
            {
                Line(label);
                Line("{");
                _indent++;
                EmitStatements(arm.Body.Statements);
                var jumpsAway = TypeChecker.Terminates(arm.Body) || arm.Body.Statements.LastOrDefault() is BoundBreak or BoundContinue;
                if (!jumpsAway)
                    Line("break;");
                _indent--;
                Line("}");
            }
        }
        if (!match.Arms.Any(a => a.IsReachable && a.Guard is null && Irrefutable(a.Pattern)))
        {
            Line("default:");
            Line("    throw new global::System.Diagnostics.UnreachableException();");
        }

        if (loop is not null)
            loop.SwitchDepth--;
        _indent--;
        Line("}");
    }

    // A pattern C# knows matches everything, after which a default label would be unreachable.
    private static bool Irrefutable(BoundPattern pattern) => pattern switch
    {
        BoundWildcardPattern => true,
        BoundBindingPattern binding => !binding.Typed,
        BoundTuplePattern tuple => tuple.Rest is null && tuple.Elements.All(Irrefutable),
        BoundAsPattern { Typed: false } named => Irrefutable(named.Pattern),
        BoundOrPattern choice => choice.Alternatives.Any(Irrefutable),
        _ => false,
    };

    /// <summary>A switch statement's labels for an arm: one per alternative its or-patterns expand to (SPEC §8).</summary>
    private IEnumerable<string> CaseLabels(BoundPattern pattern, BoundExpr? guard) =>
        pattern is BoundWildcardPattern && guard is null
            ? ["default:"]
            : GuardedPatterns(pattern, guard).Select(p => p.StartsWith("_ ", StringComparison.Ordinal) || p == "_" ? $"case var {p}:" : $"case {p}:");

    /// <summary>
    /// A pattern as C# cases, each with the <c>when</c> that binds what C# patterns can't (a starred
    /// name, a mapping's values) and tests the arm's guard (SPEC §8).
    /// </summary>
    private List<string> GuardedPatterns(BoundPattern pattern, BoundExpr? guard) =>
        [.. Expand(pattern).Select(alternative =>
        {
            var guards = new List<string>();
            var text = Pattern(alternative, guards);
            if (guard is not null)
                guards.Add($"({Expr(guard)})");
            return guards.Count == 0 ? text : $"{text} when {string.Join(" && ", guards)}";
        })];

    /// <summary>A pattern as C# writes it in a <c>case</c> label or a switch expression's arm.</summary>
    private string Pattern(BoundPattern pattern, List<string> guards)
    {
        string Sub(BoundPattern sub) => Pattern(sub, guards);
        switch (pattern)
        {
            case BoundWildcardPattern:
                return "_";
            case BoundBindingPattern { Typed: true } binding when binding.Symbol.Type is not TupleOfType:
                return $"{TypeName(binding.Symbol.Type)} @{_locals[binding.Symbol]}";
            case BoundBindingPattern binding:
                return $"var @{_locals[binding.Symbol]}";
            case BoundNonePattern:
                return "null";
            case BoundSomePattern some:
                return Sub(some.Inner);
            case BoundOrPattern choice:
                return $"({string.Join(" or ", choice.Alternatives.Select(Sub))})";
            case BoundAsPattern { Pattern: BoundWildcardPattern, Typed: false } named:
                return $"var @{_locals[named.Symbol]}";
            case BoundAsPattern named:
                var designation = named.Typed && named.Symbol.Type is not TupleOfType ? TypeName(named.Symbol.Type) : "var";
                return $"({Sub(named.Pattern)}) and {designation} @{_locals[named.Symbol]}";
            case BoundMappingPattern mapping:
                return MappingPattern(mapping, guards);
            case BoundLiteralPattern literal:
                return Literal(literal.Value);
            case BoundVariantPattern { Fields.IsEmpty: true } variant:
                return VariantTypeName(variant.Variant, variant.Enum);
            case BoundVariantPattern variant:
                return $"{VariantTypeName(variant.Variant, variant.Enum)}({string.Join(", ", variant.Fields.Select(Sub))})";
            case BoundClassPattern { Fields.IsEmpty: true } classPattern:
                return TypeName(classPattern.Class);
            case BoundClassPattern classPattern:
                return $"{TypeName(classPattern.Class)} {{ {string.Join(", ", classPattern.Fields.Select(f => $"@{Member(f.Field, classPattern.Class)}: {Sub(f.Pattern)}"))} }}";
            case BoundTuplePattern { Rest: { } tupleRest } tuple:
            {
                // The starred elements are held, then gathered into a list in the guard (SPEC §8, M38).
                var held = Enumerable.Range(0, tuple.StarCount).Select(_ => $"@{_locals.Fresh("element")}").ToList();
                var elements = tuple.Elements.Select((e, i) => i >= tuple.StarAt && i < tuple.StarAt + tuple.StarCount ? $"var {held[i - tuple.StarAt]}" : Sub(e));
                var list = TypeName(tupleRest.Type);
                guards.Add($"{Runtime}.Ops.Let(new {list} {{ {string.Join(", ", held)} }}, out {list} @{_locals[tupleRest]})");
                return $"({string.Join(", ", elements)})";
            }
            case BoundTuplePattern tuple:
                return $"({string.Join(", ", tuple.Elements.Select(Sub))})";
            case BoundListPattern list:
                IEnumerable<string> parts = list.HasStar
                    ? [.. list.Before.Select(Sub), "..", .. list.After.Select(Sub)]
                    : list.Before.Select(Sub);
                var text = $"[{string.Join(", ", parts)}]";
                if (list.Rest is not { } rest)
                    return text;
                // C#'s slice pattern needs a range indexer, which List<T> lacks: the guard takes the rest.
                var whole = _locals.Fresh("list");
                guards.Add($"{Runtime}.Ops.Rest(@{whole}, {list.Before.Length}, {list.After.Length}, out var @{_locals[rest]})");
                return $"{text} @{whole}";
            default:
                throw new InvalidOperationException($"unexpected pattern {pattern.GetType().Name}");
        }
    }

    /// <summary>
    /// A switch expression (SPEC §8). A value of another type than the result is cast to it, since
    /// C# can't always find one type for the arms (int and float, sibling classes, None); a raising
    /// arm is a throw. With a block arm (M29), it's a switch statement in a lambda that's called at once.
    /// </summary>
    private string MatchExpr(BoundMatchExpr match)
    {
        var result = Concrete(match.Type);
        string Value(BoundExpr value) => Concrete(value.Type) == result ? Expr(value) : $"({TypeName(result)})({Expr(value)})";
        var reachable = match.Arms.Where(a => a.IsReachable).ToList();
        if (reachable.Any(a => a.Body is not null))
            return BlockMatchExpr(match, reachable, Value);
        var arms = reachable.SelectMany(arm => GuardedPatterns(arm.Pattern, arm.Guard)
            .Select(pattern => $"{pattern} => {(arm.Raise is { } raise ? Throw(raise) : Value(arm.Value!))}")).ToList();
        if (!reachable.Any(a => a.Guard is null && Irrefutable(a.Pattern)))
            arms.Add("_ => throw new global::System.Diagnostics.UnreachableException()");
        return $"{Operand(match.Scrutinee)} switch {{ {string.Join(", ", arms)} }}";
    }

    private string BlockMatchExpr(BoundMatchExpr match, List<BoundValueArm> arms, Func<BoundExpr, string> value)
    {
        var (outer, outerIndent, outerLoops) = (_out, _indent, _loops);
        _out = new System.Text.StringBuilder();
        _loops = new Stack<LoopContext>();
        _indent = outerIndent + 1;
        Line("{");
        _indent++;
        Line($"switch ({Expr(match.Scrutinee)})");
        Line("{");
        _indent++;
        foreach (var arm in arms)
        {
            foreach (var label in CaseLabels(arm.Pattern, arm.Guard))
            {
                Line(label);
                Line("{");
                _indent++;
                if (arm.Body is { } body)
                    EmitStatements(body.Statements);
                if (arm.Raise is { } raise)
                    Line($"{Throw(raise)};");
                else if (arm.Value is { } armValue)
                    Line($"return {value(armValue)};");
                _indent--;
                Line("}");
            }
        }
        if (!arms.Any(a => a.Guard is null && Irrefutable(a.Pattern)))
        {
            Line("default:");
            Line("    throw new global::System.Diagnostics.UnreachableException();");
        }
        _indent--;
        Line("}");
        _indent--;
        Line(new string(' ', 4 * (outerIndent + 1)) + "}");
        var lambda = _out.ToString();
        (_out, _indent, _loops) = (outer, outerIndent, outerLoops);
        return $"((global::System.Func<{TypeName(Concrete(match.Type))}>)(() =>\n{lambda.TrimEnd('\n')}))()";
    }

    private string Throw(BoundRaise raise) =>
        raise.Value is null ? $"throw {Runtime}.Exceptions.Rethrow({_caught.Peek()})"
        : raise.Cause is { } cause ? $"throw {Runtime}.Exceptions.Raise({Expr(raise.Value)}, {Expr(cause)})"
        : $"throw {Runtime}.Exceptions.Raise({Expr(raise.Value)})";

    /// <summary>A variant's nested record, inside its enum's class (or the enum instance's, if generic).</summary>
    private string VariantTypeName(VariantSymbol variant, TaipanType enumType) => $"{TypeName(enumType)}.@{variant.Name}";

    // ---- Expressions ----

    private string Expr(BoundExpr expr)
    {
        if (_hoisted.TryGetValue(expr, out var temporary))
            return temporary;
        // Arguments collected out of order, or unpacked from a tuple, are evaluated first (M30).
        if (ShapeOf(expr) is { Sources.IsEmpty: false } shape && _withSources.Add(expr))
        {
            try
            {
                return CallWithSources(expr, shape);
            }
            finally
            {
                _withSources.Remove(expr);
            }
        }

        var text = expr switch
        {
            BoundLiteral literal => Literal(literal.Value),
            BoundVariable variable when variable.Symbol == _self => "this",
            // In a class method, cls is the class object the method runs on (SPEC §8).
            BoundVariable variable when variable.Symbol == _cls => "this",
            BoundVariable { Symbol.RuntimePath: { } path } => $"global::{path}",
            BoundVariable { Symbol.Kind: VariableKind.Constant } variable => Global(_names[variable.Symbol]),
            BoundVariable variable => $"@{_locals[variable.Symbol]}",
            BoundUnary { Op: UnaryOp.Negate, Type: PrimitiveType { Name: "py" } } unary => $"{Interop}.Negate({Expr(unary.Operand)})",
            BoundUnary { Op: UnaryOp.Negate } unary when Concrete(unary.Operand.Type) is ClassType => $"{Operand(unary.Operand)}.@__neg__()",
            BoundUnary { Op: UnaryOp.Negate } unary when OverflowsAtCompileTime(unary) =>
                $"{Runtime}.Ops.Negate({Expr(unary.Operand)})",
            BoundUnary unary => $"{(unary.Op == UnaryOp.Negate ? "-" : "!")}{Operand(unary.Operand)}",
            BoundAwait awaited => $"(await {Operand(awaited.Operand)})",
            BoundBinary binary => Binary(binary),
            BoundCall call => Call(call),
            BoundFString fstring => FString(fstring),
            BoundFormat format => Format(format),
            BoundListLiteral list => ListLiteral(list),
            BoundSetLiteral set => SetLiteral(set),
            BoundMinMax minMax => MinMax(minMax),
            BoundComprehension comprehension => Comprehension(comprehension),
            BoundRangeValues range => RangeValues(range),
            BoundRuntimeMethodCall { Kind: "TextFile" } call => $"{Operand(call.Target)}.@{call.Name}({string.Join(", ", call.Arguments.Select(Expr))})",
            BoundRuntimeMethodCall call => $"{Runtime}.{call.Kind}Methods.@{call.Name}({string.Join(", ", [Expr(call.Target), .. call.Arguments.Select(Expr)])})",
            BoundRuntimeProperty property => $"{Operand(property.Target)}.@{property.Name}",
            BoundPrint print => Print(print),
            BoundCallableObject callable => CallableObject(callable),
            BoundInPlaceCheck check => $"{Runtime}.Ops.InPlace({Expr(check.Target)}, {Expr(check.Call)}, \"{check.Op}\", \"{check.Call.Method.Name}\")",
            BoundDictLiteral dict => DictLiteral(dict),
            BoundConstruct construct => Construct(construct),
            BoundConstructVariant construct => ConstructVariant(construct),
            BoundFieldAccess access => $"{Operand(access.Target)}.@{Member(access.Field, access.Target.Type)}",
            BoundPropertyGet get => $"{Operand(get.Target)}.@{MethodName(get.Property)}()",
            BoundClassAttribute attribute => ClassAttribute(attribute),
            BoundClassMethodCall call => ClassMethodCall(call),
            BoundClsNew created => ClsNew(created),
            BoundIndex index => $"{Runtime}.Ops.Index({Expr(index.Target)}, {Expr(index.Index)})",
            BoundMethodCall call => MethodCall(call),
            BoundBuiltinMethodCall call => BuiltinMethodCall(call),
            BoundNone => "null",
            BoundPyGetAttr get => $"{Interop}.GetAttr({Expr(get.Target)}, \"{get.Name}\")",
            BoundPyGetItem get => $"{Interop}.GetItem({Expr(get.Target)}, {Expr(get.Index)})",
            BoundPyCall call => PyCall(call),
            BoundToPy convert => $"{Interop}.From({Expr(convert.Value)})",
            BoundCast cast => Cast(cast),
            BoundProtocolCast cast => $"(({TypeName(cast.Type)}){Expr(cast.Value)})",
            BoundIsSome isSome => $"{Operand(isSome.Operand)} is {{}} @{_locals[isSome.Binding]}",
            BoundIsNone isNone => $"{Operand(isNone.Operand)} is {(isNone.Negated ? "not " : "")}null",
            BoundPyIsInstance test => PyIsInstance(test),
            BoundIs identity => $"{(identity.Negated ? "!" : "")}{(identity.Left.Type == TaipanType.Py ? "global::Taipan.Runtime.Interop" : "global::Taipan.Runtime.Ops")}.Is({Expr(identity.Left)}, {Expr(identity.Right)})",
            BoundNew construct => New(construct),
            BoundSuperCall call => SuperCall(call),
            BoundIsInstance isInstance => $"{Operand(isInstance.Operand)} is {string.Join(" or ", isInstance.Tested.Select(TypeName))}",
            BoundNarrowed narrowed => Narrowed(narrowed),
            BoundClassCast cast => ClassCast(cast),
            BoundConvertFunction convert => ConvertFunction(convert),
            BoundLambda lambda => Lambda(lambda),
            BoundFunctionValue value => FunctionValue(value),
            BoundInvoke invoke => $"{Operand(invoke.Callee)}.Invoke({ShapedArguments(invoke.Arguments, invoke.Shape,
                p => $"arg{p + 1}", p => Expr(invoke.Function!.Defaults[p]!))})",
            BoundSort sort => Sort(sort),
            BoundTupleLiteral tuple => NewTuple(((TupleOfType)Concrete(tuple.Type)).Elements, [.. tuple.Elements.Select(Expr)]),
            BoundVarTupleLiteral tuple => $"new {TypeName(Concrete(tuple.Type))}([{string.Join(", ", tuple.Elements.Select(Expr))}])",
            BoundTupleElement element => $"{Operand(element.Tuple)}.Item{element.Index + 1}",
            BoundCollectedArguments collected => CollectedArguments(collected),
            BoundToIterable converted => ToIterable(converted),
            BoundToList converted => $"{Runtime}.Iterators.ToList({Expr(converted.Values)})",
            BoundCollectedKeywords collected => CollectedKeywords(collected),
            BoundListElement { Index: >= 0 } element => $"{Operand(element.List)}[{element.Index}]",
            BoundListElement element => $"{Operand(element.List)}[{Operand(element.List)}.Count - {-element.Index}]",
            BoundListRest rest => $"{Runtime}.Ops.UnpackRest({Expr(rest.List)}, {rest.Start}, {rest.After})",
            BoundTupleRest rest => $"new {TypeName(rest.ListType)} {{ {string.Join(", ", Enumerable.Range(rest.Start, rest.End - rest.Start).Select(i =>
                $"({TypeName(rest.ListType.Element)}){Operand(rest.Tuple)}.Item{i + 1}"))} }}",
            BoundSlice slice => Slice(slice),
            BoundMatchExpr match => MatchExpr(match),
            BoundDotNetCall call => DotNetCall(call),
            BoundDotNetIndex index => DotNetIndex(index),
            BoundDotNetOperator op => DotNetOperator(op),
            BoundDotNetNew created => DotNetNew(created),
            BoundDotNetMember member => DotNetMember(member),
            _ => throw new InvalidOperationException($"unexpected bound expression {expr.GetType().Name}"),
        };
        return SelfCast(expr, text);
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
        // A py value narrowed by isinstance converts at each read (SPEC §3.18).
        if (narrowed.Value.Type == TaipanType.Py)
            return Cast(new BoundCast(narrowed.Value, narrowed.Type, narrowed.Span));
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
    private string Global(string name) => _self is null ? $"@{name}" : $"{_programPath}.@{name}";

    private string FunctionReference(BoundCall call) => Global(FunctionName(call)) + TypeArgumentList(call);

    private string FunctionName(BoundCall call) =>
        call.TypeArguments.IsEmpty || IsNetGeneric(call.Function) ? _names[call.Function] : InstanceName(call.Function, [.. call.TypeArguments.Select(Concrete)]);

    // A .NET generic function's type arguments, which C# can't always infer (SPEC §14.2).
    private string TypeArgumentList(BoundCall call) =>
        IsNetGeneric(call.Function) ? $"<{string.Join(", ", call.TypeArguments.Select(TypeName))}>" : "";

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
        expr is BoundBinary or BoundUnary or BoundIsNone or BoundIs or BoundIsSome or BoundIsInstance && !_hoisted.ContainsKey(expr) ? $"({Expr(expr)})" : Expr(expr);

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
        // A .NET generic's type parameter could be any of these (SPEC §14.2).
        var structural = compared is ListOfType or SetOfType or DictOfType or ClassType or EnumType or TupleOfType or VarTupleOfType or FunctionType or TypeParameterType;
        var tuple = leftType is TupleOfType or VarTupleOfType;
        // A class's special methods (SPEC §3.11, §4.8): 'a > b' evaluates a, then b, and may call b.__lt__(a).
        if (SpecialMethods.Forward(binary.Op) is not null && SpecialBinary(binary) is { } special)
            return special;
        return binary.Op switch
        {
            BinaryOp.Add when leftType is ListOfType => $"{Runtime}.Ops.Concat({l}, {r})",
            // Sets (SPEC §3.15): new sets, and subset tests.
            BinaryOp.Union when leftType is SetOfType => $"{l}.Union({r})",
            BinaryOp.Intersection when leftType is SetOfType => $"{l}.Intersection({r})",
            BinaryOp.SymmetricDifference when leftType is SetOfType => $"{l}.SymmetricDifference({r})",
            BinaryOp.Subtract when leftType is SetOfType => $"{l}.Difference({r})",
            BinaryOp.LessEqual when leftType is SetOfType => $"{l}.IsSubset({r})",
            BinaryOp.Less when leftType is SetOfType => $"{l}.IsProperSubset({r})",
            BinaryOp.GreaterEqual when leftType is SetOfType => $"{r}.IsSubset({l})",
            BinaryOp.Greater when leftType is SetOfType => $"{r}.IsProperSubset({l})",
            BinaryOp.In when Concrete(binary.Right.Type) is SetOfType => $"{r}.Contains({l})",
            BinaryOp.NotIn when Concrete(binary.Right.Type) is SetOfType => $"!{r}.Contains({l})",
            BinaryOp.Power => $"{Runtime}.Ops.Pow({l}, {r})",
            BinaryOp.Add => $"{left} + {right}",
            BinaryOp.Subtract => $"{left} - {right}",
            BinaryOp.Multiply => $"{left} * {right}",
            BinaryOp.Divide => $"{Runtime}.Ops.Div({l}, {r})",
            BinaryOp.FloorDivide => $"{Runtime}.Ops.FloorDiv({l}, {r})",
            BinaryOp.Modulo => $"{Runtime}.Ops.Mod({l}, {r})",
            // .NET values compare with .NET's Equals (SPEC §13.3).
            BinaryOp.Equal when TypeChecker.Unwrap(leftType) is DotNetType => $"global::System.Object.Equals({l}, {r})",
            BinaryOp.NotEqual when TypeChecker.Unwrap(leftType) is DotNetType => $"!global::System.Object.Equals({l}, {r})",
            BinaryOp.Equal when structural => $"{Runtime}.Equality.Equal({l}, {r})",
            BinaryOp.NotEqual when structural => $"!{Runtime}.Equality.Equal({l}, {r})",
            BinaryOp.Equal => $"{left} == {right}",
            BinaryOp.NotEqual => $"{left} != {right}",
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual when tuple => $"{Runtime}.Ops.{binary.Op}({l}, {r})",
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual when leftType == TaipanType.Str =>
                $"string.CompareOrdinal({l}, {r}) {TypeChecker.Display(binary.Op)} 0",
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual =>
                $"{left} {TypeChecker.Display(binary.Op)} {right}",
            // __contains__ (SPEC §4.9): the item is evaluated first, as in Python.
            BinaryOp.In or BinaryOp.NotIn when Concrete(binary.Right.Type) is ClassType && SpecialMethods.Method(Concrete(binary.Right.Type), "__contains__") is not null =>
                $"{(binary.Op == BinaryOp.NotIn ? "!" : "")}{Runtime}.Ops.Reversed({l}, {r}, static (@container, @item) => @container.@__contains__(@item))",
            BinaryOp.In when Concrete(binary.Right.Type) is IteratorOfType or IterableOfType => $"{Runtime}.Iterators.In({l}, {r})",
            BinaryOp.NotIn when Concrete(binary.Right.Type) is IteratorOfType or IterableOfType => $"!{Runtime}.Iterators.In({l}, {r})",
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
                p => $"@{call.Function.Parameters[p].Name}", p => $"{Global(DefaultName(FunctionName(call), call.Function, p))}{TypeArgumentList(call)}()")})",
            null => $"{FunctionReference(call)}({arguments})",
            BuiltinFunction.Print => $"{Runtime}.Builtins.Print({arguments})",
            // M37 (SPEC §6.1).
            BuiltinFunction.Input => $"{Runtime}.Builtins.Input({arguments})",
            BuiltinFunction.Open => $"{Runtime}.TextFile.@open({arguments})",
            BuiltinFunction.GetEnv => $"{Runtime}.Os.@getenv({arguments})",
            BuiltinFunction.Exit => $"{Runtime}.Exceptions.Throw({Exit(call)})",
            // M35 (SPEC §6).
            BuiltinFunction.Abs when argumentType == TaipanType.Int => $"{Runtime}.Numbers.Abs({arguments})",
            BuiltinFunction.Abs => $"global::System.Math.Abs({arguments})",
            BuiltinFunction.Sum => $"{Runtime}.Numbers.Sum({Expr(call.Arguments[0])}, {AsFloatIf(call.Type == TaipanType.Float, call.Arguments[1])})",
            BuiltinFunction.Any => $"{Runtime}.Numbers.Any({arguments})",
            BuiltinFunction.All => $"{Runtime}.Numbers.All({arguments})",
            BuiltinFunction.Round when call.Arguments.Length == 1 && argumentType == TaipanType.Int => arguments,
            BuiltinFunction.Round => $"{Runtime}.Numbers.Round({arguments})",
            BuiltinFunction.DivMod when call.Arguments.All(a => Concrete(a.Type) == TaipanType.Int) => $"{Runtime}.Numbers.DivMod({arguments})",
            BuiltinFunction.DivMod => $"{Runtime}.Numbers.DivMod({AsFloatIf(true, call.Arguments[0])}, {AsFloatIf(true, call.Arguments[1])})",
            BuiltinFunction.Set => $"new {TypeName(Concrete(call.Type))}({arguments})",
            BuiltinFunction.Tuple => call.Arguments.IsEmpty ? $"{TypeName(Concrete(call.Type))}.Empty" : $"new {TypeName(Concrete(call.Type))}({arguments})",
            BuiltinFunction.Len when argumentType == TaipanType.Py => $"{Interop}.Len({arguments})",
            BuiltinFunction.Len when argumentType is ClassType => $"{Runtime}.Ops.CheckLength({Operand(call.Arguments[0])}.@__len__())",
            BuiltinFunction.Len => $"{Runtime}.Builtins.Len({arguments})",
            BuiltinFunction.Str => $"{Runtime}.Display.Format({arguments})",
            BuiltinFunction.Repr => $"{Runtime}.Display.Repr({arguments})",
            BuiltinFunction.Hash => $"{Runtime}.Ops.Hash({arguments})",
            BuiltinFunction.Int when argumentType == TaipanType.Str => $"{Runtime}.Ops.ParseInt({arguments})",
            BuiltinFunction.Float when argumentType == TaipanType.Str => $"{Runtime}.Ops.ParseFloat({arguments})",
            BuiltinFunction.Int when argumentType == TaipanType.Float => $"{Runtime}.Ops.ToInt({arguments})",
            BuiltinFunction.Int => arguments,
            BuiltinFunction.Float when argumentType == TaipanType.Int => $"(double){Operand(call.Arguments[0])}",
            BuiltinFunction.Float => arguments,
            BuiltinFunction.Map => $"{Runtime}.Iterators.Map({arguments})",
            BuiltinFunction.Iter => $"{Runtime}.Iterators.Iter({arguments}, \"{IteratorName(call.Arguments[0])}\")",
            // next(it, None) gives T? (SPEC §6).
            BuiltinFunction.Next when call.Arguments is [var iterator, BoundNone] =>
                $"{Runtime}.Iterators.{(IsValueType(((IteratorOfType)Concrete(iterator.Type)).Element) ? "NextValueOrNone" : "NextOrNone")}({Expr(iterator)})",
            BuiltinFunction.Next => $"{Runtime}.Iterators.Next({arguments})",
            BuiltinFunction.Filter => $"{Runtime}.Iterators.Filter({arguments})",
            BuiltinFunction.Enumerate => $"{Runtime}.Iterators.Enumerate({arguments})",
            BuiltinFunction.Zip => ZipRows(call, arguments),
            _ => throw new InvalidOperationException($"unexpected builtin {call.Function.Builtin}"),
        };
    }

    // zip of three or more lists: the runtime's untyped rows, each made into the result's tuple (SPEC §6).
    private string ZipRows(BoundCall call, string arguments)
    {
        var elements = ((TupleOfType)((IteratorOfType)Concrete(call.Type)).Element).Elements;
        var row = $"@{_locals.Fresh("row")}";
        var tuple = NewTuple(elements, [.. elements.Select((e, i) => $"({TypeName(e)}){row}[{i}]!")]);
        return $"{Runtime}.Iterators.Zip(static {row} => {tuple}, {arguments})";
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
    private string PyIsInstance(BoundPyIsInstance test)
    {
        if (test.Narrows is { } type)
            return $"({Cast(new BoundCast(test.Operand, new OptionalOf(type), test.Span))} is not null)";
        return test.Builtin is { } builtin
            ? $"{Interop}.IsInstanceOf({Expr(test.Operand)}, \"{builtin}\")"
            : $"{Interop}.IsInstance({Expr(test.Operand)}, {Expr(test.Class!)})";
    }

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
            case VarTupleOfType varTuple:
            {
                var e = $"@{_locals.Fresh("e")}";
                return $"new {TypeName(varTuple)}({Interop}.ToList({value}, {e} => {Converter(varTuple.Element, e)}))";
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

    // An int widened to a float, where a float overload is wanted.
    private string AsFloatIf(bool widen, BoundExpr value) =>
        widen && Concrete(value.Type) == TaipanType.Int ? $"(double){Operand(value)}" : Expr(value);

    /// <summary><c>min</c> and <c>max</c> (SPEC §6): ordered as <c>sorted</c> orders, with a key and a default.</summary>
    private string MinMax(BoundMinMax minMax)
    {
        var element = Concrete(minMax.Element);
        var key = minMax.Key is null ? "static x => x" : $"{Operand(minMax.Key)}.Invoke";
        var ordered = minMax.Key is null ? element : ((FunctionType)TypeChecker.Unwrap(Concrete(minMax.Key.Type))).Result;
        var less = LessThan(ordered) ?? "null";
        var arguments = $"{Expr(minMax.Values)}, {key}, {less}, {(minMax.IsMax ? "true" : "false")}";
        return minMax.Default switch
        {
            null => $"{Runtime}.Numbers.Extreme({arguments}, \"{(minMax.IsMax ? "max" : "min")}\")",
            BoundNone => $"{Runtime}.Numbers.{(IsValueType(element) ? "ExtremeValueOrNone" : "ExtremeOrNone")}({arguments})",
            var fallback => $"{Runtime}.Numbers.ExtremeOr({arguments}, {Expr(fallback)})",
        };
    }

    /// <summary>A class's <c>__lt__</c> as a comparison, which ordering by Python's rules needs (SPEC §4.8); null for built-in types.</summary>
    private string? LessThan(TaipanType ordered)
    {
        if (ordered is not ClassType orderedClass)
            return null;
        var (a, b) = (_locals.Fresh("a"), _locals.Fresh("b"));
        var less = orderedClass.Symbol.HasSubclasses ? $"{Runtime}.Ops.Compare(@{a}, @{b}, {ClassInterface}.Lt)" : $"@{a}.@__lt__(@{b})";
        return $"static (@{a}, @{b}) => {less}";
    }

    private string SetLiteral(BoundSetLiteral set) => $"new {TypeName(set.SetType)} {{ {Arguments(set.Elements)} }}";

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
            else if (part is BoundFormat format)
                text.Append($"{{({Format(format)})}}");
            else
                text.Append($"{{({Runtime}.Display.Format({Expr(part)}))}}");
        }
        return text.Append('"').ToString();
    }

    // A hole's conversion (!r is the form inside a list, !s the display form), then its format spec (SPEC §6).
    private string Format(BoundFormat format)
    {
        var value = format.Conversion switch
        {
            'r' => $"{Runtime}.Display.Repr({Expr(format.Value)})",
            's' => $"{Runtime}.Display.Format({Expr(format.Value)})",
            _ => Expr(format.Value),
        };
        return format.SpecText is { } spec ? $"{Runtime}.Display.Format({value}, \"{Escape(spec)}\")" : value;
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
        VarTupleOfType varTuple => $"{Runtime}.VarTuple<{TypeName(varTuple.Element)}>",
        SetOfType set => $"{Runtime}.OrderedSet<{TypeName(set.Element)}>",
        IteratorOfType iterator => $"{Runtime}.IIterator<{TypeName(iterator.Element)}>",
        AwaitableOfType { Result: NothingType } => Tasks,
        AwaitableOfType awaitable => $"{Tasks}<{TypeName(awaitable.Result)}>",
        IterableOfType iterable => $"{Collections}.IEnumerable<{TypeName(iterable.Element)}>",
        DictOfType dict => $"{Collections}.OrderedDictionary<{TypeName(dict.Key)}, {TypeName(dict.Value)}>",
        ClassType { Symbol.IsProtocol: true } protocol => InstancePath(ProtocolName(protocol)),
        ClassType { TypeArguments.IsEmpty: true } classType => TypePath(classType.Symbol),
        ClassType classType when IsNetGeneric(classType.Symbol) => $"{TypePath(classType.Symbol)}<{string.Join(", ", classType.TypeArguments.Select(TypeName))}>",
        ClassType classType => InstancePath(InstanceName(classType.Symbol, classType.TypeArguments)),
        EnumType { TypeArguments.IsEmpty: true } enumType => TypePath(enumType.Symbol),
        EnumType enumType when IsNetGeneric(enumType.Symbol) => $"{TypePath(enumType.Symbol)}<{string.Join(", ", enumType.TypeArguments.Select(TypeName))}>",
        EnumType enumType => InstancePath(InstanceName(enumType.Symbol, enumType.TypeArguments)),
        TypeParameterType parameter => $"@{parameter.Symbol.Name}",
        OptionalOf optional => $"{TypeName(optional.Inner)}?",
        FunctionType function => $"{Runtime}.Function<{DelegateName(function)}>",
        TupleOfType tuple => $"({string.Join(", ", tuple.Elements.Select(TypeName))})",
        DotNetType net => net.Symbol.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.FullyQualifiedFormat),
        OpaqueType opaque => $"global::{opaque.RuntimeName}",
        SelfType when _selfClass is { } selfClass => TypeName(selfClass),
        ClassOfType => $"{Runtime}.ClassValue",
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
        type == TaipanType.Int || type == TaipanType.Float || type == TaipanType.Bool || type is TupleOfType or DotNetType { Symbol.IsValueType: true };

    // ---- Output ----

    /// <summary>
    /// Maps the next lines to <paramref name="span"/>'s <c>.tpn</c> line. In a native program, a
    /// <paramref name="statement"/> also sets the method's line (SPEC §15.3).
    /// </summary>
    private void LineDirective(TextSpan span, bool statement = true)
    {
        _statementSpan = span;
        var line = _lineSource.GetLinePosition(span.Start).Line;
        _out.Append($"#line {line} \"{_lineSource.Path}\"\n");
        if (statement && _lineVariable is not null)
        {
            if (_lineFile != _lineSource.Path)
            {
                Line($"@{_fileVariable} = \"{Escape(_lineSource.Path)}\";");
                _lineFile = _lineSource.Path;
            }
            Line($"@{_lineVariable} = {line};");
        }
    }

    private void HiddenDirective() => _out.Append("#line hidden\n");

    private void Line(string text = "")
    {
        if (text.Length > 0)
            _out.Append(' ', _indent * 4);
        _out.Append(text).Append('\n');
    }
}
