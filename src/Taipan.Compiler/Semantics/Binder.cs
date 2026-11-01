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
public sealed record BindResult(BoundProgram Program, ImmutableArray<Diagnostic> Diagnostics, string? Unsupported);

/// <summary>
/// Resolves names and checks types (SPEC §3–§6), producing the bound tree for a whole program
/// (§4.7). Works in passes over every file: type and variant names; type imports; base classes;
/// the other hoisted declarations (constants, function signatures, class fields and method
/// signatures); inheritance checks; value imports; defaults; then function and method bodies, and
/// the main file's top-level statements.
/// </summary>
public sealed class Binder
{
    private static readonly BigInteger MaxInt = long.MaxValue;
    private static readonly HashSet<string> BuiltinTypeNames = ["int", "float", "bool", "str", "list", "dict", "py", "Callable", "tuple"];

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
    private string? _unsupported;

    private Binder()
    {
        _scope = _builtins;
    }

    public static BindResult Bind(SourceText source, CompilationUnit unit) =>
        Bind([new SourceModule(Path.GetFileNameWithoutExtension(source.Path), source, unit)]);

    /// <summary>Binds a program: the main file first, then its modules (SPEC §4.7).</summary>
    public static BindResult Bind(ImmutableArray<SourceModule> files)
    {
        var binder = new Binder();
        var program = binder.BindProgram(files);
        return new BindResult(program, binder._diagnostics.ToImmutable(), binder._unsupported);
    }

    // ---- Program ----

    private void Enter(Module module)
    {
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
        _diagnostics.FileOrder = [.. files.Select(f => f.Source)];

        var modules = files.Select(f => new Module(f, new Scope(_builtins))).ToList();
        var byName = modules.ToDictionary(m => m.File.Name);
        var main = modules[0];

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
            }
        }

        foreach (var module in modules)
        {
            Enter(module);
            BindImports(module, byName);
        }

        // Base classes, once every class name and import is known (SPEC §4.3).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.ClassDefs)
                BindBase(syntax, symbol);
        }
        BreakInheritanceCycles(modules);

        foreach (var module in modules)
        {
            Enter(module);
            BindDeclarations(module);
        }

        // Every class's members are known now, so overrides can be checked against bases (SPEC §4.3).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.ClassDefs)
                CheckInheritance(syntax, symbol, module.Methods.Where(m => m.Symbol.Owner == symbol));
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
            CollectScriptNames(stmt);

        // Defaults before any body, since a call anywhere may leave them out (SPEC §4.2).
        foreach (var module in modules)
        {
            Enter(module);
            foreach (var (syntax, symbol) in module.Functions)
                BindDefaults(symbol, syntax.Params, symbol.TypeParameters);
            foreach (var (syntax, symbol) in module.Methods)
                BindDefaults(symbol, syntax.Params, symbol.Owner!.TypeParameters);
            foreach (var (syntax, symbol) in module.ClassDefs)
                BindFieldDefaults(syntax, symbol);
        }

        var functions = new List<BoundFunction>();
        var classes = new List<BoundClass>();
        foreach (var module in modules)
        {
            Enter(module);
            functions.AddRange(module.Functions.SelectMany(f => BindFunctionBodies(f.Symbol, f.Syntax)));
            classes.AddRange(module.ClassDefs.Select(c => new BoundClass(
                c.Symbol,
                [.. module.Methods.Where(m => m.Symbol.Owner == c.Symbol).Select(m => BindMethodBody(c.Symbol, m.Syntax, m.Symbol))],
                c.Syntax.Span)));
        }

        Enter(main);
        _scope = new Scope(_global);
        var unit = main.File.Unit;
        var topLevel = unit.Items.OfType<Stmt>().Select(BindStmt).ToImmutableArray();
        var order = InitializationOrder(main, byName);
        return new BoundProgram(
            [.. order.SelectMany(m => m.PythonImports)],
            [.. modules.SelectMany(m => m.EnumDefs.Select(e => e.Symbol))],
            [.. classes],
            [.. order.SelectMany(m => m.Constants)],
            [.. functions],
            new BoundBlock(topLevel, unit.Span));
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
                case FromImportStmt { Module.Text: ModuleLoader.PythonModule } fromPython:
                    // Each name holds a Python module, imported at startup (SPEC §10.1).
                    foreach (var name in fromPython.Names)
                    {
                        var bound = name.Alias ?? name.Name;
                        var symbol = new VariableSymbol(bound.Text, TaipanType.Py, VariableKind.Constant);
                        if (DeclareGlobal(bound, symbol, isDefinition: false))
                            module.PythonImports.Add(new BoundPythonImport(symbol, name.Name.Text, name.Span, _source));
                    }
                    break;
                case ImportStmt import when byName.TryGetValue(import.Module.Text, out var target):
                    var alias = import.Alias ?? import.Module;
                    DeclareGlobal(alias, target.Symbol(alias.Text), isDefinition: false);
                    break;
                case FromImportStmt from when byName.TryGetValue(from.Module.Text, out var source):
                    foreach (var name in from.Names)
                    {
                        if (source.Classes.TryGetValue(name.Name.Text, out var classSymbol))
                            DeclareGlobal(name.Alias ?? name.Name, classSymbol, isDefinition: false);
                        else if (source.Enums.TryGetValue(name.Name.Text, out var enumSymbol))
                            ImportEnum(name, enumSymbol);
                        else
                            module.PendingImports.Add((source, name));
                    }
                    break;
            }
            // A module that wasn't found was already reported (TP0221) by the loader.
        }
    }

    private void ImportEnum(ImportName name, EnumSymbol enumSymbol)
    {
        if (!DeclareGlobal(name.Alias ?? name.Name, enumSymbol, isDefinition: false))
            return;
        foreach (var variant in enumSymbol.Variants)
            DeclareVariant(variant, name.Span, isDefinition: false);
    }

    private void ImportValue(Module target, ImportName name)
    {
        if (target.Definitions.TryGetValue(name.Name.Text, out var symbol) && symbol is FunctionSymbol or VariableSymbol or VariantSymbol)
            DeclareGlobal(name.Alias ?? name.Name, symbol, isDefinition: false);
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
            else if (BuiltinTypeNames.Contains(name.Text) || ResolveTypeName(name.Text) is not null)
                _diagnostics.ReportShadowing(name.Span, name.Text);
            if (!allowConstraints && !parameter.Constraints.IsEmpty)
                _diagnostics.ReportConstraintNotOnFunction(parameter.Constraints[0].Span);
        }
    }

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

    private ClassSymbol DeclareClass(ClassDef classDef)
    {
        var isDataclass = CheckDecorators(classDef.Decorators, "dataclass");
        var symbol = new ClassSymbol(classDef.Name.Text, NewTypeParameters(classDef.TypeParams), isDataclass);
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
            var name = decorator.Name.Text;
            if (!seen.Add(name))
                _diagnostics.ReportDuplicateDecorator(decorator.Span, name);
            else if (name is not ("dataclass" or "override"))
                _diagnostics.ReportUnknownDecorator(decorator.Span, name);
            else if (name != allowed)
                _diagnostics.ReportMisplacedDecorator(decorator.Span, name);
            else
                found = true;
        }
        return found;
    }

    /// <summary>
    /// A class's base (SPEC §4.3): one non-generic class of the same kind, for a class that isn't
    /// itself generic. Anything else is TP0225, and the class is bound without a base.
    /// </summary>
    private void BindBase(ClassDef classDef, ClassSymbol symbol)
    {
        if (classDef.Bases.IsEmpty)
            return;
        if (classDef.Bases.Length > 1)
            _diagnostics.ReportMultipleBases(classDef.Bases[1].Span);
        var syntax = classDef.Bases[0];
        if (!symbol.TypeParameters.IsEmpty)
        {
            _diagnostics.ReportGenericWithBase(syntax.Span);
            return;
        }
        var type = BindType(syntax);
        switch (type)
        {
            case { ContainsError: true }:
                return;
            case ClassType { Symbol.TypeParameters.IsEmpty: false }:
                _diagnostics.ReportGenericBase(syntax.Span);
                return;
            case ClassType { Symbol: var baseClass } when baseClass.IsDataclass != symbol.IsDataclass:
                _diagnostics.ReportMixedBase(syntax.Span, symbol.IsDataclass);
                return;
            case ClassType { Symbol: var baseClass }:
                symbol.Base = baseClass;
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
                    _diagnostics.ReportInheritanceCycle(syntax.Bases[0].Span, symbol.Name);
                    symbol.Base = null;
                    break;
                }
            }
        }
        foreach (var symbol in modules.SelectMany(m => m.ClassDefs).Select(c => c.Symbol))
        {
            if (symbol.Base is { } baseClass)
                baseClass.HasSubclasses = true;
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

    // Literals, unary minus, and + - * / // % (SPEC §4.5).
    private static bool IsConstantExpression(Expr expr) => expr switch
    {
        IntLiteral or FloatLiteral or StringLiteral or BoolLiteral => true,
        UnaryExpr { Op: UnaryOp.Negate } unary => IsConstantExpression(unary.Operand),
        BinaryExpr
        {
            Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide
                or BinaryOp.FloorDivide or BinaryOp.Modulo,
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
        var symbol = new FunctionSymbol(
            funcDef.Name.Text, BindParams(funcDef.Params), BindReturnType(funcDef.ReturnType), typeParameters: typeParameters)
        {
            Defaults = PendingDefaults(funcDef.Params),
        };
        _typeParameters = outer;
        DeclareGlobal(funcDef.Name, symbol);
        return symbol;
    }

    private ImmutableArray<VariableSymbol> BindParams(ImmutableArray<Param> parameters) =>
        [.. parameters.Select(p => new VariableSymbol(p.Name.Text, BindType(p.Type), VariableKind.Parameter))];

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
        CheckTypeParameters(classDef.TypeParams, allowConstraints: false);
        var outer = EnterTypeParameters(symbol.TypeParameters);
        var names = new HashSet<string>();
        var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
        foreach (var field in classDef.Fields)
        {
            var fieldSymbol = new FieldSymbol(field.Name.Text, BindType(field.Type)) { Owner = symbol, HasDefault = field.Default is not null };
            if (names.Add(field.Name.Text))
                fields.Add(fieldSymbol);
            else
                _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
        }
        symbol.Fields = fields.ToImmutable();

        var methods = new List<(MethodDef, FunctionSymbol)>();
        foreach (var method in classDef.Methods)
        {
            var name = method.Name.Text;
            var isOverride = CheckDecorators(method.Decorators, "override");
            var returnType = BindReturnType(method.ReturnType);
            if (name == "__init__" && method.ReturnType is { } initReturn)
            {
                _diagnostics.ReportInitReturnType(initReturn.Span);
                returnType = TaipanType.Nothing;
            }
            var self = new VariableSymbol("self", symbol.Type, VariableKind.Parameter);
            var methodSymbol = new FunctionSymbol(name, BindParams(method.Params), returnType, self: self)
            {
                Defaults = PendingDefaults(method.Params),
                IsMarkedOverride = isOverride,
            };
            methods.Add((method, methodSymbol));
            if (!names.Add(name))
            {
                _diagnostics.ReportDuplicateDefinition(method.Name.Span, name);
            }
            else if (name == "__init__")
            {
                if (symbol.IsDataclass)
                    _diagnostics.ReportDataclassInit(method.Name.Span);
                else
                    symbol.Init = methodSymbol;
                if (isOverride)
                    _diagnostics.ReportInitOverride(method.Name.Span);
            }
            // Other special methods are deferred to protocols (SPEC §4.3, M17).
            else if (name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal))
            {
                _diagnostics.ReportSpecialMethod(method.Name.Span, name);
            }
            else
            {
                symbol.Methods[name] = methodSymbol;
            }
        }
        _typeParameters = outer;
        return methods;
    }

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
            if (baseClass?.FindField(field.Name.Text) is not null || baseClass?.FindMethod(field.Name.Text) is not null)
                _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
        }
        foreach (var (syntax, method) in methods)
        {
            if (symbol.Methods.GetValueOrDefault(method.Name) != method)
                continue;
            if (baseClass?.FindField(method.Name) is not null)
            {
                _diagnostics.ReportDuplicateDefinition(syntax.Name.Span, method.Name);
                continue;
            }
            var overridden = baseClass?.FindMethod(method.Name);
            if (overridden is null)
            {
                if (method.IsMarkedOverride)
                    _diagnostics.ReportNothingToOverride(syntax.Name.Span, method.Name, baseClass?.Name);
                continue;
            }
            if (method.IsMarkedOverride)
                CheckOverride(syntax, method, overridden);
            else
                _diagnostics.ReportMissingOverride(syntax.Name.Span, method.Name, overridden.Owner!.Name);
            method.Overrides = overridden;
        }

        if (symbol.IsDataclass)
        {
            // The fields are the constructor's parameters, inherited ones first (SPEC §4.3).
            var sawDefault = baseClass?.AllFields.Any(f => f.HasDefault) == true;
            foreach (var field in classDef.Fields)
            {
                if (field.Default is not null)
                    sawDefault = true;
                else if (sawDefault)
                    _diagnostics.ReportFieldOrder(field.Name.Span);
            }
        }
        else if (symbol.Init is null)
        {
            foreach (var field in classDef.Fields.Where(f => f.Default is null))
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
        if (method.Parameters.Length != overridden.Parameters.Length)
        {
            _diagnostics.ReportOverrideParameters(syntax.Name.Span, name, owner);
            return;
        }
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var (parameter, expected) = (method.Parameters[i], overridden.Parameters[i]);
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

        var (result, expectedResult) = (method.ReturnType, overridden.ReturnType);
        var span = syntax.ReturnType?.Span ?? syntax.Name.Span;
        static bool IsClass(TaipanType type) => TypeChecker.Unwrap(type) is ClassType;
        if (expectedResult is NothingType)
        {
            if (result is not NothingType)
                _diagnostics.ReportOverrideReturn(span, name, null, owner);
        }
        else if (result != expectedResult && !result.ContainsError
            && !(IsClass(result) && IsClass(expectedResult) && TypeChecker.IsAssignable(result, expectedResult)))
        {
            _diagnostics.ReportOverrideReturn(span, name, expectedResult, owner);
        }
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
        // Named after the class, for TP0211.
        var context = new FunctionSymbol(symbol.Name, [], TaipanType.Nothing);
        (_function, _outerFunction, _scope) = (context, context, new Scope(_global));
        foreach (var field in classDef.Fields)
        {
            if (field.Default is { } value && symbol.Fields.FirstOrDefault(f => f.Name == field.Name.Text) is { } fieldSymbol)
                fieldSymbol.Default = BindExpected(value, fieldSymbol.Type);
        }
        (_function, _outerFunction, _scope) = (null, null, _global);
        _typeParameters = outerTypeParameters;
    }

    /// <summary>A method's body; an <c>__init__</c>'s is also checked for strict initialization (SPEC §4.3).</summary>
    private BoundFunction BindMethodBody(ClassSymbol owner, MethodDef syntax, FunctionSymbol method)
    {
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
        var outerTypeParameters = EnterTypeParameters(
            symbol.Self?.Type is ClassType { Symbol: var owner } ? owner.TypeParameters : symbol.TypeParameters);
        _function = symbol;
        _outerFunction = symbol;
        _loopDepth = 0;
        _scope = new Scope(_global);
        if (symbol.Self is { } self)
            _scope.Declare(self);
        var parameterSymbols = specializedParameters ?? symbol.Parameters;
        for (var i = 0; i < parameterSymbols.Length; i++)
            DeclareLocal(parameters[i].Name, parameterSymbols[i]);

        var boundBody = BindBlock(body);
        if (symbol.ReturnType != TaipanType.Nothing && !TypeChecker.Terminates(boundBody))
            _diagnostics.ReportNotAllPathsReturn(name.Span, symbol.Name);

        _function = null;
        _outerFunction = null;
        _scope = _global;
        _typeParameters = outerTypeParameters;
        return new BoundFunction(symbol, boundBody, span) { Source = _source };
    }

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
                _scriptNames.UnionWith(forStmt.Variables.Select(v => v.Text).Where(n => n != "_"));
                CollectScriptNames(forStmt.Body);
                break;
            case UnpackStmt unpack:
                _scriptNames.UnionWith(unpack.Names.Select(n => n.Text).Where(n => n != "_"));
                break;
            case FuncDefStmt funcDef:
                _scriptNames.Add(funcDef.Function.Name.Text);
                break;
            case MatchStmt match:
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is VariantPattern { Bindings: { } names })
                        _scriptNames.UnionWith(names.Select(n => n.Name.Text).Where(n => n != "_"));
                    CollectScriptNames(arm.Body);
                }
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
        return true;
    }

    private void DeclareLocal(Identifier name, Symbol symbol)
    {
        if (_scope.LookupLocal(name.Text) is not null)
            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
        else if (_scope.Parent?.Lookup(name.Text) is not null)
            _diagnostics.ReportShadowing(name.Span, name.Text);
        _scope.Declare(symbol);
    }

    /// <summary>Looks up a value name, reporting TP0201 or TP0211 if it isn't visible.</summary>
    private Symbol? LookupName(string name, TextSpan span)
    {
        if (_scope.Lookup(name) is { } symbol)
            return symbol;
        if (_outerFunction is not null && _scriptNames.Contains(name))
            _diagnostics.ReportTopLevelVariableInFunction(span, _function!.Name, name);
        else
            _diagnostics.ReportUndefinedName(span, name);
        return null;
    }

    private TaipanType BindType(TypeSyntax type)
    {
        switch (type)
        {
            case NamedType { Name.Text: "int" }:
                return TaipanType.Int;
            case NamedType { Name.Text: "float" }:
                return TaipanType.Float;
            case NamedType { Name.Text: "bool" }:
                return TaipanType.Bool;
            case NamedType { Name.Text: "str" }:
                return TaipanType.Str;
            case NamedType { Name.Text: "py" }:
                return TaipanType.Py;
            case NamedType { Name.Text: "Callable" } bare:
                _diagnostics.ReportBareCallable(bare.Span);
                return TaipanType.Error;
            case TupleType tuple:
                var elements = tuple.Elements.Select(BindType).ToImmutableArray();
                if (elements.Length >= 2)
                    return new TupleOfType(elements);
                _diagnostics.ReportTupleTooShort(tuple.Span);
                return TaipanType.Error;
            case CallableType callable:
                return new FunctionType(
                    [.. callable.Parameters.Select(BindType)], callable.Result is null ? TaipanType.Nothing : BindType(callable.Result));
            case NamedType named when _typeParameters.TryGetValue(named.Name.Text, out var typeParameter):
                return typeParameter;
            case NamedType named when ResolveTypeName(named.Name.Text) is { } typeSymbol:
                return BindGenericType(named.Name, [], named.Span, typeSymbol);
            case GenericType generic:
                return BindGenericType(generic.Name, generic.Arguments, generic.Span, ResolveTypeName(generic.Name.Text));
            case QualifiedType qualified:
                return BindQualifiedType(qualified);
            case NamedType named:
                _diagnostics.ReportUndefinedType(named.Span, named.Name.Text);
                return TaipanType.Error;
            case ListType list:
                return new ListOfType(BindType(list.Element));
            case DictType dict:
                var key = BindType(dict.Key);
                if (!DictOfType.IsValidKey(key))
                    _diagnostics.ReportInvalidDictKey(dict.Key.Span, key, key is ClassType { Symbol.IsDataclass: true });
                return new DictOfType(key, BindType(dict.Value));
            case OptionalType optional:
                var inner = BindType(optional.Inner);
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
        if (_scope.Lookup(qualified.Module.Text) is not ModuleSymbol module)
        {
            _diagnostics.ReportUndefinedName(qualified.Module.Span, qualified.Module.Text);
            return TaipanType.Error;
        }
        Symbol? symbol = module.Classes.TryGetValue(name.Text, out var classSymbol) ? classSymbol
            : module.Enums.TryGetValue(name.Text, out var enumSymbol) ? enumSymbol : null;
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

        if (arguments.Length != parameters.Length)
        {
            _diagnostics.ReportWrongTypeArgumentCount(span, name.Text, parameters.Length, arguments.Length);
            return TaipanType.Error;
        }
        return symbol is ClassSymbol s
            ? new ClassType(s, arguments)
            : new EnumType((EnumSymbol)symbol!, arguments);
    }

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
        var statements = block.Statements.Select(BindStmt).ToImmutableArray();
        _scope = outer;
        return new BoundBlock(statements, block.Span);
    }

    private BoundStmt BindStmt(Stmt stmt) => stmt switch
    {
        LetStmt let => BindVariableDecl(let.Name, let.Type, let.Value, VariableKind.Let, let.Span),
        VarStmt var => BindVariableDecl(var.Name, var.Type, var.Value, VariableKind.Var, var.Span),
        AssignStmt assign => BindAssign(assign),
        ReturnStmt ret => BindReturn(ret),
        PassStmt pass => new BoundBlock([], pass.Span),
        BreakStmt brk => BindBreak(brk),
        ContinueStmt cont => BindContinue(cont),
        ExprStmt exprStmt => new BoundExprStmt(BindExpr(exprStmt.Expr), exprStmt.Span),
        IfStmt ifStmt => BindIf(ifStmt),
        WhileStmt whileStmt => BindWhile(whileStmt),
        ForStmt forStmt => BindFor(forStmt),
        MatchStmt match => BindMatch(match),
        FuncDefStmt funcDef => BindLocalFunction(funcDef),
        UnpackStmt unpack => BindUnpack(unpack),
        TupleAssignStmt assign => BindTupleAssign(assign),
        _ => throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}"),
    };

    // ---- Unpacking (SPEC §3.8) ----

    /// <summary>
    /// The element types of a tuple being unpacked into <paramref name="count"/> names, or null
    /// after reporting a value that isn't such a tuple (silently, if it already has an error).
    /// </summary>
    private ImmutableArray<TaipanType>? UnpackedTypes(BoundExpr value, TextSpan span, int count)
    {
        if (value.Type.ContainsError || ReportIfOptional(value, span))
            return null;
        if (value.Type is not TupleOfType tuple)
        {
            _diagnostics.ReportCannotUnpack(span, value.Type);
            return null;
        }
        if (tuple.Elements.Length != count)
        {
            _diagnostics.ReportUnpackCount(span, tuple, tuple.Elements.Length, count);
            return null;
        }
        return tuple.Elements;
    }

    /// <summary>
    /// Declarations that unpack <paramref name="temp"/>'s elements into <paramref name="names"/>
    /// (<c>_</c> declares nothing). The element types are the error type if it can't be unpacked.
    /// </summary>
    private ImmutableArray<BoundStmt> UnpackInto(
        VariableSymbol temp, ImmutableArray<TaipanType>? elements, ImmutableArray<Identifier> names, VariableKind kind, TextSpan span)
    {
        var stores = ImmutableArray.CreateBuilder<BoundStmt>();
        for (var i = 0; i < names.Length; i++)
        {
            if (names[i].Text == "_")
                continue;
            var type = elements?[i] ?? TaipanType.Error;
            var symbol = new VariableSymbol(names[i].Text, type, kind);
            DeclareLocal(names[i], symbol);
            stores.Add(new BoundVariableDecl(symbol, new BoundTupleElement(new BoundVariable(temp, span), i, type, span), span));
        }
        return stores.ToImmutable();
    }

    // The value is held in a temporary named `_`, which the emitter always renames.
    private static VariableSymbol UnpackTemp(TaipanType type) => new("_", type, VariableKind.Let);

    private BoundUnpack BindUnpack(UnpackStmt unpack)
    {
        var value = BindValue(unpack.Value);
        var elements = UnpackedTypes(value, unpack.Value.Span, unpack.Names.Length);
        var temp = UnpackTemp(value.Type);
        var stores = UnpackInto(temp, elements, unpack.Names, unpack.IsVar ? VariableKind.Var : VariableKind.Let, unpack.Span);
        return new BoundUnpack(temp, value, stores, unpack.Span);
    }

    /// <summary>
    /// <c>a, b = e</c>: the value first, then each target in turn, as for <c>=</c>. A tuple
    /// literal on the right is bound without expected types; its elements are checked against
    /// the targets as they're stored.
    /// </summary>
    private BoundUnpack BindTupleAssign(TupleAssignStmt assign)
    {
        var value = BindValue(assign.Value);
        var elements = UnpackedTypes(value, assign.Value.Span, assign.Targets.Length);
        var temp = UnpackTemp(value.Type);
        var stores = assign.Targets
            .Select((target, i) => BindAssign(
                new AssignStmt(target, AssignOp.Assign, assign.Value, assign.Span),
                new BoundTupleElement(new BoundVariable(temp, assign.Value.Span), i, elements?[i] ?? TaipanType.Error, assign.Value.Span)))
            .ToImmutableArray();
        return new BoundUnpack(temp, value, stores, assign.Span);
    }

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
        var returnType = BindReturnType(def.ReturnType);
        // Its defaults see what's visible here, so they may use the bindings it captures (SPEC §4.2).
        var function = new FunctionSymbol(def.Name.Text, parameters, returnType) { Defaults = BindDefaultValues(def.Params, parameters) };
        var symbol = new VariableSymbol(def.Name.Text, new FunctionType([.. parameters.Select(p => p.Type)], returnType), VariableKind.Function);
        DeclareLocal(def.Name, symbol);
        _localFunctions[symbol] = function;

        var (outerFunction, outerLoopDepth, outerScope) = (_function, _loopDepth, _scope);
        _function = function;
        _loopDepth = 0;
        _scope = new Scope(outerScope);
        for (var i = 0; i < parameters.Length; i++)
            DeclareLocal(def.Params[i].Name, parameters[i]);
        var body = BindBlock(def.Body);
        if (returnType != TaipanType.Nothing && !TypeChecker.Terminates(body))
            _diagnostics.ReportNotAllPathsReturn(def.Name.Span, def.Name.Text);
        (_function, _loopDepth, _scope) = (outerFunction, outerLoopDepth, outerScope);
        _typeParameters = outerTypeParameters;
        return new BoundLocalFunction(symbol, function, parameters, body, stmt.Span);
    }

    // ---- match (SPEC §5.1) ----

    private BoundMatch BindMatch(MatchStmt match)
    {
        var scrutinee = BindValue(match.Scrutinee);
        var type = scrutinee.Type;
        if (ReportIfOptional(scrutinee, match.Scrutinee.Span))
            type = TaipanType.Error;
        else if (!type.ContainsError && type is not (EnumType or ClassType) && type != TaipanType.Int && type != TaipanType.Str && type != TaipanType.Bool)
        {
            _diagnostics.ReportCannotMatch(match.Scrutinee.Span, type);
            type = TaipanType.Error;
        }

        var covered = new MatchCoverage();
        var arms = ImmutableArray.CreateBuilder<BoundArm>();
        var allValid = true;
        foreach (var arm in match.Arms)
        {
            var (pattern, bindings, key) = BindPattern(arm.Pattern, type);
            allValid &= key is not null;
            var reachable = covered.Add(key);
            if (!reachable)
                _diagnostics.ReportUnreachableArm(arm.Pattern.Span);

            var outer = _scope;
            _scope = new Scope(outer);
            foreach (var (identifier, symbol) in bindings)
                DeclareLocal(identifier, symbol);
            // A class pattern narrows the scrutinee in its arm (SPEC §5.1).
            var outerNarrowing = _narrowing;
            if (pattern is BoundClassPattern classPattern)
                _narrowing = _narrowing.With(Narrow(scrutinee, classPattern.Class.Symbol.DerivesFrom(((ClassType)type).Symbol) ? classPattern.Class : type));
            var body = BindBlock(arm.Body);
            _narrowing = outerNarrowing;
            _scope = outer;
            arms.Add(new BoundArm(pattern, body, reachable));
        }

        // An invalid pattern was already reported; don't also call its variant missing.
        if (allValid && !type.ContainsError && covered.Missing(type) is { Count: > 0 } missing)
            _diagnostics.ReportNonExhaustiveMatch(new TextSpan(match.Span.Start, "match".Length), missing);
        return new BoundMatch(scrutinee, arms.ToImmutable(), match.Span);
    }

    /// <summary>
    /// Binds a pattern against the scrutinee type. Returns its bindings to declare in the arm, and
    /// a coverage key: <c>_</c>, the variant, or the literal value (null if the pattern is invalid).
    /// </summary>
    private (BoundPattern Pattern, List<(Identifier, VariableSymbol)> Bindings, object? Key) BindPattern(Pattern pattern, TaipanType type)
    {
        var bindings = new List<(Identifier, VariableSymbol)>();
        switch (pattern)
        {
            case WildcardPattern:
                return (new BoundWildcardPattern(), bindings, MatchCoverage.Wildcard);
            case IntPattern intPattern:
                return LiteralPattern(pattern, TaipanType.Int, type, intPattern.Value >= long.MinValue && intPattern.Value <= long.MaxValue
                    ? (long)intPattern.Value
                    : OutOfRange(intPattern.Span));
            case StringPattern stringPattern:
                return LiteralPattern(pattern, TaipanType.Str, type, stringPattern.Value);
            case BoolPattern boolPattern:
                return LiteralPattern(pattern, TaipanType.Bool, type, boolPattern.Value);
            case VariantPattern classPattern when type is ClassType classType:
                return BindClassPattern(classPattern, classType);
            case VariantPattern variantPattern:
                var variant = ResolveVariantPattern(variantPattern, type);
                if (variant is null)
                {
                    return (new BoundWildcardPattern(), bindings, null);
                }
                var symbols = ImmutableArray<VariableSymbol?>.Empty;
                if (variantPattern.Bindings is { } names && !(names.IsEmpty && variant.Fields.IsEmpty))
                {
                    if (names.Length != variant.Fields.Length)
                    {
                        _diagnostics.ReportBindingCount(variantPattern.Span, variant.Name, variant.Fields.Length, names.Length);
                        return (new BoundWildcardPattern(), bindings, null);
                    }
                    var builder = ImmutableArray.CreateBuilder<VariableSymbol?>();
                    foreach (var (name, field) in names.Select(n => n.Name).Zip(variant.Fields))
                    {
                        if (name.Text == "_")
                        {
                            builder.Add(null);
                            continue;
                        }
                        var symbol = new VariableSymbol(name.Text, FieldType(type, field), VariableKind.Let);
                        if (bindings.Any(b => b.Item1.Text == name.Text))
                            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
                        else
                            bindings.Add((name, symbol));
                        builder.Add(symbol);
                    }
                    symbols = builder.ToImmutable();
                }
                return (new BoundVariantPattern(variant, symbols), bindings, variant);
            default:
                return (new BoundWildcardPattern(), bindings, null);
        }
    }

    /// <summary>
    /// <c>C(…)</c> on a class scrutinee (SPEC §5.1): it matches instances of <c>C</c>, which must
    /// derive from the scrutinee's class. Fields bind by name, or for a dataclass also by position.
    /// Its coverage key is the class.
    /// </summary>
    private (BoundPattern, List<(Identifier, VariableSymbol)>, object?) BindClassPattern(VariantPattern pattern, ClassType scrutinee)
    {
        var bindings = new List<(Identifier, VariableSymbol)>();
        var invalid = (new BoundWildcardPattern(), bindings, (object?)null);
        if (pattern.Module is not null)
        {
            _diagnostics.ReportNotAClass(pattern.Span, _source.ToString(pattern.Span));
            return invalid;
        }
        ClassSymbol? classSymbol;
        if (pattern.Enum is { } moduleName)
        {
            if (_scope.Lookup(moduleName.Text) is not ModuleSymbol module)
            {
                _diagnostics.ReportUndefinedName(moduleName.Span, moduleName.Text);
                return invalid;
            }
            if (!module.Definitions.TryGetValue(pattern.Variant.Text, out var definition))
            {
                _diagnostics.ReportNoDefinition(pattern.Variant.Span, module.Name, pattern.Variant.Text);
                return invalid;
            }
            classSymbol = definition as ClassSymbol;
        }
        else
        {
            if (LookupName(pattern.Variant.Text, pattern.Variant.Span) is not { } found)
                return invalid;
            classSymbol = found as ClassSymbol;
        }
        var name = _source.ToString(new TextSpan(pattern.Span.Start, pattern.Variant.Span.End - pattern.Span.Start));
        if (classSymbol is null)
        {
            _diagnostics.ReportNotAClass(pattern.Span, name);
            return invalid;
        }
        if (pattern.Bindings is not { } names)
        {
            _diagnostics.ReportBareClassPattern(pattern.Span, name);
            return invalid;
        }
        if (!classSymbol.DerivesFrom(scrutinee.Symbol))
        {
            _diagnostics.ReportNeverInstance(pattern.Span, scrutinee, classSymbol.Name);
            return invalid;
        }

        // Generic classes don't derive, so the pattern's class is the scrutinee's type or isn't generic.
        var classType = classSymbol == scrutinee.Symbol ? scrutinee : new ClassType(classSymbol);
        var positional = names.TakeWhile(n => n.Field is null).Count();
        var fields = classSymbol.AllFields;
        if (positional > 0 && !classSymbol.IsDataclass)
        {
            _diagnostics.ReportPositionalOnPlainClass(pattern.Span, classSymbol.Name);
            return invalid;
        }
        if (positional > fields.Length)
        {
            _diagnostics.ReportBindingCount(pattern.Span, classSymbol.Name, fields.Length, positional);
            return invalid;
        }
        var bound = ImmutableArray.CreateBuilder<(FieldSymbol, VariableSymbol)>();
        var given = new HashSet<FieldSymbol>();
        var valid = true;
        for (var i = 0; i < names.Length; i++)
        {
            var binding = names[i];
            FieldSymbol? field;
            if (binding.Field is null)
            {
                field = fields[i];
            }
            else
            {
                field = classSymbol.FindField(binding.Field.Text);
                if (field is null)
                {
                    _diagnostics.ReportNoField(binding.Field.Span, classType, binding.Field.Text);
                    valid = false;
                    continue;
                }
            }
            if (!given.Add(field))
            {
                _diagnostics.ReportRepeatedField((binding.Field ?? binding.Name).Span, field.Name);
                valid = false;
                continue;
            }
            if (binding.Name.Text == "_")
                continue;
            var symbol = new VariableSymbol(binding.Name.Text, FieldType(classType, field), VariableKind.Let);
            if (bindings.Any(b => b.Item1.Text == binding.Name.Text))
                _diagnostics.ReportDuplicateDefinition(binding.Name.Span, binding.Name.Text);
            else
                bindings.Add((binding.Name, symbol));
            bound.Add((field, symbol));
        }
        return valid ? (new BoundClassPattern(classType, bound.ToImmutable()), bindings, classSymbol) : invalid;
    }

    private object? OutOfRange(TextSpan span)
    {
        _diagnostics.ReportIntegerOutOfRange(span);
        return null;
    }

    private (BoundPattern, List<(Identifier, VariableSymbol)>, object?) LiteralPattern(Pattern pattern, TaipanType patternType, TaipanType type, object? value)
    {
        if (!type.ContainsError && type != patternType)
        {
            _diagnostics.ReportTypeMismatch(pattern.Span, type, patternType);
            value = null;
        }
        return (value is null ? new BoundWildcardPattern() : new BoundLiteralPattern(value), [], value);
    }

    /// <summary>Variant patterns resolve against the scrutinee's enum, so they're never ambiguous (SPEC §5.1).</summary>
    private VariantSymbol? ResolveVariantPattern(VariantPattern pattern, TaipanType type)
    {
        if (type.ContainsError)
            return null;
        if (type is not EnumType { Symbol: var enumSymbol })
        {
            var found = pattern.Enum?.Text ?? (_global.Lookup(pattern.Variant.Text) switch
            {
                VariantSymbol v => v.Enum.Name,
                AmbiguousVariantSymbol a => a.Variants[0].Enum.Name,
                _ => $"variant '{pattern.Variant.Text}'",
            });
            _diagnostics.ReportTypeMismatch(pattern.Span, type, found);
            return null;
        }
        if (pattern.Enum is { } qualifier)
        {
            EnumSymbol? named;
            if (pattern.Module is { } moduleName)
            {
                if (_scope.Lookup(moduleName.Text) is not ModuleSymbol module)
                {
                    _diagnostics.ReportUndefinedName(moduleName.Span, moduleName.Text);
                    return null;
                }
                named = module.Enums.GetValueOrDefault(qualifier.Text);
                if (named is null)
                {
                    _diagnostics.ReportNoDefinition(qualifier.Span, module.Name, qualifier.Text);
                    return null;
                }
            }
            else
            {
                named = ResolveTypeName(qualifier.Text) as EnumSymbol;
                if (named is null)
                {
                    _diagnostics.ReportUndefinedName(qualifier.Span, qualifier.Text);
                    return null;
                }
            }
            if (named != enumSymbol)
            {
                _diagnostics.ReportTypeMismatch(pattern.Span, type, named.Name);
                return null;
            }
        }
        var variant = enumSymbol.FindVariant(pattern.Variant.Text);
        if (variant is null)
            _diagnostics.ReportNoVariant(pattern.Variant.Span, type, pattern.Variant.Text);
        return variant;
    }

    /// <summary>Which arms a match has covered so far, for TP0300 and TP0204.</summary>
    private sealed class MatchCoverage
    {
        public static readonly object Wildcard = new();

        private readonly HashSet<object> _keys = [];

        /// <summary>Records an arm; false if an earlier arm already covers it (unreachable).</summary>
        public bool Add(object? key)
        {
            if (_keys.Contains(Wildcard))
                return false;
            // A class pattern covers every class deriving from it (SPEC §5.1).
            if (key is ClassSymbol classSymbol && _keys.OfType<ClassSymbol>().Any(classSymbol.DerivesFrom))
                return false;
            return key is null || _keys.Add(key);
        }

        public List<string> Missing(TaipanType type)
        {
            if (_keys.Contains(Wildcard))
                return [];
            return type switch
            {
                EnumType e => [.. e.Symbol.Variants.Where(v => !_keys.Contains(v)).Select(v => v.Name)],
                PrimitiveType { Name: "bool" } => [.. new[] { true, false }.Where(b => !_keys.Contains(b)).Select(b => b ? "True" : "False")],
                ClassType c when _keys.Contains(c.Symbol) => [],
                _ => ["_"],
            };
        }
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
        return new BoundAssign(variable, value, assign.Span);
    }

    private BoundStmt BindFieldAssign(AssignStmt assign, MemberExpr member, BoundExpr? preset)
    {
        var target = BindValue(member.Target);
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
            return OrphanAssign(assign, preset);
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
                var fieldType = FieldType(target.Type, field);
                var value = BindAssignedValue(assign, new BoundFieldAccess(target, field, fieldType, member.Span), fieldType, preset);
                return new BoundFieldAssign(target, field, value, assign.Op != AssignOp.Assign, assign.Span);
            }
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
        if (target.Type is TupleOfType)
        {
            _diagnostics.ReportCannotAssignTupleElement(indexExpr.Span);
            return OrphanAssign(assign, preset);
        }
        if (target.Type == TaipanType.Str)
        {
            _diagnostics.ReportCannotAssignStrElement(indexExpr.Span);
            return OrphanAssign(assign, preset);
        }

        var read = BindIndexOf(target, indexExpr);
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
        var value = BindBinaryOperation(current, TypeChecker.ToBinaryOp(assign.Op), operand, assign.Span);
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

        var returnType = TypeChecker.Substitute(_function.ReturnType, _specialization);
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
        return new BoundReturn(BindExpected(ret.Value, returnType), ret.Span);
    }

    private BoundBreak BindBreak(BreakStmt brk)
    {
        if (_loopDepth == 0)
            _diagnostics.ReportBreakOutsideLoop(brk.Span);
        return new BoundBreak(brk.Span);
    }

    private BoundContinue BindContinue(ContinueStmt cont)
    {
        if (_loopDepth == 0)
            _diagnostics.ReportContinueOutsideLoop(cont.Span);
        return new BoundContinue(cont.Span);
    }

    /// <summary>Each later clause sees what holds when the earlier conditions are false (SPEC §3.9).</summary>
    private BoundIf BindIf(IfStmt ifStmt)
    {
        var outer = _narrowing;
        var clauses = ImmutableArray.CreateBuilder<BoundIfClause>();
        foreach (var clause in ifStmt.Clauses)
        {
            var (condition, body) = BindGuarded(clause.Condition, clause.Body, isLoop: false);
            clauses.Add(new BoundIfClause(condition, body));
            _narrowing = _narrowing.With(Facts(condition, whenTrue: false));
        }
        var elseBlock = ifStmt.Else is null ? null : BindBlock(ifStmt.Else);
        _narrowing = outer;
        return new BoundIf(clauses.ToImmutable(), elseBlock, ifStmt.Span);
    }

    private BoundWhile BindWhile(WhileStmt whileStmt)
    {
        var (condition, body) = BindGuarded(whileStmt.Condition, whileStmt.Body, isLoop: true);
        return new BoundWhile(condition, body, whileStmt.Span);
    }

    /// <summary>
    /// Binds a condition and the block it guards. For <c>x is some v</c>, <c>v</c> is declared in a
    /// scope around the block, so it's visible in that branch only (SPEC §3.4).
    /// </summary>
    private (BoundExpr Condition, BoundBlock Body) BindGuarded(Condition condition, Block body, bool isLoop)
    {
        BoundExpr bound;
        VariableSymbol? binding = null;
        switch (condition)
        {
            case IsSomeCondition isSome:
                var someOperand = BindValue(isSome.Expr);
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
        var outerNarrowing = _narrowing;
        _narrowing = _narrowing.With(Facts(bound, whenTrue: true));
        var block = BindBlock(body);
        _narrowing = outerNarrowing;
        if (isLoop)
            _loopDepth--;
        _scope = outer;
        return (bound, block);
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
        BoundExpr? start = null;
        BoundExpr iterable;
        TaipanType variableType;
        var isRange = forStmt.Iterable is CallExpr { Callee: NameExpr { Name: "range" } }
            && _scope.Lookup("range") is FunctionSymbol { Builtin: BuiltinFunction.Range };
        if (isRange)
        {
            (start, iterable) = BindRangeArguments((CallExpr)forStmt.Iterable);
            variableType = TaipanType.Int;
        }
        else
        {
            iterable = BindValue(forStmt.Iterable);
            variableType = iterable.Type switch
            {
                ListOfType list => list.Element,
                DictOfType dict => dict.Key,
                PrimitiveType { Name: "str" } => TaipanType.Str,
                PrimitiveType { Name: "py" } => TaipanType.Py,
                { ContainsError: true } => TaipanType.Error,
                OptionalOf when ReportIfOptional(iterable, forStmt.Iterable.Span) => TaipanType.Error,
                _ => CannotIterate(forStmt.Iterable.Span, iterable.Type),
            };
        }

        // The loop variable is a let scoped to the body (SPEC §5.2). With several names, the
        // element is held in a temporary and unpacked into them at the start of the body.
        var outer = _scope;
        _scope = new Scope(outer);
        VariableSymbol variable;
        var unpacked = ImmutableArray<BoundStmt>.Empty;
        if (forStmt.Variables is [var single])
        {
            variable = new VariableSymbol(single.Text, variableType, VariableKind.LoopVariable);
            DeclareLocal(single, variable);
        }
        else
        {
            variable = UnpackTemp(variableType);
            var elements = UnpackedTypes(new BoundVariable(variable, forStmt.Iterable.Span), forStmt.Iterable.Span, forStmt.Variables.Length);
            unpacked = UnpackInto(variable, elements, forStmt.Variables, VariableKind.LoopVariable, forStmt.Span);
        }
        _loopDepth++;
        var statements = forStmt.Body.Statements.Select(BindStmt).ToImmutableArray();
        _loopDepth--;
        _scope = outer;

        var body = new BoundBlock([.. unpacked, .. statements], forStmt.Body.Span);
        return isRange
            ? new BoundForRange(variable, start, iterable, body, forStmt.Span)
            : new BoundForEach(variable, iterable, body, forStmt.Span);
    }

    private TaipanType CannotIterate(TextSpan span, TaipanType type)
    {
        _diagnostics.ReportCannotIterate(span, type);
        return TaipanType.Error;
    }

    private (BoundExpr? Start, BoundExpr Stop) BindRangeArguments(CallExpr call)
    {
        ReportKeywordArguments(call, "range");
        var arguments = call.Arguments.Select(a => BindExpected(a.Value, TaipanType.Int)).ToImmutableArray();
        switch (arguments.Length)
        {
            case 1:
                return (null, arguments[0]);
            case 2:
                return (arguments[0], arguments[1]);
            default:
                _diagnostics.ReportWrongArgumentCount(call.Callee.Span, "range", "1 or 2 arguments", arguments.Length);
                return (null, new BoundErrorExpr(call.Span));
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
        // A Taipan value where a py is expected is converted to Python (SPEC §10.3).
        if (expected == TaipanType.Py && value.Type != TaipanType.Py && !value.Type.ContainsError && value.Type.IsConvertibleToPython)
            return new BoundToPy(value, value.Span);
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
        if (TypeChecker.Unwrap(value.Type) is ClassType from && TypeChecker.Unwrap(target) is ClassType to)
        {
            return CheckRelated(to, from, cast.Type.Span) is null
                ? new BoundErrorExpr(cast.Span)
                : new BoundClassCast(value, target, cast.Span);
        }
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
        return new BoundTupleElement(target, (int)normalized, tuple.Elements[(int)normalized], indexExpr.Span);
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
        if (type is not (ListOfType or PrimitiveType { Name: "str" or "py" }) && !type.ContainsError)
        {
            if (!ReportIfOptional(target, slice.Target.Span))
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

        BoundExpr body;
        // An error result (an undefined type, say) was already reported, so any body fits it.
        if (result is NothingType or ErrorType)
        {
            body = BindExpr(lambda.Body);
        }
        else if (result is not null)
        {
            body = BindExpected(lambda.Body, result);
        }
        else
        {
            body = resultMayBeNothing ? BindExpr(lambda.Body) : BindValue(lambda.Body);
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

        var value = BindValue(expr);
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

    private BoundExpr BindExpr(Expr expr, TaipanType? expected = null) => expr switch
    {
        IntLiteral literal => BindIntLiteral(literal.Value, literal.Span),
        FloatLiteral literal => new BoundLiteral(literal.Value, TaipanType.Float, literal.Span),
        StringLiteral literal => new BoundLiteral(literal.Value, TaipanType.Str, literal.Span),
        BoolLiteral literal => new BoundLiteral(literal.Value, TaipanType.Bool, literal.Span),
        FStringExpr fstring => BindFString(fstring),
        NameExpr name => BindName(name, expected),
        UnaryExpr unary => BindUnary(unary),
        BinaryExpr { Op: BinaryOp.Coalesce } binary => BindCoalesce(binary),
        BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual } equality => BindEquality(equality),
        BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } logical => BindLogical(logical),
        IsNoneExpr isNone => BindIsNone(isNone),
        BinaryExpr binary => BindBinaryOperation(BindValue(binary.Left), binary.Op, BindValue(binary.Right), binary.Span),
        CallExpr call => BindCall(call, expected),
        ListExpr list => BindList(list, expected),
        DictExpr dict => BindDict(dict, expected),
        MemberExpr member => BindMember(member, expected),
        IndexExpr index => BindIndexOf(BindValue(index.Target), index),
        NoneLiteral => new BoundNone(expr.Span),
        CastExpr cast => BindCast(cast),
        LambdaExpr lambda => BindLambda(lambda, expected),
        TupleExpr tuple => BindTuple(tuple, expected),
        SliceExpr slice => BindSlice(slice),
        ErrorExpr => new BoundErrorExpr(expr.Span),
        _ => throw new InvalidOperationException($"unexpected expression {expr.GetType().Name}"),
    };

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
        var parts = fstring.Segments.Select(segment => segment switch
        {
            FStringTextSegment text => new BoundLiteral(text.Value, TaipanType.Str, text.Span),
            FStringHoleSegment hole => BindValue(hole.Expr),
            _ => throw new InvalidOperationException($"unexpected f-string segment {segment.GetType().Name}"),
        }).ToImmutableArray();
        return new BoundFString(parts, fstring.Span);
    }

    private BoundExpr BindName(NameExpr name, TaipanType? expected) =>
        BindSymbolValue(LookupName(name.Name, name.Span), name.Name, name.Span, expected);

    private BoundExpr BindSymbolValue(Symbol? symbol, string name, TextSpan span, TaipanType? expected)
    {
        switch (symbol)
        {
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
            case FunctionSymbol function:
                return new BoundFunctionValue(function, new FunctionType([.. function.Parameters.Select(p => p.Type)], function.ReturnType), span);
            case ClassSymbol or EnumSymbol:
                _diagnostics.ReportTypeAsValue(span, name);
                return new BoundErrorExpr(span);
            case ModuleSymbol:
                _diagnostics.ReportModuleAsValue(span, name);
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
        if (!operand.Type.IsNumeric)
        {
            _diagnostics.ReportUnaryOperatorType(unary.Span, "-", operand.Type);
            return new BoundUnary(unary.Op, operand, TaipanType.Error, unary.Span);
        }
        return new BoundUnary(unary.Op, operand, operand.Type, unary.Span);
    }

    private BoundExpr BindBinaryOperation(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        if (left.Type.ContainsError || right.Type.ContainsError)
            return new BoundBinary(left, op, right, TaipanType.Error, span);
        if (left.Type == TaipanType.Py || right.Type == TaipanType.Py)
            return BindPyBinary(left, op, right, span);
        // Only == and != accept optionals (SPEC §3.4).
        if (op is not (BinaryOp.Equal or BinaryOp.NotEqual)
            && (ReportIfOptional(left, left.Span) | ReportIfOptional(right, right.Span)))
            return new BoundBinary(left, op, right, TaipanType.Error, span);

        var type = op switch
        {
            BinaryOp.And or BinaryOp.Or => CheckLogicalOperands(left, right),
            BinaryOp.Equal or BinaryOp.NotEqual => CheckComparable(left, right, span),
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual =>
                CheckOrdered(left, op, right, span),
            BinaryOp.In or BinaryOp.NotIn => CheckOperands(left, op, right, span, MembershipResult(left.Type, right.Type)),
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
        var outer = _narrowing;
        _narrowing = _narrowing.With(Facts(left, whenTrue: binary.Op == BinaryOp.And));
        var right = BindValue(binary.Right);
        _narrowing = outer;
        return BindBinaryOperation(left, binary.Op, right, binary.Span);
    }

    private BoundIsNone BindIsNone(IsNoneExpr isNone)
    {
        var operand = BindValue(isNone.Operand);
        RequireOptional(operand, isNone.Operand.Span, isNone.Negated ? "is not None" : "is None");
        return new BoundIsNone(operand, isNone.Negated, isNone.Span);
    }

    // ---- Narrowing (SPEC §3.9) ----

    /// <summary>
    /// Names narrowed to new types, and the vars and fields a condition would have narrowed if
    /// they couldn't change (keyed by symbol, or by variable and field), for the help on TP0203 and TP0210.
    /// </summary>
    private sealed record Narrowing(ImmutableDictionary<VariableSymbol, TaipanType> Types, ImmutableHashSet<object> Unnarrowable)
    {
        public static readonly Narrowing None = new(ImmutableDictionary<VariableSymbol, TaipanType>.Empty, []);

        public Narrowing With(Narrowing other) =>
            new(Types.SetItems(other.Types), Unnarrowable.Union(other.Unnarrowable));
    }

    /// <summary>What holds when <paramref name="condition"/> is true, or when it's false.</summary>
    private static Narrowing Facts(BoundExpr condition, bool whenTrue) => condition switch
    {
        BoundIsInstance isInstance when whenTrue => Narrow(isInstance.Operand, InstanceType(isInstance)),
        BoundIsNone isNone when isNone.Negated == whenTrue => Narrow(isNone.Operand, TypeChecker.Unwrap(isNone.Operand.Type)),
        BoundUnary { Op: UnaryOp.Not } negation => Facts(negation.Operand, !whenTrue),
        BoundBinary { Op: BinaryOp.And } both when whenTrue => Facts(both.Left, true).With(Facts(both.Right, true)),
        BoundBinary { Op: BinaryOp.Or } either when !whenTrue => Facts(either.Left, false).With(Facts(either.Right, false)),
        _ => Narrowing.None,
    };

    /// <summary>The type <c>isinstance(x, C)</c> narrows <c>x</c> to: <c>C</c>, unless <c>x</c>'s own class already derives from it.</summary>
    private static TaipanType InstanceType(BoundIsInstance isInstance) =>
        TypeChecker.Unwrap(isInstance.Operand.Type) is ClassType current && !isInstance.Class.Symbol.DerivesFrom(current.Symbol)
            ? current
            : isInstance.Class;

    private static Narrowing Narrow(BoundExpr operand, TaipanType type)
    {
        if (type.ContainsError)
            return Narrowing.None;
        var variable = operand switch
        {
            BoundVariable v => v.Symbol,
            BoundNarrowed n => n.Value.Symbol,
            _ => null,
        };
        if (variable is { Kind: VariableKind.Let or VariableKind.Parameter or VariableKind.LoopVariable })
            return Narrowing.None with { Types = Narrowing.None.Types.Add(variable, type) };
        object? key = variable is not null ? variable
            : operand is BoundFieldAccess { Target: BoundVariable target } field ? (target.Symbol, field.Field) : null;
        return key is null ? Narrowing.None : Narrowing.None with { Unnarrowable = [key] };
    }

    /// <summary>Whether <paramref name="value"/> is a var or field that a condition here would have narrowed.</summary>
    private bool IsUnnarrowable(BoundExpr value) => value switch
    {
        BoundVariable v => _narrowing.Unnarrowable.Contains(v.Symbol),
        BoundFieldAccess { Target: BoundVariable target } field => _narrowing.Unnarrowable.Contains((target.Symbol, field.Field)),
        _ => false,
    };

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
        var left = BindValue(binary.Left);
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
        DictOfType dict when TypeChecker.AreComparable(item, dict.Key) => TaipanType.Bool,
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
        if (!TypeChecker.AreComparable(left.Type, right.Type))
        {
            _diagnostics.ReportCannotCompare(span, left.Type, right.Type);
            return TaipanType.Error;
        }
        var ordered = left.Type.IsOrdered && right.Type.IsOrdered;
        return CheckOperands(left, op, right, span, ordered ? TaipanType.Bool : null);
    }

    private TaipanType CheckOperands(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span, TaipanType? result)
    {
        if (result is not null)
            return result;
        _diagnostics.ReportBinaryOperatorTypes(span, TypeChecker.Display(op), left.Type, right.Type);
        return TaipanType.Error;
    }

    // ---- Lists, dicts, members and indexing ----

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
                _diagnostics.ReportInvalidDictKey(dict.Entries[0].Key.Span, pyKeyType, pyKeyType is ClassType { Symbol.IsDataclass: true });
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
            _diagnostics.ReportCannotInferEmpty(dict.Span, "dict");
            return new BoundErrorExpr(dict.Span);
        }

        var entries = dict.Entries.Select(e => (Key: BindValue(e.Key), Value: BindValue(e.Value))).ToImmutableArray();
        var keyType = InferElementType([.. entries.Select(e => e.Key)]);
        if (!DictOfType.IsValidKey(keyType))
        {
            _diagnostics.ReportInvalidDictKey(dict.Entries[0].Key.Span, keyType, keyType is ClassType { Symbol.IsDataclass: true });
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
            return qualified is null ? new BoundErrorExpr(member.Span) : BindVariantValue(qualified, member.Span, expected);
        if (TryModuleMember(member, out var definition))
            return BindSymbolValue(definition, member.Member.Text, member.Span, expected);

        var target = BindValue(member.Target);
        var name = member.Member.Text;
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
            return new BoundErrorExpr(member.Span);
        if (target.Type == TaipanType.Py)
            return new BoundPyGetAttr(target, name, member.Span);

        if (target.Type is ClassType { Symbol: var classSymbol })
        {
            if (classSymbol.FindField(name) is { } field)
                return new BoundFieldAccess(target, field, FieldType(target.Type, field), member.Span);
            if (name == "__init__")
                _diagnostics.ReportInitCalled(member.Member.Span);
            else if (classSymbol.FindMethod(name) is not null)
                _diagnostics.ReportMethodAsValue(member.Member.Span, name);
            else
                _diagnostics.ReportNoField(member.Member.Span, target.Type, name, IsUnnarrowable(target));
        }
        else if (BuiltinMethodOf(target.Type, name) is not null)
        {
            _diagnostics.ReportMethodAsValue(member.Member.Span, name);
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
                return new BoundIndex(target, BindExpected(indexExpr.Index, TaipanType.Int), list.Element, indexExpr.Span);
            case DictOfType dict:
                return new BoundIndex(target, BindExpected(indexExpr.Index, dict.Key), dict.Value, indexExpr.Span);
            case PrimitiveType { Name: "str" }:
                return new BoundIndex(target, BindExpected(indexExpr.Index, TaipanType.Int), TaipanType.Str, indexExpr.Span);
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
        switch (symbol)
        {
            case VariableSymbol variable when _localFunctions.TryGetValue(variable, out var local):
                return BindLocalCall(call, new BoundVariable(variable, nameSpan), local, nameSpan);
            case VariableSymbol variable when TypeChecker.Unwrap(variable.Type) is FunctionType:
                ReportKeywordArguments(call, function: null);
                return BindInvoke(call, new BoundVariable(variable, nameSpan), name, nameSpan);
            case ClassSymbol classSymbol:
                return BindConstruct(call, classSymbol, expected, nameSpan);
            case VariantSymbol variant:
                return BindConstructVariant(call, variant, expected);
            case AmbiguousVariantSymbol ambiguous:
                BindArguments(call);
                ReportAmbiguous(ambiguous, nameSpan);
                return new BoundErrorExpr(call.Span);
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
        if (MatchArguments(call, function, function.Name, nameSpan) is not { } shape)
            return new BoundInvoke(callee, BindArguments(call), function.ReturnType, call.Span);
        var arguments = call.Arguments.Select((a, i) => BindExpected(a.Value, function.Parameters[shape.Parameters[i]].Type)).ToImmutableArray();
        return new BoundInvoke(callee, arguments, function.ReturnType, call.Span) { Shape = Simplify(shape), Function = function };
    }

    private BoundExpr BindUserCall(CallExpr call, FunctionSymbol function, TaipanType? expected, TextSpan nameSpan)
    {
        if (MatchArguments(call, function, function.Name, nameSpan) is not { } shape)
            return new BoundCall(function, BindArguments(call), function.TypeParameters.IsEmpty ? function.ReturnType : TaipanType.Error, call.Span);

        var (bound, map) = InferAndBind(
            function.Name, call.Callee.Span, function.TypeParameters, [.. shape.Parameters.Select(i => function.Parameters[i].Type)],
            function.ReturnType, [.. call.Arguments.Select(a => a.Value)], expected);
        return new BoundCall(function, bound, TypeChecker.Substitute(function.ReturnType, map), call.Span)
        {
            TypeArguments = [.. function.TypeParameters.Select(p => map[p])],
            Shape = Simplify(shape),
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
        foreach (var i in Enumerable.Range(0, arguments.Count).OrderBy(i => arguments[i] is LambdaExpr ? 1 : 0))
        {
            var parameter = parameterTypes[i];
            var argument = arguments[i] is LambdaExpr lambda && TypeChecker.Unwrap(parameter) is FunctionType callable && !inference.IsKnown(callable)
                ? BindLambdaArgument(lambda, callable, inference, map)
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
        for (var i = 0; i < bound.Length; i++)
        {
            var parameter = TypeChecker.Substitute(parameterTypes[i], map);
            CheckAssignable(bound[i], parameter, arguments[i].Span);
            bound[i] = ConvertFunction(bound[i], parameter);
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
            DictOfType d => IsKnown(d.Key) && IsKnown(d.Value),
            ClassType s => s.TypeArguments.All(IsKnown),
            EnumType e => e.TypeArguments.All(IsKnown),
            FunctionType f => f.Parameters.All(IsKnown) && IsKnown(f.Result),
            TupleOfType t => t.Elements.All(IsKnown),
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
                case DictOfType d:
                    GiveUp(d.Key);
                    GiveUp(d.Value);
                    break;
                case ClassType s:
                    s.TypeArguments.ToList().ForEach(GiveUp);
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
                case (DictOfType p, DictOfType a):
                    Unify(p.Key, a.Key, fromArgument);
                    Unify(p.Value, a.Value, fromArgument);
                    break;
                case (ClassType p, ClassType a) when p.Symbol == a.Symbol:
                    foreach (var (pa, aa) in p.TypeArguments.Zip(a.TypeArguments))
                        Unify(pa, aa, fromArgument);
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
        a is ClassType x && b is ClassType y && x.Symbol.CommonBase(y.Symbol) is { } common ? common.Type : null;

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

    /// <summary>
    /// Matches a call's arguments to a function's parameters as in Python (SPEC §4.2): positional
    /// ones from the left, then keywords by name, and the rest take their defaults. Reports TP0209
    /// and TP0218, and returns null if the arguments don't fit.
    /// </summary>
    private ArgumentShape? MatchArguments(CallExpr call, FunctionSymbol function, string name, TextSpan nameSpan)
    {
        var count = function.Parameters.Length;
        var required = Enumerable.Range(0, count).Count(i => function.DefaultFor(i) is null);
        var positional = call.Arguments.TakeWhile(a => a.Name is null).Count();
        // Only positional arguments: a wrong count is reported as a count.
        if (positional > count || (positional == call.Arguments.Length && positional < required))
        {
            var expected = required == count ? Arguments(count) : $"{required} to {count} arguments";
            _diagnostics.ReportWrongArgumentCount(nameSpan, name, expected, positional);
            return null;
        }

        var parameters = ImmutableArray.CreateBuilder<int>();
        parameters.AddRange(Enumerable.Range(0, positional));
        var valid = true;
        var unknown = false;
        foreach (var argument in call.Arguments.Skip(positional))
        {
            var keyword = argument.Name!;
            var index = function.Parameters.Select(p => p.Name).ToList().IndexOf(keyword.Text);
            if (index < 0)
            {
                _diagnostics.ReportNoKeywordArgument(keyword.Span, name, keyword.Text);
                (valid, unknown) = (false, true);
            }
            else if (parameters.Contains(index))
            {
                _diagnostics.ReportRepeatedArgument(keyword.Span, keyword.Text);
                valid = false;
            }
            parameters.Add(index);
        }
        // After an unknown keyword, a missing argument is probably the one it misspells.
        if (!unknown)
        {
            foreach (var missing in Enumerable.Range(0, count).Where(i => function.DefaultFor(i) is null && !parameters.Contains(i)))
            {
                _diagnostics.ReportMissingArgument(nameSpan, function.Parameters[missing].Name);
                valid = false;
            }
        }
        return valid ? new ArgumentShape(parameters.ToImmutable(), count) : null;
    }

    /// <summary>Null for the common shape: every parameter given, positionally.</summary>
    private static ArgumentShape? Simplify(ArgumentShape shape) =>
        shape.InOrder && shape.Parameters.Length == shape.ParameterCount ? null : shape;

    /// <summary>
    /// A dataclass's constructor takes its fields; a plain class's takes its <c>__init__</c>'s
    /// parameters, matched like a function's (SPEC §4.3).
    /// </summary>
    private BoundExpr BindConstruct(CallExpr call, ClassSymbol classSymbol, TaipanType? expected, TextSpan nameSpan)
    {
        if (classSymbol.IsDataclass)
        {
            var (arguments, isKeyword, map) = BindFieldArguments(
                call, classSymbol.Name, classSymbol.AllFields, classSymbol.TypeParameters, classSymbol.Type, expected);
            return arguments is { } valid
                ? new BoundConstruct((ClassType)TypeChecker.Substitute(classSymbol.Type, map), valid, isKeyword, call.Span)
                : new BoundErrorExpr(call.Span);
        }

        var constructor = classSymbol.Constructor;
        if (MatchArguments(call, constructor, classSymbol.Name, nameSpan) is not { } shape)
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        var (bound, typeArguments) = InferAndBind(
            classSymbol.Name, call.Callee.Span, classSymbol.TypeParameters, [.. shape.Parameters.Select(i => constructor.Parameters[i].Type)],
            classSymbol.Type, [.. call.Arguments.Select(a => a.Value)], expected);
        return new BoundNew((ClassType)TypeChecker.Substitute(classSymbol.Type, typeArguments), constructor, bound, call.Span) { Shape = Simplify(shape) };
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
        var operand = BindValue(call.Arguments[0].Value);
        var operandClass = TypeChecker.Unwrap(operand.Type) as ClassType;
        if (operandClass is null && !operand.Type.ContainsError)
        {
            _diagnostics.ReportIsInstanceNeedsClass(call.Arguments[0].Value.Span, operand.Type);
            operand = new BoundErrorExpr(operand.Span);
        }
        var target = ResolveClass(call.Arguments[1].Value, TypeChecker.Unwrap(operand.Type));
        return target is null || operand.Type.ContainsError
            ? new BoundErrorExpr(call.Span)
            : new BoundIsInstance(operand, target, call.Span);
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
            case ClassSymbol classSymbol:
                return CheckRelated(new ClassType(classSymbol), tested, expr.Span);
            default:
                _diagnostics.ReportNotAClass(expr.Span, _source.ToString(expr.Span));
                return null;
        }
    }

    /// <summary>TP0200 unless a <paramref name="tested"/> value could be a <paramref name="target"/>: one derives from the other.</summary>
    private ClassType? CheckRelated(ClassType target, TaipanType tested, TextSpan span)
    {
        if (tested is ClassType testedClass && !TypeChecker.AreRelated(testedClass.Symbol, target.Symbol))
        {
            _diagnostics.ReportNeverInstance(span, tested, target.Symbol.Name);
            return null;
        }
        return target;
    }

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
        if (MatchArguments(call, method, name, member.Member.Span) is not { } shape)
            return new BoundSuperCall(method, self, BindArguments(call), method.ReturnType, call.Span);
        var arguments = call.Arguments.Select((a, i) => BindExpected(a.Value, method.Parameters[shape.Parameters[i]].Type)).ToImmutableArray();
        return new BoundSuperCall(method, self, arguments, method.ReturnType, call.Span) { Shape = Simplify(shape) };
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
            owner, call.Callee.Span, typeParameters, [.. chosen.Select(c => c.Field.Type)], ownerType, [.. chosen.Select(c => c.Value)],
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
        if (!module.Definitions.TryGetValue(member.Member.Text, out symbol))
            _diagnostics.ReportNoDefinition(member.Member.Span, module.Name, member.Member.Text);
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
        variant = enumSymbol.FindVariant(member.Member.Text);
        if (variant is null)
            _diagnostics.ReportNoVariant(member.Member.Span, enumSymbol.Type, member.Member.Text);
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

        var target = BindValue(member.Target);
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

        if (target.Type is ClassType { Symbol: var classSymbol })
        {
            // A field holding a function is called like a method (SPEC §3.7).
            if (classSymbol.FindField(name) is { } field && TypeChecker.Unwrap(FieldType(target.Type, field)) is FunctionType)
            {
                ReportKeywordArguments(call, function: null);
                return BindInvoke(call, new BoundFieldAccess(target, field, FieldType(target.Type, field), member.Span), name, member.Member.Span);
            }
            if (name == "__init__")
            {
                BindArguments(call);
                _diagnostics.ReportInitCalled(member.Member.Span);
                return new BoundErrorExpr(call.Span);
            }
            if (classSymbol.FindMethod(name) is { } method)
            {
                var map = TypeChecker.MapOf(target.Type);
                var returnType = TypeChecker.Substitute(method.ReturnType, map);
                if (MatchArguments(call, method, name, member.Member.Span) is not { } shape)
                    return new BoundMethodCall(target, method, BindArguments(call), returnType, call.Span);
                var arguments = call.Arguments
                    .Select((a, i) => BindExpected(a.Value, TypeChecker.Substitute(method.Parameters[shape.Parameters[i]].Type, map)))
                    .ToImmutableArray();
                return new BoundMethodCall(target, method, arguments, returnType, call.Span) { Shape = Simplify(shape) };
            }
            BindArguments(call);
            if (classSymbol.FindField(name) is not null)
                _diagnostics.ReportFieldNotMethod(member.Member.Span, name);
            else
                _diagnostics.ReportNoMethod(member.Member.Span, target.Type, name, IsUnnarrowable(target));
            return new BoundErrorExpr(call.Span);
        }

        if (BuiltinMethodOf(target.Type, name) is not { } builtin)
        {
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

    private static bool IsSortable(TaipanType type) => type.IsOrdered;

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
        var list = BindValue(call.Arguments[1].Value);
        var element = RequireList(list, call.Arguments[1].Value.Span);
        if (builtin == BuiltinFunction.Filter)
        {
            var (predicate, _) = BindCallback(call.Arguments[0].Value, [element], required: TaipanType.Bool, hint: null, resultName: "bool");
            return new BoundCall(function, [predicate, list], element.ContainsError ? TaipanType.Error : list.Type, call.Span);
        }
        var hint = expected is null ? null : TypeChecker.Unwrap(expected) is ListOfType expectedList ? expectedList.Element : null;
        var (mapper, result) = BindCallback(call.Arguments[0].Value, [element], required: null, hint, resultName: "U");
        return new BoundCall(function, [mapper, list], new ListOfType(result), call.Span);
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

        var items = BindValue(positional[0].Value);
        var itemsSpan = positional[0].Value.Span;
        TaipanType element = TaipanType.Error;
        if (items.Type is ListOfType list)
            element = list.Element;
        else if (items.Type == TaipanType.Str)
            element = TaipanType.Str;
        else if (!items.Type.ContainsError && !ReportIfOptional(items, itemsSpan))
            _diagnostics.ReportTypeMismatch(itemsSpan, "list or str", items.Type);

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
        var type = element.ContainsError ? TaipanType.Error : new ListOfType(new TupleOfType([TaipanType.Int, element]));
        return new BoundCall(function, start is null ? [items] : [items, start], type, call.Span);
    }

    // zip(xs, ys): a list of pairs, as long as the shorter list (SPEC §6).
    private BoundExpr BindZip(CallExpr call, FunctionSymbol function)
    {
        if (call.Arguments.Length != 2)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, Arguments(2), call.Arguments.Length);
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        var lists = call.Arguments.Select(a => BindValue(a.Value)).ToImmutableArray();
        var elements = lists.Select((l, i) => RequireList(l, call.Arguments[i].Value.Span)).ToImmutableArray();
        var type = elements.Any(e => e.ContainsError) ? TaipanType.Error : new ListOfType(new TupleOfType(elements));
        return new BoundCall(function, lists, type, call.Span);
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
            case BuiltinFunction.Len when argument.Type is not (ListOfType or DictOfType) && argument.Type != TaipanType.Py && !argument.Type.ContainsError:
                CheckAssignable(argument, TaipanType.Str, argumentSpan, "str, list or dict");
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
            return;
        if (target == TaipanType.Py)
        {
            _diagnostics.ReportCannotPassToPython(span, value.Type);
            return;
        }
        if (value.Type is OptionalOf optional && TypeChecker.IsAssignable(optional.Inner, target))
            _diagnostics.ReportOptionalNotNarrowed(span, expectedText ?? target.ToString(), value.Type);
        else
            _diagnostics.ReportTypeMismatch(span, expectedText ?? target.ToString(), value.Type);
    }

    /// <summary>Reports TP0203 for an optional used where its value is needed (SPEC §3.4).</summary>
    private bool ReportIfOptional(BoundExpr value, TextSpan span)
    {
        if (value.Type is not OptionalOf)
            return false;
        _diagnostics.ReportOptionalUse(span, value.Type, IsUnnarrowable(value));
        return true;
    }
}
