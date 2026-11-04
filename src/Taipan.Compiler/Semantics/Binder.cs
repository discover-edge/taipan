using System.Collections.Immutable;
using System.Numerics;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

/// <param name="Unsupported">
/// Set when the program uses a feature from a later milestone. The program and diagnostics are
/// then incomplete and shouldn't be reported.
/// </param>
public sealed record BindResult(BoundProgram Program, ImmutableArray<Diagnostic> Diagnostics, string? Unsupported)
{
    /// <summary>The names the binder resolved, for the language server (SPEC §16.3).</summary>
    public SemanticIndex Index { get; init; } = new();
}

/// <summary>
/// Resolves names and checks types (SPEC §3–§6), producing the bound tree for a whole program
/// (§4.7). Works in passes over every file: type and variant names; type imports; base classes;
/// the other hoisted declarations (constants, function signatures, class fields and method
/// signatures); inheritance checks; value imports; defaults; then function and method bodies, and
/// the main file's top-level statements.
/// </summary>
public sealed partial class Binder
{
    private static readonly BigInteger MaxInt = long.MaxValue;
    private static readonly HashSet<string> BuiltinTypeNames = ["int", "float", "bool", "str", "list", "dict", "py", "Callable", "tuple", "Protocol", "Self", "ABC", "ClassVar", "Iterator", "Iterable", "Awaitable", "set", "TextIO", "Generator", "AsyncIterator", "AsyncIterable", "AsyncGenerator", "object"];

    // Every class the program defines, for checks across a hierarchy (SPEC §4.8).
    private readonly List<ClassSymbol> _allClasses = [];

    // Dict key types that name classes, checked once every class's members are declared (SPEC §4.8).
    private readonly List<(Module Module, TextSpan Span, TaipanType Key, bool IsSet)> _pendingKeys = [];

    // Generic classes converted to a protocol type, with one such protocol (SPEC §14.2).
    private readonly Dictionary<ClassSymbol, string> _protocolUses = [];

    // Protocols written as types before every protocol's methods were known (SPEC §3.18).
    private bool _protocolsDeclared;
    private readonly List<(Module Module, TextSpan Span, ClassSymbol Protocol)> _pendingProtocolTypes = [];

    /// <summary>A protocol used as a type must use Self only as a whole result (TP0229, SPEC §3.18).</summary>
    private bool CheckProtocolType(TextSpan span, ClassSymbol protocol)
    {
        if (Protocols.SelfProblem(protocol) is not { } method)
            return true;
        _diagnostics.ReportProtocolSelf(span, protocol.Name, method);
        return false;
    }
    private bool _membersDeclared;

    private DiagnosticBag _diagnostics = new();
    private readonly Scope _builtins = new(null);

    // The module being bound: its file, global scope and type names. Switched by Enter.
    private SourceText _source = null!;
    private Scope _global = null!;
    private Dictionary<string, ClassSymbol> _classes = null!;
    private Dictionary<string, EnumSymbol> _enums = null!;
    private Dictionary<string, Symbol> _definitions = null!;

    /// <summary>One file of the program, with what it defines (and so exports).</summary>
    private sealed class Module(SourceModule file, Scope global)
    {
        public SourceModule File { get; } = file;

        public Scope Global { get; } = global;

        public Dictionary<string, ClassSymbol> Classes { get; } = [];

        public Dictionary<string, EnumSymbol> Enums { get; } = [];

        public Dictionary<string, Symbol> Definitions { get; } = [];

        public List<(ClassDef Syntax, ClassSymbol Symbol)> ClassDefs { get; } = [];

        public List<(EnumDef Syntax, EnumSymbol Symbol)> EnumDefs { get; } = [];

        public List<TypeAliasSymbol> Aliases { get; } = [];

        /// <summary>Each protocol a class declares in its base list, where it's written (SPEC §3.11).</summary>
        public List<(ClassSymbol Class, ClassType Protocol, TextSpan Span)> DeclaredProtocols { get; } = [];

        public List<BoundConstant> Constants { get; } = [];

        public List<(FuncDef Syntax, FunctionSymbol Symbol)> Functions { get; } = [];

        public List<(MethodDef Syntax, FunctionSymbol Symbol)> Methods { get; } = [];

        public List<BoundPythonImport> PythonImports { get; } = [];

        /// <summary><c>from m import x</c> names that weren't types, resolved once functions and constants exist.</summary>
        public List<(Module Target, ImportName Name)> PendingImports { get; } = [];

        public ModuleSymbol Symbol(string name) => new(name, Definitions, Classes, Enums);
    }
    // Type names in scope inside a generic definition: each type parameter, or the concrete type it
    // stands for while a constrained body is checked for one combination (SPEC §3.6).
    private Dictionary<string, TaipanType> _typeParameters = [];
    private Dictionary<TypeParameterSymbol, TaipanType> _specialization = [];

    // Each type alias's module and definition, in source order, and the aliases being bound, for
    // cycles (SPEC §3.12).
    private readonly Dictionary<TypeAliasSymbol, (Module Module, TypeAliasDef Syntax, int Order)> _aliases = [];
    private readonly List<TypeAliasSymbol> _resolving = [];
    private Module? _module;
    private Module? _mainModule;
    private readonly HashSet<string> _scriptNames = [];
    private Scope _scope;
    private FunctionSymbol? _function;

    // The top-level function or method whose body is being bound, outside any nested function.
    // Its body can't see top-level variables (TP0211); a top-level statement's can.
    private FunctionSymbol? _outerFunction;

    // Each nested def's name, with its function, so calls by name can use keywords and defaults.
    private readonly Dictionary<VariableSymbol, FunctionSymbol> _localFunctions = [];

    // The names narrowed where the code being bound runs (SPEC §3.9).
    private Narrowing _narrowing = Narrowing.None;
    private int _loopDepth;

    // Exception handling in the function being bound (SPEC §3.10): how many except blocks enclose
    // the code, for a bare `raise`, and the loop depth where the innermost finally block began, for
    // the `return`, `break` and `continue` that would leave it.
    private int _handlerDepth;
    private int? _finallyLoopDepth;
    private string? _unsupported;

    private Binder(DotNetLibrary library, bool asLibrary, bool asNative, bool asExtension)
    {
        _scope = _builtins;
        _library = library;
        _asLibrary = asLibrary;
        // An extension module runs natively (SPEC §17.1).
        _asNative = asNative || asExtension;
        _asExtension = asExtension;
    }

    // A program checked for a native build (SPEC §15.2).
    private readonly bool _asNative;

    public static BindResult Bind(SourceText source, CompilationUnit unit) =>
        Bind([new SourceModule(Path.GetFileNameWithoutExtension(source.Path), source, unit)]);

    /// <summary>
    /// Binds a program: the main file first, then its modules (SPEC §4.7). Its .NET types come
    /// from <paramref name="library"/>, the runtime's own assemblies by default (SPEC §13.2). With
    /// <paramref name="asLibrary"/>, it's bound as a .NET library (SPEC §14), and with
    /// <paramref name="asNative"/> for a native executable (SPEC §15), and with
    /// <paramref name="asExtension"/> as a CPython extension module (SPEC §17).
    /// </summary>
    public static BindResult Bind(
        ImmutableArray<SourceModule> files, DotNetLibrary? library = null, bool asLibrary = false, bool asNative = false, bool asExtension = false,
        string? libraryNamespace = null)
    {
        var binder = new Binder(library ?? DotNetLibrary.Framework, asLibrary, asNative, asExtension) { _libraryNamespace = libraryNamespace };
        var program = binder.BindProgram(files);
        return new BindResult(program, binder._diagnostics.ToImmutable(), binder._unsupported) { Index = binder._index };
    }

    // ---- Program ----

    private void Enter(Module module)
    {
        _module = module;
        _source = module.File.Source;
        _diagnostics.Source = _source;
        _global = module.Global;
        _scope = _global;
        _classes = module.Classes;
        _enums = module.Enums;
        _definitions = module.Definitions;
    }

    private BoundProgram BindProgram(ImmutableArray<SourceModule> files)
    {
        foreach (var builtin in FunctionSymbol.CreateBuiltins())
            _builtins.Declare(builtin);
        foreach (var exception in BuiltinClasses.All)
            _builtins.Declare(exception);
        _builtins.Declare(BuiltinClasses.Comparable);
        _diagnostics.FileOrder = [.. files.Select(f => f.Source)];

        var modules = files.Select(f => new Module(f, new Scope(_builtins))).ToList();
        var byName = modules.ToDictionary(m => m.File.Name);
        var main = modules[0];
        _mainModule = main;

        // Type names first, in source order, so a clash is reported at the later definition.
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var item in module.File.Unit.Items)
            {
                if (item is ClassDef classDef)
                    module.ClassDefs.Add((classDef, DeclareClass(classDef)));
                else if (item is EnumDef enumDef)
                    module.EnumDefs.Add(DeclareEnum(enumDef));
                else if (item is TypeAliasDef aliasDef)
                    module.Aliases.Add(DeclareAlias(module, aliasDef));
            }
        }

        foreach (var module in modules)
        {
            Enter(module);
            BindImports(module, byName);
        }

        // Aliases' right-hand sides, once every type name and import is known (SPEC §3.12).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var alias in module.Aliases)
                AliasTarget(alias);
        }

        // Base classes, once every class name and import is known (SPEC §4.3).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.ClassDefs)
                BindBase(module, syntax, symbol);
        }
        BreakInheritanceCycles(modules);

        // Protocols' members first, so other declarations can tell which can be types (SPEC §3.18).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.ClassDefs.Where(c => c.Symbol.IsProtocol))
                module.Methods.AddRange(BindClassMembers(syntax, symbol));
        }
        _protocolsDeclared = true;
        foreach (var (module, span, protocol) in _pendingProtocolTypes)
        {
            Enter(module);
            CheckProtocolType(span, protocol);
        }
        foreach (var module in modules)
        {
            Enter(module);
            BindDeclarations(module);
        }
        GenerateMethods(modules);
        _membersDeclared = true;
        foreach (var (module, span, key, isSet) in _pendingKeys)
        {
            Enter(module);
            if (!DictOfType.IsValidKey(key))
                ReportInvalidKey(span, key, isSet);
        }

        // Every class's members are known now, so overrides can be checked against bases (SPEC §4.3).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.ClassDefs.Where(c => !c.Symbol.IsProtocol))
                CheckInheritance(syntax, symbol, module.Methods.Where(m => m.Symbol.Owner == symbol));
        }

        // Classes against the protocols they declare, and inherit declarations of (SPEC §3.11).
        foreach (var module in modules)
        {
            Enter(module);
            CheckDeclaredProtocols(module);
        }

        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (target, name) in module.PendingImports)
                ImportValue(target, name);
            if (module != main)
            {
                foreach (var stmt in module.File.Unit.Items.OfType<Stmt>())
                    _diagnostics.ReportStatementInModule(stmt.Span);
            }
        }

        Enter(main);
        foreach (var stmt in main.File.Unit.Items.OfType<Stmt>())
        {
            // A library has no top level (SPEC §14.3), nor does an extension module (SPEC §17.1).
            if (_asLibrary)
                _diagnostics.ReportStatementInLibrary(stmt.Span);
            else if (_asExtension)
                _diagnostics.ReportStatementInExtension(stmt.Span);
            CollectScriptNames(stmt);
        }

        // Defaults before any body, since a call anywhere may leave them out (SPEC §4.2).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.Functions)
                BindDefaults(symbol, syntax.Params, symbol.TypeParameters);
            foreach (var (syntax, symbol) in module.Methods)
                BindDefaults(symbol, syntax.Params, symbol.Owner!.TypeParameters);
            foreach (var (syntax, symbol) in module.ClassDefs.Where(c => !c.Symbol.IsProtocol))
                BindFieldDefaults(syntax, symbol);
        }

        var functions = new List<BoundFunction>();
        var classes = new List<BoundClass>();
        foreach (var module in modules)
        {
            Enter(module);
            functions.AddRange(module.Functions.SelectMany(f => BindFunctionBodies(f.Symbol, f.Syntax)));
            // A protocol has no bodies and no C# form (SPEC §8).
            classes.AddRange(module.ClassDefs.Where(c => !c.Symbol.IsProtocol).Select(c => new BoundClass(
                c.Symbol,
                [.. module.Methods.Where(m => m.Symbol.Owner == c.Symbol).Select(m => BindMethodBody(c.Symbol, m.Syntax, m.Symbol))],
                c.Syntax.Span)));
        }

        foreach (var module in modules)
        {
            Enter(module);
            CheckClsConstruction(module.ClassDefs.Where(c => !c.Symbol.IsProtocol), module.Methods);
        }

        Enter(main);
        _scope = new Scope(_global);
        var unit = main.File.Unit;
        _inTopLevel = true;
        var topLevel = BindStatements([.. unit.Items.OfType<Stmt>()]);
        _inTopLevel = false;
        var order = InitializationOrder(main, byName);
        var library = _asLibrary ? BindLibrary(modules, byName, functions, classes) : null;
        var extension = _asExtension ? BindExtension(main) : null;
        return new BoundProgram(
            [.. order.SelectMany(m => m.PythonImports)],
            [.. modules.SelectMany(m => m.EnumDefs.Select(e => e.Symbol))],
            [.. classes],
            [.. order.SelectMany(m => m.Constants)],
            [.. functions],
            new BoundBlock(topLevel, unit.Span))
        {
            // A library always compiles against its framework's reference assemblies (SPEC §14.4).
            References = _asLibrary || _library.IsExact ? _library.References : _dotnet?.References ?? [],
            Library = library,
            Extension = extension,
            IsAsync = _topLevelAwaits,
        };
    }

    /// <summary>Constants, function signatures, class members and variant fields, in source order.</summary>
    private void BindDeclarations(Module module)
    {
        foreach (var item in module.File.Unit.Items)
        {
            switch (item)
            {
                case ConstDef constDef:
                    module.Constants.Add(BindConstDef(constDef) with { Source = _source });
                    break;
                case FuncDef funcDef:
                    module.Functions.Add((funcDef, BindFunctionSignature(funcDef)));
                    break;
                case ClassDef classDef:
                    var symbol = module.ClassDefs.First(c => c.Syntax == classDef).Symbol;
                    if (!symbol.IsProtocol)
                        module.Methods.AddRange(BindClassMembers(classDef, symbol));
                    break;
                case EnumDef enumDef:
                    BindVariantFields(enumDef, module.EnumDefs.First(e => e.Syntax == enumDef).Symbol);
                    break;
            }
        }
    }

    /// <summary>
    /// <c>import m</c> binds the module; <c>from m import x</c> binds types (and an enum's variants)
    /// now, and other names once they're declared (SPEC §4.7).
    /// </summary>
    private void BindImports(Module module, Dictionary<string, Module> byName)
    {
        foreach (var item in module.File.Unit.Items)
        {
            switch (item)
            {
                case ImportStmt import when import.Module.Text == ModuleLoader.PythonModule:
                    _diagnostics.ReportPythonNotModule(import.Module.Span);
                    break;
                case ImportStmt import when import.Module.Text == ModuleLoader.DotNetModule:
                    _diagnostics.ReportDotNetNotModule(import.Module.Span);
                    break;
                case ImportStmt import when ModuleLoader.IsDotNet(import.Module.Text):
                    ImportDotNetNamespace(import);
                    break;
                case FromImportStmt from when ModuleLoader.IsDotNet(from.Module.Text):
                    ImportDotNet(from);
                    break;
                case FromImportStmt { Module.Text: ModuleLoader.PythonModule } fromPython:
                    // A native program can't use Python (SPEC §15.2).
                    if (_asNative)
                        _diagnostics.ReportNativePython(fromPython.Span, _asExtension);
                    // Each name holds a Python module, imported at startup (SPEC §10.1).
                    foreach (var name in fromPython.Names)
                    {
                        var bound = name.Alias ?? name.Name;
                        var symbol = new VariableSymbol(bound.Text, TaipanType.Py, VariableKind.Constant);
                        if (DeclareGlobal(bound, symbol, isDefinition: false))
                            module.PythonImports.Add(new BoundPythonImport(symbol, name.Name.Text, name.Span, _source));
                    }
                    break;
                // sys, os and types are part of Taipan, never files (SPEC §4.7, M37).
                case ImportStmt import when BuiltinModules.IsBuiltin(import.Module.Text):
                    DeclareGlobal(import.Alias ?? import.Module, BuiltinModules.Symbol(import.Module.Text, (import.Alias ?? import.Module).Text), isDefinition: false);
                    break;
                case FromImportStmt from when BuiltinModules.IsBuiltin(from.Module.Text):
                    var builtinModule = BuiltinModules.Symbol(from.Module.Text, from.Module.Text);
                    foreach (var name in from.Names)
                    {
                        if (builtinModule.Definitions.TryGetValue(name.Name.Text, out var member))
                            DeclareImported(name, member);
                        else
                            _diagnostics.ReportNoDefinition(name.Name.Span, from.Module.Text, name.Name.Text);
                    }
                    break;
                case ImportStmt import when byName.TryGetValue(import.Module.Text, out var target):
                    var alias = import.Alias ?? import.Module;
                    var moduleSymbol = target.Symbol(alias.Text);
                    // A module's definition is the start of its file (SPEC §16.3).
                    _index.Locate(moduleSymbol, target.File.Source, default);
                    Index(import.Module.Span, moduleSymbol);
                    DeclareGlobal(alias, moduleSymbol, isDefinition: false);
                    break;
                case FromImportStmt from when byName.TryGetValue(from.Module.Text, out var source):
                    var fromModule = source.Symbol(from.Module.Text);
                    _index.Locate(fromModule, source.File.Source, default);
                    Index(from.Module.Span, fromModule);
                    foreach (var name in from.Names)
                    {
                        if (source.Classes.TryGetValue(name.Name.Text, out var classSymbol))
                            DeclareImported(name, classSymbol);
                        else if (source.Enums.TryGetValue(name.Name.Text, out var enumSymbol))
                            ImportEnum(name, enumSymbol);
                        else if (source.Definitions.GetValueOrDefault(name.Name.Text) is TypeAliasSymbol imported)
                            DeclareImported(name, imported);
                        else
                            module.PendingImports.Add((source, name));
                    }
                    break;
            }
            // A module that wasn't found was already reported (TP0221) by the loader.
        }
    }

    /// <summary>
    /// Declares an imported name under its alias, if any. With an alias, the name it imports is a
    /// reference too, so rename and references find it (SPEC §16.6).
    /// </summary>
    private bool DeclareImported(ImportName name, Symbol symbol)
    {
        if (name.Alias is not null)
            Index(name.Name.Span, symbol);
        return DeclareGlobal(name.Alias ?? name.Name, symbol, isDefinition: false);
    }

    private void ImportEnum(ImportName name, EnumSymbol enumSymbol)
    {
        if (!DeclareImported(name, enumSymbol))
            return;
        foreach (var variant in enumSymbol.Variants)
            DeclareVariant(variant, name.Span, isDefinition: false);
    }

    private void ImportValue(Module target, ImportName name)
    {
        if (target.Definitions.TryGetValue(name.Name.Text, out var symbol) && symbol is FunctionSymbol or VariableSymbol or VariantSymbol)
            DeclareImported(name, symbol);
        else
            _diagnostics.ReportNoDefinition(name.Name.Span, target.File.Name, name.Name.Text);
    }

    /// <summary>Modules after the modules they import; a cycle is broken where it's first reached (SPEC §4.7).</summary>
    private static List<Module> InitializationOrder(Module main, Dictionary<string, Module> byName)
    {
        var order = new List<Module>();
        var visited = new HashSet<Module>();
        void Visit(Module module)
        {
            if (!visited.Add(module))
                return;
            foreach (var import in ModuleLoader.Imports(module.File.Unit))
            {
                if (byName.TryGetValue(import.Text, out var target))
                    Visit(target);
            }
            order.Add(module);
        }
        Visit(main);
        return order;
    }

    private static ImmutableArray<TypeParameterSymbol> NewTypeParameters(ImmutableArray<TypeParam> parameters) =>
        [.. parameters.Select(p => new TypeParameterSymbol(p.Name.Text))];

    /// <summary>
    /// A repeated type parameter is TP0214; one that hides a type name is TP0207. Constraints are
    /// TP0220 except on functions (SPEC §3.6).
    /// </summary>
    private void CheckTypeParameters(ImmutableArray<TypeParam> parameters, bool allowConstraints)
    {
        var seen = new HashSet<string>();
        foreach (var parameter in parameters)
        {
            var name = parameter.Name;
            if (!seen.Add(name.Text))
                _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
            else if (BuiltinTypeNames.Contains(name.Text) || ResolveTypeName(name.Text) is not null || _global.Lookup(name.Text) is TypeAliasSymbol or DotNetTypeSymbol)
                _diagnostics.ReportShadowing(name.Span, name.Text);
            if (!allowConstraints && !parameter.Constraints.IsEmpty)
                _diagnostics.ReportConstraintNotOnFunction(parameter.Constraints[0].Span);
            if (!allowConstraints && parameter.Bound is { } bound)
                _diagnostics.ReportBoundNotOnFunction(bound.Span);
        }
    }

    /// <summary>
    /// A function's bounds: protocols (SPEC §3.11), and since M40 classes (§3.18), whose type
    /// arguments can't use type parameters. Anything else is TP0220.
    /// </summary>
    private void BindBounds(ImmutableArray<TypeParam> syntax, ImmutableArray<TypeParameterSymbol> symbols)
    {
        foreach (var (parameter, symbol) in syntax.Zip(symbols))
        {
            if (parameter.Bound is not { } bound)
                continue;
            if (PeekClass(bound) is { IsProtocol: true, TypeParameters.IsEmpty: true } protocol)
            {
                symbol.Bound = protocol.Type;
                continue;
            }
            var type = BindType(bound);
            if (type.ContainsError)
                continue;
            if (type is not ClassType classType || BuiltinClasses.IsRuntimeOnly(classType.Symbol))
                _diagnostics.ReportNotProtocol(bound.Span, type);
            else if (MentionsTypeParameter(classType))
                _diagnostics.ReportBoundTypeParameters(bound.Span);
            else
                symbol.Bound = classType;
        }
    }

    /// <summary>
    /// A protocol in a class's base list, with its type arguments when it's generic (SPEC §3.18),
    /// which may use the class's type parameters; null if the base isn't a protocol.
    /// </summary>
    private ClassType? DeclaredProtocol(TypeSyntax syntax, ClassSymbol owner)
    {
        if (PeekClass(syntax) is { IsProtocol: true } protocol)
        {
            if (protocol.TypeParameters.IsEmpty)
                return protocol.Type;
            _diagnostics.ReportWrongTypeArgumentCount(syntax.Span, protocol.Name, protocol.TypeParameters.Length, 0);
            return new ClassType(protocol, [TaipanType.Error]);
        }
        if (syntax is not GenericType generic || ResolveTypeName(generic.Name.Text) is not ClassSymbol { IsProtocol: true } genericProtocol)
            return null;
        var outer = EnterTypeParameters(owner.TypeParameters);
        var arguments = generic.Arguments.Select(BindType).ToImmutableArray();
        _typeParameters = outer;
        if (arguments.Length == genericProtocol.TypeParameters.Length)
            return new ClassType(genericProtocol, arguments);
        _diagnostics.ReportWrongTypeArgumentCount(syntax.Span, genericProtocol.Name, genericProtocol.TypeParameters.Length, arguments.Length);
        return new ClassType(genericProtocol, [TaipanType.Error]);
    }

    /// <summary>The class or protocol a plain or qualified type name refers to, without reporting anything.</summary>
    private ClassSymbol? PeekClass(TypeSyntax type) => type switch
    {
        NamedType named when !_typeParameters.ContainsKey(named.Name.Text) => ResolveTypeName(named.Name.Text) as ClassSymbol,
        QualifiedType { Type: NamedType named } qualified when _scope.Lookup(qualified.Module.Text) is ModuleSymbol module
            => module.Classes.GetValueOrDefault(named.Name.Text),
        _ => null,
    };

    /// <summary>Binds and validates a function's constraint tuples (TP0220), with its type parameters in scope.</summary>
    private void BindConstraints(ImmutableArray<TypeParam> syntax, ImmutableArray<TypeParameterSymbol> symbols)
    {
        foreach (var (parameter, symbol) in syntax.Zip(symbols))
        {
            if (parameter.Constraints.IsEmpty)
                continue;
            var types = ImmutableArray.CreateBuilder<TaipanType>();
            foreach (var constraint in parameter.Constraints)
            {
                var type = BindType(constraint);
                if (type is OptionalOf || MentionsTypeParameter(type))
                    _diagnostics.ReportConstraintNotConcrete(constraint.Span);
                else if (types.Contains(type))
                    _diagnostics.ReportConstraintRepeated(constraint.Span, type);
                else if (!type.ContainsError)
                    types.Add(type);
            }
            if (parameter.Constraints.Length < 2)
                _diagnostics.ReportConstraintTooShort(parameter.Constraints[0].Span);
            symbol.Constraints = types.Count >= 2 ? types.ToImmutable() : [];
        }
    }

    private static bool MentionsTypeParameter(TaipanType type) => type switch
    {
        TypeParameterType => true,
        OptionalOf o => MentionsTypeParameter(o.Inner),
        ListOfType l => MentionsTypeParameter(l.Element),
        VarTupleOfType v => MentionsTypeParameter(v.Element),
        SetOfType s => MentionsTypeParameter(s.Element),
        DictOfType d => MentionsTypeParameter(d.Key) || MentionsTypeParameter(d.Value),
        ClassType s => s.TypeArguments.Any(MentionsTypeParameter),
        EnumType e => e.TypeArguments.Any(MentionsTypeParameter),
        FunctionType f => f.Parameters.Any(MentionsTypeParameter) || MentionsTypeParameter(f.Result),
        TupleOfType t => t.Elements.Any(MentionsTypeParameter),
        _ => false,
    };

    /// <summary>Makes <paramref name="parameters"/> the type parameters in scope; returns the previous ones.</summary>
    private Dictionary<string, TaipanType> EnterTypeParameters(ImmutableArray<TypeParameterSymbol> parameters)
    {
        var previous = _typeParameters;
        _typeParameters = [];
        foreach (var parameter in parameters)
            _typeParameters.TryAdd(parameter.Name, _specialization.GetValueOrDefault(parameter, parameter.Type));
        return previous;
    }

    // Aliases declared in the blocks around the code being bound, and the class whose aliases it sees (SPEC §3.18).
    private ImmutableDictionary<string, TypeAliasSymbol> _localAliases = ImmutableDictionary<string, TypeAliasSymbol>.Empty;
    private ClassSymbol? _aliasClass;

    /// <summary>The alias a type name refers to: a block's, the class's, or a global one.</summary>
    private TypeAliasSymbol? LookupAlias(string name) =>
        _localAliases.GetValueOrDefault(name)
        ?? (_aliasClass ?? _outerFunction?.Owner)?.Aliases.GetValueOrDefault(name)
        ?? _global.Lookup(name) as TypeAliasSymbol;

    /// <summary>
    /// Declares the aliases of a block or class body (SPEC §3.18), all at once, so they can refer to
    /// each other in any order, then checks each one's right-hand side.
    /// </summary>
    private Dictionary<string, TypeAliasSymbol> DeclareLocalAliases(IEnumerable<TypeAliasDef> definitions, ClassSymbol? owner, HashSet<string>? members = null)
    {
        var declared = new Dictionary<string, TypeAliasSymbol>();
        foreach (var definition in definitions)
        {
            var name = definition.Name.Text;
            if (declared.ContainsKey(name) || members?.Contains(name) == true)
            {
                _diagnostics.ReportDuplicateDefinition(definition.Name.Span, name);
                continue;
            }
            if (_typeParameters.ContainsKey(name) || BuiltinTypeNames.Contains(name) || ResolveTypeName(name) is not null
                || LookupAlias(name) is not null || _global.Lookup(name) is DotNetTypeSymbol)
            {
                _diagnostics.ReportShadowing(definition.Name.Span, name);
                continue;
            }
            var symbol = new TypeAliasSymbol(name, NewTypeParameters(definition.TypeParams)) { LocalType = definition.Type };
            _aliases[symbol] = (_module!, definition, _aliases.Count);
            declared[name] = symbol;
            Index(definition.Name.Span, symbol);
        }
        if (owner is null)
            _localAliases = _localAliases.SetItems(declared);
        else
            foreach (var (name, symbol) in declared)
                owner.Aliases[name] = symbol;
        foreach (var symbol in declared.Values)
        {
            CheckTypeParameters(_aliases[symbol].Syntax.TypeParams, allowConstraints: false);
            symbol.Target = LocalAliasTarget(symbol);
        }
        return declared;
    }

    /// <summary>A block's or class's alias's right-hand side, bound with the type parameters in scope here.</summary>
    private TaipanType LocalAliasTarget(TypeAliasSymbol alias)
    {
        if (alias.Target is ErrorType)
            return TaipanType.Error;
        var index = _resolving.IndexOf(alias);
        if (index >= 0)
        {
            ReportAliasCycle(_resolving[index..]);
            return TaipanType.Error;
        }
        _resolving.Add(alias);
        var outer = _typeParameters;
        _typeParameters = new(outer);
        foreach (var parameter in alias.TypeParameters)
            _typeParameters[parameter.Name] = parameter.Type;
        var bound = BindType(alias.LocalType!);
        _typeParameters = outer;
        _resolving.RemoveAt(_resolving.Count - 1);
        return bound;
    }

    /// <summary>Declares an alias's name; its right-hand side is bound once imports are known (SPEC §3.12).</summary>
    private TypeAliasSymbol DeclareAlias(Module module, TypeAliasDef aliasDef)
    {
        var symbol = new TypeAliasSymbol(aliasDef.Name.Text, NewTypeParameters(aliasDef.TypeParams));
        _aliases[symbol] = (module, aliasDef, _aliases.Count);
        if (BuiltinTypeNames.Contains(symbol.Name))
            _diagnostics.ReportDuplicateDefinition(aliasDef.Name.Span, symbol.Name);
        else
            DeclareGlobal(aliasDef.Name, symbol);
        return symbol;
    }

    /// <summary>
    /// An alias's right-hand side, bound in its own module the first time it's needed. A cycle is
    /// TP0230, and leaves every alias in it an error (SPEC §3.12).
    /// </summary>
    private TaipanType AliasTarget(TypeAliasSymbol alias)
    {
        if (alias.LocalType is not null)
        {
            // Errors in its right-hand side are reported once, where it's declared.
            if (_resolving.Count > 0)
                return LocalAliasTarget(alias);
            var diagnostics = _diagnostics;
            _diagnostics = new DiagnosticBag { Source = _source, FileOrder = diagnostics.FileOrder };
            var local = LocalAliasTarget(alias);
            _diagnostics = diagnostics;
            return local;
        }
        if (alias.Target is { } target)
            return target;
        var index = _resolving.IndexOf(alias);
        if (index >= 0)
        {
            ReportAliasCycle(_resolving[index..]);
            return TaipanType.Error;
        }

        var (module, syntax, _) = _aliases[alias];
        var (previous, scope) = (_module!, _scope);
        Enter(module);
        _resolving.Add(alias);
        CheckTypeParameters(syntax.TypeParams, allowConstraints: false);
        var outer = EnterTypeParameters(alias.TypeParameters);
        var bound = BindType(syntax.Type);
        _typeParameters = outer;
        _resolving.RemoveAt(_resolving.Count - 1);
        Enter(previous);
        _scope = scope;
        return alias.Target ??= bound;
    }

    /// <summary>A cycle of aliases, reported once at the alias in it that comes first in source order.</summary>
    private void ReportAliasCycle(List<TypeAliasSymbol> cycle)
    {
        var first = cycle.IndexOf(cycle.MinBy(a => _aliases[a].Order)!);
        var rotated = cycle[first..].Concat(cycle[..first]).ToList();
        var (module, syntax, _) = _aliases[rotated[0]];
        _diagnostics.Source = module.File.Source;
        _diagnostics.ReportRecursiveAlias(syntax.Name.Span, rotated[0].Name, [.. rotated.Skip(1).Select(a => a.Name)]);
        _diagnostics.Source = _source;
        foreach (var alias in cycle)
            alias.Target = TaipanType.Error;
    }

    /// <summary><c>A</c> or <c>A[…]</c>: the alias's type, with its type arguments in place of its parameters (SPEC §3.12).</summary>
    private TaipanType ExpandAlias(TypeAliasSymbol alias, Identifier name, ImmutableArray<TypeSyntax> argumentSyntax, TextSpan span)
    {
        var arguments = argumentSyntax.Select(BindType).ToImmutableArray();
        var target = AliasTarget(alias);
        if (arguments.Length != alias.TypeParameters.Length)
        {
            _diagnostics.ReportWrongTypeArgumentCount(span, name.Text, alias.TypeParameters.Length, arguments.Length);
            return TaipanType.Error;
        }
        var expanded = TypeChecker.Substitute(target, alias.TypeParameters.Zip(arguments).ToDictionary(p => p.First, p => p.Second));
        // Diagnostics show the type by the alias's name (SPEC §3.18).
        return expanded.ContainsError ? expanded : expanded with { Alias = arguments.IsEmpty ? name.Text : $"{name.Text}[{string.Join(", ", arguments)}]" };
    }

    /// <summary>The alias a plain, generic or qualified type name refers to, without reporting anything.</summary>
    private TypeAliasSymbol? PeekAlias(TypeSyntax type) => type switch
    {
        NamedType named when !_typeParameters.ContainsKey(named.Name.Text) => LookupAlias(named.Name.Text),
        GenericType generic => LookupAlias(generic.Name.Text),
        QualifiedType qualified when _scope.Lookup(qualified.Module.Text) is ModuleSymbol module
            => module.Definitions.GetValueOrDefault(TypeName(qualified.Type)) as TypeAliasSymbol,
        _ => null,
    };

    /// <summary>The name a plain, generic or qualified type is written with.</summary>
    private static string TypeName(TypeSyntax type) => type switch
    {
        NamedType named => named.Name.Text,
        GenericType generic => generic.Name.Text,
        QualifiedType qualified => TypeName(qualified.Type),
        _ => "",
    };

    /// <summary>The alias a name or <c>module.Name</c> expression refers to, if it's an alias rather than a value.</summary>
    private TypeAliasSymbol? NamedAlias(Expr expr) => expr switch
    {
        NameExpr name when !_typeParameters.ContainsKey(name.Name) => _scope.Lookup(name.Name) as TypeAliasSymbol ?? LookupAlias(name.Name),
        MemberExpr { Target: NameExpr module } member when _scope.Lookup(module.Name) is ModuleSymbol symbol
            => symbol.Definitions.GetValueOrDefault(member.Member.Text) as TypeAliasSymbol,
        _ => null,
    };

    private ClassSymbol DeclareClass(ClassDef classDef)
    {
        // A class whose base list names Protocol is a protocol (SPEC §3.11), which takes no decorators.
        if (classDef.Bases.Any(IsProtocolMarker))
        {
            CheckDecorators(classDef.Decorators, allowed: null);
            // Generic protocols since M40 (SPEC §3.18).
            var protocol = BuiltinClasses.Protocol(classDef.Name.Text, NewTypeParameters(classDef.TypeParams));
            if (BuiltinTypeNames.Contains(protocol.Name))
                _diagnostics.ReportDuplicateDefinition(classDef.Name.Span, protocol.Name);
            else if (DeclareGlobal(classDef.Name, protocol))
                _classes[protocol.Name] = protocol;
            return protocol;
        }
        var marks = CheckClassDecorators(classDef.Decorators);
        var symbol = new ClassSymbol(classDef.Name.Text, NewTypeParameters(classDef.TypeParams), marks.Contains("dataclass"))
        {
            Module = _module == _mainModule ? null : _module?.File.Name,
            IsFrozen = marks.Contains("frozen"),
            IsOrdered = marks.Contains("order"),
            IsFinal = marks.Contains("final"),
            TotalOrdering = marks.Contains("total_ordering"),
        };
        if (BuiltinTypeNames.Contains(symbol.Name))
            _diagnostics.ReportDuplicateDefinition(classDef.Name.Span, symbol.Name);
        else if (DeclareGlobal(classDef.Name, symbol))
            _classes[symbol.Name] = symbol;
        return symbol;
    }

    /// <summary>
    /// Reports TP0224 for any decorator but <paramref name="allowed"/>, and returns whether that
    /// one is present (SPEC §2): <c>@dataclass</c> on a class, <c>@override</c> on a method.
    /// </summary>
    private bool CheckDecorators(ImmutableArray<Decorator> decorators, string? allowed)
    {
        var seen = new HashSet<string>();
        var found = false;
        foreach (var decorator in decorators)
        {
            var name = decorator.Member is { } member ? $"{decorator.Name.Text}.{member.Text}" : decorator.Name.Text;
            if (decorator.Arguments is not null)
                _diagnostics.ReportDecoratorArguments(decorator.Span, name);
            else if (!seen.Add(name))
                _diagnostics.ReportDuplicateDecorator(decorator.Span, name);
            else if (name is not ("dataclass" or "override") && !MethodDecorators.Contains(name))
                _diagnostics.ReportUnknownDecorator(decorator.Span, name);
            else if (name != allowed)
            {
                if (name is "dataclass" or "override")
                    _diagnostics.ReportMisplacedDecorator(decorator.Span, name);
                else
                    _diagnostics.ReportMisplacedDecorator(decorator.Span, name, $"'@{name}' goes on a method");
            }
            else
                found = true;
        }
        return found;
    }

    /// <summary>
    /// A class's decorators (SPEC §4.9): <c>@dataclass</c>, with <c>frozen</c> and <c>order</c> as
    /// literal booleans, <c>@final</c> and <c>@total_ordering</c>. Returns those in effect, with
    /// <c>frozen</c> and <c>order</c> when they're True. Anything else is TP0224.
    /// </summary>
    private HashSet<string> CheckClassDecorators(ImmutableArray<Decorator> decorators)
    {
        var marks = new HashSet<string>();
        foreach (var decorator in decorators)
        {
            var name = decorator.Member is { } member ? $"{decorator.Name.Text}.{member.Text}" : decorator.Name.Text;
            if (marks.Contains(name))
                _diagnostics.ReportDuplicateDecorator(decorator.Span, name);
            else if (name is not ("dataclass" or "final" or "total_ordering"))
            {
                if (name == "override")
                    _diagnostics.ReportMisplacedDecorator(decorator.Span, name);
                else if (MethodDecorators.Contains(name))
                    _diagnostics.ReportMisplacedDecorator(decorator.Span, name, $"'@{name}' goes on a method");
                else
                    _diagnostics.ReportUnknownDecorator(decorator.Span, name);
            }
            else if (decorator.Arguments is { } arguments && (name != "dataclass"
                || arguments.Any(a => a.Name?.Text is not ("frozen" or "order") || a.Value is not BoolLiteral)
                || arguments.DistinctBy(a => a.Name!.Text).Count() != arguments.Length))
            {
                _diagnostics.ReportDecoratorArguments(decorator.Span, name);
                // Still a dataclass, so its fields aren't also reported.
                marks.Add(name);
            }
            else
            {
                marks.Add(name);
                foreach (var argument in decorator.Arguments ?? [])
                {
                    if (argument.Value is BoolLiteral { Value: true })
                        marks.Add(argument.Name!.Text);
                }
            }
        }
        return marks;
    }

    private static bool IsProtocolMarker(TypeSyntax type) => type is NamedType { Name.Text: "Protocol" };

    // ABC lets a class declare abstract methods, and isn't a base (SPEC §4.8).
    private static bool IsAbcMarker(TypeSyntax type) => type is NamedType { Name.Text: "ABC" };

    /// <summary>
    /// A class's base list (SPEC §4.3, §3.11): the protocols it declares, and at most one base: a
    /// non-generic class of the same kind, for a class that isn't itself generic. Anything else is
    /// TP0225, and the class is bound without a base. A protocol's only base is <c>Protocol</c>.
    /// </summary>
    private void BindBase(Module module, ClassDef classDef, ClassSymbol symbol)
    {
        if (symbol.IsProtocol)
        {
            foreach (var other in classDef.Bases.Where(b => !IsProtocolMarker(b)))
                _diagnostics.ReportProtocolBase(other.Span);
            return;
        }
        var protocols = ImmutableArray.CreateBuilder<ClassType>();
        var bases = new List<TypeSyntax>();
        var dotnetBases = new List<(DotNetType Type, TextSpan Span)>();
        foreach (var syntax in classDef.Bases)
        {
            if (IsAbcMarker(syntax))
            {
                symbol.IsAbc = true;
                continue;
            }
            // .NET classes and interfaces (SPEC §13.16, M43).
            if (IsDotNetBase(syntax))
            {
                if (BindDotNetBase(syntax, symbol, classDef.Name.Span) is { } dotnetBase)
                    dotnetBases.Add((dotnetBase, syntax.Span));
                continue;
            }
            if (DeclaredProtocol(syntax, symbol) is not { } protocol)
            {
                bases.Add(syntax);
                continue;
            }
            if (protocol.ContainsError)
                continue;
            if (protocols.Any(p => p.Symbol == protocol.Symbol))
            {
                _diagnostics.ReportProtocolRepeated(syntax.Span, protocol.Symbol.Name, declaredBy: null);
                continue;
            }
            protocols.Add(protocol);
            module.DeclaredProtocols.Add((symbol, protocol, syntax.Span));
        }
        symbol.Protocols = protocols.ToImmutable();
        // A .NET class counts as the one base class, and comes first among the .NET bases.
        var dotnetClasses = dotnetBases.Where(b => b.Type.Symbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class).ToList();
        if (dotnetClasses.Count > 0 && (bases.Count > 0 || dotnetClasses.Count > 1))
        {
            _diagnostics.ReportMultipleBases(bases.Count > 0 && bases[0].Span.Start > dotnetClasses[0].Span.Start ? bases[0].Span : dotnetClasses[^1].Span);
            dotnetBases.RemoveAll(b => dotnetClasses.Skip(1).Contains(b));
        }
        symbol.DotNetBases = [.. dotnetBases.OrderBy(b => b.Type.Symbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class ? 0 : 1).Select(b => b.Type)];
        if (bases.Count == 0 || dotnetClasses.Count > 0)
            return;
        if (bases.Count > 1)
            _diagnostics.ReportMultipleBases(bases[1].Span);
        BindBaseClass(bases[0], symbol, classDef.Name.Span);
    }

    private void BindBaseClass(TypeSyntax syntax, ClassSymbol symbol, TextSpan nameSpan)
    {
        if (PeekAlias(syntax) is { } alias)
        {
            _diagnostics.ReportInvalidBase(syntax.Span, TypeName(syntax), aliasOf: alias.TargetName);
            return;
        }
        // A generic class's base may use its type parameters (SPEC §3.18).
        var outer = EnterTypeParameters(symbol.TypeParameters);
        var type = BindType(syntax);
        _typeParameters = outer;
        switch (type)
        {
            case { ContainsError: true }:
                return;
            // Only the runtime makes a PythonError or a DotNetError (SPEC §3.10).
            case ClassType { Symbol: var baseClass } when BuiltinClasses.IsRuntimeOnly(baseClass):
                _diagnostics.ReportInvalidBase(syntax.Span, type);
                return;
            case ClassType { Symbol: var baseClass } when !symbol.TypeParameters.IsEmpty && BuiltinClasses.IsException(baseClass):
                _diagnostics.ReportGenericException(nameSpan);
                return;
            // A dataclass exception's base may be a built-in exception class (SPEC §3.19, M41).
            case ClassType { Symbol: var baseClass } when symbol.IsDataclass && BuiltinClasses.IsDataclassBase(baseClass):
                symbol.BaseType = (ClassType)type;
                return;
            case ClassType { Symbol: var baseClass } when baseClass.IsDataclass != symbol.IsDataclass:
                _diagnostics.ReportMixedBase(syntax.Span, symbol.IsDataclass, symbol.IsDataclass && BuiltinClasses.IsException(baseClass));
                return;
            // M39 (SPEC §4.9): a final class has no subclasses, and frozen dataclasses derive only from frozen ones.
            case ClassType { Symbol.IsFinal: true } finalBase:
                _diagnostics.ReportFinalBase(syntax.Span, finalBase.Symbol.Name);
                return;
            case ClassType { Symbol: var baseClass } when baseClass.IsFrozen != symbol.IsFrozen:
                _diagnostics.ReportFrozenBase(syntax.Span, symbol.IsFrozen);
                return;
            case ClassType baseType:
                symbol.BaseType = baseType;
                return;
            default:
                _diagnostics.ReportInvalidBase(syntax.Span, type);
                return;
        }
    }

    /// <summary>A class that derives from itself is TP0225, and loses its base (SPEC §4.3).</summary>
    private void BreakInheritanceCycles(List<Module> modules)
    {
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.ClassDefs)
            {
                var seen = new HashSet<ClassSymbol>();
                for (var ancestor = symbol.Base; ancestor is not null && seen.Add(ancestor); ancestor = ancestor.Base)
                {
                    if (ancestor != symbol)
                        continue;
                    _diagnostics.ReportInheritanceCycle(syntax.Bases.First(b => !IsAbcMarker(b) && PeekClass(b) is not { IsProtocol: true }).Span, symbol.Name);
                    symbol.BaseType = null;
                    break;
                }
            }
        }
        foreach (var symbol in modules.SelectMany(m => m.ClassDefs).Select(c => c.Symbol))
        {
            // Built-in classes are shared, and always open to subclasses.
            if (symbol.Base is { RuntimeName: null } baseClass)
            {
                baseClass.HasSubclasses = true;
                baseClass.Subclasses.Add(symbol);
            }
            _allClasses.Add(symbol);
        }
    }

    /// <summary>
    /// Declares an enum and its variant names (SPEC §4.4). A variant name shared with another enum
    /// becomes ambiguous; one shared with any other global is TP0214.
    /// </summary>
    private (EnumDef Syntax, EnumSymbol Symbol) DeclareEnum(EnumDef enumDef)
    {
        var symbol = new EnumSymbol(enumDef.Name.Text, NewTypeParameters(enumDef.TypeParams));
        if (BuiltinTypeNames.Contains(symbol.Name))
            _diagnostics.ReportDuplicateDefinition(enumDef.Name.Span, symbol.Name);
        else if (DeclareGlobal(enumDef.Name, symbol))
            _enums[symbol.Name] = symbol;

        var variants = ImmutableArray.CreateBuilder<VariantSymbol>();
        foreach (var variantDef in enumDef.Variants)
        {
            var variant = new VariantSymbol(variantDef.Name.Text, symbol);
            if (variants.Any(v => v.Name == variant.Name))
            {
                _diagnostics.ReportDuplicateDefinition(variantDef.Name.Span, variant.Name);
                continue;
            }
            variants.Add(variant);
            DeclareVariant(variant, variantDef.Name.Span, isDefinition: true);
        }
        symbol.Variants = variants.ToImmutable();
        return (enumDef, symbol);
    }

    /// <summary>
    /// A variant's unqualified name. Shared with another enum's variant it becomes ambiguous
    /// (TP0216 on use); shared with any other global it's TP0214 (SPEC §4.4).
    /// </summary>
    private void DeclareVariant(VariantSymbol variant, TextSpan span, bool isDefinition)
    {
        if (isDefinition)
            Index(span, variant, isDefinition: true);
        Symbol declared;
        switch (_global.Lookup(variant.Name))
        {
            case null:
                declared = variant;
                break;
            case VariantSymbol other when other != variant:
                declared = new AmbiguousVariantSymbol(variant.Name, [other, variant]);
                break;
            case VariantSymbol:
                return;
            case AmbiguousVariantSymbol ambiguous when !ambiguous.Variants.Contains(variant):
                declared = new AmbiguousVariantSymbol(variant.Name, [.. ambiguous.Variants, variant]);
                break;
            case AmbiguousVariantSymbol:
                return;
            default:
                _diagnostics.ReportDuplicateDefinition(span, variant.Name);
                return;
        }
        _global.Declare(variant.Name, declared);
        if (isDefinition)
            _definitions[variant.Name] = declared;
    }

    private void BindVariantFields(EnumDef enumDef, EnumSymbol symbol)
    {
        CheckTypeParameters(enumDef.TypeParams, allowConstraints: false);
        var outer = EnterTypeParameters(symbol.TypeParameters);
        foreach (var variantDef in enumDef.Variants)
        {
            if (symbol.FindVariant(variantDef.Name.Text) is not { } variant || !variant.Fields.IsEmpty)
                continue;
            var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            foreach (var field in variantDef.Fields)
            {
                if (fields.Any(f => f.Name == field.Name.Text))
                    _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
                else
                    fields.Add(new FieldSymbol(field.Name.Text, BindType(field.Type)));
            }
            variant.Fields = fields.ToImmutable();
        }
        _typeParameters = outer;
    }

    private BoundConstant BindConstDef(ConstDef constDef)
    {
        var annotation = constDef.Type is null ? null : BindType(constDef.Type);
        BoundExpr value;
        if (IsConstantExpression(constDef.Value))
        {
            value = annotation is null ? BindValue(constDef.Value) : BindExpected(constDef.Value, annotation);
        }
        else
        {
            _diagnostics.ReportNotConstant(constDef.Value.Span);
            value = new BoundErrorExpr(constDef.Value.Span);
        }

        var symbol = new VariableSymbol(constDef.Name.Text, annotation ?? value.Type, VariableKind.Constant);
        DeclareGlobal(constDef.Name, symbol);
        return new BoundConstant(symbol, value, constDef.Span);
    }

    // A class attribute's value is evaluated once, before any code runs: a constant, None, or a
    // list, dict or tuple of those (SPEC §4.8).
    private BoundExpr BindClassAttributeValue(Expr value, TaipanType type)
    {
        if (IsAttributeConstant(value))
            return BindExpected(value, type);
        _diagnostics.ReportClassAttributeNotConstant(value.Span);
        return new BoundErrorExpr(value.Span);
    }

    private static bool IsAttributeConstant(Expr expr) => expr switch
    {
        NoneLiteral => true,
        ListExpr list => list.Elements.All(IsAttributeConstant),
        TupleExpr tuple => tuple.Elements.All(IsAttributeConstant),
        DictExpr dict => dict.Entries.All(e => IsAttributeConstant(e.Key) && IsAttributeConstant(e.Value)),
        _ => IsConstantExpression(expr),
    };

    // Literals, unary minus, and + - * / // % (SPEC §4.5).
    private static bool IsConstantExpression(Expr expr) => expr switch
    {
        IntLiteral or FloatLiteral or StringLiteral or BoolLiteral => true,
        UnaryExpr { Op: UnaryOp.Negate } unary => IsConstantExpression(unary.Operand),
        BinaryExpr
        {
            Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide
                or BinaryOp.FloorDivide or BinaryOp.Modulo or BinaryOp.Power,
        } binary => IsConstantExpression(binary.Left) && IsConstantExpression(binary.Right),
        _ => false,
    };

    private FunctionSymbol BindFunctionSignature(FuncDef funcDef)
    {
        CheckDecorators(funcDef.Decorators, allowed: null);
        CheckTypeParameters(funcDef.TypeParams, allowConstraints: true);
        var typeParameters = NewTypeParameters(funcDef.TypeParams);
        var outer = EnterTypeParameters(typeParameters);
        BindConstraints(funcDef.TypeParams, typeParameters);
        BindBounds(funcDef.TypeParams, typeParameters);
        var symbol = new FunctionSymbol(
            funcDef.Name.Text, BindParams(funcDef.Params), AsyncResult(funcDef.IsAsync && !Yields(funcDef.Body), BindReturnType(funcDef.ReturnType)), typeParameters: typeParameters)
        {
            Defaults = PendingDefaults(funcDef.Params),
            Kinds = KindsOf(funcDef.Params),
            IsGenerator = Yields(funcDef.Body),
            IsAsync = funcDef.IsAsync,
        };
        CheckGeneratorReturn(symbol, funcDef.Name);
        _typeParameters = outer;
        DeclareGlobal(funcDef.Name, symbol);
        return symbol;
    }

    // `*args: T` is a list[T], and `**kwargs: T` a dict[str, T] (SPEC §4.2, M30).
    private ImmutableArray<VariableSymbol> BindParams(ImmutableArray<Param> parameters) =>
        [.. parameters.Select(p => new VariableSymbol(p.Name.Text, p.Kind switch
        {
            ParamKind.VarArgs => new ListOfType(BindType(p.Type)),
            ParamKind.VarKeywords => new DictOfType(TaipanType.Str, BindType(p.Type)),
            _ => BindType(p.Type),
        }, VariableKind.Parameter))];

    private static ImmutableArray<ParamKind> KindsOf(ImmutableArray<Param> parameters) => [.. parameters.Select(p => p.Kind)];

    /// <summary>
    /// Placeholders for a top-level function's or method's defaults, so calls know which parameters
    /// may be left out before the defaults themselves are bound.
    /// </summary>
    private static ImmutableArray<BoundExpr?> PendingDefaults(ImmutableArray<Param> parameters) =>
        [.. parameters.Select(p => p.Default is null ? null : (BoundExpr)new BoundErrorExpr(p.Default.Span))];

    /// <summary>
    /// Binds a top-level function's or method's defaults where it's defined: in the file's global
    /// scope, with its type parameters but not its parameters (SPEC §4.2). Like its body, a default
    /// can't use top-level variables (TP0211).
    /// </summary>
    private void BindDefaults(FunctionSymbol symbol, ImmutableArray<Param> parameters, ImmutableArray<TypeParameterSymbol> typeParameters)
    {
        if (parameters.All(p => p.Default is null))
            return;
        var outerTypeParameters = EnterTypeParameters(typeParameters);
        (_function, _outerFunction, _scope) = (symbol, symbol, new Scope(_global));
        symbol.Defaults = BindDefaultValues(parameters, symbol.Parameters);
        (_function, _outerFunction, _scope) = (null, null, _global);
        _typeParameters = outerTypeParameters;
    }

    /// <summary>Each parameter's default, checked against its type, in the current scope.</summary>
    private ImmutableArray<BoundExpr?> BindDefaultValues(ImmutableArray<Param> parameters, ImmutableArray<VariableSymbol> symbols) =>
        [.. parameters.Zip(symbols, (p, s) => p.Default is null ? null : BindExpected(p.Default, s.Type))];

    private TaipanType BindReturnType(TypeSyntax? type) => type is null ? TaipanType.Nothing : BindType(type);

    private List<(MethodDef, FunctionSymbol)> BindClassMembers(ClassDef classDef, ClassSymbol symbol)
    {
        if (symbol.IsProtocol)
            return BindProtocolMembers(classDef, symbol);
        CheckTypeParameters(classDef.TypeParams, allowConstraints: false);
        var outer = EnterTypeParameters(symbol.TypeParameters);
        var outerAliasClass = _aliasClass;
        _aliasClass = symbol;
        DeclareLocalAliases(classDef.Aliases, symbol, [.. classDef.Fields.Select(f => f.Name.Text), .. classDef.Methods.Select(m => m.Name.Text)]);
        var names = new HashSet<string>();
        var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
        var attributes = ImmutableArray.CreateBuilder<ClassAttributeSymbol>();
        foreach (var field in classDef.Fields)
        {
            // `count: ClassVar[int] = 0` is a class attribute (SPEC §4.8).
            if (field.Type is GenericType { Name.Text: "ClassVar" } classVar)
            {
                if (DeclareClassAttribute(symbol, field, classVar) is not { } attribute)
                    continue;
                if (names.Add(field.Name.Text))
                    attributes.Add(attribute);
                else
                    _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
                continue;
            }
            var fieldSymbol = new FieldSymbol(field.Name.Text, BindType(field.Type)) { Owner = symbol, HasDefault = field.Default is not null };
            if (names.Add(field.Name.Text))
            {
                fields.Add(fieldSymbol);
                Index(field.Name.Span, fieldSymbol, isDefinition: true);
            }
            else
                _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
        }
        symbol.Fields = fields.ToImmutable();
        symbol.ClassAttributes = attributes.ToImmutable();

        var methods = new List<(MethodDef, FunctionSymbol)>();
        MethodDef? previous = null;
        foreach (var method in classDef.Methods)
        {
            if (DeclareMethod(symbol, method, previous, names) is { } methodSymbol)
                methods.Add((method, methodSymbol));
            previous = method;
        }
        _typeParameters = outer;
        _aliasClass = outerAliasClass;
        return methods;
    }

    /// <summary>Whether a <c>__lt__</c> takes one parameter of an accepted type and returns bool.</summary>
    private static bool IsLessThan(FunctionSymbol method, Func<TaipanType, bool> accepts) =>
        method.Parameters is [var other] && (other.Type.ContainsError || accepts(other.Type))
        && (method.ReturnType == TaipanType.Bool || method.ReturnType.ContainsError);

    /// <summary>
    /// A protocol's methods (SPEC §3.11): signatures in which <c>Self</c> is the satisfying type,
    /// each with <c>...</c> as its body and no defaults. Its fields are TP0225.
    /// </summary>
    private List<(MethodDef, FunctionSymbol)> BindProtocolMembers(ClassDef classDef, ClassSymbol symbol)
    {
        CheckTypeParameters(classDef.TypeParams, allowConstraints: false);
        // Fields, without defaults or Self (SPEC §3.18).
        var outer = EnterTypeParameters(symbol.TypeParameters);
        var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
        foreach (var field in classDef.Fields)
        {
            if (field.Default is { } value)
                _diagnostics.ReportProtocolFieldDefault(value.Span);
            var type = BindType(field.Type);
            if (fields.Any(f => f.Name == field.Name.Text))
                _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
            else
                fields.Add(new FieldSymbol(field.Name.Text, type) { Owner = symbol });
        }
        symbol.Fields = fields.ToImmutable();
        _typeParameters = outer;
        outer = EnterTypeParameters([.. symbol.TypeParameters, symbol.SelfParameter!]);
        var methods = new List<(MethodDef, FunctionSymbol)>();
        foreach (var method in classDef.Methods)
        {
            var name = method.Name.Text;
            CheckDecorators(method.Decorators, allowed: null);
            foreach (var parameter in method.Params.Where(p => p.Default is not null))
                _diagnostics.ReportProtocolDefault(parameter.Default!.Span);
            if (method.Body.Statements is not [ExprStmt { Expr: EllipsisExpr }])
                _diagnostics.ReportProtocolBody(method.Body.Statements.FirstOrDefault()?.Span ?? method.Name.Span);
            var self = new VariableSymbol("self", symbol.Type, VariableKind.Parameter);
            var methodSymbol = new FunctionSymbol(name, BindParams(method.Params), BindReturnType(method.ReturnType), self: self) { Kinds = KindsOf(method.Params) };
            if (symbol.Methods.ContainsKey(name) || symbol.Fields.Any(f => f.Name == name))
                _diagnostics.ReportDuplicateDefinition(method.Name.Span, name);
            else if (name == "__lt__" && !IsLessThan(methodSymbol, other => other == symbol.SelfParameter!.Type))
                _diagnostics.ReportLessThanSignature(method.Name.Span, "Self");
            else if (name != "__lt__" && SpecialMethods.IsSpecialName(name))
                _diagnostics.ReportSpecialMethod(method.Name.Span, name);
            else
                symbol.Methods[name] = methodSymbol;
        }
        _typeParameters = outer;
        return methods;
    }

    /// <summary>
    /// Each class against the protocols it declares, at the declaration, and against those its base
    /// classes declare, at its name (SPEC §3.11). A class whose base already fails isn't reported again.
    /// </summary>
    private void CheckDeclaredProtocols(Module module)
    {
        foreach (var (symbol, protocol, span) in module.DeclaredProtocols)
        {
            if (Ancestors(symbol).FirstOrDefault(a => a.Protocols.Any(p => p.Symbol == protocol.Symbol)) is { } declarer)
                _diagnostics.ReportProtocolRepeated(span, protocol.Symbol.Name, declarer.Name);
            else if (Unsatisfied(symbol.Type, protocol) is { } why)
                _diagnostics.ReportUnsatisfied(span, symbol.Type, protocol.ToString(), declaredBy: null, why);
        }
        foreach (var (syntax, symbol) in module.ClassDefs.Where(c => !c.Symbol.IsProtocol))
        {
            var checkedProtocols = new HashSet<ClassSymbol>(symbol.Protocols.Select(p => p.Symbol));
            foreach (var ancestor in Ancestors(symbol))
            {
                foreach (var declared in ancestor.Protocols.Where(p => checkedProtocols.Add(p.Symbol)))
                {
                    // A generic base's declaration, with the subclass's type arguments (SPEC §3.18).
                    var protocol = (ClassType)TypeChecker.Substitute(declared, TypeChecker.MapOf(symbol.Type));
                    if (Unsatisfied(TypeChecker.AsBase(symbol.Type, ancestor)!, protocol) is null && Unsatisfied(symbol.Type, protocol) is { } why)
                        _diagnostics.ReportUnsatisfied(syntax.Name.Span, symbol.Type, protocol.ToString(), ancestor.Name, why);
                }
            }
        }
    }

    private static IEnumerable<ClassSymbol> Ancestors(ClassSymbol symbol)
    {
        for (var ancestor = symbol.Base; ancestor is not null; ancestor = ancestor.Base)
            yield return ancestor;
    }

    private static string? Unsatisfied(TaipanType type, ClassType protocol) => Protocols.Unsatisfied(type, protocol);

    private static (FunctionSymbol Method, Dictionary<TypeParameterSymbol, TaipanType> Map)? MethodOf(TaipanType type, string name) =>
        Protocols.MethodOf(type, name);

    /// <summary>
    /// Checks a class's own members against its base's (SPEC §4.3): no redeclared names, overrides
    /// marked and compatible; for a dataclass, defaulted fields last, and for a plain class without
    /// <c>__init__</c>, a default for every field.
    /// </summary>
    private void CheckInheritance(ClassDef classDef, ClassSymbol symbol, IEnumerable<(MethodDef Syntax, FunctionSymbol Symbol)> methods)
    {
        var baseClass = symbol.Base;
        foreach (var field in classDef.Fields)
        {
            var name = field.Name.Text;
            if (baseClass?.FindField(name) is not null || baseClass?.FindMethod(name) is not null || baseClass?.FindClassAttribute(name) is not null)
                _diagnostics.ReportDuplicateDefinition(field.Name.Span, name);
        }
        foreach (var (syntax, method) in methods)
        {
            if (symbol.Methods.GetValueOrDefault(method.Name) != method)
                continue;
            if (baseClass?.FindField(method.Name) is not null || baseClass?.FindClassAttribute(method.Name) is not null)
            {
                _diagnostics.ReportDuplicateDefinition(syntax.Name.Span, method.Name);
                continue;
            }
            var overridden = baseClass?.FindMethod(method.Name);
            if (overridden is null)
            {
                // A .NET base class's virtual methods are checked with its other members (SPEC §13.16).
                if (method.IsMarkedOverride && !(symbol.DotNetBaseClass is { } dotnetBase && VirtualMethods(dotnetBase, method.Name).Count > 0))
                    _diagnostics.ReportNothingToOverride(syntax.Name.Span, method.Name, baseClass?.Name ?? symbol.DotNetBaseClass?.ToString());
                continue;
            }
            if (overridden.IsFinal)
                _diagnostics.ReportFinalOverride(syntax.Name.Span, overridden.Owner!.Name, method.Name);
            else if (!method.IsMarkedOverride)
                _diagnostics.ReportMissingOverride(syntax.Name.Span, method.Name, overridden.Owner!.Name);
            else if (overridden.Kind != method.Kind)
                _diagnostics.ReportMemberKind(syntax.Name.Span, method.Name, KindName(overridden.Kind), overridden.Owner!.Name);
            else
                CheckOverride(syntax, method, overridden);
            method.Overrides = overridden;
        }
        CheckSpecialInheritance(classDef, symbol, methods);
        CheckDotNetBases(classDef, symbol, methods);

        if (symbol.IsDataclass)
        {
            // The fields are the constructor's parameters, inherited ones first (SPEC §4.3).
            var sawDefault = baseClass?.AllFields.Any(f => f.HasDefault) == true;
            foreach (var field in classDef.Fields.Where(f => f.Type is not GenericType { Name.Text: "ClassVar" }))
            {
                if (field.Default is not null)
                    sawDefault = true;
                else if (sawDefault)
                    _diagnostics.ReportFieldOrder(field.Name.Span);
            }
        }
        else if (symbol.Init is null)
        {
            foreach (var field in classDef.Fields.Where(f => f.Default is null && f.Type is not GenericType { Name.Text: "ClassVar" }))
                _diagnostics.ReportFieldWithoutInit(field.Name.Span, field.Name.Text, symbol.Name);
        }
    }

    /// <summary>
    /// An override takes the base method's parameters, with its defaults, and returns what it does
    /// or, for a class result, a class derived from it (TP0228, SPEC §4.3).
    /// </summary>
    private void CheckOverride(MethodDef syntax, FunctionSymbol method, FunctionSymbol overridden)
    {
        var (name, owner) = (method.Name, overridden.Owner!.Name);
        // A generic base's members are checked with its type arguments in place (SPEC §3.18).
        var map = TypeChecker.MapOf(method.Owner!.Type);
        // The same kinds too: '/', '*', '*args' and '**kwargs' (SPEC §4.3, M30).
        if (method.Parameters.Length != overridden.Parameters.Length || !method.Kinds.SequenceEqual(overridden.Kinds))
        {
            _diagnostics.ReportOverrideParameters(syntax.Name.Span, name, owner);
            return;
        }
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var (parameter, expected) = (method.Parameters[i], overridden.Parameters[i]);
            expected = new VariableSymbol(expected.Name, TypeChecker.Substitute(expected.Type, map), expected.Kind);
            if (parameter.Name != expected.Name)
            {
                _diagnostics.ReportOverrideParameters(syntax.Params[i].Name.Span, name, owner);
                return;
            }
            if (parameter.Type != expected.Type && !parameter.Type.ContainsError && !expected.Type.ContainsError)
                _diagnostics.ReportOverrideParameterType(syntax.Params[i].Span, parameter.Name, name, expected.Type, owner);
            else if (overridden.DefaultFor(i) is not null && method.DefaultFor(i) is null)
                _diagnostics.ReportOverrideDefault(syntax.Params[i].Span, parameter.Name, name, owner);
        }

        var (result, expectedResult) = (method.ReturnType, TypeChecker.Substitute(overridden.ReturnType, map));
        var span = syntax.ReturnType?.Span ?? syntax.Name.Span;
        static bool IsClass(TaipanType type) => TypeChecker.Unwrap(type) is ClassType;
        // An override may narrow a class result to Self, but must keep a Self result (SPEC §4.9).
        if (result is SelfType && IsClass(expectedResult))
            result = expectedResult;
        if (expectedResult is NothingType)
        {
            if (result is not NothingType)
                _diagnostics.ReportOverrideReturn(span, name, null, owner);
        }
        // A property with a setter is also assigned, so it keeps its type (SPEC §4.8).
        else if (result != expectedResult && !result.ContainsError
            && (overridden.Setter is not null || !(IsClass(result) && IsClass(expectedResult) && TypeChecker.IsAssignable(result, expectedResult))))
        {
            _diagnostics.ReportOverrideReturn(span, name, expectedResult, owner);
        }
        if (overridden.Setter is not null && method.Setter is null)
            _diagnostics.ReportSetterRequired(syntax.Name.Span, name, owner);
    }

    /// <summary>
    /// A class's field defaults, bound like a function's (SPEC §4.3): in the file's global scope,
    /// with the class's type parameters, and checked against each field's type.
    /// </summary>
    private void BindFieldDefaults(ClassDef classDef, ClassSymbol symbol)
    {
        if (classDef.Fields.All(f => f.Default is null))
            return;
        var outerTypeParameters = EnterTypeParameters(symbol.TypeParameters);
        _aliasClass = symbol;
        // Named after the class, for TP0211.
        var context = new FunctionSymbol(symbol.Name, [], TaipanType.Nothing);
        (_function, _outerFunction, _scope) = (context, context, new Scope(_global));
        foreach (var field in classDef.Fields)
        {
            if (field.Default is not { } value)
                continue;
            if (field.Type is GenericType { Name.Text: "ClassVar" })
            {
                if (symbol.ClassAttributes.FirstOrDefault(a => a.Name == field.Name.Text) is { } attribute)
                    attribute.Value = BindClassAttributeValue(value, attribute.Type);
            }
            else if (symbol.Fields.FirstOrDefault(f => f.Name == field.Name.Text) is { } fieldSymbol)
            {
                fieldSymbol.Default = BindExpected(value, fieldSymbol.Type);
            }
        }
        (_function, _outerFunction, _scope) = (null, null, _global);
        _typeParameters = outerTypeParameters;
        _aliasClass = null;
    }

    /// <summary>A method's body; an <c>__init__</c>'s is also checked for strict initialization (SPEC §4.3).</summary>
    private BoundFunction BindMethodBody(ClassSymbol owner, MethodDef syntax, FunctionSymbol method)
    {
        // An abstract method has no body to run (SPEC §4.8).
        if (method.IsAbstract || _stubs.Contains(method))
            return new BoundFunction(method, new BoundBlock([], syntax.Body.Span), syntax.Span) { Source = _source };
        var body = BindBody(method, syntax.Params, syntax.Name, syntax.Body, syntax.Span);
        if (method == owner.Init)
            InitChecker.Check(owner, method.Self!, body.Body, syntax.Name.Span, _diagnostics);
        return body;
    }

    /// <summary>
    /// A function's body, or for constrained type parameters one body per combination of their
    /// types (SPEC §3.6). Diagnostics only some combinations produce say which ones.
    /// </summary>
    private IEnumerable<BoundFunction> BindFunctionBodies(FunctionSymbol symbol, FuncDef syntax)
    {
        var constrained = symbol.TypeParameters.Where(p => !p.Constraints.IsEmpty).ToList();
        if (constrained.Count == 0)
            return [BindBody(symbol, syntax.Params, syntax.Name, syntax.Body, syntax.Span)];

        var combinations = constrained.Aggregate(
            (IEnumerable<ImmutableDictionary<TypeParameterSymbol, TaipanType>>)[ImmutableDictionary<TypeParameterSymbol, TaipanType>.Empty],
            (partial, parameter) => partial.SelectMany(map => parameter.Constraints.Select(type => map.Add(parameter, type)))).ToList();

        var outerDiagnostics = _diagnostics;
        var bodies = new List<BoundFunction>();
        var reports = new List<(string When, ImmutableArray<Diagnostic> Diagnostics)>();
        foreach (var combination in combinations)
        {
            _diagnostics = new DiagnosticBag { Source = _source };
            _specialization = new Dictionary<TypeParameterSymbol, TaipanType>(combination);
            var parameters = symbol.Parameters
                .Select(p => new VariableSymbol(p.Name, TypeChecker.Substitute(p.Type, _specialization), VariableKind.Parameter))
                .ToImmutableArray();
            var body = BindBody(symbol, syntax.Params, syntax.Name, syntax.Body, syntax.Span, parameters);
            bodies.Add(body with
            {
                Parameters = parameters,
                Specialization = [.. symbol.TypeParameters.Select(p => _specialization.GetValueOrDefault(p, p.Type))],
            });
            var when = string.Join(", ", constrained.Select(p => $"'{p.Name}' is {combination[p]}"));
            reports.Add((when, _diagnostics.ToImmutable()));
        }
        _specialization = [];
        _diagnostics = outerDiagnostics;

        // Report each distinct diagnostic once, saying which combinations produce it if not all.
        foreach (var diagnostic in reports.SelectMany(r => r.Diagnostics).Distinct())
        {
            var when = reports.Where(r => r.Diagnostics.Contains(diagnostic)).Select(r => r.When).ToList();
            _diagnostics.AddRange([when.Count == reports.Count
                ? diagnostic
                : diagnostic with { Message = $"{diagnostic.Message} (when {string.Join(" or ", when)})" }]);
        }
        return bodies;
    }

    private BoundFunction BindBody(
        FunctionSymbol symbol, ImmutableArray<Param> parameters, Identifier name, Block body, TextSpan span,
        ImmutableArray<VariableSymbol>? specializedParameters = null)
    {
        // A method sees its class's type parameters; a function its own.
        var outerTypeParameters = EnterTypeParameters(symbol.Owner is { } owner ? owner.TypeParameters : symbol.TypeParameters);
        _function = symbol;
        _outerFunction = symbol;
        _loopDepth = 0;
        (_handlerDepth, _finallyLoopDepth) = (0, null);
        _scope = new Scope(_global);
        if (symbol.Self is { } self)
            _scope.Declare(self);
        if (symbol.Cls is { } cls)
            _scope.Declare(cls);
        var parameterSymbols = specializedParameters ?? symbol.Parameters;
        for (var i = 0; i < parameterSymbols.Length; i++)
            DeclareLocal(parameters[i].Name, parameterSymbols[i]);

        var boundBody = BindBlock(body);
        var stub = body.Statements is [ExprStmt { Expr: EllipsisExpr }];
        if (symbol.BodyResult != TaipanType.Nothing && !stub && !symbol.IsGenerator && !TypeChecker.Terminates(boundBody))
            _diagnostics.ReportNotAllPathsReturn(name.Span, symbol.Name);
        CheckGeneratorEnd(symbol, name, boundBody);

        _function = null;
        _outerFunction = null;
        _scope = _global;
        _typeParameters = outerTypeParameters;
        return new BoundFunction(symbol, boundBody, span) { Source = _source };
    }

    // The names an unpacking or a pattern may bind (SPEC §3.8, §5.1), for TP0211.
    private static IEnumerable<string> UnpackedNames(IEnumerable<Unpacked> items) => items.SelectMany(item => item switch
    {
        UnpackedName { Name.Text: not "_" } name => [name.Name.Text],
        UnpackedGroup group => UnpackedNames(group.Items),
        _ => (IEnumerable<string>)[],
    });

    private static IEnumerable<string> PatternNames(Pattern pattern) => pattern switch
    {
        VariantPattern { Module: null, Enum: null, Bindings: null } bare => [bare.Variant.Text],
        VariantPattern { Bindings: { } arguments } => arguments.SelectMany(a => PatternNames(a.Pattern)),
        TuplePattern tuple => tuple.Elements.SelectMany(PatternNames),
        ListPattern list => list.Elements.SelectMany(PatternNames),
        StarPattern { Name.Text: not "_" } star => [star.Name.Text],
        _ => [],
    };

    /// <summary>Records every let/var/loop variable outside functions, for TP0211.</summary>
    private void CollectScriptNames(Stmt stmt)
    {
        switch (stmt)
        {
            case LetStmt let:
                _scriptNames.Add(let.Name.Text);
                break;
            case VarStmt var:
                _scriptNames.Add(var.Name.Text);
                break;
            case IfStmt ifStmt:
                foreach (var clause in ifStmt.Clauses)
                {
                    if (clause.Condition is IsSomeCondition isSome)
                        _scriptNames.Add(isSome.Binding.Text);
                    CollectScriptNames(clause.Body);
                }
                if (ifStmt.Else is not null)
                    CollectScriptNames(ifStmt.Else);
                break;
            case WhileStmt whileStmt:
                if (whileStmt.Condition is IsSomeCondition whileSome)
                    _scriptNames.Add(whileSome.Binding.Text);
                CollectScriptNames(whileStmt.Body);
                break;
            case ForStmt forStmt:
                _scriptNames.UnionWith(UnpackedNames(forStmt.Variables));
                CollectScriptNames(forStmt.Body);
                break;
            case UnpackStmt unpack:
                _scriptNames.UnionWith(UnpackedNames(unpack.Names));
                break;
            case FuncDefStmt funcDef:
                _scriptNames.Add(funcDef.Function.Name.Text);
                break;
            case MatchStmt match:
                foreach (var arm in match.Arms)
                {
                    _scriptNames.UnionWith(PatternNames(arm.Pattern));
                    CollectScriptNames(arm.Body);
                }
                break;
            case WithStmt with:
                _scriptNames.UnionWith(with.Items.Select(i => i.Binding?.Text).OfType<string>());
                CollectScriptNames(with.Body);
                break;
        }
    }

    private void CollectScriptNames(Block block)
    {
        foreach (var stmt in block.Statements)
            CollectScriptNames(stmt);
    }

    // ---- Declarations and lookup ----

    /// <summary>
    /// Declares a name in the module's global scope under <paramref name="name"/> (an import may
    /// rename it). A definition is also exported (SPEC §4.7).
    /// </summary>
    private bool DeclareGlobal(Identifier name, Symbol symbol, bool isDefinition = true)
    {
        if (_global.Lookup(name.Text) is not null)
        {
            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
            return false;
        }
        _global.Declare(name.Text, symbol);
        if (isDefinition)
            _definitions[name.Text] = symbol;
        Index(name.Span, symbol, isDefinition);
        return true;
    }

    private void DeclareLocal(Identifier name, Symbol symbol)
    {
        if (_scope.LookupLocal(name.Text) is not null)
            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
        else if (!_mayShadow && _scope.Parent?.Lookup(name.Text) is { } outer && !IsShadowableBuiltin(outer))
            _diagnostics.ReportShadowing(name.Span, name.Text);
        _scope.Declare(symbol);
        if (symbol is VariableSymbol variable)
            _declaredIn[variable] = _function;
        Index(name.Span, symbol, isDefinition: true);
    }

    /// <summary>
    /// A built-in function a local may reuse the name of, as in Python (SPEC §4.5, M35): not one
    /// that's also a type's name (<c>int</c>, <c>str</c>, <c>set</c>), nor a built-in class.
    /// </summary>
    private bool IsShadowableBuiltin(Symbol symbol) =>
        symbol is FunctionSymbol { Builtin: not null } && _builtins.LookupLocal(symbol.Name) == symbol && !BuiltinTypeNames.Contains(symbol.Name);

    /// <summary>Looks up a value name, reporting TP0201 or TP0211 if it isn't visible.</summary>
    private Symbol? LookupName(string name, TextSpan span)
    {
        if (_scope.Lookup(name) is { } symbol)
        {
            Index(span, symbol);
            return symbol;
        }
        if (_outerFunction is not null && _scriptNames.Contains(name))
            _diagnostics.ReportTopLevelVariableInFunction(span, _function!.Name, name);
        else
            _diagnostics.ReportUndefinedName(span, name);
        Unresolved(span, inType: false);
        return null;
    }

    private TaipanType BindType(TypeSyntax type)
    {
        switch (type)
        {
            case NamedType { Name.Text: "int" or "float" or "bool" or "str" } primitive:
                Index(primitive.Name.Span, new SemanticIndex.BuiltinType(primitive.Name.Text));
                return primitive.Name.Text switch
                {
                    "int" => TaipanType.Int,
                    "float" => TaipanType.Float,
                    "bool" => TaipanType.Bool,
                    _ => TaipanType.Str,
                };
            case NamedType { Name.Text: "py" } py:
                Index(py.Name.Span, new SemanticIndex.BuiltinType("py"));
                if (_asNative)
                    _diagnostics.ReportNativePython(py.Span, _asExtension);
                return TaipanType.Py;
            case NamedType { Name.Text: "TextIO" } file when ResolveTypeName("TextIO") is null:
                Index(file.Name.Span, new SemanticIndex.BuiltinType("TextIO"));
                return TaipanType.TextIO;
            case NamedType named when _global.Lookup(named.Name.Text) is BuiltinTypeSymbol builtinType:
                Index(named.Name.Span, builtinType);
                return builtinType.Type;
            // type[C] (SPEC §3.17, M37).
            case GenericType { Name.Text: "type" } classOf when ResolveTypeName("type") is null:
                if (classOf.Arguments is not [var classSyntax])
                {
                    _diagnostics.ReportWrongTypeArgumentCount(classOf.Span, "type", 1, classOf.Arguments.Length);
                    return TaipanType.Error;
                }
                Index(classOf.Name.Span, new SemanticIndex.BuiltinType("type"));
                var ofClass = BindType(classSyntax);
                if (ofClass is ClassType { Symbol.IsProtocol: false } heldClass)
                    return new ClassOfType(heldClass);
                if (!ofClass.ContainsError)
                    _diagnostics.ReportTypeOfNonClass(classSyntax.Span, ofClass);
                return TaipanType.Error;
            case NamedType { Name.Text: "Callable" } bare:
                _diagnostics.ReportBareCallable(bare.Span);
                return TaipanType.Error;
            case TupleType tuple:
                IndexBuiltinType(tuple.Span, "tuple");
                var elements = tuple.Elements.Select(BindType).ToImmutableArray();
                if (elements.Length >= 2)
                    return new TupleOfType(elements);
                _diagnostics.ReportTupleTooShort(tuple.Span);
                return TaipanType.Error;
            case VarTupleType varTuple:
                IndexBuiltinType(varTuple.Span, "tuple");
                var varElement = BindType(varTuple.Element);
                return varElement.IsError ? TaipanType.Error : new VarTupleOfType(varElement);
            case AwaitableType awaitable:
                IndexBuiltinType(awaitable.Span, "Awaitable");
                return new AwaitableOfType(awaitable.Result is null ? TaipanType.Nothing : BindType(awaitable.Result));
            case CallableType callable:
                IndexBuiltinType(callable.Span, "Callable");
                return new FunctionType(
                    [.. callable.Parameters.Select(BindType)], callable.Result is null ? TaipanType.Nothing : BindType(callable.Result));
            case NamedType named when _typeParameters.TryGetValue(named.Name.Text, out var typeParameter):
                if (typeParameter is TypeParameterType { Symbol: var parameterSymbol })
                    Index(named.Name.Span, parameterSymbol);
                return typeParameter;
            case NamedType named when LookupAlias(named.Name.Text) is { } alias:
                Index(named.Name.Span, alias);
                return ExpandAlias(alias, named.Name, [], named.Span);
            case NamedType named when _global.Lookup(named.Name.Text) is DotNetTypeSymbol dotnet:
                Index(named.Name.Span, dotnet);
                return BindDotNetType(dotnet.Types, named.Name.Text, ImmutableArray<TaipanType>.Empty, named.Span);
            // A generic .NET type with its type arguments (SPEC §13.13, M43).
            case GenericType generic when _global.Lookup(generic.Name.Text) is DotNetTypeSymbol dotnet:
                Index(generic.Name.Span, dotnet);
                return BindDotNetType(dotnet.Types, generic.Name.Text, generic.Arguments, generic.Span);
            case GenericType generic when LookupAlias(generic.Name.Text) is { } alias:
                Index(generic.Name.Span, alias);
                return ExpandAlias(alias, generic.Name, generic.Arguments, generic.Span);
            case NamedType named when ResolveTypeName(named.Name.Text) is { } typeSymbol:
                Index(named.Name.Span, typeSymbol);
                return BindGenericType(named.Name, [], named.Span, typeSymbol);
            // set[T] (SPEC §3.15, M35).
            case GenericType { Name.Text: "set" } setType when ResolveTypeName("set") is null:
                if (setType.Arguments is not [var elementSyntax])
                {
                    _diagnostics.ReportWrongTypeArgumentCount(setType.Span, "set", 1, setType.Arguments.Length);
                    return TaipanType.Error;
                }
                Index(setType.Name.Span, new SemanticIndex.BuiltinType("set"));
                var setElement = BindType(elementSyntax);
                CheckSetElement(elementSyntax.Span, setElement);
                return new SetOfType(setElement);
            // ClassVar only declares a class attribute (SPEC §4.8).
            case GenericType { Name.Text: "ClassVar", Arguments: [var held] } classVar:
                _diagnostics.ReportClassVarMisplaced(classVar.Span);
                return BindType(held);
            // Iterator[T] and Iterable[T] (SPEC §3.13).
            case GenericType { Name.Text: "Iterator" or "Iterable" } stream when ResolveTypeName(stream.Name.Text) is null:
                if (stream.Arguments is not [var element])
                {
                    _diagnostics.ReportWrongTypeArgumentCount(stream.Span, stream.Name.Text, 1, stream.Arguments.Length);
                    return TaipanType.Error;
                }
                Index(stream.Name.Span, new SemanticIndex.BuiltinType(stream.Name.Text));
                return stream.Name.Text == "Iterator" ? new IteratorOfType(BindType(element)) : new IterableOfType(BindType(element));
            // AsyncIterator[T] and AsyncIterable[T], Generator[Y, S, R] and AsyncGenerator[Y, S] (SPEC §3.20, M42).
            case GenericType { Name.Text: "AsyncIterator" or "AsyncIterable" or "Generator" or "AsyncGenerator" } stream when ResolveTypeName(stream.Name.Text) is null:
            {
                var count = stream.Name.Text switch { "Generator" => 3, "AsyncGenerator" => 2, _ => 1 };
                if (stream.Arguments.Length != count)
                {
                    _diagnostics.ReportWrongTypeArgumentCount(stream.Span, stream.Name.Text, count, stream.Arguments.Length);
                    return TaipanType.Error;
                }
                Index(stream.Name.Span, new SemanticIndex.BuiltinType(stream.Name.Text));
                var arguments = stream.Arguments.Select(BindType).ToArray();
                return stream.Name.Text switch
                {
                    "AsyncIterator" => new AsyncIteratorOfType(arguments[0]),
                    "AsyncIterable" => new AsyncIterableOfType(arguments[0]),
                    "Generator" => new GeneratorOfType(arguments[0], arguments[1], arguments[2]),
                    _ => new AsyncGeneratorOfType(arguments[0], arguments[1]),
                };
            }
            // None as a Generator's argument (SPEC §3.20).
            case NoneTypeSyntax:
                return TaipanType.None;
            case NamedType { Name.Text: "object" } objectType when ResolveTypeName("object") is null:
                Index(objectType.Name.Span, new SemanticIndex.BuiltinType("object"));
                return TaipanType.Object;
            case GenericType generic:
                return BindGenericType(generic.Name, generic.Arguments, generic.Span, ResolveTypeName(generic.Name.Text));
            case QualifiedType qualified:
                return BindQualifiedType(qualified);
            case NamedType named:
                _diagnostics.ReportUndefinedType(named.Span, named.Name.Text);
                Unresolved(named.Name.Span, inType: true);
                return TaipanType.Error;
            case ListType list:
                IndexBuiltinType(list.Span, "list");
                return new ListOfType(BindType(list.Element));
            case DictType dict:
                IndexBuiltinType(dict.Span, "dict");
                var key = BindType(dict.Key);
                CheckDictKey(dict.Key.Span, key);
                return new DictOfType(key, BindType(dict.Value));
            case OptionalType optional:
                var inner = BindType(optional.Inner);
                // An optional flattens through an alias; only a literal T?? is nested (SPEC §3.12).
                if (inner is OptionalOf && optional.Inner is not OptionalType)
                    return inner;
                if (inner is OptionalOf)
                {
                    _diagnostics.ReportNestedOptional(optional.Span);
                    return TaipanType.Error;
                }
                return new OptionalOf(inner);
            default:
                return TaipanType.Error;
        }
    }

    // `list[…]`, `dict[…]`, `tuple[…]` and `Callable[…]` start with their name (SPEC §16.7).
    private void IndexBuiltinType(TextSpan span, string name)
    {
        if (span.Length >= name.Length && _source.ToString(new TextSpan(span.Start, name.Length)) == name)
            Index(new TextSpan(span.Start, name.Length), new SemanticIndex.BuiltinType(name));
    }

    /// <summary>A class or enum visible in this file, defined or imported (SPEC §4.7).</summary>
    private Symbol? ResolveTypeName(string name) => _global.Lookup(name) is ClassSymbol or EnumSymbol ? _global.Lookup(name) : null;

    /// <summary><c>m.Point</c> or <c>m.Box[int]</c>: a type that module <c>m</c> defines.</summary>
    private TaipanType BindQualifiedType(QualifiedType qualified)
    {
        var (name, arguments) = qualified.Type switch
        {
            NamedType named => (named.Name, ImmutableArray<TypeSyntax>.Empty),
            GenericType generic => (generic.Name, generic.Arguments),
            _ => throw new InvalidOperationException($"unexpected qualified type {qualified.Type.GetType().Name}"),
        };
        // A .NET type nested in an imported one: Environment.SpecialFolder (SPEC §13.1).
        if (_scope.Lookup(qualified.Module.Text) is DotNetTypeSymbol outer && arguments.IsEmpty)
        {
            if (NestedType(outer.Type, name.Text) is { } nested)
                return DotNetTypeOf(nested, qualified.Span);
            _diagnostics.ReportUndefinedType(name.Span, name.Text);
            return TaipanType.Error;
        }
        // A type in an imported .NET namespace: io.FileInfo, gen.HashSet[int] (SPEC §13.1, M43).
        if (_scope.Lookup(qualified.Module.Text) is DotNetNamespaceSymbol ns)
        {
            if (ns.TypesNamed(name.Text) is { IsEmpty: false } types)
                return BindDotNetType(types, name.Text, arguments, qualified.Span);
            _diagnostics.ReportNoDotNetType(name.Span, ns.Namespace.ToDisplayString(), name.Text);
            return TaipanType.Error;
        }
        if (_scope.Lookup(qualified.Module.Text) is not ModuleSymbol module)
        {
            _diagnostics.ReportUndefinedName(qualified.Module.Span, qualified.Module.Text);
            return TaipanType.Error;
        }
        Index(qualified.Module.Span, module);
        RecordReceiver(name.Span, module);
        if (module.Definitions.GetValueOrDefault(name.Text) is BuiltinTypeSymbol builtinType && arguments.IsEmpty)
        {
            Index(name.Span, builtinType);
            return builtinType.Type;
        }
        if (module.Definitions.GetValueOrDefault(name.Text) is TypeAliasSymbol alias)
        {
            Index(name.Span, alias);
            return ExpandAlias(alias, name, arguments, qualified.Span);
        }
        Symbol? symbol = module.Classes.TryGetValue(name.Text, out var classSymbol) ? classSymbol
            : module.Enums.TryGetValue(name.Text, out var enumSymbol) ? enumSymbol : null;
        if (symbol is not null)
            Index(name.Span, symbol);
        if (symbol is null)
        {
            _diagnostics.ReportNoDefinition(name.Span, module.Name, name.Text);
            return TaipanType.Error;
        }
        return BindGenericType(name, arguments, qualified.Span, symbol);
    }

    /// <summary>A class or enum type with exactly as many type arguments as it has parameters (SPEC §3.6).</summary>
    private TaipanType BindGenericType(Identifier name, ImmutableArray<TypeSyntax> argumentSyntax, TextSpan span, Symbol? symbol)
    {
        var arguments = argumentSyntax.Select(BindType).ToImmutableArray();
        var parameters = symbol switch
        {
            ClassSymbol classSymbol => classSymbol.TypeParameters,
            EnumSymbol enumSymbol => enumSymbol.TypeParameters,
            _ => default,
        };
        if (parameters.IsDefault)
        {
            _diagnostics.ReportUndefinedType(name.Span, name.Text);
            return TaipanType.Error;
        }

        // A protocol is a type when it uses Self only as a whole result (SPEC §3.18), which is
        // checked once its methods are known.
        if (symbol is ClassSymbol { IsProtocol: true } protocol)
        {
            if (!_protocolsDeclared)
                _pendingProtocolTypes.Add((_module!, span, protocol));
            else if (!CheckProtocolType(span, protocol))
                return TaipanType.Error;
        }
        if (arguments.Length != parameters.Length)
        {
            _diagnostics.ReportWrongTypeArgumentCount(span, name.Text, parameters.Length, arguments.Length);
            return TaipanType.Error;
        }
        return symbol is ClassSymbol s
            ? new ClassType(s, arguments)
            : new EnumType((EnumSymbol)symbol!, arguments);
    }

    /// <summary>
    /// A dict key type must be hashable (SPEC §3.1, §4.8). A class's hashability depends on its
    /// methods, so until every class's members are declared the check waits.
    /// </summary>
    private void CheckDictKey(TextSpan span, TaipanType key, bool isSet = false)
    {
        if (!_membersDeclared && Mentions(key, t => t is ClassType))
            _pendingKeys.Add((_module!, span, key, isSet));
        else if (!DictOfType.IsValidKey(key))
            ReportInvalidKey(span, key, isSet);
    }

    /// <summary>A set's elements are what can be dict keys (SPEC §3.15).</summary>
    private void CheckSetElement(TextSpan span, TaipanType element) => CheckDictKey(span, element, isSet: true);

    private void ReportInvalidKey(TextSpan span, TaipanType key, bool isSet = false)
    {
        if (isSet)
            _diagnostics.ReportInvalidSetElement(span, key);
        else if (Unhashable(key) is { Symbol.IsProtocol: true } protocol)
            _diagnostics.ReportUnhashableKey(span, protocol, null, "a class that satisfies a protocol needn't be hashable");
        else if (Unhashable(key) is { } unhashable)
            _diagnostics.ReportUnhashableKey(span, unhashable, unhashable.Symbol.IsDataclass ? null : EqualityDefiner(unhashable.Symbol),
                // A frozen dataclass is hashable when its fields are (SPEC §4.9).
                unhashable.Symbol.IsFrozen && unhashable.Symbol.UnhashableField is { } field ? $"'{field.Name}' is {field.Type}, which isn't hashable" : null);
        else
            _diagnostics.ReportInvalidDictKey(span, key, isDataclass: false);
    }

    // The class whose __eq__ without __hash__ makes a class unhashable: it, or a base (SPEC §4.8).
    private static string EqualityDefiner(ClassSymbol symbol) =>
        symbol.Methods.ContainsKey("__eq__") || symbol.Base is null ? symbol.Name : EqualityDefiner(symbol.Base);

    // The class in a key type that isn't hashable, if that's why it isn't a valid key.
    private static ClassType? Unhashable(TaipanType key) => key switch
    {
        ClassType { Symbol.IsHashable: false } classType => classType,
        TupleOfType tuple => tuple.Elements.Select(Unhashable).FirstOrDefault(c => c is not null),
        VarTupleOfType varTuple => Unhashable(varTuple.Element),
        _ => null,
    };

    private static bool Mentions(TaipanType type, Func<TaipanType, bool> test) =>
        test(type) || type is TupleOfType tuple && tuple.Elements.Any(e => Mentions(e, test));

    private TaipanType Unsupported(string feature)
    {
        _unsupported ??= feature;
        return TaipanType.Error;
    }

    // ---- Statements ----

    private BoundBlock BindBlock(Block block)
    {
        var outer = _scope;
        _scope = new Scope(outer);
        var statements = BindStatements(block.Statements);
        _scope = outer;
        return new BoundBlock(statements, block.Span);
    }

    /// <summary>
    /// Binds a block's statements, warning at the first one that can never run: one after a
    /// statement that terminates (§4.5), or after a <c>break</c> or <c>continue</c> (TP0300, M16).
    /// </summary>
    private ImmutableArray<BoundStmt> BindStatements(ImmutableArray<Stmt> syntax)
    {
        var outerAliases = _localAliases;
        foreach (var alias in DeclareLocalAliases(syntax.OfType<TypeAliasStmt>().Select(s => s.Alias), owner: null).Values)
            _scope.Declare(alias);
        var statements = syntax.Select(BindStmt).ToImmutableArray();
        _localAliases = outerAliases;
        for (var i = 0; i < statements.Length - 1; i++)
        {
            // A break or continue outside a loop, or a return outside a function, is already an error (TP0215).
            var stmt = statements[i];
            if ((stmt is BoundBreak or BoundContinue && _loopDepth > 0) || (TypeChecker.Terminates(stmt) && !(stmt is BoundReturn && _function is null)))
            {
                _diagnostics.ReportUnreachableCode(syntax[i + 1].Span);
                break;
            }
        }
        return statements;
    }

    private BoundStmt BindStmt(Stmt stmt)
    {
        var bound = BindStmtCore(stmt);
        EndElementsAfter(bound, bound.Span);
        return bound;
    }

    private BoundStmt BindStmtCore(Stmt stmt) => stmt switch
    {
        LetStmt let => BindVariableDecl(let.Name, let.Type, let.Value, VariableKind.Let, let.Span),
        VarStmt var => BindVariableDecl(var.Name, var.Type, var.Value, VariableKind.Var, var.Span),
        AssignStmt assign => BindAssign(assign),
        ReturnStmt ret => BindReturn(ret),
        PassStmt pass => new BoundBlock([], pass.Span),
        BreakStmt brk => BindBreak(brk),
        ContinueStmt cont => BindContinue(cont),
        ExprStmt exprStmt => BindExprStmt(exprStmt),
        IfStmt ifStmt => BindIf(ifStmt),
        WhileStmt whileStmt => BindWhile(whileStmt),
        ForStmt forStmt => BindFor(forStmt),
        MatchStmt match => BindMatch(match),
        RaiseStmt raise => BindRaise(raise),
        DelStmt delete => BindDelete(delete),
        TryStmt tryStmt => BindTry(tryStmt),
        WithStmt with => BindWith(with),
        FuncDefStmt funcDef => BindLocalFunction(funcDef),
        UnpackStmt unpack => BindUnpack(unpack),
        YieldStmt yield => BindYield(yield),
        TupleAssignStmt assign => BindTupleAssign(assign),
        TypeAliasStmt alias => new BoundBlock([], alias.Span),
        _ => throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}"),
    };

    /// <summary>
    /// A nested <c>def</c> (SPEC §3.7): a local function value, visible from here to the end of the
    /// block and in its own body. Its body sees the enclosing bindings, and has its own returns and loops.
    /// </summary>
    private BoundLocalFunction BindLocalFunction(FuncDefStmt stmt)
    {
        var def = stmt.Function;
        CheckDecorators(def.Decorators, allowed: null);
        var outerTypeParameters = _typeParameters;
        if (!def.TypeParams.IsEmpty)
        {
            // Bound as opaque types anyway, so the rest of the function doesn't report more errors.
            _diagnostics.ReportNestedTypeParameters(def.TypeParams[0].Span);
            _typeParameters = new Dictionary<string, TaipanType>(_typeParameters);
            foreach (var parameter in NewTypeParameters(def.TypeParams))
                _typeParameters.TryAdd(parameter.Name, parameter.Type);
        }
        var parameters = BindParams(def.Params);
        var returnType = AsyncResult(def.IsAsync && !Yields(def.Body), BindReturnType(def.ReturnType));
        // Its defaults see what's visible here, so they may use the bindings it captures (SPEC §4.2).
        var function = new FunctionSymbol(def.Name.Text, parameters, returnType)
        {
            Defaults = BindDefaultValues(def.Params, parameters),
            Kinds = KindsOf(def.Params),
            IsGenerator = Yields(def.Body),
            IsAsync = def.IsAsync,
        };
        CheckGeneratorReturn(function, def.Name);
        var symbol = new VariableSymbol(def.Name.Text, new FunctionType([.. parameters.Select(p => p.Type)], returnType), VariableKind.Function);
        DeclareLocal(def.Name, symbol);
        _localFunctions[symbol] = function;

        var (outerFunction, outerLoopDepth, outerScope) = (_function, _loopDepth, _scope);
        var (outerHandlerDepth, outerFinallyLoopDepth) = (_handlerDepth, _finallyLoopDepth);
        _function = function;
        _loopDepth = 0;
        (_handlerDepth, _finallyLoopDepth) = (0, null);
        _scope = new Scope(outerScope);
        for (var i = 0; i < parameters.Length; i++)
            DeclareLocal(def.Params[i].Name, parameters[i]);
        // It runs later, so it doesn't see narrowed vars or paths (SPEC §3.9).
        var body = InDeferredRegion(() => BindBlock(def.Body));
        if (function.BodyResult != TaipanType.Nothing && !function.IsGenerator && !TypeChecker.Terminates(body))
            _diagnostics.ReportNotAllPathsReturn(def.Name.Span, def.Name.Text);
        CheckGeneratorEnd(function, def.Name, body);
        (_function, _loopDepth, _scope) = (outerFunction, outerLoopDepth, outerScope);
        (_handlerDepth, _finallyLoopDepth) = (outerHandlerDepth, outerFinallyLoopDepth);
        _typeParameters = outerTypeParameters;
        return new BoundLocalFunction(symbol, function, parameters, body, stmt.Span);
    }

    private BoundVariableDecl BindVariableDecl(
        Identifier name, TypeSyntax? annotation, Expr valueSyntax, VariableKind kind, TextSpan span)
    {
        var declared = annotation is null ? null : BindType(annotation);
        var value = declared is null ? BindValue(valueSyntax) : BindExpected(valueSyntax, declared);
        if (declared is null && value.Type is NoneType)
        {
            _diagnostics.ReportCannotInferNone(valueSyntax.Span);
            declared = TaipanType.Error;
        }
        var symbol = new VariableSymbol(name.Text, declared ?? value.Type, kind);
        DeclareLocal(name, symbol);
        // A var starts narrowed to its value's type, if that's narrower than its declared one (SPEC §3.9).
        AfterAssigning(symbol, value, span);
        return new BoundVariableDecl(symbol, value, span);
    }

    /// <summary>
    /// An assignment. <paramref name="value"/>, when given, is the already-bound value to store
    /// in place of <c>assign.Value</c>: an element being unpacked.
    /// </summary>
    private BoundStmt BindAssign(AssignStmt assign, BoundExpr? value = null) => assign.Target switch
    {
        MemberExpr member => BindFieldAssign(assign, member, value),
        IndexExpr index => BindIndexAssign(assign, index, value),
        SliceExpr slice => BindSliceAssign(assign, slice, value),
        NameExpr name => BindVariableAssign(assign, name, value),
        _ => throw new InvalidOperationException($"unexpected assignment target {assign.Target.GetType().Name}"),
    };

    // The value of an assignment whose target is invalid, bound for its own errors.
    private BoundExprStmt OrphanAssign(AssignStmt assign, BoundExpr? value) =>
        new(value ?? BindValue(assign.Value), assign.Span);

    private BoundStmt BindVariableAssign(AssignStmt assign, NameExpr target, BoundExpr? preset)
    {
        var symbol = LookupName(target.Name, target.Span);
        if (symbol is not VariableSymbol variable)
        {
            if (symbol is FunctionSymbol or ClassSymbol or EnumSymbol or VariantSymbol or AmbiguousVariantSymbol or ModuleSymbol)
                _diagnostics.ReportCannotAssign(target.Span, target.Name, "function", null);
            return OrphanAssign(assign, preset);
        }

        // A let can't be rebound, but an in-place method that gives it back may change it (SPEC §4.9, M39).
        if (variable.Kind == VariableKind.Let && assign.Op != AssignOp.Assign && preset is null && variable.Type is ClassType
            && SpecialMethods.InPlace(assign.Op) is { } inPlace && SpecialMethods.Method(variable.Type, inPlace) is var (_, _, inPlaceResult)
            && inPlaceResult == variable.Type)
        {
            var current = new BoundVariable(variable, target.Span);
            if (BindInPlace(current, assign.Op, BindValue(assign.Value), assign.Span) is { } call)
                return new BoundExprStmt(new BoundInPlaceCheck(current, call, OpText(assign.Op), assign.Span), assign.Span);
        }

        if (variable.Kind != VariableKind.Var)
        {
            var (what, help) = variable.Kind switch
            {
                VariableKind.Let => ("let binding", "declare it with 'var'"),
                VariableKind.Parameter => ("parameter", null),
                VariableKind.LoopVariable => ("loop variable", null),
                VariableKind.Function => ("function", null),
                _ => ("constant", (string?)null),
            };
            _diagnostics.ReportCannotAssign(target.Span, target.Name, what, help);
        }

        var value = BindAssignedValue(assign, new BoundVariable(variable, target.Span), variable.Type, preset);
        AfterAssigning(variable, value, assign.Span);
        return new BoundAssign(variable, value, assign.Span);
    }

    private BoundStmt BindFieldAssign(AssignStmt assign, MemberExpr member, BoundExpr? preset)
    {
        if (DotNetTypeNamed(member.Target) is { } namedNet)
            return namedNet is DotNetType owner ? BindDotNetAssign(assign, null, owner, member, preset) : OrphanAssign(assign, preset);
        // A class attribute through its class (SPEC §4.8).
        if (ClassNamed(member.Target) is { } named)
            return BindMemberAssign(assign, member, null, named, named.Name, preset) ?? NotAnAttribute(assign, member, named, preset);
        if (IsCls(member.Target))
            return BindMemberAssign(assign, member, null, _outerFunction!.Owner!, "cls", preset) ?? NotAnAttribute(assign, member, _outerFunction!.Owner!, preset);
        var target = Upcast(BindValue(member.Target));
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
            return OrphanAssign(assign, preset);
        if (target.Type is DotNetType net)
            return BindDotNetAssign(assign, target, net, member, preset);
        if (target.Type == TaipanType.Py)
        {
            var current = new BoundPyGetAttr(target, member.Member.Text, member.Span);
            var pyValue = BindAssignedValue(assign, current, TaipanType.Py, preset);
            return new BoundPySetAttr(target, member.Member.Text, pyValue, assign.Op != AssignOp.Assign, assign.Span);
        }

        if (target.Type is ClassType { Symbol: var classSymbol })
        {
            if (classSymbol.FindField(member.Member.Text) is { } field)
            {
                Index(member.Member.Span, field);
                if (field.IsReadOnly)
                    _diagnostics.ReportCannotAssign(member.Member.Span, field.Name, "read-only property", null);
                // A frozen dataclass's fields are set only by its constructor (SPEC §4.9, M39).
                else if (classSymbol.IsFrozen)
                    _diagnostics.ReportFrozenAssign(member.Member.Span, field.Name, classSymbol.Name);
                var fieldType = FieldType(target.Type, field);
                var value = BindAssignedValue(assign, NarrowedField(new BoundFieldAccess(target, field, fieldType, member.Span)), fieldType, preset);
                EndPathsThrough(field, assign.Span);
                return new BoundFieldAssign(target, field, value, assign.Op != AssignOp.Assign, assign.Span);
            }
            if (BindMemberAssign(assign, member, target, classSymbol, null, preset) is { } memberAssign)
                return memberAssign;
            if (classSymbol.FindMethod(member.Member.Text) is not null || member.Member.Text == "__init__")
                _diagnostics.ReportCannotAssign(member.Member.Span, member.Member.Text, "method", null);
            else
                _diagnostics.ReportNoField(member.Member.Span, target.Type, member.Member.Text);
        }
        else
        {
            _diagnostics.ReportNoField(member.Member.Span, target.Type, member.Member.Text);
        }
        return OrphanAssign(assign, preset);
    }

    private BoundStmt BindIndexAssign(AssignStmt assign, IndexExpr indexExpr, BoundExpr? preset)
    {
        var target = BindValue(indexExpr.Target);
        if (target.Type is TupleOfType or VarTupleOfType)
        {
            _diagnostics.ReportCannotAssignTupleElement(indexExpr.Span);
            return OrphanAssign(assign, preset);
        }
        if (target.Type == TaipanType.Str)
        {
            _diagnostics.ReportCannotAssignStrElement(indexExpr.Span);
            return OrphanAssign(assign, preset);
        }

        // x[k] = v through __setitem__, and x[k] op= v through __getitem__ too (SPEC §4.9, M39).
        if (target.Type is ClassType && SpecialMethods.Method(target.Type, "__setitem__") is { } setter
            && (assign.Op == AssignOp.Assign || SpecialMethods.Method(target.Type, "__getitem__") is not null))
        {
            var (_, keyType, _) = setter;
            var valueType = TypeChecker.Substitute(setter.Method.Parameters[1].Type, TypeChecker.MapOf(target.Type));
            if (assign.Op == AssignOp.Assign)
            {
                var key = BindExpected(indexExpr.Index, keyType!);
                return new BoundClassIndexAssign(target, key, BindAssignedValue(assign, new BoundErrorExpr(indexExpr.Span), valueType, preset), false, assign.Span);
            }
            var current = (BoundMethodCall)BindIndexOf(target, indexExpr);
            var stored = BindAssignedValue(assign, current, valueType, preset);
            return new BoundClassIndexAssign(target, current.Arguments[0], stored, true, assign.Span);
        }

        var read = BindIndexOf(target, indexExpr);
        if (read is BoundDotNetIndex dotnetIndex)
            return BindDotNetIndexAssign(assign, dotnetIndex, preset);
        if (read is BoundPyGetItem pyItem)
        {
            var pyValue = BindAssignedValue(assign, pyItem, TaipanType.Py, preset);
            return new BoundPySetItem(target, pyItem.Index, pyValue, assign.Op != AssignOp.Assign, assign.Span);
        }
        if (read is not BoundIndex index)
            return OrphanAssign(assign, preset);

        var value = BindAssignedValue(assign, index, index.Type, preset);
        return new BoundIndexAssign(target, index.Index, value, assign.Op != AssignOp.Assign, assign.Span);
    }

    /// <summary>
    /// The value stored by an assignment. A compound assignment <c>t op= e</c> binds as
    /// <c>t op e</c>, reading through <paramref name="current"/>.
    /// </summary>
    private BoundExpr BindAssignedValue(AssignStmt assign, BoundExpr current, TaipanType targetType, BoundExpr? preset)
    {
        if (preset is not null)
        {
            CheckAssignable(preset, targetType, preset.Span);
            return targetType == TaipanType.Py && preset.Type != TaipanType.Py && !preset.Type.ContainsError
                ? new BoundToPy(preset, preset.Span)
                : ConvertFunction(preset, targetType);
        }
        if (assign.Op == AssignOp.Assign)
            return BindExpected(assign.Value, targetType);

        var operand = BindValue(assign.Value);
        // A class's in-place method runs first, as in Python (SPEC §4.9, M39).
        BoundExpr value = BindInPlace(current, assign.Op, operand, assign.Span)
            ?? BindBinaryOperation(current, TypeChecker.ToBinaryOp(assign.Op), operand, assign.Span);
        CheckAssignable(value, targetType, assign.Value.Span);
        return value;
    }

    private BoundStmt BindReturn(ReturnStmt ret)
    {
        if (_function is null)
        {
            var orphan = ret.Value is null ? null : BindValue(ret.Value);
            _diagnostics.ReportReturnOutsideFunction(ret.Span);
            return new BoundReturn(orphan, ret.Span);
        }

        if (_finallyLoopDepth is not null)
            _diagnostics.ReportLeavesFinally(new TextSpan(ret.Span.Start, "return".Length), "return");
        else if (LeavesMatchExpression(isReturn: true))
            _diagnostics.ReportLeavesMatchExpression(new TextSpan(ret.Span.Start, "return".Length), "return");
        var returnType = TypeChecker.Substitute(_function.BodyResult, _specialization);
        // In the body, Self is the class, but only Self values are one (SPEC §4.9, M39).
        var returnsSelf = returnType is SelfType or OptionalOf { Inner: SelfType };
        if (_function.Owner is { } owner && TypeChecker.MentionsSelf(returnType))
            returnType = TypeChecker.SubstituteSelf(returnType, owner.Type);
        // A generator's return ends it, with a value only for a Generator[Y, S, R] (SPEC §3.20).
        if (BindGeneratorReturn(ret) is { } generatorReturn)
            return generatorReturn;
        if (ret.Value is null)
        {
            if (returnType != TaipanType.Nothing)
                _diagnostics.ReportMissingReturnValue(ret.Span, _function.Name, returnType);
            return new BoundReturn(null, ret.Span);
        }

        if (returnType == TaipanType.Nothing)
        {
            var value = BindValue(ret.Value);
            _diagnostics.ReportUnexpectedReturnValue(ret.Value.Span, _function.Name);
            return new BoundReturn(value, ret.Span);
        }
        var returned = BindExpected(ret.Value, returnType);
        if (returnsSelf && !returned.Type.ContainsError && returned.Type is not NoneType && !IsSelfValued(returned))
            _diagnostics.ReportExpectedSelf(ret.Value.Span, returned.Type);
        return new BoundReturn(returned, ret.Span);
    }

    private BoundBreak BindBreak(BreakStmt brk)
    {
        if (_loopDepth == 0)
            _diagnostics.ReportBreakOutsideLoop(brk.Span);
        else if (_finallyLoopDepth == _loopDepth)
            _diagnostics.ReportLeavesFinally(brk.Span, "break");
        else if (LeavesMatchExpression(isReturn: false))
            _diagnostics.ReportLeavesMatchExpression(brk.Span, "break");
        return new BoundBreak(brk.Span);
    }

    private BoundContinue BindContinue(ContinueStmt cont)
    {
        if (_loopDepth == 0)
            _diagnostics.ReportContinueOutsideLoop(cont.Span);
        else if (_finallyLoopDepth == _loopDepth)
            _diagnostics.ReportLeavesFinally(cont.Span, "continue");
        else if (LeavesMatchExpression(isReturn: false))
            _diagnostics.ReportLeavesMatchExpression(cont.Span, "continue");
        return new BoundContinue(cont.Span);
    }

    // ---- Exceptions (SPEC §3.10) ----

    private BoundRaise BindRaise(RaiseStmt raise)
    {
        if (raise.Value is not { } syntax)
        {
            if (_handlerDepth == 0)
                _diagnostics.ReportBareRaiseOutsideHandler(raise.Span);
            return new BoundRaise(null, raise.Span);
        }

        // Python lets a class be raised; Taipan needs an instance.
        if (NamedClass(syntax) is { } named)
        {
            _diagnostics.ReportCannotRaise(syntax.Span, named.Name, named.Name);
            return new BoundRaise(new BoundErrorExpr(syntax.Span), raise.Span);
        }
        var value = BindValue(syntax);
        var cause = raise.Cause is null ? null : BindCause(raise.Cause);
        if (value.Type.ContainsError)
            return new BoundRaise(value, raise.Span) { Cause = cause };
        // A .NET exception is thrown as it is, and has no cause (SPEC §13.6, M43).
        if (value.Type is DotNetType net)
        {
            if (!IsDotNetException(net.Symbol))
                _diagnostics.ReportNotExceptionClass(syntax.Span, net.ToString());
            else if (raise.Cause is { } causeSyntax && cause is { Type.ContainsError: false } and not { Type: DotNetType })
                _diagnostics.ReportDotNetCause(causeSyntax.Span, isCause: false);
            return new BoundRaise(value, raise.Span) { Cause = cause };
        }
        if (TypeChecker.Unwrap(value.Type) is ClassType { Symbol: var symbol } && BuiltinClasses.IsException(symbol))
            CheckAssignable(value, BuiltinClasses.BaseException.Type, syntax.Span, TypeChecker.Unwrap(value.Type).ToString());
        else
            _diagnostics.ReportCannotRaise(syntax.Span, value.Type);
        return new BoundRaise(value, raise.Span) { Cause = cause };
    }

    /// <summary><c>from c</c>'s cause (SPEC §3.19): an exception, an optional of one, or <c>None</c>.</summary>
    private BoundExpr BindCause(Expr syntax)
    {
        if (NamedClass(syntax) is { } named)
        {
            _diagnostics.ReportBadCause(syntax.Span, named.Name, named.Name);
            return new BoundErrorExpr(syntax.Span);
        }
        var cause = BindValue(syntax);
        if (cause.Type is DotNetType net && IsDotNetException(net.Symbol))
        {
            _diagnostics.ReportDotNetCause(syntax.Span, isCause: true);
            return new BoundErrorExpr(syntax.Span);
        }
        var isException = cause.Type is NoneType
            || TypeChecker.Unwrap(cause.Type) is ClassType { Symbol: var symbol } && BuiltinClasses.IsException(symbol);
        if (!cause.Type.ContainsError && !isException)
            _diagnostics.ReportBadCause(syntax.Span, cause.Type);
        return cause;
    }

    /// <summary>The class a name or <c>module.Name</c> refers to, if it's a class rather than a value.</summary>
    private ClassSymbol? NamedClass(Expr expr) => expr switch
    {
        NameExpr name when !_typeParameters.ContainsKey(name.Name) => _scope.Lookup(name.Name) as ClassSymbol,
        MemberExpr { Target: NameExpr module } member when _scope.Lookup(module.Name) is ModuleSymbol symbol
            => symbol.Classes.GetValueOrDefault(member.Member.Text),
        _ => null,
    };

    // Each block can be left at any point, so they and the code after see what held before the try,
    // less what any of them can end (SPEC §3.9).
    private BoundTry BindTry(TryStmt tryStmt)
    {
        return Repeatable(() => BindTryBlocks(tryStmt),
            t => new BoundStmt?[] { t.Body, t.Else, t.Finally }.Concat(t.Handlers.Select(h => h.Body)).OfType<BoundStmt>());
    }

    private BoundTry BindTryBlocks(TryStmt tryStmt)
    {
        var start = _narrowing;
        var body = BindBlock(tryStmt.Body);
        _narrowing = start;

        // The classes earlier clauses catch, for clauses that can never match (TP0300).
        var caught = new List<ClassSymbol>();
        var caughtDotNet = new List<Microsoft.CodeAnalysis.INamedTypeSymbol>();
        var handlers = tryStmt.Handlers.Select(clause => { _narrowing = start; return BindHandler(clause, caught, caughtDotNet); }).ToImmutableArray();
        _narrowing = start;
        var elseBlock = tryStmt.Else is null ? null : BindBlock(tryStmt.Else);
        _narrowing = start;

        BoundBlock? finallyBlock = null;
        if (tryStmt.Finally is not null)
        {
            var outer = _finallyLoopDepth;
            _finallyLoopDepth = _loopDepth;
            finallyBlock = BindBlock(tryStmt.Finally);
            _finallyLoopDepth = outer;
        }
        _narrowing = start;
        return new BoundTry(body, handlers, elseBlock, finallyBlock, tryStmt.Span);
    }

    private BoundHandler BindHandler(ExceptClause clause, List<ClassSymbol> caught, List<Microsoft.CodeAnalysis.INamedTypeSymbol> caughtDotNet)
    {
        var classes = new List<ClassSymbol>();
        var dotnetClasses = new List<Microsoft.CodeAnalysis.INamedTypeSymbol>();
        var pythonClasses = new List<BoundExpr>();
        var valid = true;
        foreach (var type in clause.Types)
        {
            switch (BindHandlerType(type))
            {
                case ClassSymbol symbol:
                    classes.Add(symbol);
                    break;
                case BoundExpr pythonClass:
                    pythonClasses.Add(pythonClass);
                    break;
                case DotNetType dotnetClass:
                    dotnetClasses.Add(dotnetClass.Symbol);
                    break;
                default:
                    valid = false;
                    break;
            }
        }
        if (clause.Types.IsEmpty)
            classes.Add(BuiltinClasses.Exception);

        // A clause whose classes earlier clauses all catch can never match.
        var reachable = true;
        if (valid && pythonClasses.Count == 0 && classes.All(c => caught.Any(c.DerivesFrom))
            && dotnetClasses.All(c => CaughtBy(c, caught, caughtDotNet) is not null))
        {
            reachable = false;
            var caughtBy = classes.Count > 0 ? caught.First(classes[0].DerivesFrom).Name : CaughtBy(dotnetClasses[0], caught, caughtDotNet)!;
            _diagnostics.ReportUnreachableHandler(clause.TypesSpan, caughtBy);
        }
        caught.AddRange(classes);
        caughtDotNet.AddRange(dotnetClasses);

        // A Python class matches a PythonError; several classes bind their nearest common base.
        // .NET classes bind theirs, and can't be bound with Taipan classes (SPEC §13.6).
        IEnumerable<ClassSymbol> bound = pythonClasses.Count > 0 ? [.. classes, BuiltinClasses.PythonError] : classes;
        TaipanType bindingType;
        if (!valid)
        {
            bindingType = TaipanType.Error;
        }
        else if (dotnetClasses.Count == 0)
        {
            bindingType = bound.Aggregate((a, b) => a.CommonBase(b) ?? BuiltinClasses.BaseException).Type;
        }
        else if (!bound.Any())
        {
            bindingType = new DotNetType(dotnetClasses.Aggregate(DotNetCommonBase));
        }
        else
        {
            if (clause.Binding is not null)
                _diagnostics.ReportMixedExceptionBinding(clause.TypesSpan);
            bindingType = TaipanType.Error;
        }

        var outer = _scope;
        _scope = new Scope(outer);
        VariableSymbol? binding = null;
        if (clause.Binding is { } name)
        {
            binding = new VariableSymbol(name.Text, bindingType, VariableKind.Let);
            DeclareLocal(name, binding);
        }
        _handlerDepth++;
        var body = BindBlock(clause.Body);
        _handlerDepth--;
        _scope = outer;

        // Catching everything is deliberate when the block re-raises or uses the exception (TP0301).
        var broad = classes.Contains(BuiltinClasses.Exception) || classes.Contains(BuiltinClasses.BaseException);
        if (broad && !Raises(body) && !(binding is not null && Uses(body, binding)))
            _diagnostics.ReportBroadHandler(new TextSpan(clause.Span.Start, "except".Length));

        return new BoundHandler([.. classes], [.. dotnetClasses.Select(c => new DotNetType(c))], [.. pythonClasses], binding, body, reachable);
    }

    /// <summary>
    /// An <c>except</c> clause's type: an exception class, a <c>py</c> value for a Python class, or
    /// null after reporting anything else.
    /// </summary>
    private object? BindHandlerType(Expr type)
    {
        var display = type switch
        {
            NameExpr name => name.Name,
            MemberExpr member => member.Member.Text,
            _ => "",
        };
        if (type is NameExpr { Name: var typeParameter } && _typeParameters.ContainsKey(typeParameter))
        {
            _diagnostics.ReportNotExceptionClass(type.Span, display);
            return null;
        }
        if (NamedAlias(type) is { } alias)
        {
            _diagnostics.ReportAliasAsValue(type.Span, display, alias.TargetName);
            return null;
        }
        if (DotNetTypeNamed(type) is { } dotnetClass)
        {
            if (dotnetClass is DotNetType { IsGeneric: false } net && IsDotNetException(net.Symbol))
                return net;
            _diagnostics.ReportNotExceptionClass(type.Span, display);
            return null;
        }
        if (NamedClass(type) is { } symbol)
        {
            if (symbol.IsProtocol)
            {
                _diagnostics.ReportProtocolAsClass(type.Span, symbol.Name, constructing: false);
                return null;
            }
            if (BuiltinClasses.IsException(symbol))
                return symbol;
            _diagnostics.ReportNotExceptionClass(type.Span, display);
            return null;
        }
        if (type is MemberExpr { Target: NameExpr module } qualified && _scope.Lookup(module.Name) is ModuleSymbol moduleSymbol)
        {
            if (moduleSymbol.Enums.ContainsKey(qualified.Member.Text))
                _diagnostics.ReportNotExceptionClass(type.Span, display);
            else
                _diagnostics.ReportNoDefinition(qualified.Member.Span, moduleSymbol.Name, qualified.Member.Text);
            return null;
        }
        // Only a variable can hold a Python class; a function, enum or other name can't.
        if (type is NameExpr { Name: var other } && _scope.Lookup(other) is not (null or VariableSymbol))
        {
            _diagnostics.ReportNotExceptionClass(type.Span, display);
            return null;
        }

        var value = BindValue(type);
        if (value.Type.ContainsError)
            return null;
        if (value.Type == TaipanType.Py)
            return value;
        _diagnostics.ReportNotExceptionClass(type.Span, display);
        return null;
    }

    /// <summary>Whether a block contains a <c>raise</c>, outside any nested function.</summary>
    private static bool Raises(BoundStmt stmt) =>
        stmt is BoundRaise || BoundTree.Expressions(stmt).Any(RaisesIn) || (stmt is not BoundLocalFunction && BoundTree.Statements(stmt).Any(Raises));

    /// <summary>Whether an expression contains a match expression with a raising arm (SPEC §5.1.1).</summary>
    private static bool RaisesIn(BoundExpr expr) =>
        expr is BoundMatchExpr match && match.Arms.Any(a => a.Raise is not null) || BoundTree.Children(expr).Any(RaisesIn);

    /// <summary>Whether a statement reads <paramref name="variable"/> anywhere, nested functions and lambdas included.</summary>
    private static bool Uses(BoundStmt stmt, VariableSymbol variable)
    {
        bool InExpr(BoundExpr expr) =>
            expr is BoundVariable { Symbol: var symbol } && symbol == variable
            || BoundTree.Children(expr).Any(InExpr);
        return BoundTree.Expressions(stmt).Any(InExpr) || BoundTree.Statements(stmt).Any(s => Uses(s, variable));
    }

    /// <summary>Each later clause sees what holds when the earlier conditions are false (SPEC §3.9).</summary>
    // After an if, the rest of the block sees what holds at the end of every branch that can
    // finish, a missing else being one where every condition was false (SPEC §3.9).
    private BoundIf BindIf(IfStmt ifStmt)
    {
        var clauses = ImmutableArray.CreateBuilder<BoundIfClause>();
        var ends = new List<Narrowing>();
        foreach (var clause in ifStmt.Clauses)
        {
            var (condition, body, afterCondition) = BindGuarded(clause.Condition, clause.Body, isLoop: false);
            clauses.Add(new BoundIfClause(condition, body));
            if (!Exits(body))
                ends.Add(_narrowing);
            _narrowing = afterCondition.With(Facts(condition, whenTrue: false));
        }
        BoundBlock? elseBlock = null;
        if (ifStmt.Else is not null)
        {
            elseBlock = BindBlock(ifStmt.Else);
            if (!Exits(elseBlock))
                ends.Add(_narrowing);
        }
        else
        {
            ends.Add(_narrowing);
        }
        if (ends.Count > 0)
            _narrowing = Join(ends);
        return new BoundIf(clauses.ToImmutable(), elseBlock, ifStmt.Span);
    }

    // The body runs again, so it starts from what it can't end; after a loop with no break, the
    // condition is false (SPEC §3.9).
    private BoundWhile BindWhile(WhileStmt whileStmt)
    {
        var (condition, body, afterCondition) = Repeatable(
            () => BindGuarded(whileStmt.Condition, whileStmt.Body, isLoop: true),
            bound => [new BoundExprStmt(bound.Condition, bound.Condition.Span), bound.Body]);
        _narrowing = Breaks(body) ? afterCondition : afterCondition.With(Facts(condition, whenTrue: false));
        return new BoundWhile(condition, body, whileStmt.Span);
    }

    /// <summary>
    /// Binds a condition and the block it guards. For <c>x is some v</c>, <c>v</c> is declared in a
    /// scope around the block, so it's visible in that branch only (SPEC §3.4).
    /// </summary>
    private (BoundExpr Condition, BoundBlock Body, Narrowing AfterCondition) BindGuarded(Condition condition, Block body, bool isLoop)
    {
        BoundExpr bound;
        VariableSymbol? binding = null;
        switch (condition)
        {
            case IsSomeCondition isSome:
                var someOperand = Declared(BindValue(isSome.Expr));
                var inner = RequireOptional(someOperand, isSome.Expr.Span, "is some");
                binding = new VariableSymbol(isSome.Binding.Text, inner, VariableKind.Let);
                bound = new BoundIsSome(someOperand, binding, isSome.Span);
                break;
            default:
                bound = BindCondition((ExprCondition)condition);
                break;
        }

        var outer = _scope;
        if (binding is not null)
        {
            _scope = new Scope(outer);
            DeclareLocal(((IsSomeCondition)condition).Binding, binding);
        }
        if (isLoop)
            _loopDepth++;
        // The block's end state is left in _narrowing, for an if to join (SPEC §3.9).
        var afterCondition = _narrowing;
        _narrowing = _narrowing.With(Facts(bound, whenTrue: true));
        var block = BindBlock(body);
        if (isLoop)
            _loopDepth--;
        _scope = outer;
        return (bound, block, afterCondition);
    }

    /// <summary>The inner type of an optional operand of <c>is some</c> / <c>is None</c>, else TP0200.</summary>
    private TaipanType RequireOptional(BoundExpr operand, TextSpan span, string construct)
    {
        if (operand.Type is OptionalOf optional)
            return optional.Inner;
        if (!operand.Type.ContainsError)
            _diagnostics.ReportNeedsOptional(span, construct, operand.Type);
        return TaipanType.Error;
    }

    private BoundStmt BindFor(ForStmt forStmt)
    {
        BoundExpr? start = null, step = null;
        BoundExpr iterable;
        TaipanType variableType;
        var isRange = !forStmt.IsAsync && forStmt.Iterable is CallExpr { Callee: NameExpr { Name: "range" } }
            && _scope.Lookup("range") is FunctionSymbol { Builtin: BuiltinFunction.Range };
        if (forStmt.IsAsync)
        {
            CheckAsyncContext(new TextSpan(forStmt.Span.Start, "async".Length), "async for");
            (iterable, variableType) = BindAsyncIterable(forStmt.Iterable);
        }
        else if (isRange)
        {
            (start, iterable, step) = BindRangeArguments((CallExpr)forStmt.Iterable);
            variableType = TaipanType.Int;
        }
        else
        {
            (iterable, variableType) = BindLoopIterable(forStmt.Iterable);
        }

        // The loop variable is a let scoped to the body (SPEC §5.2). With several names, the
        // element is held in a temporary and unpacked into them at the start of the body.
        var outer = _scope;
        _scope = new Scope(outer);
        VariableSymbol variable;
        var unpacked = ImmutableArray<BoundStmt>.Empty;
        if (forStmt.Variables is [UnpackedName { Starred: false } single])
        {
            variable = new VariableSymbol(single.Name.Text, variableType, VariableKind.LoopVariable);
            DeclareLocal(single.Name, variable);
        }
        else
        {
            variable = UnpackTemp(variableType);
            unpacked = [Unpack(new BoundVariable(variable, forStmt.Iterable.Span), forStmt.Iterable.Span, forStmt.Variables,
                (item, element) => DeclareUnpacked(item, element, VariableKind.LoopVariable), forStmt.Span)];
        }
        _loopDepth++;
        // The body runs again, and maybe not at all, so it and the code after it see what it can't end (SPEC §3.9).
        var bodyStart = _narrowing;
        var statements = Repeatable(() => { bodyStart = _narrowing; return BindStatements(forStmt.Body.Statements); }, bound => bound);
        _narrowing = bodyStart;
        _loopDepth--;
        _scope = outer;

        var body = new BoundBlock([.. unpacked, .. statements], forStmt.Body.Span);
        return isRange
            ? new BoundForRange(variable, start, iterable, body, forStmt.Span) { Step = step }
            : new BoundForEach(variable, iterable, body, forStmt.Span) { IsAsync = forStmt.IsAsync };
    }

    /// <summary>What an <c>async for</c> goes over (SPEC §3.20): an async iterator, which a class's <c>__aiter__</c> or <c>__anext__</c> gives.</summary>
    private (BoundExpr Iterable, TaipanType Element) BindAsyncIterable(Expr syntax)
    {
        var iterable = BindValue(syntax);
        if (iterable.Type.ContainsError || ReportIfOptional(iterable, syntax.Span))
            return (iterable, TaipanType.Error);
        if (TypeChecker.AsyncElementOf(iterable.Type) is not { } element)
        {
            _diagnostics.ReportCannotAsyncIterate(syntax.Span, iterable.Type, isPlain: TypeChecker.ElementOf(iterable.Type) is not null || iterable.Type == TaipanType.Py);
            return (iterable, TaipanType.Error);
        }
        return (Convert(iterable, new AsyncIterableOfType(element)), element);
    }

    private TaipanType CannotIterate(TextSpan span, TaipanType type)
    {
        _diagnostics.ReportCannotIterate(span, type);
        return TaipanType.Error;
    }

    /// <summary>What a <c>for</c> loop, or a comprehension's <c>for</c>, goes over, and the type of each value (SPEC §5.2).</summary>
    private (BoundExpr Iterable, TaipanType Element) BindLoopIterable(Expr syntax)
    {
        var iterable = BindValue(syntax);
        // A class iterates through its __iter__ or __next__ (SPEC §3.13), and a generator as an iterator (§3.20).
        if (iterable.Type is ClassType or GeneratorOfType && TypeChecker.ElementOf(iterable.Type) is { } classElement)
            iterable = Convert(iterable, new IterableOfType(classElement));
        var element = iterable.Type switch
        {
            ListOfType list => list.Element,
            VarTupleOfType varTuple => varTuple.Element,
            SetOfType set => set.Element,
            DictOfType dict => dict.Key,
            PrimitiveType { Name: "str" } => TaipanType.Str,
            PrimitiveType { Name: "py" } => TaipanType.Py,
            IteratorOfType iterator => iterator.Element,
            IterableOfType iterated => iterated.Element,
            // A file gives its lines (SPEC §6.1).
            OpaqueType { Name: "TextIO" } => TaipanType.Str,
            // A .NET IEnumerable<T> (SPEC §13.13, M43).
            DotNetType net when DotNetTypes.EnumerableElement(net) is { } enumerable => enumerable.Element,
            { ContainsError: true } => TaipanType.Error,
            OptionalOf when ReportIfOptional(iterable, syntax.Span) => TaipanType.Error,
            _ => CannotIterate(syntax.Span, iterable.Type),
        };
        return (iterable, element);
    }

    private (BoundExpr? Start, BoundExpr Stop, BoundExpr? Step) BindRangeArguments(CallExpr call)
    {
        ReportKeywordArguments(call, "range");
        var arguments = call.Arguments.Select(a => BindExpected(a.Value, TaipanType.Int)).ToImmutableArray();
        switch (arguments.Length)
        {
            case 1:
                return (null, arguments[0], null);
            case 2:
                return (arguments[0], arguments[1], null);
            // A step since M35 (SPEC §6).
            case 3:
                return (arguments[0], arguments[1], arguments[2]);
            default:
                _diagnostics.ReportWrongArgumentCount(call.Callee.Span, "range", "1 to 3 arguments", arguments.Length);
                return (null, new BoundErrorExpr(call.Span), null);
        }
    }

    private BoundExpr BindCondition(ExprCondition condition)
    {
        var value = BindValue(condition.Expr);
        if (value.Type is OptionalOf { Inner: PrimitiveType { Name: "bool" } })
            _diagnostics.ReportOptionalNotNarrowed(condition.Expr.Span, TaipanType.Bool, value.Type);
        else if (!value.Type.ContainsError && value.Type != TaipanType.Bool)
            _diagnostics.ReportConditionNotBool(condition.Expr.Span, value.Type);
        return value;
    }

    // ---- Expressions ----

    /// <summary>Binds against an expected type (SPEC §4.6) and checks the result is assignable to it.</summary>
    private BoundExpr BindExpected(Expr expr, TaipanType expected)
    {
        var value = BindValue(expr, expected);
        CheckAssignable(value, expected, expr.Span);
        return Convert(value, expected);
    }

    /// <summary>
    /// A value checked against <paramref name="expected"/>, converted for it: to Python where a py
    /// is expected (SPEC §10.3), or to another function type (SPEC §3.3).
    /// </summary>
    private static BoundExpr Convert(BoundExpr value, TaipanType expected)
    {
        // An object with __call__ where a function is expected (SPEC §4.9, M39).
        if (TypeChecker.Unwrap(expected) is FunctionType && value.Type is ClassType && TypeChecker.IsAssignable(value.Type, expected))
            value = AsFunctionValue(value);
        if (expected == TaipanType.Py && value.Type != TaipanType.Py && !value.Type.ContainsError && value.Type.IsConvertibleToPython)
            return new BoundToPy(value, value.Span);
        // One protocol type as another (SPEC §3.18).
        if (TypeChecker.Unwrap(value.Type) is ClassType { Symbol.IsProtocol: true } fromProtocol
            && TypeChecker.Unwrap(expected) is ClassType { Symbol.IsProtocol: true } toProtocol
            && fromProtocol != toProtocol && TypeChecker.IsAssignable(value.Type, expected))
            return new BoundProtocolCast(value, expected, value.Span);
        // A list, dict, str or class used as an iterable or iterator is wrapped (SPEC §3.13, §8).
        if (TypeChecker.Unwrap(expected) is IterableOfType or IteratorOfType && value.Type is not (IterableOfType or IteratorOfType or NoneType or OptionalOf or OpaqueType)
            && !value.Type.ContainsError && TypeChecker.IsAssignable(value.Type, expected))
            return new BoundToIterable(value, TypeChecker.Unwrap(expected), value.Span);
        // So is an async generator or class used as an async iterable or iterator (SPEC §3.20).
        if (TypeChecker.Unwrap(expected) is AsyncIterableOfType or AsyncIteratorOfType && value.Type is not (AsyncIterableOfType or AsyncIteratorOfType or NoneType or OptionalOf)
            && !value.Type.ContainsError && TypeChecker.IsAssignable(value.Type, expected))
            return new BoundToIterable(value, TypeChecker.Unwrap(expected), value.Span);
        return ConvertFunction(value, expected);
    }

    /// <summary>A function value stored where another function type is expected is converted to it (SPEC §3.3).</summary>
    private static BoundExpr ConvertFunction(BoundExpr value, TaipanType target) =>
        value.Type is FunctionType from && TypeChecker.Unwrap(target) is FunctionType to && from != to
        && !from.ContainsError && !to.ContainsError && TypeChecker.IsAssignable(from, to)
            ? new BoundConvertFunction(value, to, value.Span)
            : value;

    /// <summary>
    /// A value passed into a py operation: it must be convertible to Python (SPEC §10.3). py is its
    /// expected type, so list and dict literals here may mix element types.
    /// </summary>
    private BoundExpr BindPyArgument(Expr expr)
    {
        var value = BindValue(expr, TaipanType.Py);
        if (!value.Type.IsConvertibleToPython)
            _diagnostics.ReportCannotPassToPython(expr.Span, value.Type);
        return value;
    }

    /// <summary>A call to a py value: positional then keyword arguments, all converted (SPEC §10.2).</summary>
    private BoundPyCall BindPyCall(CallExpr call, BoundExpr callee)
    {
        var positional = call.Arguments.Where(a => a.Name is null).Select(a => BindPyArgument(a.Value)).ToImmutableArray();
        var keywords = call.Arguments.Where(a => a.Name is not null).Select(a => (a.Name!.Text, BindPyArgument(a.Value))).ToImmutableArray();
        return new BoundPyCall(callee, positional, keywords, call.Span);
    }

    // `x as T` (SPEC §10.4): x must be py, and T a type Python values can convert to; or both are
    // classes, one deriving from the other (§4.3).
    private BoundExpr BindCast(CastExpr cast)
    {
        var value = BindValue(cast.Value);
        var target = BindType(cast.Type);
        // Between .NET types, and Taipan classes with .NET bases (SPEC §13.17, M43).
        if (TypeChecker.Unwrap(value.Type) is DotNetType or ClassType && TypeChecker.Unwrap(target) is DotNetType or ClassType
            && (TypeChecker.Unwrap(value.Type) is DotNetType || TypeChecker.Unwrap(target) is DotNetType))
        {
            return CheckDotNetRelated(TypeChecker.Unwrap(value.Type), TypeChecker.Unwrap(target), cast.Type.Span)
                ? new BoundClassCast(value, target, cast.Span)
                : new BoundErrorExpr(cast.Span);
        }
        if (TypeChecker.Unwrap(value.Type) is ClassType from && TypeChecker.Unwrap(target) is ClassType to)
        {
            return CheckRelated(to, from, cast.Type.Span) is null
                ? new BoundErrorExpr(cast.Span)
                : new BoundClassCast(value, target, cast.Span);
        }
        // An object converts to any type, checked at run time (SPEC §3.20).
        if (value.Type is ObjectType && !target.ContainsError)
            return new BoundCast(value, target, cast.Span);
        if (value.Type != TaipanType.Py && !value.Type.ContainsError)
        {
            _diagnostics.ReportCastNeedsPy(cast.Value.Span, value.Type);
            return new BoundErrorExpr(cast.Span);
        }
        if (!target.IsCastTarget)
        {
            _diagnostics.ReportCannotCastTo(cast.Type.Span, target);
            return new BoundErrorExpr(cast.Span);
        }
        return new BoundCast(value, target, cast.Span);
    }

    // ---- Tuples and slices (SPEC §3.8, §5.4) ----

    /// <summary>A tuple literal, checked element by element against an expected tuple type of its length.</summary>
    private BoundExpr BindTuple(TupleExpr tuple, TaipanType? expected)
    {
        if (tuple.Elements.Length < 2)
        {
            _diagnostics.ReportTupleTooShort(tuple.Span);
            BindArguments(tuple.Elements);
            return new BoundErrorExpr(tuple.Span);
        }
        if (expected is not null && TypeChecker.Unwrap(expected) is TupleOfType target && target.Elements.Length == tuple.Elements.Length)
            return new BoundTupleLiteral([.. tuple.Elements.Select((e, i) => BindExpected(e, target.Elements[i]))], target, tuple.Span);
        // A literal checked against tuple[T, ...] builds one (SPEC §3.19).
        if (expected is not null && TypeChecker.Unwrap(expected) is VarTupleOfType varTarget)
            return new BoundVarTupleLiteral([.. tuple.Elements.Select(e => BindExpected(e, varTarget.Element))], varTarget, tuple.Span);

        var elements = tuple.Elements.Select(e => BindValue(e)).ToImmutableArray();
        var types = ImmutableArray.CreateBuilder<TaipanType>();
        foreach (var element in elements)
        {
            if (element.Type is NoneType)
            {
                _diagnostics.ReportCannotInferNone(element.Span);
                types.Add(TaipanType.Error);
            }
            else
            {
                types.Add(element.Type);
            }
        }
        return new BoundTupleLiteral(elements, new TupleOfType(types.ToImmutable()), tuple.Span);
    }

    /// <summary>
    /// <c>t[i]</c> on a tuple: <c>i</c> must be an integer literal, optionally negative, within the
    /// tuple's length, so the element's type is known.
    /// </summary>
    private BoundExpr BindTupleElement(BoundExpr target, TupleOfType tuple, IndexExpr indexExpr)
    {
        BigInteger? literal = indexExpr.Index switch
        {
            IntLiteral l => l.Value,
            UnaryExpr { Op: UnaryOp.Negate, Operand: IntLiteral l } => -l.Value,
            _ => null,
        };
        if (literal is not { } index)
        {
            BindValue(indexExpr.Index);
            _diagnostics.ReportTupleIndexNotLiteral(indexExpr.Index.Span);
            return new BoundErrorExpr(indexExpr.Span);
        }
        var length = tuple.Elements.Length;
        var normalized = index < 0 ? index + length : index;
        if (normalized < 0 || normalized >= length)
        {
            _diagnostics.ReportTupleIndexOutOfRange(indexExpr.Index.Span, index, tuple);
            return new BoundErrorExpr(indexExpr.Span);
        }
        return NarrowedElement(new BoundTupleElement(target, (int)normalized, tuple.Elements[(int)normalized], indexExpr.Span));
    }

    /// <summary>
    /// <c>xs[a:b:c]</c> on a list or str, each part an int or int?; on a py value, any convertible
    /// parts (SPEC §5.4).
    /// </summary>
    private BoundExpr BindSlice(SliceExpr slice)
    {
        var target = BindValue(slice.Target);
        var isPy = target.Type == TaipanType.Py;
        BoundExpr? Part(Expr? part) => part is null ? null : isPy ? BindPyArgument(part) : BindExpected(part, new OptionalOf(TaipanType.Int));
        var (start, stop, step) = (Part(slice.Start), Part(slice.Stop), Part(slice.Step));

        var type = target.Type;
        if (type is not (ListOfType or VarTupleOfType or PrimitiveType { Name: "str" or "py" }) && !type.ContainsError)
        {
            if (type is IteratorOfType or IterableOfType)
                _diagnostics.ReportNotIterableFromList(slice.Target.Span, $"cannot slice {type}");
            else if (!ReportIfOptional(target, slice.Target.Span))
                _diagnostics.ReportCannotSlice(slice.Target.Span, type);
            type = TaipanType.Error;
        }
        return type.ContainsError
            ? new BoundErrorExpr(slice.Span)
            : new BoundSlice(target, start, stop, step, type, slice.Span);
    }

    // ---- Lambdas (SPEC §3.7) ----

    /// <summary>
    /// A lambda takes its parameter types from a function type expected for it. Without an
    /// expected type, only one without parameters can be typed, from its body.
    /// </summary>
    private BoundExpr BindLambda(LambdaExpr lambda, TaipanType? expected)
    {
        var target = expected is null ? null : TypeChecker.Unwrap(expected);
        if (target is FunctionType function)
            return BindLambdaAs(lambda, function.Parameters, function.Result);
        // Its body may return nothing, which makes it a Callable[[], none].
        if (target is null && lambda.Parameters.IsEmpty)
            return BindLambdaAs(lambda, [], result: null, resultMayBeNothing: true);

        if (target is null)
            _diagnostics.ReportCannotInferLambdaParameter(lambda.Parameters[0].Span, lambda.Parameters[0].Text);
        else if (!target.ContainsError)
            _diagnostics.ReportTypeMismatch(lambda.Span, expected!, "lambda");
        // The body is still checked, for its own errors.
        BindLambdaAs(lambda, [.. lambda.Parameters.Select(_ => TaipanType.Error)], TaipanType.Error);
        return new BoundErrorExpr(lambda.Span);
    }

    /// <summary>
    /// A lambda with the given parameter types. The body is checked against
    /// <paramref name="result"/>, discarded for <c>none</c>, or gives the result type when it's null.
    /// An inferred result must be a value unless <paramref name="resultMayBeNothing"/>: a body that
    /// returns nothing can't determine a type parameter (SPEC §3.7).
    /// </summary>
    private BoundExpr BindLambdaAs(LambdaExpr lambda, ImmutableArray<TaipanType> parameterTypes, TaipanType? result, bool resultMayBeNothing = false)
    {
        var valid = true;
        if (parameterTypes.Length != lambda.Parameters.Length)
        {
            _diagnostics.ReportLambdaParameterCount(new TextSpan(lambda.Span.Start, "lambda".Length), parameterTypes.Length, lambda.Parameters.Length);
            (parameterTypes, result, valid) = ([.. lambda.Parameters.Select(_ => TaipanType.Error)], TaipanType.Error, false);
        }

        var outer = _scope;
        _scope = new Scope(outer);
        var parameters = ImmutableArray.CreateBuilder<VariableSymbol>();
        foreach (var (name, type) in lambda.Parameters.Zip(parameterTypes))
        {
            var symbol = new VariableSymbol(name.Text, type, VariableKind.Parameter);
            if (name.Text != "_")
                DeclareLocal(name, symbol);
            parameters.Add(symbol);
        }

        // A lambda runs later, so it doesn't see narrowed vars or paths (SPEC §3.9).
        var expectedResult = result;
        _lambdaDepth++;
        var body = InDeferredRegion(() =>
            // An error result (an undefined type, say) was already reported, so any body fits it.
            expectedResult is NothingType or ErrorType ? BindExpr(lambda.Body)
            : expectedResult is not null ? BindExpected(lambda.Body, expectedResult)
            : resultMayBeNothing ? BindExpr(lambda.Body) : BindValue(lambda.Body));
        _lambdaDepth--;
        if (result is null)
        {
            result = body.Type;
            if (result is NoneType)
            {
                _diagnostics.ReportCannotInferNone(lambda.Body.Span);
                result = TaipanType.Error;
            }
        }
        _scope = outer;
        return valid
            ? new BoundLambda(parameters.ToImmutable(), body, new FunctionType(parameterTypes, result), lambda.Span)
            : new BoundErrorExpr(lambda.Span);
    }

    /// <summary>
    /// A function argument of a built-in (SPEC §6), whose parameter types are known. A lambda's
    /// result is checked against <paramref name="required"/> or <paramref name="hint"/>, or
    /// inferred; another function's must equal <paramref name="required"/> if given. Returns the
    /// argument and its result type. An inferred result is named <paramref name="resultName"/> in
    /// messages, as in the built-in's signature.
    /// </summary>
    private (BoundExpr Argument, TaipanType Result) BindCallback(
        Expr expr, ImmutableArray<TaipanType> parameters, TaipanType? required, TaipanType? hint, string resultName)
    {
        if (expr is LambdaExpr lambda)
        {
            var bound = BindLambdaAs(lambda, parameters, required ?? hint);
            return (bound, bound is BoundLambda { FunctionType.Result: var lambdaResult } ? lambdaResult : TaipanType.Error);
        }

        // A generic function, built-in or constructor takes the parameters it's given (SPEC §3.18).
        var value = CallableValueTarget(expr) is { } callable
            ? BindCallableAs(expr, callable, expr.Span, parameters, required)
            : AsFunctionValue(BindValue(expr));
        if (value.Type.ContainsError)
            return (value, TaipanType.Error);
        if (value.Type is FunctionType function && function.Parameters.SequenceEqual(parameters)
            && function.Result is not NothingType && (required is null || function.Result == required))
            return (value, function.Result);
        var expected = required is null ? $"Callable[[{string.Join(", ", parameters)}], {resultName}]" : new FunctionType(parameters, required).ToString();
        _diagnostics.ReportTypeMismatch(expr.Span, expected, value.Type);
        return (value, TaipanType.Error);
    }

    /// <summary>Binds an expression whose value is used: a call without a return type is TP0200.</summary>
    private BoundExpr BindValue(Expr expr, TaipanType? expected = null)
    {
        var bound = BindExpr(expr, expected);
        if (bound.Type is NothingType)
        {
            var name = expr switch
            {
                CallExpr { Callee: NameExpr callee } => callee.Name,
                CallExpr { Callee: MemberExpr callee } => callee.Member.Text,
                _ => _source.ToString(expr.Span),
            };
            _diagnostics.ReportNoValue(expr.Span, name);
            return new BoundErrorExpr(expr.Span);
        }
        return bound;
    }

    // Code an expression runs ends the narrowing of field paths (SPEC §3.9).
    private BoundExpr BindExpr(Expr expr, TaipanType? expected = null) => EndPathsAfter(BindExprCore(expr, expected));

    private BoundExpr BindExprCore(Expr expr, TaipanType? expected = null) => expr switch
    {
        IntLiteral literal => BindIntLiteral(literal.Value, literal.Span),
        FloatLiteral literal => new BoundLiteral(literal.Value, TaipanType.Float, literal.Span),
        StringLiteral literal => new BoundLiteral(literal.Value, TaipanType.Str, literal.Span),
        BoolLiteral literal => new BoundLiteral(literal.Value, TaipanType.Bool, literal.Span),
        FStringExpr fstring => BindFString(fstring),
        NameExpr name => BindName(name, expected),
        UnaryExpr unary => BindUnary(unary),
        AwaitExpr awaitExpr => BindAwait(awaitExpr, expected),
        YieldExpr yield => BindYieldExpr(yield.Value, yield.From, used: true, yield.Span),
        BinaryExpr { Op: BinaryOp.Coalesce } binary => BindCoalesce(binary),
        BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual } equality => BindEquality(equality),
        BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } logical => BindLogical(logical),
        IsNoneExpr isNone => BindIsNone(isNone),
        IsExpr identity => BindIs(identity),
        BinaryExpr binary => BindBinaryOperation(BindValue(binary.Left), binary.Op, BindValue(binary.Right), binary.Span),
        CallExpr call => BindCall(call, expected),
        ListExpr list => BindList(list, expected),
        SetExpr set => BindSet(set, expected),
        ComprehensionExpr comprehension => BindComprehension(comprehension, expected),
        DictExpr dict => BindDict(dict, expected),
        MemberExpr member => BindMember(member, expected),
        IndexExpr index => BindIndexOf(Upcast(BindValue(index.Target)), index),
        NoneLiteral => new BoundNone(expr.Span),
        CastExpr cast => BindCast(cast),
        LambdaExpr lambda => BindLambda(lambda, expected),
        MatchExpr match => BindMatchExpr(match, expected),
        TupleExpr tuple => BindTuple(tuple, expected),
        SliceExpr slice => BindSlice(slice),
        ErrorExpr => new BoundErrorExpr(expr.Span),
        EllipsisExpr => MisplacedEllipsis(expr.Span),
        PreboundExpr prebound => prebound.Bound,
        StarredExpr starred => MisplacedStar(starred),
        _ => throw new InvalidOperationException($"unexpected expression {expr.GetType().Name}"),
    };

    // `*x` is only an unpacking target (SPEC §3.8).
    private BoundErrorExpr MisplacedStar(StarredExpr starred)
    {
        _diagnostics.ReportMisplacedStar(starred.Span);
        return new BoundErrorExpr(starred.Span);
    }

    private BoundErrorExpr MisplacedEllipsis(TextSpan span)
    {
        _diagnostics.ReportMisplacedEllipsis(span);
        return new BoundErrorExpr(span);
    }

    private BoundErrorExpr UnsupportedExpr(string feature, TextSpan span)
    {
        Unsupported(feature);
        return new BoundErrorExpr(span);
    }

    private BoundExpr BindIntLiteral(BigInteger value, TextSpan span)
    {
        if (value > MaxInt)
        {
            _diagnostics.ReportIntegerOutOfRange(span);
            return new BoundErrorExpr(span);
        }
        return new BoundLiteral((long)value, TaipanType.Int, span);
    }

    private BoundFString BindFString(FStringExpr fstring)
    {
        var parts = ImmutableArray.CreateBuilder<BoundExpr>();
        foreach (var segment in fstring.Segments)
        {
            switch (segment)
            {
                case FStringTextSegment text:
                    parts.Add(new BoundLiteral(text.Value, TaipanType.Str, text.Span));
                    break;
                case FStringHoleSegment hole:
                    // `{x=}` writes the text as written, then the value as `!r` would, unless the
                    // hole converts or formats it itself (SPEC §6).
                    if (hole.DebugText is { } debugText)
                        parts.Add(new BoundLiteral(debugText, TaipanType.Str, hole.Span));
                    var conversion = hole.Conversion ?? (hole.DebugText is not null && hole.Spec is null ? 'r' : null);
                    parts.Add(BindHole(hole, BindValue(hole.Expr), conversion));
                    break;
            }
        }
        return new BoundFString(parts.ToImmutable(), fstring.Span);
    }

    /// <summary>A hole's value, converted and formatted as its spec says, which is checked against the value's type (SPEC §6).</summary>
    private BoundExpr BindHole(FStringHoleSegment hole, BoundExpr value, char? conversion)
    {
        // A class with __format__ formats itself, with any spec (SPEC §4.9, M39).
        var formatsItself = conversion is null && value.Type is ClassType && SpecialMethods.Method(value.Type, "__format__") is not null;
        if (formatsItself)
            return SpecialCall(value, "__format__", [new BoundLiteral(hole.Spec ?? "", TaipanType.Str, hole.Span)], hole.Span);
        // A spec outside the mini-language, or with the locale's `n` (SPEC §6).
        if (hole.Spec is { } raw && !raw.Contains('{') && Taipan.Runtime.FormatSpec.Parse(raw) is not { Type: not 'n' })
        {
            _diagnostics.ReportInvalidFormatSpec(hole.SpecSpan.Length == 0 ? new TextSpan(hole.SpecSpan.Start - 1, 1) : hole.SpecSpan, raw);
            return new BoundFormat(value, conversion, null, null, hole.Span);
        }
        if (conversion is null && hole.Spec is null)
            return value;
        // A spec the lexer rejected (TP0006) isn't checked again.
        if (hole.Spec is not { } specText || Taipan.Runtime.FormatSpec.Parse(specText) is not { } spec)
            return new BoundFormat(value, conversion, null, null, hole.Span);
        if (value.Type.ContainsError)
            return new BoundFormat(value, conversion, null, null, hole.Span);

        // After a conversion, the value is a str.
        var formatted = conversion is null ? value.Type : TaipanType.Str;
        Taipan.Runtime.FormatKind? kind = formatted switch
        {
            PrimitiveType { Name: "int" } => Taipan.Runtime.FormatKind.Int,
            PrimitiveType { Name: "float" } => Taipan.Runtime.FormatKind.Float,
            PrimitiveType { Name: "str" } => Taipan.Runtime.FormatKind.Str,
            _ => null,
        };
        var span = hole.SpecSpan.Length == 0 ? hole.Span : hole.SpecSpan;
        if (kind is not { } formatKind)
        {
            var help = formatted switch
            {
                OptionalOf => "narrow the optional first",
                PrimitiveType { Name: "bool" } => "convert it with str() or int() first",
                PrimitiveType { Name: "py" } => "convert it with 'as' first",
                _ => "convert it with str() first",
            };
            _diagnostics.ReportFormatSpecType(span, formatted, help);
        }
        else if (spec.Problem(formatKind) is { } problem)
        {
            _diagnostics.ReportFormatSpecNotValid(span, specText, formatted, problem);
        }
        return new BoundFormat(value, conversion, spec, specText, hole.Span);
    }

    private BoundExpr BindName(NameExpr name, TaipanType? expected) =>
        BindSymbolValue(LookupName(name.Name, name.Span), name.Name, name.Span, expected, name);

    private BoundExpr BindSymbolValue(Symbol? symbol, string name, TextSpan span, TaipanType? expected, Expr? syntax = null)
    {
        // Generic functions, built-ins and constructors are values as lambdas that call them (SPEC §3.18).
        if (syntax is not null && symbol is not null && CallableValueTarget(syntax) == symbol)
            return BindCallableValue(syntax, symbol, name, span, expected);
        switch (symbol)
        {
            case VariableSymbol variable when variable == _outerFunction?.Cls:
                _diagnostics.ReportClsMisuse(span);
                return new BoundErrorExpr(span);
            case VariableSymbol variable when _narrowing.Types.TryGetValue(variable, out var narrowed):
                return new BoundNarrowed(new BoundVariable(variable, span), narrowed, span);
            case VariableSymbol variable:
                return new BoundVariable(variable, span);
            // Only a top-level function that isn't generic is a value (SPEC §3.7).
            case FunctionSymbol { Builtin: not null }:
                _diagnostics.ReportBuiltinAsValue(span, name);
                return new BoundErrorExpr(span);
            case FunctionSymbol { TypeParameters.IsEmpty: false }:
                _diagnostics.ReportGenericAsValue(span, name);
                return new BoundErrorExpr(span);
            // A function type passes a fixed list of positional arguments (SPEC §4.2, M30).
            case FunctionSymbol { IsVariadic: true }:
                _diagnostics.ReportVariadicAsValue(span, name);
                return new BoundErrorExpr(span);
            case FunctionSymbol function:
                return new BoundFunctionValue(function, new FunctionType([.. function.Parameters.Select(p => p.Type)], function.ReturnType), span);
            case ClassSymbol or EnumSymbol or BuiltinTypeSymbol:
                _diagnostics.ReportTypeAsValue(span, name);
                return new BoundErrorExpr(span);
            case TypeAliasSymbol alias:
                _diagnostics.ReportAliasAsValue(span, name, alias.TargetName);
                return new BoundErrorExpr(span);
            case DotNetTypeSymbol:
                _diagnostics.ReportTypeAsValue(span, name);
                return new BoundErrorExpr(span);
            case ModuleSymbol:
                _diagnostics.ReportModuleAsValue(span, name);
                return new BoundErrorExpr(span);
            case DotNetNamespaceSymbol:
                _diagnostics.ReportNamespaceAsValue(span, name);
                return new BoundErrorExpr(span);
            case VariantSymbol variant:
                return BindVariantValue(variant, span, expected);
            case AmbiguousVariantSymbol ambiguous:
                ReportAmbiguous(ambiguous, span);
                return new BoundErrorExpr(span);
            default:
                return new BoundErrorExpr(span);
        }
    }

    private BoundExpr BindUnary(UnaryExpr unary)
    {
        // -9223372036854775808 is in range only with the minus applied to the literal (SPEC §4.5).
        if (unary is { Op: UnaryOp.Negate, Operand: IntLiteral literal } && literal.Value == MaxInt + 1)
            return new BoundLiteral(long.MinValue, TaipanType.Int, unary.Span);

        var operand = BindValue(unary.Operand);
        if (operand.Type.ContainsError || ReportIfOptional(operand, unary.Operand.Span))
            return new BoundUnary(unary.Op, operand, TaipanType.Error, unary.Span);

        if (unary.Op == UnaryOp.Not)
        {
            if (operand.Type != TaipanType.Bool)
                _diagnostics.ReportConditionNotBool(unary.Operand.Span, operand.Type);
            return new BoundUnary(unary.Op, operand, TaipanType.Bool, unary.Span);
        }

        if (operand.Type == TaipanType.Py)
            return new BoundUnary(unary.Op, operand, TaipanType.Py, unary.Span);
        if (operand.Type is DotNetType && BindDotNetNegation(operand, unary.Span) is { } dotnet)
            return dotnet;
        // -x on a class calls its __neg__ (SPEC §4.8).
        if (operand.Type is ClassType && NegatedType(operand.Type) is { } negated)
            return new BoundUnary(unary.Op, operand, negated, unary.Span);
        if (!operand.Type.IsNumeric)
        {
            _diagnostics.ReportUnaryOperatorType(unary.Span, "-", operand.Type,
                operand.Type is ClassType { Symbol.Name: var className } ? $"define '__neg__' on '{className}'" : null);
            return new BoundUnary(unary.Op, operand, TaipanType.Error, unary.Span);
        }
        return new BoundUnary(unary.Op, operand, operand.Type, unary.Span);
    }

    private BoundExpr BindBinaryOperation(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        // A class-bounded type parameter has its class's operators (SPEC §3.18).
        if (op is not (BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.And or BinaryOp.Or or BinaryOp.Coalesce))
            (left, right) = (Upcast(left), Upcast(right));
        if (left.Type.ContainsError || right.Type.ContainsError)
            return new BoundBinary(left, op, right, TaipanType.Error, span);
        if (left.Type == TaipanType.Py || right.Type == TaipanType.Py)
            return BindPyBinary(left, op, right, span);
        // Only == and != accept optionals (SPEC §3.4).
        if (op is not (BinaryOp.Equal or BinaryOp.NotEqual)
            && (ReportIfOptional(left, left.Span) | ReportIfOptional(right, right.Span)))
            return new BoundBinary(left, op, right, TaipanType.Error, span);

        // `x in c` calls the class's __contains__ (SPEC §4.9, M39), or else takes values through its __iter__ or __next__ (SPEC §3.13).
        if (op is BinaryOp.In or BinaryOp.NotIn && right.Type is ClassType && SpecialMethods.Method(right.Type, "__contains__") is var (_, item, _))
        {
            CheckAssignable(left, item!, left.Span);
            return new BoundBinary(Convert(left, item!), op, right, TaipanType.Bool, span);
        }
        if (op is BinaryOp.In or BinaryOp.NotIn && right.Type is ClassType && TypeChecker.ElementOf(right.Type) is { } element)
            right = Convert(right, new IterableOfType(element));
        // A flags enum's |, &, ^ and in (SPEC §13.19, M43).
        if (left.Type is DotNetType && BindFlags(left, op, right, span) is { } flags)
            return flags;
        // An operator on a .NET value calls its type's operator (SPEC §13.10).
        if ((left.Type is DotNetType || right.Type is DotNetType) && BindDotNetOperator(left, op, right, span) is { } dotnet)
            return dotnet;
        // An operator on a class value calls a special method (SPEC §4.8).
        if (SpecialMethods.Forward(op) is { } forward && (left.Type is ClassType || right.Type is ClassType))
        {
            if (BindSpecialBinary(left, op, right, span) is { } special)
                return special;
            var help = left.Type is ClassType { Symbol.Name: var className } ? $"define '{forward}' on '{className}'" : null;
            _diagnostics.ReportBinaryOperatorTypes(span, TypeChecker.Display(op), left.Type, right.Type, help);
            return new BoundBinary(left, op, right, TaipanType.Error, span);
        }

        var type = op switch
        {
            BinaryOp.And or BinaryOp.Or => CheckLogicalOperands(left, right),
            BinaryOp.Equal or BinaryOp.NotEqual => CheckComparable(left, right, span),
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual =>
                CheckOrdered(left, op, right, span),
            BinaryOp.In or BinaryOp.NotIn => CheckOperands(left, op, right, span, MembershipResult(left.Type, right.Type)),
            BinaryOp.Power => CheckPower(left, right, span),
            _ => CheckOperands(left, op, right, span, TypeChecker.Arithmetic(op, left.Type, right.Type)),
        };
        return new BoundBinary(left, op, right, type, span);
    }

    // With a py operand, Python does the operation and the result is py, comparisons included.
    // `and`/`or` need bool operands, so a py one is TP0205 (SPEC §10.2).
    private BoundExpr BindPyBinary(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        if (op is BinaryOp.And or BinaryOp.Or)
            return new BoundBinary(left, op, right, CheckLogicalOperands(left, right), span);
        foreach (var operand in (ReadOnlySpan<BoundExpr>)[left, right])
        {
            if (!operand.Type.IsConvertibleToPython)
                _diagnostics.ReportCannotPassToPython(operand.Span, operand.Type);
        }
        return new BoundBinary(left, op, right, TaipanType.Py, span);
    }

    /// <summary>In <c>a and b</c>, <c>b</c> sees what holds when <c>a</c> is true; in <c>a or b</c>, when it's false (SPEC §3.9).</summary>
    private BoundExpr BindLogical(BinaryExpr binary)
    {
        var left = BindValue(binary.Left);
        var (outer, mark) = (_narrowing, _endings.Count);
        _narrowing = _narrowing.With(Facts(left, whenTrue: binary.Op == BinaryOp.And));
        var right = BindValue(binary.Right);
        _narrowing = Replay(outer, mark);
        return BindBinaryOperation(left, binary.Op, right, binary.Span);
    }

    private BoundIsNone BindIsNone(IsNoneExpr isNone)
    {
        var operand = Declared(BindValue(isNone.Operand));
        // An object may hold None, but testing it doesn't narrow it (SPEC §3.20).
        if (operand.Type is not ObjectType)
            RequireOptional(operand, isNone.Operand.Span, isNone.Negated ? "is not None" : "is None");
        return new BoundIsNone(operand, isNone.Negated, isNone.Span);
    }

    /// <summary><c>a is b</c> (SPEC §3.18): both of types with an identity, one assignable to the other, or both py.</summary>
    private BoundExpr BindIs(IsExpr identity)
    {
        var left = BindValue(identity.Left);
        var right = BindValue(identity.Right);
        if (left.Type.ContainsError || right.Type.ContainsError)
            return new BoundErrorExpr(identity.Span);
        if (new[] { left, right }.FirstOrDefault(o => !HasIdentity(o.Type)) is { } without)
        {
            _diagnostics.ReportNoIdentity(identity.Span, without.Type);
            return new BoundErrorExpr(identity.Span);
        }
        if ((left.Type == TaipanType.Py) != (right.Type == TaipanType.Py)
            || !TypeChecker.IsAssignable(left.Type, right.Type) && !TypeChecker.IsAssignable(right.Type, left.Type))
        {
            _diagnostics.ReportCannotCompare(identity.Span, left.Type, right.Type);
            return new BoundErrorExpr(identity.Span);
        }
        return new BoundIs(left, right, identity.Negated, identity.Span);
    }

    private static bool HasIdentity(TaipanType type) => type switch
    {
        ClassType or ListOfType or DictOfType or SetOfType or FunctionType or IteratorOfType or IterableOfType or ClassOfType => true,
        OpaqueType or NoneType => true,
        OptionalOf optional => HasIdentity(optional.Inner),
        _ => type == TaipanType.Py,
    };

    /// <summary>A value of a class-bounded type parameter as its bound, for its members and operators (SPEC §3.18).</summary>
    private static BoundExpr Upcast(BoundExpr value) =>
        value.Type is TypeParameterType { Symbol.ClassBound: { } bound } ? new BoundNarrowed(value, bound, value.Span) : value;

    // ---- Narrowing (SPEC §3.9) ----

    /// <summary>
    /// What's known: names and field paths narrowed to new types (keyed by symbol or
    /// <see cref="FieldPath"/>), and, for those that aren't, why, for the help on TP0203 and TP0210 (SPEC §3.9).
    /// </summary>
    private sealed record Narrowing(ImmutableDictionary<object, TaipanType> Types, ImmutableDictionary<object, string> Why)
    {
        public static readonly Narrowing None = new(ImmutableDictionary<object, TaipanType>.Empty, ImmutableDictionary<object, string>.Empty);

        public Narrowing With(Narrowing other) =>
            new(Types.SetItems(other.Types), Why.RemoveRange(other.Types.Keys).SetItems(other.Why));

        public Narrowing WithoutPaths() => this with { Types = Types.Where(pair => pair.Key is not FieldPath).ToImmutableDictionary() };
    }

    /// <summary>
    /// What holds when <paramref name="condition"/> is true, or when it's false. In <c>a and b</c>,
    /// code that <c>b</c> runs ends the paths <c>a</c> narrowed (SPEC §3.9).
    /// </summary>
    private Narrowing Facts(BoundExpr condition, bool whenTrue) => condition switch
    {
        BoundIsInstance isInstance when whenTrue => Narrow(isInstance.Operand, InstanceType(isInstance)),
        BoundPyIsInstance { Narrows: { } type } test when whenTrue => Narrow(test.Operand, type),
        BoundTypeTest test when whenTrue => Narrow(test.Operand, test.Target),
        BoundIsNone isNone when isNone.Negated == whenTrue => Narrow(isNone.Operand, TypeChecker.Unwrap(isNone.Operand.Type)),
        BoundIsSome isSome when whenTrue => Narrow(isSome.Operand, TypeChecker.Unwrap(isSome.Operand.Type)),
        BoundBinary { Op: BinaryOp.Equal or BinaryOp.NotEqual } equality => EqualityFacts(equality, (equality.Op == BinaryOp.Equal) == whenTrue),
        BoundUnary { Op: UnaryOp.Not } negation => Facts(negation.Operand, !whenTrue),
        BoundBinary { Op: BinaryOp.And } both when whenTrue => Sequence(Facts(both.Left, true), both.Right, Facts(both.Right, true)),
        BoundBinary { Op: BinaryOp.Or } either when !whenTrue => Sequence(Facts(either.Left, false), either.Right, Facts(either.Right, false)),
        _ => Narrowing.None,
    };

    /// <summary>
    /// <c>x == lit</c> when it holds, or <c>x == None</c> when it doesn't, rules out None for an
    /// optional <c>x</c> (SPEC §3.18).
    /// </summary>
    private Narrowing EqualityFacts(BoundBinary equality, bool equal)
    {
        foreach (var (value, other) in new[] { (equality.Left, equality.Right), (equality.Right, equality.Left) })
        {
            if (Declared(value).Type is OptionalOf optional && (equal ? IsLiteral(other) : other is BoundNone))
                return Narrow(value, optional.Inner);
        }
        return Narrowing.None;
    }

    private static bool IsLiteral(BoundExpr expr) =>
        expr is BoundLiteral { Value: not null } or BoundUnary { Op: UnaryOp.Negate, Operand: BoundLiteral };

    private static Narrowing Sequence(Narrowing first, BoundExpr between, Narrowing then) =>
        (ContainsCode(between) ? first.WithoutPaths() : first).With(then);

    /// <summary>The type <c>isinstance(x, C)</c> narrows <c>x</c> to: <c>C</c>, unless <c>x</c>'s own class already derives from it.</summary>
    private static TaipanType InstanceType(BoundIsInstance isInstance) =>
        TypeChecker.Unwrap(isInstance.Operand.Type) is ClassType { Symbol.IsProtocol: false } current && !isInstance.Class.Symbol.DerivesFrom(current.Symbol)
            ? current
            : isInstance.Class;

    // A name or path narrowed to a type, or, for one that can't be, why (SPEC §3.9).
    private Narrowing Narrow(BoundExpr operand, TaipanType type)
    {
        if (type.ContainsError)
            return Narrowing.None;
        var key = KeyOf(operand);
        string? why = key switch
        {
            FieldPath { CanNarrow: true, Root: VariableSymbol { Kind: VariableKind.Var } root } => _nestedAssigned.Contains(root) ? CopyHelp : null,
            VariableSymbol { Kind: VariableKind.Let or VariableKind.Parameter or VariableKind.LoopVariable } or FieldPath { CanNarrow: true } => null,
            VariableSymbol { Kind: VariableKind.Var } variable => _nestedAssigned.Contains(variable) ? "a nested function assigns it, so it isn't narrowed" : null,
            FieldPath => CopyHelp,
            _ => "",
        };
        return (key, why) switch
        {
            (null, _) or (_, "") => Narrowing.None,
            (_, null) => Narrowing.None with { Types = Narrowing.None.Types.Add(key, type) },
            _ => Narrowing.None with { Why = Narrowing.None.Why.Add(key, why) },
        };
    }

    private const string CopyHelp = "copy it to a 'let' first";

    // In `a == b` and `a != b`, a's type is b's expected type, so `m == Nothing` and `xs == []` work (SPEC §5.3).
    // A tuple's isn't: its elements need only be comparable, so `(1, 2) == (1.0, 2)` works (§3.8).
    private BoundExpr BindEquality(BinaryExpr binary)
    {
        var left = BindValue(binary.Left);
        var expected = left.Type.ContainsError || left.Type == TaipanType.Py || left.Type is TupleOfType ? null : left.Type;
        return BindBinaryOperation(left, binary.Op, BindValue(binary.Right, expected), binary.Span);
    }

    // `x ?? d` (SPEC §3.4): the default is checked against T; a T? default keeps the result optional.
    private BoundExpr BindCoalesce(BinaryExpr binary)
    {
        var left = Declared(BindValue(binary.Left));
        if (left.Type is not OptionalOf optional)
        {
            if (!left.Type.ContainsError)
                _diagnostics.ReportNeedsOptional(binary.Left.Span, "??", left.Type);
            var orphan = BindValue(binary.Right);
            return new BoundBinary(left, BinaryOp.Coalesce, orphan, TaipanType.Error, binary.Span);
        }

        var right = BindValue(binary.Right, optional.Inner);
        if (right.Type is OptionalOf or NoneType)
        {
            if (!TypeChecker.IsAssignable(right.Type, optional))
                _diagnostics.ReportTypeMismatch(binary.Right.Span, optional.Inner, right.Type);
            return new BoundBinary(left, BinaryOp.Coalesce, right, optional, binary.Span);
        }
        CheckAssignable(right, optional.Inner, binary.Right.Span);
        return new BoundBinary(left, BinaryOp.Coalesce, right, optional.Inner, binary.Span);
    }

    // `x in c` (SPEC §5.3): str in str, T in list[T], K in dict[K, V].
    private static TaipanType? MembershipResult(TaipanType item, TaipanType container) => container switch
    {
        PrimitiveType { Name: "str" } when item == TaipanType.Str => TaipanType.Bool,
        ListOfType list when TypeChecker.AreComparable(item, list.Element) => TaipanType.Bool,
        VarTupleOfType varTuple when TypeChecker.AreComparable(item, varTuple.Element) => TaipanType.Bool,
        SetOfType set when TypeChecker.AreComparable(item, set.Element) => TaipanType.Bool,
        DictOfType dict when TypeChecker.AreComparable(item, dict.Key) => TaipanType.Bool,
        IteratorOfType or IterableOfType when TypeChecker.ElementOf(container) is { } element && TypeChecker.AreComparable(item, element) => TaipanType.Bool,
        _ => null,
    };

    // §3.5: the operands of and/or are conditions.
    private TaipanType CheckLogicalOperands(BoundExpr left, BoundExpr right)
    {
        foreach (var operand in (ReadOnlySpan<BoundExpr>)[left, right])
        {
            if (operand.Type != TaipanType.Bool)
                _diagnostics.ReportConditionNotBool(operand.Span, operand.Type);
        }
        return TaipanType.Bool;
    }

    private TaipanType CheckComparable(BoundExpr left, BoundExpr right, TextSpan span)
    {
        if (TypeChecker.AreComparable(left.Type, right.Type))
            return TaipanType.Bool;
        _diagnostics.ReportCannotCompare(span, left.Type, right.Type);
        return TaipanType.Error;
    }

    private TaipanType CheckOrdered(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        // A class with __lt__, or a type parameter bounded by a protocol with it (SPEC §3.11):
        // 'a < b' is a.__lt__(b), and 'a > b' is b.__lt__(a).
        var (receiver, argument) = op == BinaryOp.Greater ? (right.Type, left.Type) : (left.Type, right.Type);
        if (op is BinaryOp.Less or BinaryOp.Greater && LessThanParameter(receiver) is { } parameter
            && (TypeChecker.IsAssignable(argument, parameter) || argument.ContainsError))
            return TaipanType.Bool;
        if (op is BinaryOp.LessEqual or BinaryOp.GreaterEqual && (LessThanParameter(left.Type) ?? LessThanParameter(right.Type)) is not null)
        {
            _diagnostics.ReportBinaryOperatorTypes(span, TypeChecker.Display(op), left.Type, right.Type, "'__lt__' defines only '<' and '>'");
            return TaipanType.Error;
        }
        if (!TypeChecker.AreComparable(left.Type, right.Type))
        {
            _diagnostics.ReportCannotCompare(span, left.Type, right.Type);
            return TaipanType.Error;
        }
        // On sets, the comparisons are subset and superset tests (SPEC §3.15).
        var ordered = left.Type.IsOrdered && right.Type.IsOrdered || left.Type is SetOfType && left.Type == right.Type;
        return CheckOperands(left, op, right, span, ordered ? TaipanType.Bool : null);
    }

    /// <summary><c>a ** b</c> (SPEC §5.3): an int power can't have a negative literal exponent, whose result Python makes a float.</summary>
    private TaipanType CheckPower(BoundExpr left, BoundExpr right, TextSpan span)
    {
        var result = CheckOperands(left, BinaryOp.Power, right, span, TypeChecker.Arithmetic(BinaryOp.Power, left.Type, right.Type));
        if (result == TaipanType.Int && right is BoundUnary { Op: UnaryOp.Negate, Operand: BoundLiteral { Value: long n } } && n > 0)
            _diagnostics.ReportNegativeIntExponent(right.Span);
        return result;
    }

    private TaipanType CheckOperands(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span, TaipanType? result)
    {
        if (result is not null)
            return result;
        _diagnostics.ReportBinaryOperatorTypes(span, TypeChecker.Display(op), left.Type, right.Type);
        return TaipanType.Error;
    }

    // ---- Lists, dicts, members and indexing ----

    /// <summary><c>{a, b}</c>: a set, typed as a list's elements are (SPEC §3.15).</summary>
    private BoundExpr BindSet(SetExpr set, TaipanType? expected)
    {
        if (TypeChecker.Unwrap(expected ?? TaipanType.Error) is SetOfType expectedSet)
            return new BoundSetLiteral([.. set.Elements.Select(e => BindExpected(e, expectedSet.Element))], expectedSet, set.Span);
        var elements = set.Elements.Select(e => BindValue(e)).ToImmutableArray();
        var elementType = InferElementType(elements);
        CheckSetElement(set.Elements[0].Span, elementType);
        return new BoundSetLiteral(elements, new SetOfType(elementType), set.Span);
    }

    private BoundExpr BindList(ListExpr list, TaipanType? expected)
    {
        // Bound for Python: a list[py] whose elements may have any convertible types (SPEC §10.3).
        if (expected == TaipanType.Py)
            expected = new ListOfType(TaipanType.Py);
        if (expected is ListOfType expectedList)
        {
            var checkedElements = list.Elements.Select(e => BindExpected(e, expectedList.Element)).ToImmutableArray();
            return new BoundListLiteral(checkedElements, expectedList, list.Span);
        }
        if (list.Elements.IsEmpty)
        {
            // An annotation that failed to bind was already reported.
            if (expected is not { IsError: true })
                _diagnostics.ReportCannotInferEmpty(list.Span, "list");
            return new BoundErrorExpr(list.Span);
        }

        var elements = list.Elements.Select(e => BindValue(e)).ToImmutableArray();
        var elementType = InferElementType(elements);
        return new BoundListLiteral(elements, new ListOfType(elementType), list.Span);
    }

    private BoundExpr BindDict(DictExpr dict, TaipanType? expected)
    {
        // Bound for Python: values may have any convertible types; keys keep the usual rules.
        if (expected == TaipanType.Py && !dict.Entries.IsEmpty)
        {
            var pyKeys = dict.Entries.Select(e => BindValue(e.Key)).ToImmutableArray();
            var pyKeyType = InferElementType(pyKeys);
            if (!DictOfType.IsValidKey(pyKeyType))
                ReportInvalidKey(dict.Entries[0].Key.Span, pyKeyType);
            var pyValues = dict.Entries.Select(e => BindExpected(e.Value, TaipanType.Py)).ToImmutableArray();
            return new BoundDictLiteral([.. pyKeys.Zip(pyValues)], new DictOfType(pyKeyType, TaipanType.Py), dict.Span);
        }
        if (expected is DictOfType expectedDict)
        {
            var checkedEntries = dict.Entries
                .Select(e => (BindExpected(e.Key, expectedDict.Key), BindExpected(e.Value, expectedDict.Value)))
                .ToImmutableArray();
            return new BoundDictLiteral(checkedEntries, expectedDict, dict.Span);
        }
        if (dict.Entries.IsEmpty)
        {
            // An annotation that failed to bind was already reported.
            if (expected is not { IsError: true })
                _diagnostics.ReportCannotInferEmpty(dict.Span, "dict");
            return new BoundErrorExpr(dict.Span);
        }

        var entries = dict.Entries.Select(e => (Key: BindValue(e.Key), Value: BindValue(e.Value))).ToImmutableArray();
        var keyType = InferElementType([.. entries.Select(e => e.Key)]);
        if (!DictOfType.IsValidKey(keyType))
        {
            ReportInvalidKey(dict.Entries[0].Key.Span, keyType);
            keyType = TaipanType.Error;
        }
        var valueType = InferElementType([.. entries.Select(e => e.Value)]);
        return new BoundDictLiteral([.. entries.Select(e => (e.Key, e.Value))], new DictOfType(keyType, valueType), dict.Span);
    }

    /// <summary>
    /// The common type of literal elements (SPEC §4.6, §3.4): all equal, int and float mixing to
    /// float, and none or T? mixing with T to T?.
    /// </summary>
    private TaipanType InferElementType(ImmutableArray<BoundExpr> elements)
    {
        TaipanType? common = null;
        var optional = false;
        foreach (var element in elements)
        {
            var type = element.Type;
            if (type.ContainsError)
                return TaipanType.Error;
            if (type is NoneType)
            {
                optional = true;
                continue;
            }
            if (type is OptionalOf inner)
            {
                optional = true;
                type = inner.Inner;
            }

            if (common is null || type == common)
                common = type;
            else if (type.IsNumeric && common.IsNumeric)
                common = TaipanType.Float;
            else if (Join(type, common) is { } join)
                common = join;
            else
            {
                _diagnostics.ReportTypeMismatch(element.Span, optional ? new OptionalOf(common) : common, element.Type);
                return TaipanType.Error;
            }
        }

        if (common is null)
        {
            _diagnostics.ReportCannotInferNone(elements[0].Span);
            return TaipanType.Error;
        }
        return optional ? new OptionalOf(common) : common;
    }

    private BoundExpr BindMember(MemberExpr member, TaipanType? expected)
    {
        if (TryQualifiedVariant(member, out var qualified))
        {
            return qualified is null ? new BoundErrorExpr(member.Span)
                : qualified.Fields.IsEmpty ? BindVariantValue(qualified, member.Span, expected)
                : BindCallableValue(member, qualified, $"{qualified.Enum.Name}.{qualified.Name}", member.Span, expected);
        }
        if (TryModuleMember(member, out var definition))
            return BindSymbolValue(definition, member.Member.Text, member.Span, expected, member);
        if (DotNetTypeNamed(member.Target) is { } namedNet)
        {
            if (namedNet is not DotNetType owner)
                return new BoundErrorExpr(member.Span);
            RecordReceiver(member.Member.Span, owner.Symbol);
            return BindDotNetMember(null, owner, member);
        }
        // A class attribute, or a static or class method as a value, through a class or cls (SPEC §4.8, §4.9).
        if (GenericClassReceiver(member.Target) is { } instantiation)
        {
            RecordReceiver(member.Member.Span, instantiation.Symbol);
            return BindClassMember(member, instantiation.Symbol, instantiation);
        }
        if ((ClassNamed(member.Target) ?? (IsCls(member.Target) ? _outerFunction!.Owner! : null)) is { } named)
        {
            RecordReceiver(member.Member.Span, named);
            return BindClassMember(member, named, viaCls: IsCls(member.Target));
        }

        var target = Upcast(BindValue(member.Target));
        RecordReceiver(member.Member.Span, target.Type);
        var name = member.Member.Text;
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
            return new BoundErrorExpr(member.Span);
        if (target.Type == TaipanType.Py)
            return new BoundPyGetAttr(target, name, member.Span);
        if (target.Type is DotNetType net)
            return BindDotNetMember(target, net, member);
        if (IoAttribute(target.Type, name) is { } ioAttribute)
            return new BoundRuntimeProperty(target, name, ioAttribute, member.Span);
        // An object has only its value, read with 'as' (SPEC §3.20).
        if (target.Type is ObjectType)
        {
            _diagnostics.ReportObjectAttribute(member.Member.Span, name);
            return new BoundErrorExpr(member.Span);
        }

        if (target.Type is ClassType { Symbol: var classSymbol })
        {
            if (classSymbol.FindField(name) is { } field)
            {
                Index(member.Member.Span, field);
                return NarrowedField(new BoundFieldAccess(target, field, FieldType(target.Type, field), member.Span));
            }
            // A static or class method through an instance, as a value (SPEC §4.9).
            if (classSymbol.FindMethod(name) is { IsClassLevel: true } classLevel)
                return BindClassMethodValue(member, classSymbol, target, viaCls: false, classLevel, null);
            if (BindInstanceMember(target, classSymbol, member) is { } instanceMember)
                return instanceMember is BoundClassAttribute attribute ? NarrowedAttribute(attribute) : instanceMember;
            // The .NET base class's members (SPEC §13.16, M43).
            if (classSymbol.FindMethod(name) is null && DotNetBaseOf(classSymbol) is { } dotnetBase && HasMember(dotnetBase.Symbol, name, isStatic: false))
                return BindDotNetMember(target, dotnetBase, member);
            if (name == "__init__")
                _diagnostics.ReportInitCalled(member.Member.Span);
            else if (classSymbol.FindMethod(name) is not null)
                _diagnostics.ReportMethodAsValue(member.Member.Span, name);
            else
                _diagnostics.ReportNoField(member.Member.Span, target.Type, name, WhyNotNarrowed(target));
        }
        else if (BuiltinMethodOf(target.Type, name) is not null)
        {
            _diagnostics.ReportMethodAsValue(member.Member.Span, name);
        }
        // A list's List<T> members (SPEC §13.4, M43).
        else if (target.Type is ListOfType list && ListAsDotNet(list) is var listType && HasMember(listType.Symbol, name, isStatic: false))
        {
            return BindDotNetMember(target, listType, member);
        }
        else
        {
            _diagnostics.ReportNoField(member.Member.Span, target.Type, name);
        }
        return new BoundErrorExpr(member.Span);
    }

    /// <summary>A field's type as seen through <paramref name="owner"/>'s type arguments.</summary>
    private static TaipanType FieldType(TaipanType owner, FieldSymbol field) =>
        TypeChecker.Substitute(field.Type, TypeChecker.MapOf(owner));

    private BoundExpr BindIndexOf(BoundExpr target, IndexExpr indexExpr)
    {
        switch (target.Type)
        {
            case { ContainsError: true }:
                BindValue(indexExpr.Index);
                return new BoundErrorExpr(indexExpr.Span);
            case OptionalOf:
                ReportIfOptional(target, indexExpr.Target.Span);
                BindValue(indexExpr.Index);
                return new BoundErrorExpr(indexExpr.Span);
            case PrimitiveType { Name: "py" }:
                return new BoundPyGetItem(target, BindPyArgument(indexExpr.Index), indexExpr.Span);
            case TupleOfType tuple:
                return BindTupleElement(target, tuple, indexExpr);
            case ListOfType list:
                return NarrowedElement(new BoundIndex(target, BindExpected(indexExpr.Index, TaipanType.Int), list.Element, indexExpr.Span));
            case VarTupleOfType varTuple:
                return new BoundIndex(target, BindExpected(indexExpr.Index, TaipanType.Int), varTuple.Element, indexExpr.Span);
            case DictOfType dict:
                return NarrowedElement(new BoundIndex(target, BindExpected(indexExpr.Index, dict.Key), dict.Value, indexExpr.Span));
            case PrimitiveType { Name: "str" }:
                return new BoundIndex(target, BindExpected(indexExpr.Index, TaipanType.Int), TaipanType.Str, indexExpr.Span);
            case DotNetType net:
                return BindDotNetIndex(target, net, indexExpr);
            // A class with __getitem__ (SPEC §4.9, M39).
            case ClassType when SpecialMethods.Method(target.Type, "__getitem__") is var (_, key, _):
                return SpecialCall(target, "__getitem__", [BindExpected(indexExpr.Index, key!)], indexExpr.Span);
            // An iterator's values aren't there until they're taken (SPEC §3.13).
            case IteratorOfType or IterableOfType:
                BindValue(indexExpr.Index);
                _diagnostics.ReportNotIterableFromList(indexExpr.Target.Span, $"cannot index {target.Type}");
                return new BoundErrorExpr(indexExpr.Span);
            default:
                BindValue(indexExpr.Index);
                _diagnostics.ReportCannotIndex(indexExpr.Target.Span, target.Type);
                return new BoundErrorExpr(indexExpr.Span);
        }
    }

    // ---- Calls ----

    private BoundExpr BindCall(CallExpr call, TaipanType? expected)
    {
        if (call.Callee is MemberExpr member)
            return BindMethodCall(call, member, expected);
        // A generic .NET type's construction, or a .NET method with explicit type arguments (SPEC §13.8, §13.13, M43).
        if (call.Callee is IndexExpr indexed && BindExplicitlyTyped(call, indexed) is { } explicitlyTyped)
            return explicitlyTyped;

        if (call.Callee is not NameExpr calleeName)
        {
            var callee = BindValue(call.Callee);
            if (callee.Type == TaipanType.Py)
                return BindPyCall(call, callee);
            if (TypeChecker.Unwrap(callee.Type) is FunctionType)
            {
                ReportKeywordArguments(call, function: null);
                return BindInvoke(call, callee, name: null, call.Callee.Span);
            }
            if (IsCallableObject(callee.Type))
                return BindObjectCall(call, callee, call.Callee.Span);
            BindArguments(call);
            if (!callee.Type.ContainsError)
                _diagnostics.ReportCannotCall(call.Callee.Span, callee.Type);
            return new BoundErrorExpr(call.Span);
        }

        return BindSymbolCall(call, LookupName(calleeName.Name, calleeName.Span), calleeName.Name, calleeName.Span, expected);
    }

    /// <summary>A call whose callee names <paramref name="symbol"/>, directly or through a module.</summary>
    private BoundExpr BindSymbolCall(CallExpr call, Symbol? symbol, string name, TextSpan nameSpan, TaipanType? expected)
    {
        if (symbol is VariableSymbol { Type: PrimitiveType { Name: "py" } } python)
            return BindPyCall(call, new BoundVariable(python, nameSpan));
        // sorted's keyword arguments may follow its positional one, as in Python (SPEC §2).
        if (symbol is FunctionSymbol { Builtin: BuiltinFunction.Sorted })
            return BindSort(call, name, nameSpan, target: null);
        if (symbol is FunctionSymbol { Builtin: BuiltinFunction.Enumerate } enumerate)
            return BindEnumerate(call, enumerate, nameSpan);
        if (symbol is FunctionSymbol { Builtin: BuiltinFunction.IsInstance })
            return BindIsInstance(call, nameSpan);
        if (symbol is FunctionSymbol { Builtin: BuiltinFunction.Abs or BuiltinFunction.Min or BuiltinFunction.Max or BuiltinFunction.Sum
            or BuiltinFunction.Any or BuiltinFunction.All or BuiltinFunction.Round or BuiltinFunction.DivMod or BuiltinFunction.Set or BuiltinFunction.Tuple } everyday)
            return BindEverydayBuiltin(call, everyday, everyday.Builtin!.Value, expected);
        switch (symbol)
        {
            case VariableSymbol variable when variable == _outerFunction?.Cls:
                return BindClsNew(call, nameSpan, expected);
            case VariableSymbol variable when _localFunctions.TryGetValue(variable, out var local):
                return BindLocalCall(call, new BoundVariable(variable, nameSpan), local, nameSpan);
            case VariableSymbol variable when TypeChecker.Unwrap(variable.Type) is FunctionType:
                ReportKeywordArguments(call, function: null);
                return BindInvoke(call, new BoundVariable(variable, nameSpan), name, nameSpan);
            // An object of a class with __call__ (SPEC §4.9, M39).
            case VariableSymbol variable when IsCallableObject(variable.Type):
                return BindObjectCall(call, BindSymbolValue(variable, name, nameSpan, null), nameSpan);
            case ClassSymbol classSymbol:
                return BindConstruct(call, classSymbol, expected, nameSpan);
            case VariantSymbol variant:
                return BindConstructVariant(call, variant, expected);
            case AmbiguousVariantSymbol ambiguous:
                BindArguments(call);
                ReportAmbiguous(ambiguous, nameSpan);
                return new BoundErrorExpr(call.Span);
            case TypeAliasSymbol alias:
                BindArguments(call);
                _diagnostics.ReportAliasAsValue(nameSpan, name, alias.TargetName);
                return new BoundErrorExpr(call.Span);
            case DotNetTypeSymbol dotnet:
                return BindDotNetConstruct(call, new DotNetType(dotnet.Type), nameSpan);
            case FunctionSymbol { Builtin: BuiltinFunction.List or BuiltinFunction.Iter or BuiltinFunction.Next or BuiltinFunction.Anext } function:
                ReportKeywordArguments(call, function.Name);
                return BindIteratorBuiltin(call, function, expected);
            case FunctionSymbol { Builtin: BuiltinFunction.Repr or BuiltinFunction.Hash } function:
                ReportKeywordArguments(call, function.Name);
                return BindReprOrHash(call, function, function.Builtin!.Value);
            case FunctionSymbol { Builtin: BuiltinFunction.Print or BuiltinFunction.Input or BuiltinFunction.Open or BuiltinFunction.Exit or BuiltinFunction.GetEnv } io:
                return BindIoBuiltin(call, io, io.Builtin!.Value);
            case FunctionSymbol { Builtin: BuiltinFunction.Gather or BuiltinFunction.Sleep or BuiltinFunction.WaitFor or BuiltinFunction.Timeout or BuiltinFunction.Run } asyncio:
                ReportKeywordArguments(call, asyncio.Name);
                return BindAsyncioCall(call, asyncio, expected);
            case FunctionSymbol { Builtin: { } builtin } function:
                ReportKeywordArguments(call, function.Name);
                return BindBuiltinCall(call, function, builtin, expected);
            case FunctionSymbol function:
                return BindUserCall(call, function, expected, nameSpan);
            default:
                BindArguments(call);
                // A variable whose type already has an error was reported where it was declared.
                if (symbol is VariableSymbol { Type.ContainsError: false } or EnumSymbol or ModuleSymbol)
                    _diagnostics.ReportNotAFunction(nameSpan, name);
                return new BoundErrorExpr(call.Span);
        }
    }

    /// <summary>
    /// A call to a function value (SPEC §3.7), with positional arguments only; the caller has
    /// reported any keyword arguments. <paramref name="name"/> is null when the callee isn't a name.
    /// </summary>
    private BoundExpr BindInvoke(CallExpr call, BoundExpr callee, string? name, TextSpan calleeSpan)
    {
        if (ReportIfOptional(callee, calleeSpan))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        var function = (FunctionType)callee.Type;
        if (call.Arguments.Length != function.Parameters.Length)
        {
            var expected = Arguments(function.Parameters.Length);
            if (name is null)
                _diagnostics.ReportFunctionArgumentCount(calleeSpan, expected, call.Arguments.Length);
            else
                _diagnostics.ReportWrongArgumentCount(calleeSpan, name, expected, call.Arguments.Length);
            return new BoundInvoke(callee, BindArguments(call), function.Result, call.Span);
        }
        var arguments = call.Arguments.Select((a, i) => BindExpected(a.Value, function.Parameters[i])).ToImmutableArray();
        return new BoundInvoke(callee, arguments, function.Result, call.Span);
    }

    /// <summary>A nested <c>def</c> called by name, which takes keywords and defaults like any function (SPEC §4.2).</summary>
    private BoundExpr BindLocalCall(CallExpr call, BoundVariable callee, FunctionSymbol function, TextSpan nameSpan)
    {
        var (arguments, shape, _, valid) = BindCallArguments(call, function, function.Name, nameSpan, [], t => t, function.ReturnType, null);
        if (!valid)
            return new BoundInvoke(callee, arguments, function.ReturnType, call.Span);
        return new BoundInvoke(callee, arguments, function.ReturnType, call.Span) { Shape = shape, Function = function };
    }

    private BoundExpr BindUserCall(CallExpr call, FunctionSymbol function, TaipanType? expected, TextSpan nameSpan)
    {
        var (bound, shape, map, valid) = BindCallArguments(call, function, function.Name, nameSpan, function.TypeParameters, t => t, function.ReturnType, expected);
        if (!valid)
            return new BoundCall(function, bound, function.TypeParameters.IsEmpty ? function.ReturnType : TaipanType.Error, call.Span);
        return new BoundCall(function, bound, TypeChecker.Substitute(function.ReturnType, map), call.Span)
        {
            TypeArguments = [.. function.TypeParameters.Select(p => map[p])],
            Shape = shape,
        };
    }

    /// <summary>
    /// Binds arguments against parameter types, inferring any type parameters first (SPEC §3.6):
    /// from the expected type of the result, then from each argument in order, lambdas last (§3.7).
    /// Each argument gets its parameter as its expected type once that type is fully known, then is
    /// checked against it.
    /// </summary>
    private (ImmutableArray<BoundExpr> Arguments, Dictionary<TypeParameterSymbol, TaipanType> Map) InferAndBind(
        string name, TextSpan nameSpan, ImmutableArray<TypeParameterSymbol> typeParameters,
        IReadOnlyList<TaipanType> parameterTypes, TaipanType resultType, IReadOnlyList<Expr> arguments, TaipanType? expected)
    {
        var map = new Dictionary<TypeParameterSymbol, TaipanType>();
        if (typeParameters.IsEmpty)
            return ([.. arguments.Select((a, i) => BindExpected(a, parameterTypes[i]))], map);

        var inference = new TypeInference(typeParameters, map, _diagnostics);
        if (expected is not null)
            inference.Unify(resultType, expected, fromArgument: null);

        var bound = new BoundExpr[arguments.Count];
        // Lambdas, and generic functions, built-ins and constructors as values, come last (SPEC §3.7, §3.18).
        bool Deferred(Expr argument) => argument is LambdaExpr || CallableValueTarget(argument) is { } target && NeedsExpectedType(target);
        foreach (var i in Enumerable.Range(0, arguments.Count).OrderBy(i => Deferred(arguments[i]) ? 1 : 0))
        {
            var parameter = parameterTypes[i];
            var argument = arguments[i] is LambdaExpr lambda && TypeChecker.Unwrap(parameter) is FunctionType callable && !inference.IsKnown(callable)
                ? BindLambdaArgument(lambda, callable, inference, map)
                : Deferred(arguments[i]) && TypeChecker.Unwrap(parameter) is FunctionType wanted && !inference.IsKnown(wanted)
                ? BindCallableArgument(arguments[i], wanted, inference, map)
                : BindValue(arguments[i], inference.IsKnown(parameter) ? TypeChecker.Substitute(parameter, map) : null);
            inference.Unify(parameter, argument.Type, fromArgument: arguments[i].Span);
            bound[i] = argument;
        }

        foreach (var parameter in typeParameters.Where(p => !map.ContainsKey(p)))
        {
            _diagnostics.ReportCannotInferTypeArgument(nameSpan, parameter.Name, name);
            map[parameter] = TaipanType.Error;
        }
        foreach (var parameter in typeParameters.Where(p => !p.Constraints.IsEmpty))
            map[parameter] = CheckConstraint(parameter, map[parameter], nameSpan);
        foreach (var parameter in typeParameters.Where(p => p.Bound is not null))
        {
            // A class bound: the type argument is the class, or derives from it (SPEC §3.18).
            if (parameter.ClassBound is { } classBound)
            {
                if (map[parameter] is var argument && (argument.ContainsError || argument is not OptionalOf && TypeChecker.IsAssignable(argument, classBound)))
                    continue;
                _diagnostics.ReportNotDerived(nameSpan, map[parameter], classBound);
                map[parameter] = TaipanType.Error;
                continue;
            }
            if (Unsatisfied(map[parameter], parameter.Bound!) is not { } why)
                continue;
            _diagnostics.ReportUnsatisfied(nameSpan, map[parameter], parameter.Bound!.ToString(), declaredBy: null, why);
            map[parameter] = TaipanType.Error;
        }
        // A type parameter inferred as py takes its arguments converted to Python (SPEC §3.6, §10.3).
        for (var i = 0; i < bound.Length; i++)
        {
            var parameter = TypeChecker.Substitute(parameterTypes[i], map);
            CheckAssignable(bound[i], parameter, arguments[i].Span);
            bound[i] = Convert(bound[i], parameter);
        }
        return ([.. bound], map);
    }

    /// <summary>
    /// A lambda for a parameter of function type that still mentions uninferred type parameters
    /// (SPEC §3.7). Its parameter types must be known by now (TP0206 otherwise, after which those
    /// type parameters aren't reported again); its result is inferred from its body if it isn't.
    /// </summary>
    private BoundExpr BindLambdaArgument(LambdaExpr lambda, FunctionType callable, TypeInference inference, Dictionary<TypeParameterSymbol, TaipanType> map)
    {
        if (lambda.Parameters.Length == callable.Parameters.Length
            && callable.Parameters.Select((p, i) => (p, i)).FirstOrDefault(pair => !inference.IsKnown(pair.p)) is { p: not null } unknown)
        {
            var parameter = lambda.Parameters[unknown.i];
            _diagnostics.ReportCannotInferLambdaParameter(parameter.Span, parameter.Text);
            inference.GiveUp(callable);
            BindLambdaAs(lambda, [.. lambda.Parameters.Select(_ => TaipanType.Error)], TaipanType.Error);
            return new BoundErrorExpr(lambda.Span);
        }
        var parameterTypes = callable.Parameters.Select(p => inference.IsKnown(p) ? TypeChecker.Substitute(p, map) : TaipanType.Error);
        return BindLambdaAs(lambda, [.. parameterTypes], inference.IsKnown(callable.Result) ? TypeChecker.Substitute(callable.Result, map) : null);
    }

    /// <summary>
    /// A generic function, built-in or constructor for a parameter of function type that still
    /// mentions uninferred type parameters (SPEC §3.18): as a lambda argument is (§3.7).
    /// </summary>
    private BoundExpr BindCallableArgument(Expr argument, FunctionType callable, TypeInference inference, Dictionary<TypeParameterSymbol, TaipanType> map)
    {
        var name = argument is MemberExpr member ? member.Member.Text : _source.ToString(argument.Span);
        if (callable.Parameters.Any(p => !inference.IsKnown(p)))
        {
            _diagnostics.ReportCannotInferValue(argument.Span, name);
            inference.GiveUp(callable);
            return new BoundErrorExpr(argument.Span);
        }
        var result = inference.IsKnown(callable.Result) ? TypeChecker.Substitute(callable.Result, map) : null;
        return BindCallableAs(argument, CallableValueTarget(argument)!, argument.Span, [.. callable.Parameters.Select(p => TypeChecker.Substitute(p, map))], result);
    }

    /// <summary>
    /// An inferred type argument must be one of its parameter's constraint types; an int becomes
    /// float when only float is listed (SPEC §3.6). Otherwise TP0200, and the result is the error type.
    /// </summary>
    private TaipanType CheckConstraint(TypeParameterSymbol parameter, TaipanType inferred, TextSpan span)
    {
        if (inferred.ContainsError || parameter.Constraints.Contains(inferred))
            return inferred;
        if (inferred == TaipanType.Int && parameter.Constraints.Contains(TaipanType.Float))
            return TaipanType.Float;
        _diagnostics.ReportConstraintViolation(span, parameter.Name, parameter.Constraints.Select(c => c.ToString()), inferred);
        return TaipanType.Error;
    }

    /// <summary>Unification of parameter types with argument types, for type-argument inference.</summary>
    private sealed class TypeInference(
        ImmutableArray<TypeParameterSymbol> parameters, Dictionary<TypeParameterSymbol, TaipanType> map, DiagnosticBag diagnostics)
    {
        private readonly HashSet<TypeParameterSymbol> _fromArguments = [];

        /// <summary>Whether every type parameter in <paramref name="type"/> is already inferred.</summary>
        public bool IsKnown(TaipanType type) => type switch
        {
            TypeParameterType p => !parameters.Contains(p.Symbol) || map.ContainsKey(p.Symbol),
            OptionalOf o => IsKnown(o.Inner),
            ListOfType l => IsKnown(l.Element),
            VarTupleOfType v => IsKnown(v.Element),
            SetOfType s => IsKnown(s.Element),
            DictOfType d => IsKnown(d.Key) && IsKnown(d.Value),
            ClassType s => s.TypeArguments.All(IsKnown),
            EnumType e => e.TypeArguments.All(IsKnown),
            DotNetType n => n.TypeArguments.All(IsKnown),
            FunctionType f => f.Parameters.All(IsKnown) && IsKnown(f.Result),
            TupleOfType t => t.Elements.All(IsKnown),
            AwaitableOfType a => IsKnown(a.Result),
            IteratorOfType i => IsKnown(i.Element),
            IterableOfType i => IsKnown(i.Element),
            _ => true,
        };

        /// <summary>Marks the uninferred type parameters in <paramref name="type"/> as errors, so they aren't reported.</summary>
        public void GiveUp(TaipanType type)
        {
            switch (type)
            {
                case TypeParameterType p when parameters.Contains(p.Symbol):
                    map.TryAdd(p.Symbol, TaipanType.Error);
                    break;
                case OptionalOf o:
                    GiveUp(o.Inner);
                    break;
                case ListOfType l:
                    GiveUp(l.Element);
                    break;
                case VarTupleOfType v:
                    GiveUp(v.Element);
                    break;
                case SetOfType s:
                    GiveUp(s.Element);
                    break;
                case DictOfType d:
                    GiveUp(d.Key);
                    GiveUp(d.Value);
                    break;
                case ClassType s:
                    s.TypeArguments.ToList().ForEach(GiveUp);
                    break;
                case DotNetType n:
                    n.TypeArguments.ToList().ForEach(GiveUp);
                    break;
                case EnumType e:
                    e.TypeArguments.ToList().ForEach(GiveUp);
                    break;
                case FunctionType f:
                    f.Parameters.ToList().ForEach(GiveUp);
                    GiveUp(f.Result);
                    break;
                case TupleOfType t:
                    t.Elements.ToList().ForEach(GiveUp);
                    break;
            }
        }

        /// <summary>
        /// Matches <paramref name="pattern"/> (a parameter or result type) against an actual type.
        /// Disagreements between arguments are TP0200, except that int and float give float. An
        /// argument overrides what the expected type inferred unless it's assignable to it.
        /// </summary>
        public void Unify(TaipanType pattern, TaipanType actual, TextSpan? fromArgument)
        {
            if (actual.ContainsError)
                return;
            switch (pattern, actual)
            {
                case (TypeParameterType p, _) when parameters.Contains(p.Symbol):
                    Bind(p.Symbol, actual, fromArgument);
                    break;
                case (OptionalOf p, OptionalOf a):
                    Unify(p.Inner, a.Inner, fromArgument);
                    break;
                case (OptionalOf, NoneType):
                    break;
                case (OptionalOf p, _):
                    Unify(p.Inner, actual, fromArgument);
                    break;
                case (ListOfType p, ListOfType a):
                    Unify(p.Element, a.Element, fromArgument);
                    break;
                case (VarTupleOfType p, VarTupleOfType a):
                    Unify(p.Element, a.Element, fromArgument);
                    break;
                case (SetOfType p, SetOfType a):
                    Unify(p.Element, a.Element, fromArgument);
                    break;
                // An Iterable or Iterator takes its element type from what's passed (SPEC §3.13).
                case (IterableOfType p, _) when TypeChecker.ElementOf(actual) is { } element:
                    Unify(p.Element, element, fromArgument);
                    break;
                case (IteratorOfType p, _) when TypeChecker.IteratorElementOf(actual) is { } produced:
                    Unify(p.Element, produced, fromArgument);
                    break;
                case (AwaitableOfType p, AwaitableOfType a):
                    Unify(p.Result, a.Result, fromArgument);
                    break;
                case (DictOfType p, DictOfType a):
                    Unify(p.Key, a.Key, fromArgument);
                    Unify(p.Value, a.Value, fromArgument);
                    break;
                case (ClassType p, ClassType a) when p.Symbol == a.Symbol:
                    foreach (var (pa, aa) in p.TypeArguments.Zip(a.TypeArguments))
                        Unify(pa, aa, fromArgument);
                    break;
                // A generic .NET type, or a base or interface of the argument's (SPEC §13.13).
                case (DotNetType p, DotNetType a) when p.IsGeneric && DotNetTypes.AsBase(a, p.Symbol) is { } seen:
                    foreach (var (pa, aa) in p.TypeArguments.Zip(seen.TypeArguments))
                        Unify(pa, aa, fromArgument);
                    break;
                // A generic protocol from what the argument has (SPEC §3.18): each member's types, matched exactly.
                case (ClassType { Symbol.IsProtocol: true } p, _) when TypeChecker.IsProtocolValue(actual) && p.Symbol != (actual as ClassType)?.Symbol:
                {
                    var required = Protocols.Required(p, actual);
                    foreach (var field in p.Symbol.Fields)
                    {
                        if (Protocols.FieldOf(actual, field.Name) is { } fieldType)
                            Unify(TypeChecker.Substitute(field.Type, required), fieldType, fromArgument);
                    }
                    foreach (var method in p.Symbol.Methods.Values)
                    {
                        if (Protocols.MethodOf(actual, method.Name) is not var (found, map) || found.Parameters.Length != method.Parameters.Length)
                            continue;
                        foreach (var (wanted, given) in method.Parameters.Zip(found.Parameters))
                            Unify(TypeChecker.Substitute(wanted.Type, required), TypeChecker.Substitute(given.Type, map), fromArgument);
                        Unify(TypeChecker.Substitute(method.ReturnType, required), TypeChecker.SubstituteSelf(TypeChecker.Substitute(found.ReturnType, map), actual), fromArgument);
                    }
                    break;
                }
                // Through bases (SPEC §3.18): a Labeled[int] argument for a Box[T], or a Sub[T] result into a Box[float].
                case (ClassType p, ClassType a) when TypeChecker.AsBase(a, p.Symbol) is { } up:
                    Unify(p, up, fromArgument);
                    break;
                case (ClassType p, ClassType a) when TypeChecker.AsBase(p, a.Symbol) is { } up:
                    Unify(up, a, fromArgument);
                    break;
                case (EnumType p, EnumType a) when p.Symbol == a.Symbol:
                    foreach (var (pa, aa) in p.TypeArguments.Zip(a.TypeArguments))
                        Unify(pa, aa, fromArgument);
                    break;
                case (TupleOfType p, TupleOfType a) when p.Elements.Length == a.Elements.Length:
                    foreach (var (pe, ae) in p.Elements.Zip(a.Elements))
                        Unify(pe, ae, fromArgument);
                    break;
                case (FunctionType p, FunctionType a) when p.Parameters.Length == a.Parameters.Length:
                    foreach (var (pa, aa) in p.Parameters.Zip(a.Parameters))
                        Unify(pa, aa, fromArgument);
                    Unify(p.Result, a.Result, fromArgument);
                    break;
            }
        }

        private void Bind(TypeParameterSymbol parameter, TaipanType actual, TextSpan? fromArgument)
        {
            // A function's missing result can't be a type argument (SPEC §3.7).
            if (actual is NoneType or NothingType)
                return;
            if (!map.TryGetValue(parameter, out var existing))
            {
                map[parameter] = actual;
                if (fromArgument is not null)
                    _fromArguments.Add(parameter);
                return;
            }
            if (existing == actual || fromArgument is not { } span || existing.ContainsError)
                return;
            if (!_fromArguments.Contains(parameter))
            {
                // Inferred from the expected type: `Box(1)` into a Box[float] keeps float, but
                // `first(xs)` with xs: list[int?] into an int? needs T = int?.
                if (!TypeChecker.IsAssignable(actual, existing))
                {
                    map[parameter] = actual;
                    _fromArguments.Add(parameter);
                }
                return;
            }
            if (existing.IsNumeric && actual.IsNumeric)
            {
                map[parameter] = TaipanType.Float;
                return;
            }
            if (Join(existing, actual) is { } join)
            {
                map[parameter] = join;
                return;
            }
            diagnostics.ReportConflictingTypeArgument(span, parameter.Name, existing, actual);
            map[parameter] = TaipanType.Error;
        }
    }

    /// <summary>Two classes mix to their nearest common base (SPEC §3.2); null for anything else.</summary>
    private static TaipanType? Join(TaipanType a, TaipanType b) =>
        a is ClassType x && b is ClassType y && x.Symbol.CommonBase(y.Symbol) is { } common
        && TypeChecker.AsBase(x, common) is { } left && left == TypeChecker.AsBase(y, common)
            ? left
            : null;

    private void ReportAmbiguous(AmbiguousVariantSymbol ambiguous, TextSpan span) =>
        _diagnostics.ReportAmbiguousVariant(span, ambiguous.Name, ambiguous.Variants.Select(v => v.Enum.Name));

    /// <summary>
    /// Binds the arguments of a call that has already been reported, for their own errors. A
    /// lambda among them has nothing to infer its parameter types from, so they're the error type.
    /// </summary>
    private ImmutableArray<BoundExpr> BindArguments(CallExpr call) => BindArguments([.. call.Arguments.Select(a => a.Value)]);

    private ImmutableArray<BoundExpr> BindArguments(ImmutableArray<Expr> expressions) => [.. expressions.Select(BindIgnored)];

    private BoundExpr BindIgnored(Expr expr) =>
        expr is LambdaExpr lambda
            ? BindLambdaAs(lambda, [.. lambda.Parameters.Select(_ => TaipanType.Error)], TaipanType.Error)
            : BindValue(expr);

    /// <summary>Binds arguments that are used as they are, each without an expected type.</summary>
    private ImmutableArray<BoundExpr> BindArgumentValues(CallExpr call) =>
        [.. call.Arguments.Select(a => BindValue(a.Value))];

    /// <summary>
    /// Binds arguments against parameter types: each one gets its parameter as its expected type.
    /// A count mismatch is TP0209, reported at the callee.
    /// </summary>
    private ImmutableArray<BoundExpr> BindParameterArguments(CallExpr call, string name, ImmutableArray<TaipanType> parameters)
    {
        if (call.Arguments.Length != parameters.Length)
        {
            var countSpan = call.Callee is MemberExpr member ? member.Member.Span : call.Callee.Span;
            _diagnostics.ReportWrongArgumentCount(countSpan, name, Arguments(parameters.Length), call.Arguments.Length);
            return BindArguments(call);
        }
        return [.. call.Arguments.Select((a, i) => BindExpected(a.Value, parameters[i]))];
    }

    /// <summary>TP0218 for a keyword argument to a built-in, or to a function value when <paramref name="function"/> is null.</summary>
    private void ReportKeywordArguments(CallExpr call, string? function)
    {
        if (call.Arguments.FirstOrDefault(a => a.Name is not null) is { } keyword)
            _diagnostics.ReportKeywordArguments(keyword.Span, function);
    }

    /// <summary>Null for the common shape: every parameter given, positionally.</summary>
    private static ArgumentShape? Simplify(ArgumentShape shape) =>
        shape.InOrder && shape.Parameters.Length == shape.ParameterCount && shape.Sources.IsEmpty ? null : shape;

    /// <summary>
    /// A dataclass's constructor takes its fields; a plain class's takes its <c>__init__</c>'s
    /// parameters, matched like a function's (SPEC §4.3).
    /// </summary>
    private BoundExpr BindConstruct(CallExpr call, ClassSymbol classSymbol, TaipanType? expected, TextSpan nameSpan, bool checkAbstract = true)
    {
        if (classSymbol.IsProtocol)
        {
            BindArguments(call);
            _diagnostics.ReportProtocolAsClass(nameSpan, classSymbol.Name, constructing: true);
            return new BoundErrorExpr(call.Span);
        }
        // An abstract class has no instances of its own (SPEC §4.8).
        if (checkAbstract && classSymbol.IsAbstract)
            _diagnostics.ReportAbstractInstantiation(nameSpan, classSymbol.Name, classSymbol.AbstractMethods.Select(m => $"{m.Owner!.Name}.{m.Name}"));
        if (classSymbol.IsDataclass)
        {
            var (arguments, isKeyword, map) = BindFieldArguments(
                call, classSymbol.Name, classSymbol.AllFields, classSymbol.TypeParameters, classSymbol.Type, expected);
            return arguments is { } valid
                ? new BoundConstruct((ClassType)TypeChecker.Substitute(classSymbol.Type, map), valid, isKeyword, call.Span)
                : new BoundErrorExpr(call.Span);
        }

        if (BuiltinClasses.IsRuntimeOnly(classSymbol))
        {
            _diagnostics.ReportCannotConstruct(nameSpan, classSymbol.Name);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        var constructor = classSymbol.Constructor;
        var (bound, shape, typeArguments, matched) = BindCallArguments(
            call, constructor, classSymbol.Name, nameSpan, classSymbol.TypeParameters, t => t, classSymbol.Type, expected);
        if (!matched)
            return new BoundErrorExpr(call.Span);
        return new BoundNew((ClassType)TypeChecker.Substitute(classSymbol.Type, typeArguments), constructor, bound, call.Span) { Shape = shape };
    }

    /// <summary><c>isinstance(x, C)</c> (SPEC §6): x a class or optional class, and C a class that could be its runtime class.</summary>
    private BoundExpr BindIsInstance(CallExpr call, TextSpan nameSpan)
    {
        ReportKeywordArguments(call, "isinstance");
        if (call.Arguments.Length != 2)
        {
            BindArguments(call);
            _diagnostics.ReportWrongArgumentCount(nameSpan, "isinstance", Arguments(2), call.Arguments.Length);
            return new BoundErrorExpr(call.Span);
        }
        var operand = Upcast(BindValue(call.Arguments[0].Value));
        if (operand.Type == TaipanType.Py)
            return BindPyIsInstance(operand, call);
        // An object, a .NET value, or a .NET type to test for (SPEC §13.17, M43).
        if (TypeChecker.Unwrap(operand.Type) is ObjectType or DotNetType || DotNetTypeNamed(call.Arguments[1].Value) is not null)
            return BindTypeTest(operand, call);
        var operandClass = TypeChecker.Unwrap(operand.Type) as ClassType;
        if (operandClass is null && !operand.Type.ContainsError)
        {
            _diagnostics.ReportIsInstanceNeedsClass(call.Arguments[0].Value.Span, operand.Type);
            operand = new BoundErrorExpr(operand.Span);
        }
        // A tuple of classes tests any of them, and narrows to their nearest common base (SPEC §6, M26).
        if (call.Arguments[1].Value is TupleExpr tuple)
        {
            var classes = tuple.Elements.Select(e => ResolveClass(e, TypeChecker.Unwrap(operand.Type))).ToList();
            if (classes.Any(c => c is null) || operand.Type.ContainsError)
                return new BoundErrorExpr(call.Span);
            var tested = classes.Select(c => c!).ToImmutableArray();
            var common = tested.Select(c => c.Symbol).Aggregate((a, b) => a.CommonBase(b) ?? a);
            var narrowed = tested.All(c => c.Symbol.DerivesFrom(common)) ? common.Type : operandClass!;
            return new BoundIsInstance(operand, (ClassType)narrowed, call.Span) { Tested = tested };
        }
        var target = ResolveClass(call.Arguments[1].Value, TypeChecker.Unwrap(operand.Type));
        return target is null || operand.Type.ContainsError
            ? new BoundErrorExpr(call.Span)
            : new BoundIsInstance(operand, target, call.Span);
    }

    /// <summary>
    /// <c>isinstance(v, T)</c> on a py value (SPEC §3.18): int, float, str and bool test what
    /// <c>as</c> accepts, and narrow; list, dict, tuple and Python classes are Python's test.
    /// </summary>
    private BoundExpr BindPyIsInstance(BoundExpr operand, CallExpr call)
    {
        var target = call.Arguments[1].Value;
        string? name = target is NameExpr n && _scope.Lookup(n.Name) is not VariableSymbol ? n.Name : null;
        switch (name)
        {
            case "int" or "float" or "str" or "bool":
                var type = name switch { "int" => TaipanType.Int, "float" => TaipanType.Float, "str" => TaipanType.Str, _ => TaipanType.Bool };
                return new BoundPyIsInstance(operand, type, null, null, call.Span);
            case "list" or "dict" or "tuple":
                return new BoundPyIsInstance(operand, null, name, null, call.Span);
            case not null:
                _diagnostics.ReportPyIsInstanceTarget(target.Span, name);
                return new BoundErrorExpr(call.Span);
        }
        if (target is TupleExpr or IndexExpr)
        {
            _diagnostics.ReportPyIsInstanceTarget(target.Span, target is TupleExpr ? "tuple" : _source.ToString(target.Span));
            return new BoundErrorExpr(call.Span);
        }
        var pyClass = BindValue(target);
        if (pyClass.Type.ContainsError)
            return new BoundErrorExpr(call.Span);
        if (pyClass.Type != TaipanType.Py)
        {
            _diagnostics.ReportPyIsInstanceTarget(target.Span, pyClass.Type.ToString());
            return new BoundErrorExpr(call.Span);
        }
        return new BoundPyIsInstance(operand, null, null, pyClass, call.Span);
    }

    /// <summary>
    /// The class an expression names in <c>isinstance</c>: not generic, and related to the class of
    /// the value tested (<paramref name="tested"/>), since otherwise the test could never pass.
    /// </summary>
    private ClassType? ResolveClass(Expr expr, TaipanType tested)
    {
        Symbol? symbol;
        switch (expr)
        {
            case NameExpr name:
                symbol = LookupName(name.Name, name.Span);
                break;
            case MemberExpr member when TryModuleMember(member, out var definition):
                symbol = definition;
                break;
            default:
                BindValue(expr);
                _diagnostics.ReportNotAClass(expr.Span, _source.ToString(expr.Span));
                return null;
        }
        switch (symbol)
        {
            case null:
                return null;
            case ClassSymbol { TypeParameters.IsEmpty: false } generic:
                _diagnostics.ReportGenericClassTest(expr.Span, generic.Name);
                return null;
            case ClassSymbol { IsProtocol: true } protocol:
                _diagnostics.ReportProtocolAsClass(expr.Span, protocol.Name, constructing: false);
                return null;
            case ClassSymbol classSymbol:
                return CheckRelated(new ClassType(classSymbol), tested, expr.Span);
            case TypeAliasSymbol alias:
                _diagnostics.ReportAliasAsValue(expr.Span, alias.Name, alias.TargetName);
                return null;
            default:
                _diagnostics.ReportNotAClass(expr.Span, _source.ToString(expr.Span));
                return null;
        }
    }

    /// <summary>TP0200 unless a <paramref name="tested"/> value could be a <paramref name="target"/>: one derives from the other.</summary>
    private ClassType? CheckRelated(ClassType target, TaipanType tested, TextSpan span)
    {
        if (tested is ClassType testedClass && !CouldBe(target.Symbol, testedClass))
        {
            _diagnostics.ReportNeverInstance(span, tested, target.Symbol.Name);
            return null;
        }
        return target;
    }

    /// <summary>
    /// Whether a value of type <paramref name="tested"/> could be a <paramref name="target"/>: one
    /// class derives from the other, or for a protocol value, the class or one deriving from it
    /// satisfies the protocol (SPEC §3.18).
    /// </summary>
    private bool CouldBe(ClassSymbol target, ClassType tested) =>
        tested.Symbol.IsProtocol
            ? _allClasses.Append(target).Any(c => c.DerivesFrom(target) && c.TypeParameters.IsEmpty && Protocols.Unsatisfied(c.Type, tested) is null)
            : TypeChecker.AreRelated(tested.Symbol, target);

    /// <summary>
    /// <c>super().m(…)</c> in a method of a class with a base: the base's <c>m</c>, found from the
    /// base upwards, or its constructor for <c>__init__</c> (SPEC §4.3).
    /// </summary>
    private BoundExpr BindSuperCall(CallExpr call, MemberExpr member, CallExpr superCall)
    {
        var name = member.Member.Text;
        var superSpan = superCall.Callee.Span;
        if (_outerFunction?.Self is not { } self || self.Type is not ClassType { Symbol: var owner })
        {
            BindArguments(call);
            _diagnostics.ReportSuperOutsideMethod(superSpan);
            return new BoundErrorExpr(call.Span);
        }
        // A .NET base class's constructor (SPEC §13.16, M43).
        if (owner.Base is null && owner.DotNetBaseClass is { } dotnetBase)
        {
            if (name == "__init__" && _outerFunction != owner.Init)
                _diagnostics.ReportSuperInitOutsideInit(superSpan);
            if (name == "__init__")
                return BindDotNetSuperInit(call, dotnetBase, member.Member.Span);
            BindArguments(call);
            _diagnostics.ReportNoMethod(member.Member.Span, dotnetBase, name);
            return new BoundErrorExpr(call.Span);
        }
        if (owner.Base is not { } baseClass)
        {
            BindArguments(call);
            _diagnostics.ReportNoBaseClass(superSpan, owner.Name);
            return new BoundErrorExpr(call.Span);
        }
        if (name == "__init__" && _outerFunction != owner.Init)
            _diagnostics.ReportSuperInitOutsideInit(superSpan);
        var method = name == "__init__" ? baseClass.Constructor : baseClass.FindMethod(name);
        if (method is null)
        {
            BindArguments(call);
            _diagnostics.ReportNoMethod(member.Member.Span, baseClass.Type, name);
            return new BoundErrorExpr(call.Span);
        }
        if (method.IsAbstract)
            _diagnostics.ReportAbstractSuper(member.Member.Span, method.Owner!.Name, name);
        var superResult = TypeChecker.SubstituteSelf(method.ReturnType, self.Type);
        // A generic base's members take its type arguments (SPEC §3.18).
        var map = TypeChecker.MapOf(self.Type);
        superResult = TypeChecker.Substitute(superResult, map);
        var (arguments, shape, _, valid) = BindCallArguments(call, method, name, member.Member.Span, [], t => TypeChecker.Substitute(t, map), superResult, null);
        return new BoundSuperCall(method, self, arguments, superResult, call.Span) { Shape = valid ? shape : null };
    }

    private BoundExpr BindConstructVariant(CallExpr call, VariantSymbol variant, TaipanType? expected)
    {
        if (variant.Fields.IsEmpty)
        {
            // A variant without a payload is a value, not a function (SPEC §4.4).
            BindArguments(call);
            _diagnostics.ReportNotAFunction(call.Callee.Span, variant.Name);
            return new BoundErrorExpr(call.Span);
        }
        var enumSymbol = variant.Enum;
        var (arguments, isKeyword, map) = BindFieldArguments(call, variant.Name, variant.Fields, enumSymbol.TypeParameters, enumSymbol.Type, expected);
        return arguments is { } valid
            ? new BoundConstructVariant(variant, (EnumType)TypeChecker.Substitute(enumSymbol.Type, map), valid, isKeyword, call.Span)
            : new BoundErrorExpr(call.Span);
    }

    /// <summary>
    /// Constructor arguments for a dataclass or variant (SPEC §4.6), inferring a generic owner's type
    /// arguments (§3.6). Positional ones set the fields in order, then keywords set the rest, so
    /// every field is given exactly once. The arguments are null if any of them is wrong.
    /// </summary>
    private (ImmutableArray<(FieldSymbol, BoundExpr)>? Arguments, bool IsKeyword, Dictionary<TypeParameterSymbol, TaipanType> Map) BindFieldArguments(
        CallExpr call, string owner, ImmutableArray<FieldSymbol> fields, ImmutableArray<TypeParameterSymbol> typeParameters,
        TaipanType ownerType, TaipanType? expected)
    {
        var isKeyword = call.Arguments.Any(a => a.Name is not null);
        var positional = call.Arguments.TakeWhile(a => a.Name is null).Count();
        var required = fields.Count(f => !f.HasDefault);
        var valid = true;
        var chosen = new List<(FieldSymbol Field, Expr Value)>();
        if ((!isKeyword && positional < required) || positional > fields.Length)
        {
            var countSpan = call.Callee is MemberExpr member ? member.Member.Span : call.Callee.Span;
            var expectedCount = required == fields.Length ? Arguments(fields.Length) : $"{required} to {fields.Length} arguments";
            _diagnostics.ReportWrongArgumentCount(countSpan, owner, expectedCount, call.Arguments.Length);
            BindArguments(call);
            return (null, false, []);
        }
        chosen.AddRange(fields.Zip(call.Arguments.Take(positional).Select(a => a.Value)));
        if (isKeyword || positional < fields.Length)
        {
            var given = fields.Take(positional).Select(f => f.Name).ToHashSet();
            foreach (var argument in call.Arguments.Skip(positional))
            {
                var name = argument.Name!;
                var field = fields.FirstOrDefault(f => f.Name == name.Text);
                if (field is not null)
                    Index(name.Span, field);
                if (field is null)
                {
                    _diagnostics.ReportNoField(name.Span, owner, name.Text);
                    BindValue(argument.Value);
                    valid = false;
                    continue;
                }
                if (!given.Add(name.Text))
                {
                    _diagnostics.ReportRepeatedField(name.Span, name.Text);
                    valid = false;
                }
                chosen.Add((field, argument.Value));
            }
            foreach (var missing in fields.Where(f => !f.HasDefault && !given.Contains(f.Name)))
            {
                _diagnostics.ReportMissingField(call.Callee.Span, missing.Name, owner);
                valid = false;
            }
        }

        var (bound, map) = InferAndBind(
            owner, call.Callee.Span, typeParameters, [.. chosen.Select(c => TypeChecker.Substitute(c.Field.Type, TypeChecker.MapOf(ownerType)))], ownerType, [.. chosen.Select(c => c.Value)],
            valid ? expected : null);
        ImmutableArray<(FieldSymbol, BoundExpr)> pairs = [.. chosen.Select(c => c.Field).Zip(bound)];
        return (valid ? pairs : null, isKeyword, map);
    }

    /// <summary>
    /// <c>m.x</c> where <c>m</c> names a module: true, with what <c>m</c> defines as <c>x</c>
    /// (null, after TP0201, if nothing). False if the target isn't a module (SPEC §4.7).
    /// </summary>
    private bool TryModuleMember(MemberExpr member, out Symbol? symbol)
    {
        symbol = null;
        if (member.Target is not NameExpr moduleName || _scope.Lookup(moduleName.Name) is not ModuleSymbol module)
            return false;
        Index(moduleName.Span, module);
        RecordReceiver(member.Member.Span, module);
        if (!module.Definitions.TryGetValue(member.Member.Text, out symbol))
            _diagnostics.ReportNoDefinition(member.Member.Span, module.Name, member.Member.Text);
        else
            Index(member.Member.Span, symbol);
        return true;
    }

    /// <summary>
    /// <c>Enum.Variant</c> or <c>m.Enum.Variant</c>: the variant, when <paramref name="member"/>'s
    /// target names an enum that isn't shadowed. Reports TP0210 for a variant the enum lacks.
    /// </summary>
    private bool TryQualifiedVariant(MemberExpr member, out VariantSymbol? variant)
    {
        variant = null;
        var enumSymbol = member.Target switch
        {
            NameExpr enumName => _scope.Lookup(enumName.Name) as EnumSymbol,
            MemberExpr inner when TryModuleMember(inner, out var symbol) => symbol as EnumSymbol,
            _ => null,
        };
        if (enumSymbol is null)
            return false;
        if (member.Target is NameExpr enumReference)
            Index(enumReference.Span, enumSymbol);
        RecordReceiver(member.Member.Span, enumSymbol);
        variant = enumSymbol.FindVariant(member.Member.Text);
        if (variant is null)
            _diagnostics.ReportNoVariant(member.Member.Span, enumSymbol.Type, member.Member.Text);
        else
            Index(member.Member.Span, variant);
        return true;
    }

    /// <summary>
    /// A variant used as a value: only one without a payload is a value by itself. A generic
    /// enum's type arguments come from the expected type (SPEC §3.6).
    /// </summary>
    private BoundExpr BindVariantValue(VariantSymbol variant, TextSpan span, TaipanType? expected)
    {
        if (!variant.Fields.IsEmpty)
        {
            _diagnostics.ReportWrongArgumentCount(span, variant.Name, Arguments(variant.Fields.Length), 0);
            return new BoundErrorExpr(span);
        }

        var enumSymbol = variant.Enum;
        if (enumSymbol.TypeParameters.IsEmpty)
            return new BoundConstructVariant(variant, enumSymbol.Type, [], IsKeyword: false, span);
        if (TypeChecker.Unwrap(expected ?? TaipanType.Error) is EnumType enumType && enumType.Symbol == enumSymbol)
            return new BoundConstructVariant(variant, enumType, [], IsKeyword: false, span);

        _diagnostics.ReportCannotInferTypeArgument(span, enumSymbol.TypeParameters[0].Name, variant.Name);
        return new BoundErrorExpr(span);
    }

    private BoundExpr BindMethodCall(CallExpr call, MemberExpr member, TaipanType? expected)
    {
        if (TryQualifiedVariant(member, out var qualified))
        {
            if (qualified is not null)
                return BindConstructVariant(call, qualified, expected);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        if (TryModuleMember(member, out var definition))
        {
            if (definition is not null)
                return BindSymbolCall(call, definition, member.Member.Text, member.Member.Span, expected);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }

        if (member.Target is CallExpr { Callee: NameExpr { Name: "super" }, Arguments.IsEmpty: true } superCall
            && _scope.Lookup("super") is FunctionSymbol { Builtin: BuiltinFunction.Super })
            return BindSuperCall(call, member, superCall);

        // A type in an imported .NET namespace, constructed: io.FileInfo(path) (SPEC §13.1, M43).
        if (member.Target is NameExpr namespaceName && _scope.Lookup(namespaceName.Name) is DotNetNamespaceSymbol && DotNetTypeNamed(member) is { } inNamespace)
        {
            if (inNamespace is DotNetType created)
                return BindDotNetConstruct(call, created, member.Member.Span);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        if (DotNetTypeNamed(member.Target) is { } namedNet)
        {
            if (namedNet is DotNetType owner)
                return BindDotNetCall(call, null, owner, member.Member);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        // Static and class methods through a class or cls (SPEC §4.8), or a generic class's instantiation (SPEC §4.9).
        if (GenericClassReceiver(member.Target) is { } instantiation)
            return BindClassMethodCall(call, member, instantiation.Symbol, viaCls: false, instantiation);
        if (ClassNamed(member.Target) is { } named)
            return BindClassMethodCall(call, member, named, viaCls: false);
        if (IsCls(member.Target))
            return BindClassMethodCall(call, member, _outerFunction!.Owner!, viaCls: true);

        var receiver = BindValue(member.Target);
        var target = Upcast(receiver);
        var name = member.Member.Text;
        if (target.Type == TaipanType.Py)
            return BindPyCall(call, new BoundPyGetAttr(target, name, member.Span));
        if (target.Type is ListOfType && name == "sort")
            return BindSort(call, name, member.Member.Span, target);
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }

        if (target.Type is DotNetType net)
            return BindDotNetCall(call, target, net, member.Member);

        // Special methods are reached through operators and built-ins (SPEC §3.11, §4.8).
        if (SpecialMethods.IsSupported(name) && target.Type is ClassType or TypeParameterType)
        {
            BindArguments(call);
            _diagnostics.ReportSpecialMethodCalled(member.Member.Span, name, SpecialMethods.Form(name, _source.ToString(member.Target.Span)));
            return new BoundErrorExpr(call.Span);
        }

        // A bounded type parameter has its protocol's methods, with Self as the parameter (SPEC §3.11).
        if (target.Type is TypeParameterType { Symbol.Protocol: { } bound } && bound.Methods.GetValueOrDefault(name) is { } required)
        {
            var self = new Dictionary<TypeParameterSymbol, TaipanType> { [bound.SelfParameter!] = target.Type };
            var resultType = TypeChecker.Substitute(required.ReturnType, self);
            var (requiredArguments, requiredShape, _, requiredValid) = BindCallArguments(
                call, required, name, member.Member.Span, [], t => TypeChecker.Substitute(t, self), resultType, null);
            return new BoundMethodCall(target, required, requiredArguments, resultType, call.Span) { Shape = requiredValid ? requiredShape : null };
        }

        if (target.Type is ClassType { Symbol: var classSymbol })
        {
            // A field holding a function is called like a method (SPEC §3.7).
            if (classSymbol.FindField(name) is { } callableField && IsCallableObject(FieldType(target.Type, callableField)))
            {
                Index(member.Member.Span, callableField);
                return BindObjectCall(call, new BoundFieldAccess(target, callableField, FieldType(target.Type, callableField), member.Span), member.Member.Span);
            }
            if (classSymbol.FindField(name) is { } field && TypeChecker.Unwrap(FieldType(target.Type, field)) is FunctionType)
            {
                ReportKeywordArguments(call, function: null);
                Index(member.Member.Span, field);
                return BindInvoke(call, new BoundFieldAccess(target, field, FieldType(target.Type, field), member.Span), name, member.Member.Span);
            }
            if (name == "__init__")
            {
                BindArguments(call);
                _diagnostics.ReportInitCalled(member.Member.Span);
                return new BoundErrorExpr(call.Span);
            }
            if (classSymbol.FindMethod(name) is { IsClassLevel: true } classLevel)
                return BindStaticCall(call, member, classSymbol, target, viaCls: false, classLevel);
            if (classSymbol.FindMethod(name) is { Kind: MemberKind.Property } && BindInstanceMember(target, classSymbol, member) is { } property)
            {
                if (TypeChecker.Unwrap(property.Type) is FunctionType)
                {
                    ReportKeywordArguments(call, function: null);
                    return BindInvoke(call, property, name, member.Member.Span);
                }
                BindArguments(call);
                _diagnostics.ReportCannotCall(member.Span, property.Type);
                return new BoundErrorExpr(call.Span);
            }
            if (classSymbol.FindMethod(name) is { } method)
            {
                Index(member.Member.Span, method);
                var map = TypeChecker.MapOf(target.Type);
                // On a class-bounded type parameter, Self is the type parameter (SPEC §3.18).
                var returnType = TypeChecker.SubstituteSelf(TypeChecker.Substitute(method.ReturnType, map), receiver.Type);
                var (arguments, shape, _, valid) = BindCallArguments(
                    call, method, name, member.Member.Span, [], t => TypeChecker.Substitute(t, map), returnType, null);
                return new BoundMethodCall(target, method, arguments, returnType, call.Span) { Shape = valid ? shape : null };
            }
            // The .NET base class's methods, then extension methods (SPEC §13.15, §13.16, M43).
            if (classSymbol.FindField(name) is null && DotNetBaseOf(classSymbol) is { } dotnetBase && HasMember(dotnetBase.Symbol, name, isStatic: false))
                return BindDotNetCall(call, target, dotnetBase, member.Member);
            if (classSymbol.FindField(name) is null && BindExtensionCall(call, target, member.Member, null) is { } classExtension)
                return classExtension;
            BindArguments(call);
            if (classSymbol.FindField(name) is not null)
                _diagnostics.ReportFieldNotMethod(member.Member.Span, name);
            else
                _diagnostics.ReportNoMethod(member.Member.Span, target.Type, name, WhyNotNarrowed(target));
            return new BoundErrorExpr(call.Span);
        }

        if (target.Type is ObjectType)
        {
            BindArguments(call);
            _diagnostics.ReportObjectAttribute(member.Member.Span, name);
            return new BoundErrorExpr(call.Span);
        }
        // The methods M35 adds come first; one with an older overload of that arity is bound as before (SPEC §6).
        var older = BuiltinMethodOf(target.Type, name) is { } existing && existing.Parameters.Length == call.Arguments.Length && name != "split";
        if (!older && BindRuntimeMethod(call, target, member.Member, hasOlder: BuiltinMethodOf(target.Type, name) is not null && name != "split") is { } added)
            return added;
        if (BuiltinMethodOf(target.Type, name) is not { } builtin)
        {
            // A list's List<T> methods, then extension methods (SPEC §13.4, §13.15, M43).
            if (target.Type is ListOfType list && ListAsDotNet(list) is var listType && HasMember(listType.Symbol, name, isStatic: false))
                return BindDotNetCall(call, target, listType, member.Member);
            if (RuntimeMethodOf(target.Type, name) is null && BindExtensionCall(call, target, member.Member, null) is { } extension)
                return extension;
            BindArguments(call);
            _diagnostics.ReportNoMethod(member.Member.Span, target.Type, name);
            return new BoundErrorExpr(call.Span);
        }

        ReportKeywordArguments(call, name);
        var builtinArguments = BindParameterArguments(call, name, builtin.Parameters);
        if (builtinArguments.Length != builtin.Parameters.Length)
            return new BoundErrorExpr(call.Span);
        return new BoundBuiltinMethodCall(target, builtin.Method, builtinArguments, builtin.ReturnType, call.Span);
    }

    /// <summary>A built-in method of list, dict or str (SPEC §6), with its parameter and return types.</summary>
    private static (BuiltinMethod Method, ImmutableArray<TaipanType> Parameters, TaipanType ReturnType)? BuiltinMethodOf(TaipanType type, string name)
    {
        var str = TaipanType.Str;
        return (type, name) switch
        {
            (ListOfType list, "append") => (BuiltinMethod.ListAppend, [list.Element], TaipanType.Nothing),
            (ListOfType list, "pop") => (BuiltinMethod.ListPop, [], list.Element),
            (ListOfType list, "insert") => (BuiltinMethod.ListInsert, [TaipanType.Int, list.Element], TaipanType.Nothing),
            (ListOfType list, "contains") => (BuiltinMethod.ListContains, [list.Element], TaipanType.Bool),
            // Optionals don't nest: on a dict[K, V?], get returns V? (SPEC §3.4).
            (DictOfType dict, "get") => (BuiltinMethod.DictGet, [dict.Key], dict.Value is OptionalOf ? dict.Value : new OptionalOf(dict.Value)),
            (DictOfType dict, "keys") => (BuiltinMethod.DictKeys, [], new ListOfType(dict.Key)),
            (DictOfType dict, "values") => (BuiltinMethod.DictValues, [], new ListOfType(dict.Value)),
            (DictOfType dict, "items") => (BuiltinMethod.DictItems, [], new ListOfType(new TupleOfType([dict.Key, dict.Value]))),
            (DictOfType dict, "remove") => (BuiltinMethod.DictRemove, [dict.Key], TaipanType.Bool),
            (PrimitiveType { Name: "str" }, "upper") => (BuiltinMethod.StrUpper, [], str),
            (PrimitiveType { Name: "str" }, "lower") => (BuiltinMethod.StrLower, [], str),
            (PrimitiveType { Name: "str" }, "strip") => (BuiltinMethod.StrStrip, [], str),
            (PrimitiveType { Name: "str" }, "split") => (BuiltinMethod.StrSplit, [str], new ListOfType(str)),
            (PrimitiveType { Name: "str" }, "startswith") => (BuiltinMethod.StrStartsWith, [str], TaipanType.Bool),
            (PrimitiveType { Name: "str" }, "endswith") => (BuiltinMethod.StrEndsWith, [str], TaipanType.Bool),
            (PrimitiveType { Name: "str" }, "replace") => (BuiltinMethod.StrReplace, [str, str], str),
            _ => null,
        };
    }

    /// <summary>
    /// <c>sorted(xs, key=…, reverse=…)</c>, or <c>xs.sort(…)</c> when <paramref name="target"/> is
    /// the list (SPEC §6). The elements, or the keys, must be int, float or str.
    /// </summary>
    private BoundExpr BindSort(CallExpr call, string name, TextSpan nameSpan, BoundExpr? target)
    {
        var positional = call.Arguments.Where(a => a.Name is null).ToList();
        var positionalCount = target is null ? 1 : 0;
        if (positional.Count != positionalCount)
        {
            _diagnostics.ReportWrongArgumentCount(nameSpan, name, Arguments(positionalCount), positional.Count);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }

        var list = target ?? BindValue(positional[0].Value);
        var listSpan = target is null ? positional[0].Value.Span : ((MemberExpr)call.Callee).Target.Span;
        // sorted takes any iterable since M31, into a new list (SPEC §6).
        if (target is null && list.Type is not ListOfType && TypeChecker.ElementOf(list.Type) is { } iterated)
            list = new BoundToList(BindIterable(list, listSpan).Value, new ListOfType(iterated), list.Span);
        var element = RequireList(list, listSpan);
        var keywords = ImmutableArray.CreateBuilder<(string, BoundExpr)>();
        var given = new HashSet<string>();
        var hasKey = false;
        foreach (var argument in call.Arguments.Where(a => a.Name is not null))
        {
            var keyword = argument.Name!;
            if (keyword.Text is not ("key" or "reverse"))
            {
                _diagnostics.ReportNoKeywordArgument(keyword.Span, name, keyword.Text);
                BindIgnored(argument.Value);
            }
            else if (!given.Add(keyword.Text))
            {
                _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                BindIgnored(argument.Value);
            }
            else if (keyword.Text == "reverse")
            {
                keywords.Add(("reverse", BindExpected(argument.Value, TaipanType.Bool)));
            }
            else
            {
                var (key, keyType) = BindCallback(argument.Value, [element], required: null, hint: null, resultName: "K");
                if (!keyType.ContainsError && !IsSortable(keyType))
                    _diagnostics.ReportCannotSortBy(argument.Value.Span, keyType);
                keywords.Add(("key", key));
                hasKey = true;
            }
        }
        if (!hasKey && !element.ContainsError && !IsSortable(element))
            _diagnostics.ReportCannotSort(listSpan, list.Type);
        var type = target is not null ? TaipanType.Nothing : list.Type is ListOfType ? list.Type : TaipanType.Error;
        return new BoundSort(list, keywords.ToImmutable(), InPlace: target is not null, type, call.Span);
    }

    private static bool IsSortable(TaipanType type) =>
        type.IsOrdered || LessThanParameter(type) is { } parameter && TypeChecker.IsAssignable(type, parameter);

    /// <summary>The parameter type of a class's or bounded type parameter's <c>__lt__</c>, if it has one (SPEC §3.11).</summary>
    private static TaipanType? LessThanParameter(TaipanType type) =>
        type is ClassType or TypeParameterType && MethodOf(type, "__lt__") is var (method, map)
            ? TypeChecker.Substitute(method.Parameters[0].Type, map)
            : null;

    /// <summary>The element type of a list argument; TP0200 (or TP0203) and the error type for anything else.</summary>
    private TaipanType RequireList(BoundExpr list, TextSpan span)
    {
        if (list.Type is ListOfType listType)
            return listType.Element;
        if (!list.Type.ContainsError && !ReportIfOptional(list, span))
            _diagnostics.ReportTypeMismatch(span, "list", list.Type);
        return TaipanType.Error;
    }

    /// <summary>
    /// <c>map(f, xs)</c> and <c>filter(f, xs)</c> (SPEC §6). The list comes first, so a lambda's
    /// parameter type is known; an expected <c>list[U]</c> gives a lambda's result for map.
    /// </summary>
    private BoundExpr BindMapFilter(CallExpr call, FunctionSymbol function, BuiltinFunction builtin, TaipanType? expected)
    {
        if (call.Arguments.Length != 2)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, Arguments(2), call.Arguments.Length);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        // Lazy since M31: an iterator over any iterable (SPEC §6, §3.13).
        var (values, element) = BindIterable(BindValue(call.Arguments[1].Value), call.Arguments[1].Value.Span);
        if (builtin == BuiltinFunction.Filter)
        {
            var (predicate, _) = BindCallback(call.Arguments[0].Value, [element], required: TaipanType.Bool, hint: null, resultName: "bool");
            return new BoundCall(function, [predicate, values], element.ContainsError ? TaipanType.Error : new IteratorOfType(element), call.Span);
        }
        var hint = expected is null ? null : TypeChecker.ElementOf(TypeChecker.Unwrap(expected));
        var (mapper, result) = BindCallback(call.Arguments[0].Value, [element], required: null, hint, resultName: "U");
        return new BoundCall(function, [mapper, values], new IteratorOfType(result), call.Span);
    }

    /// <summary>
    /// <c>enumerate(xs)</c>, <c>enumerate(xs, start)</c> or <c>enumerate(xs, start=n)</c> on a list
    /// or str: a list of (index, element) tuples (SPEC §6).
    /// </summary>
    private BoundExpr BindEnumerate(CallExpr call, FunctionSymbol function, TextSpan nameSpan)
    {
        var positional = call.Arguments.Where(a => a.Name is null).ToList();
        if (positional.Count is < 1 or > 2)
        {
            _diagnostics.ReportWrongArgumentCount(nameSpan, function.Name, "1 or 2 arguments", call.Arguments.Length);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }

        var (items, element) = BindIterable(BindValue(positional[0].Value), positional[0].Value.Span);

        BoundExpr? start = positional.Count == 2 ? BindExpected(positional[1].Value, TaipanType.Int) : null;
        foreach (var argument in call.Arguments.Where(a => a.Name is not null))
        {
            var keyword = argument.Name!;
            if (keyword.Text != "start")
            {
                _diagnostics.ReportNoKeywordArgument(keyword.Span, function.Name, keyword.Text);
                BindIgnored(argument.Value);
            }
            else if (start is not null)
            {
                _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                BindIgnored(argument.Value);
            }
            else
            {
                start = BindExpected(argument.Value, TaipanType.Int);
            }
        }
        var type = element.ContainsError ? TaipanType.Error : new IteratorOfType(new TupleOfType([TaipanType.Int, element]));
        return new BoundCall(function, start is null ? [items] : [items, start], type, call.Span);
    }

    // zip(xs, ys, …): a list of tuples, as long as the shortest list (SPEC §6). Two or more lists
    // since M26; one would need a one-element tuple.
    private BoundExpr BindZip(CallExpr call, FunctionSymbol function)
    {
        if (call.Arguments.Length < 2)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, "2 or more arguments", call.Arguments.Length);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        var sources = call.Arguments.Select(a => BindIterable(BindValue(a.Value), a.Value.Span)).ToList();
        var elements = sources.Select(s => s.Element).ToImmutableArray();
        var type = elements.Any(e => e.ContainsError) ? TaipanType.Error : new IteratorOfType(new TupleOfType(elements));
        return new BoundCall(function, [.. sources.Select(s => s.Value)], type, call.Span);
    }

    private BoundExpr BindBuiltinCall(CallExpr call, FunctionSymbol function, BuiltinFunction builtin, TaipanType? expected)
    {
        switch (builtin)
        {
            case BuiltinFunction.Zip:
                return BindZip(call, function);
            case BuiltinFunction.Map or BuiltinFunction.Filter:
                return BindMapFilter(call, function, builtin, expected);
            case BuiltinFunction.Print:
                return new BoundCall(function, BindArgumentValues(call), TaipanType.Nothing, call.Span);
            case BuiltinFunction.Range:
                BindArguments(call);
                _diagnostics.ReportRangeOutsideFor(call.Callee.Span);
                return new BoundErrorExpr(call.Span);
            case BuiltinFunction.Super:
                // `super().m(…)` is bound as a method call (SPEC §4.3); this is any other use.
                BindArguments(call);
                _diagnostics.ReportSuperMisuse(call.Callee.Span);
                return new BoundErrorExpr(call.Span);
        }

        var arguments = BindArgumentValues(call);
        if (arguments.Length != 1)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, Arguments(1), arguments.Length);
            return new BoundCall(function, arguments, function.ReturnType, call.Span);
        }

        var argument = arguments[0];
        var argumentSpan = call.Arguments[0].Value.Span;
        if (builtin is not BuiltinFunction.Str && ReportIfOptional(argument, argumentSpan))
            return new BoundCall(function, arguments, function.ReturnType, call.Span);
        switch (builtin)
        {
            // A class with __len__ (SPEC §4.9, M39).
            case BuiltinFunction.Len when argument.Type is ClassType && SpecialMethods.Method(argument.Type, "__len__") is not null:
                break;
            // An iterator has no length until it's a list (SPEC §3.13).
            case BuiltinFunction.Len when argument.Type is IteratorOfType or IterableOfType:
                _diagnostics.ReportNotIterableFromList(argumentSpan, $"'len' takes a list, dict or str, found {argument.Type}");
                break;
            case BuiltinFunction.Len when argument.Type is not (ListOfType or DictOfType or SetOfType or VarTupleOfType) && argument.Type != TaipanType.Py && !argument.Type.ContainsError:
                CheckAssignable(argument, TaipanType.Str, argumentSpan, "str, list, dict or set");
                break;
            case BuiltinFunction.Int or BuiltinFunction.Float when argument.Type == TaipanType.Str:
                // Parsing returns none on failure (SPEC §6).
                return new BoundCall(function, arguments, new OptionalOf(function.ReturnType), call.Span);
            case BuiltinFunction.Int or BuiltinFunction.Float:
                // Both accept int and float (SPEC §6); int widens to float.
                CheckAssignable(argument, TaipanType.Float, argumentSpan);
                break;
        }
        return new BoundCall(function, arguments, function.ReturnType, call.Span);
    }

    private static string Arguments(int count) => count == 1 ? "1 argument" : $"{count} arguments";

    private void CheckAssignable(BoundExpr value, TaipanType target, TextSpan span, string? expectedText = null)
    {
        if (TypeChecker.IsAssignable(value.Type, target))
        {
            // A library compiles a generic class used as a protocol type per instantiation (SPEC §14.2).
            if (TypeChecker.Unwrap(target) is ClassType { Symbol.IsProtocol: true } protocol
                && TypeChecker.Unwrap(value.Type) is ClassType { Symbol.IsProtocol: false, Symbol.TypeParameters.IsEmpty: false } generic)
                _protocolUses.TryAdd(generic.Symbol, protocol.ToString());
            return;
        }
        if (target == TaipanType.Py)
        {
            _diagnostics.ReportCannotPassToPython(span, value.Type);
            return;
        }
        if (value.Type is OptionalOf optional && TypeChecker.IsAssignable(optional.Inner, target))
            _diagnostics.ReportOptionalNotNarrowed(span, expectedText ?? target.ToString(), value.Type, WhyNotNarrowed(value));
        // A value for a protocol type, with what it lacks (SPEC §3.18).
        else if (TypeChecker.Unwrap(target) is ClassType { Symbol.IsProtocol: true } protocol && TypeChecker.Unwrap(value.Type) is var inner
            && TypeChecker.IsProtocolValue(inner) && Protocols.Unsatisfied(inner, protocol) is { } why)
            _diagnostics.ReportUnsatisfied(span, inner, protocol.ToString(), declaredBy: null, why);
        else
            _diagnostics.ReportTypeMismatch(span, expectedText ?? target.ToString(), value.Type);
    }

    /// <summary>Reports TP0203 for an optional used where its value is needed (SPEC §3.4).</summary>
    private bool ReportIfOptional(BoundExpr value, TextSpan span)
    {
        if (value.Type is not OptionalOf)
            return false;
        _diagnostics.ReportOptionalUse(span, value.Type, WhyNotNarrowed(value));
        return true;
    }
}
