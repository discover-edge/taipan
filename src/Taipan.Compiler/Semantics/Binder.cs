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
/// (§4.7). Works in passes over every file: type and variant names; type imports; the other
/// hoisted declarations (constants, function signatures, struct fields and method signatures);
/// value imports; then function and method bodies, and the main file's top-level statements.
/// </summary>
public sealed class Binder
{
    private static readonly BigInteger MaxInt = long.MaxValue;
    private static readonly HashSet<string> BuiltinTypeNames = ["int", "float", "bool", "str", "list", "dict", "py"];

    private DiagnosticBag _diagnostics = new();
    private readonly Scope _builtins = new(null);

    // The module being bound: its file, global scope and type names. Switched by Enter.
    private SourceText _source = null!;
    private Scope _global = null!;
    private Dictionary<string, StructSymbol> _structs = null!;
    private Dictionary<string, EnumSymbol> _enums = null!;
    private Dictionary<string, Symbol> _definitions = null!;

    /// <summary>One file of the program, with what it defines (and so exports).</summary>
    private sealed class Module(SourceModule file, Scope global)
    {
        public SourceModule File { get; } = file;

        public Scope Global { get; } = global;

        public Dictionary<string, StructSymbol> Structs { get; } = [];

        public Dictionary<string, EnumSymbol> Enums { get; } = [];

        public Dictionary<string, Symbol> Definitions { get; } = [];

        public List<(StructDef Syntax, StructSymbol Symbol)> StructDefs { get; } = [];

        public List<(EnumDef Syntax, EnumSymbol Symbol)> EnumDefs { get; } = [];

        public List<BoundConstant> Constants { get; } = [];

        public List<(FuncDef Syntax, FunctionSymbol Symbol)> Functions { get; } = [];

        public List<(MethodDef Syntax, FunctionSymbol Symbol)> Methods { get; } = [];

        public List<BoundPythonImport> PythonImports { get; } = [];

        /// <summary><c>from m import x</c> names that weren't types, resolved once functions and constants exist.</summary>
        public List<(Module Target, ImportName Name)> PendingImports { get; } = [];

        public ModuleSymbol Symbol(string name) => new(name, Definitions, Structs, Enums);
    }
    // Type names in scope inside a generic definition: each type parameter, or the concrete type it
    // stands for while a constrained body is checked for one combination (SPEC §3.6).
    private Dictionary<string, TaipanType> _typeParameters = [];
    private Dictionary<TypeParameterSymbol, TaipanType> _specialization = [];
    private readonly HashSet<string> _scriptNames = [];
    private Scope _scope;
    private FunctionSymbol? _function;
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
        _structs = module.Structs;
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
                if (item is StructDef structDef)
                    module.StructDefs.Add((structDef, DeclareStruct(structDef)));
                else if (item is EnumDef enumDef)
                    module.EnumDefs.Add(DeclareEnum(enumDef));
            }
        }

        foreach (var module in modules)
        {
            Enter(module);
            BindImports(module, byName);
        }

        foreach (var module in modules)
        {
            Enter(module);
            BindDeclarations(module);
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

        var functions = new List<BoundFunction>();
        var structs = new List<BoundStruct>();
        foreach (var module in modules)
        {
            Enter(module);
            functions.AddRange(module.Functions.SelectMany(f => BindFunctionBodies(f.Symbol, f.Syntax)));
            structs.AddRange(module.StructDefs.Select(s => new BoundStruct(
                s.Symbol,
                [.. module.Methods.Where(m => m.Symbol.Self!.Type == s.Symbol.Type)
                    .Select(m => BindBody(m.Symbol, m.Syntax.Params, m.Syntax.Name, m.Syntax.Body, m.Syntax.Span))],
                s.Syntax.Span)));
        }

        Enter(main);
        _scope = new Scope(_global);
        var unit = main.File.Unit;
        var topLevel = unit.Items.OfType<Stmt>().Select(BindStmt).ToImmutableArray();
        var order = InitializationOrder(main, byName);
        return new BoundProgram(
            [.. order.SelectMany(m => m.PythonImports)],
            [.. modules.SelectMany(m => m.EnumDefs.Select(e => e.Symbol))],
            [.. structs],
            [.. order.SelectMany(m => m.Constants)],
            [.. functions],
            new BoundBlock(topLevel, unit.Span));
    }

    /// <summary>Constants, function signatures, struct members and variant fields, in source order.</summary>
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
                case StructDef structDef:
                    var symbol = module.StructDefs.First(s => s.Syntax == structDef).Symbol;
                    module.Methods.AddRange(BindStructMembers(structDef, symbol));
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
                        if (source.Structs.TryGetValue(name.Name.Text, out var structSymbol))
                            DeclareGlobal(name.Alias ?? name.Name, structSymbol, isDefinition: false);
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
        StructType s => s.TypeArguments.Any(MentionsTypeParameter),
        EnumType e => e.TypeArguments.Any(MentionsTypeParameter),
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

    private StructSymbol DeclareStruct(StructDef structDef)
    {
        var symbol = new StructSymbol(structDef.Name.Text, NewTypeParameters(structDef.TypeParams));
        if (BuiltinTypeNames.Contains(symbol.Name))
            _diagnostics.ReportDuplicateDefinition(structDef.Name.Span, symbol.Name);
        else if (DeclareGlobal(structDef.Name, symbol))
            _structs[symbol.Name] = symbol;
        return symbol;
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
        CheckTypeParameters(funcDef.TypeParams, allowConstraints: true);
        var typeParameters = NewTypeParameters(funcDef.TypeParams);
        var outer = EnterTypeParameters(typeParameters);
        BindConstraints(funcDef.TypeParams, typeParameters);
        var symbol = new FunctionSymbol(
            funcDef.Name.Text, BindParams(funcDef.Params), BindReturnType(funcDef.ReturnType), typeParameters: typeParameters);
        _typeParameters = outer;
        DeclareGlobal(funcDef.Name, symbol);
        return symbol;
    }

    private ImmutableArray<VariableSymbol> BindParams(ImmutableArray<Param> parameters) =>
        [.. parameters.Select(p => new VariableSymbol(p.Name.Text, BindType(p.Type), VariableKind.Parameter))];

    private TaipanType BindReturnType(TypeSyntax? type) => type is null ? TaipanType.Nothing : BindType(type);

    private List<(MethodDef, FunctionSymbol)> BindStructMembers(StructDef structDef, StructSymbol symbol)
    {
        CheckTypeParameters(structDef.TypeParams, allowConstraints: false);
        var outer = EnterTypeParameters(symbol.TypeParameters);
        var names = new HashSet<string>();
        var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
        foreach (var field in structDef.Fields)
        {
            var fieldSymbol = new FieldSymbol(field.Name.Text, BindType(field.Type));
            if (names.Add(field.Name.Text))
                fields.Add(fieldSymbol);
            else
                _diagnostics.ReportDuplicateDefinition(field.Name.Span, field.Name.Text);
        }
        symbol.Fields = fields.ToImmutable();

        var methods = new List<(MethodDef, FunctionSymbol)>();
        foreach (var method in structDef.Methods)
        {
            var self = new VariableSymbol("self", symbol.Type, VariableKind.Parameter);
            var methodSymbol = new FunctionSymbol(
                method.Name.Text, BindParams(method.Params), BindReturnType(method.ReturnType), self: self);
            if (names.Add(method.Name.Text))
                symbol.Methods[method.Name.Text] = methodSymbol;
            else
                _diagnostics.ReportDuplicateDefinition(method.Name.Span, method.Name.Text);
            methods.Add((method, methodSymbol));
        }
        _typeParameters = outer;
        return methods;
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
        // A method sees its struct's type parameters; a function its own.
        var outerTypeParameters = EnterTypeParameters(
            symbol.Self?.Type is StructType { Symbol: var owner } ? owner.TypeParameters : symbol.TypeParameters);
        _function = symbol;
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
                _scriptNames.Add(forStmt.Variable.Text);
                CollectScriptNames(forStmt.Body);
                break;
            case MatchStmt match:
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is VariantPattern { Bindings: { } names })
                        _scriptNames.UnionWith(names.Select(n => n.Text).Where(n => n != "_"));
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
        if (_function is not null && _scriptNames.Contains(name))
            _diagnostics.ReportTopLevelVariableInFunction(span, _function.Name, name);
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
                    _diagnostics.ReportInvalidDictKey(dict.Key.Span, key);
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

    /// <summary>A struct or enum visible in this file, defined or imported (SPEC §4.7).</summary>
    private Symbol? ResolveTypeName(string name) => _global.Lookup(name) is StructSymbol or EnumSymbol ? _global.Lookup(name) : null;

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
        Symbol? symbol = module.Structs.TryGetValue(name.Text, out var structSymbol) ? structSymbol
            : module.Enums.TryGetValue(name.Text, out var enumSymbol) ? enumSymbol : null;
        if (symbol is null)
        {
            _diagnostics.ReportNoDefinition(name.Span, module.Name, name.Text);
            return TaipanType.Error;
        }
        return BindGenericType(name, arguments, qualified.Span, symbol);
    }

    /// <summary>A struct or enum type with exactly as many type arguments as it has parameters (SPEC §3.6).</summary>
    private TaipanType BindGenericType(Identifier name, ImmutableArray<TypeSyntax> argumentSyntax, TextSpan span, Symbol? symbol)
    {
        var arguments = argumentSyntax.Select(BindType).ToImmutableArray();
        var parameters = symbol switch
        {
            StructSymbol structSymbol => structSymbol.TypeParameters,
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
        return symbol is StructSymbol s
            ? new StructType(s, arguments)
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
        _ => throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}"),
    };

    // ---- match (SPEC §5.1) ----

    private BoundMatch BindMatch(MatchStmt match)
    {
        var scrutinee = BindValue(match.Scrutinee);
        var type = scrutinee.Type;
        if (ReportIfOptional(scrutinee, match.Scrutinee.Span))
            type = TaipanType.Error;
        else if (!type.ContainsError && type is not EnumType && type != TaipanType.Int && type != TaipanType.Str && type != TaipanType.Bool)
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
            var body = BindBlock(arm.Body);
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
                    foreach (var (name, field) in names.Zip(variant.Fields))
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

    private BoundStmt BindAssign(AssignStmt assign) => assign.Target switch
    {
        MemberExpr member => BindFieldAssign(assign, member),
        IndexExpr index => BindIndexAssign(assign, index),
        NameExpr name => BindVariableAssign(assign, name),
        _ => throw new InvalidOperationException($"unexpected assignment target {assign.Target.GetType().Name}"),
    };

    private BoundStmt BindVariableAssign(AssignStmt assign, NameExpr target)
    {
        var symbol = LookupName(target.Name, target.Span);
        if (symbol is not VariableSymbol variable)
        {
            if (symbol is FunctionSymbol or StructSymbol or EnumSymbol or VariantSymbol or AmbiguousVariantSymbol or ModuleSymbol)
                _diagnostics.ReportCannotAssign(target.Span, target.Name, "function", null);
            return new BoundExprStmt(BindValue(assign.Value), assign.Span);
        }

        if (variable.Kind != VariableKind.Var)
        {
            var (what, help) = variable.Kind switch
            {
                VariableKind.Let => ("let binding", "declare it with 'var'"),
                VariableKind.Parameter => ("parameter", null),
                VariableKind.LoopVariable => ("loop variable", null),
                _ => ("constant", (string?)null),
            };
            _diagnostics.ReportCannotAssign(target.Span, target.Name, what, help);
        }

        var value = BindAssignedValue(assign, new BoundVariable(variable, target.Span), variable.Type);
        return new BoundAssign(variable, value, assign.Span);
    }

    private BoundStmt BindFieldAssign(AssignStmt assign, MemberExpr member)
    {
        var target = BindValue(member.Target);
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
            return new BoundExprStmt(BindValue(assign.Value), assign.Span);
        if (target.Type == TaipanType.Py)
        {
            var current = new BoundPyGetAttr(target, member.Member.Text, member.Span);
            var pyValue = BindAssignedValue(assign, current, TaipanType.Py);
            return new BoundPySetAttr(target, member.Member.Text, pyValue, assign.Op != AssignOp.Assign, assign.Span);
        }

        if (target.Type is StructType { Symbol: var structSymbol })
        {
            if (structSymbol.FindField(member.Member.Text) is { } field)
            {
                var fieldType = FieldType(target.Type, field);
                var value = BindAssignedValue(assign, new BoundFieldAccess(target, field, fieldType, member.Span), fieldType);
                return new BoundFieldAssign(target, field, value, assign.Op != AssignOp.Assign, assign.Span);
            }
            if (structSymbol.Methods.ContainsKey(member.Member.Text))
                _diagnostics.ReportCannotAssign(member.Member.Span, member.Member.Text, "method", null);
            else
                _diagnostics.ReportNoField(member.Member.Span, target.Type, member.Member.Text);
        }
        else
        {
            _diagnostics.ReportNoField(member.Member.Span, target.Type, member.Member.Text);
        }
        return new BoundExprStmt(BindValue(assign.Value), assign.Span);
    }

    private BoundStmt BindIndexAssign(AssignStmt assign, IndexExpr indexExpr)
    {
        var target = BindValue(indexExpr.Target);
        if (target.Type == TaipanType.Str)
        {
            _diagnostics.ReportCannotAssignStrElement(indexExpr.Span);
            return new BoundExprStmt(BindValue(assign.Value), assign.Span);
        }

        var read = BindIndexOf(target, indexExpr);
        if (read is BoundPyGetItem pyItem)
        {
            var pyValue = BindAssignedValue(assign, pyItem, TaipanType.Py);
            return new BoundPySetItem(target, pyItem.Index, pyValue, assign.Op != AssignOp.Assign, assign.Span);
        }
        if (read is not BoundIndex index)
            return new BoundExprStmt(BindValue(assign.Value), assign.Span);

        var value = BindAssignedValue(assign, index, index.Type);
        return new BoundIndexAssign(target, index.Index, value, assign.Op != AssignOp.Assign, assign.Span);
    }

    /// <summary>
    /// The value stored by an assignment. A compound assignment <c>t op= e</c> binds as
    /// <c>t op e</c>, reading through <paramref name="current"/>.
    /// </summary>
    private BoundExpr BindAssignedValue(AssignStmt assign, BoundExpr current, TaipanType targetType)
    {
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

    private BoundIf BindIf(IfStmt ifStmt)
    {
        var clauses = ifStmt.Clauses
            .Select(c => BindGuarded(c.Condition, c.Body, isLoop: false))
            .Select(g => new BoundIfClause(g.Condition, g.Body))
            .ToImmutableArray();
        var elseBlock = ifStmt.Else is null ? null : BindBlock(ifStmt.Else);
        return new BoundIf(clauses, elseBlock, ifStmt.Span);
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
            case IsNoneCondition isNone:
                var noneOperand = BindValue(isNone.Expr);
                RequireOptional(noneOperand, isNone.Expr.Span, "is none");
                bound = new BoundIsNone(noneOperand, isNone.Span);
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
        var block = BindBlock(body);
        if (isLoop)
            _loopDepth--;
        _scope = outer;
        return (bound, block);
    }

    /// <summary>The inner type of an optional operand of <c>is some</c> / <c>is none</c>, else TP0200.</summary>
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

        // The loop variable is a let scoped to the body (SPEC §5.2).
        var outer = _scope;
        _scope = new Scope(outer);
        var variable = new VariableSymbol(forStmt.Variable.Text, variableType, VariableKind.LoopVariable);
        DeclareLocal(forStmt.Variable, variable);
        _loopDepth++;
        var statements = forStmt.Body.Statements.Select(BindStmt).ToImmutableArray();
        _loopDepth--;
        _scope = outer;

        var body = new BoundBlock(statements, forStmt.Body.Span);
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
        ReportKeywordArguments(call);
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
        return value;
    }

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

    /// <summary>
    /// Positional then keyword arguments are only valid in calls to py values (SPEC §2): TP0100
    /// anywhere else. (The parser rejects a positional argument after a keyword.)
    /// </summary>
    private bool ReportMixedArguments(CallExpr call)
    {
        if (!call.Arguments.Any(a => a.Name is null) || call.Arguments.FirstOrDefault(a => a.Name is not null) is not { } keyword)
            return false;
        _diagnostics.ReportMixedArguments(keyword.Span);
        return true;
    }

    // `x as T` (SPEC §10.4): x must be py, and T a type Python values can convert to.
    private BoundExpr BindCast(CastExpr cast)
    {
        var value = BindValue(cast.Value);
        var target = BindType(cast.Type);
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
        BinaryExpr binary => BindBinaryOperation(BindValue(binary.Left), binary.Op, BindValue(binary.Right), binary.Span),
        CallExpr call => BindCall(call, expected),
        ListExpr list => BindList(list, expected),
        DictExpr dict => BindDict(dict, expected),
        MemberExpr member => BindMember(member, expected),
        IndexExpr index => BindIndexOf(BindValue(index.Target), index),
        NoneLiteral => new BoundNone(expr.Span),
        CastExpr cast => BindCast(cast),
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
            case VariableSymbol variable:
                return new BoundVariable(variable, span);
            case FunctionSymbol or StructSymbol:
                _diagnostics.ReportFunctionAsValue(span, name);
                return new BoundErrorExpr(span);
            case EnumSymbol:
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

    // In `a == b` and `a != b`, a's type is b's expected type, so `m == Nothing` and `xs == []` work (SPEC §5.3).
    private BoundExpr BindEquality(BinaryExpr binary)
    {
        var left = BindValue(binary.Left);
        var expected = left.Type.ContainsError || left.Type == TaipanType.Py ? null : left.Type;
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
        var ordered = left.Type.IsNumeric || left.Type == TaipanType.Str;
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
                _diagnostics.ReportInvalidDictKey(dict.Entries[0].Key.Span, pyKeyType);
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
            _diagnostics.ReportInvalidDictKey(dict.Entries[0].Key.Span, keyType);
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

        if (target.Type is StructType { Symbol: var structSymbol })
        {
            if (structSymbol.FindField(name) is { } field)
                return new BoundFieldAccess(target, field, FieldType(target.Type, field), member.Span);
            if (structSymbol.Methods.ContainsKey(name))
                _diagnostics.ReportMethodAsValue(member.Member.Span, name);
            else
                _diagnostics.ReportNoField(member.Member.Span, target.Type, name);
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
            BindArguments(call);
            if (!callee.Type.ContainsError)
                _diagnostics.ReportNotAFunction(call.Callee.Span, _source.ToString(call.Callee.Span));
            return new BoundErrorExpr(call.Span);
        }

        return BindSymbolCall(call, LookupName(calleeName.Name, calleeName.Span), calleeName.Name, calleeName.Span, expected);
    }

    /// <summary>A call whose callee names <paramref name="symbol"/>, directly or through a module.</summary>
    private BoundExpr BindSymbolCall(CallExpr call, Symbol? symbol, string name, TextSpan nameSpan, TaipanType? expected)
    {
        if (symbol is VariableSymbol { Type: PrimitiveType { Name: "py" } } python)
            return BindPyCall(call, new BoundVariable(python, nameSpan));
        if (ReportMixedArguments(call))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        switch (symbol)
        {
            case StructSymbol structSymbol:
                return BindConstruct(call, structSymbol, expected);
            case VariantSymbol variant:
                return BindConstructVariant(call, variant, expected);
            case AmbiguousVariantSymbol ambiguous:
                BindArguments(call);
                ReportAmbiguous(ambiguous, nameSpan);
                return new BoundErrorExpr(call.Span);
            case FunctionSymbol { Builtin: { } builtin } function:
                ReportKeywordArguments(call);
                return BindBuiltinCall(call, function, builtin);
            case FunctionSymbol function:
                ReportKeywordArguments(call);
                return BindUserCall(call, function, expected);
            default:
                BindArguments(call);
                if (symbol is VariableSymbol or EnumSymbol or ModuleSymbol)
                    _diagnostics.ReportNotAFunction(nameSpan, name);
                return new BoundErrorExpr(call.Span);
        }
    }

    private BoundExpr BindUserCall(CallExpr call, FunctionSymbol function, TaipanType? expected)
    {
        var parameterTypes = function.Parameters.Select(p => p.Type).ToImmutableArray();
        if (call.Arguments.Length != parameterTypes.Length)
        {
            var arguments = BindParameterArguments(call, function.Name, parameterTypes);
            return new BoundCall(function, arguments, function.TypeParameters.IsEmpty ? function.ReturnType : TaipanType.Error, call.Span);
        }

        var (bound, map) = InferAndBind(
            function.Name, call.Callee.Span, function.TypeParameters, parameterTypes, function.ReturnType,
            [.. call.Arguments.Select(a => a.Value)], expected);
        return new BoundCall(function, bound, TypeChecker.Substitute(function.ReturnType, map), call.Span)
        {
            TypeArguments = [.. function.TypeParameters.Select(p => map[p])],
        };
    }

    /// <summary>
    /// Binds arguments against parameter types, inferring any type parameters first (SPEC §3.6):
    /// from the expected type of the result, then from each argument in order. Each argument gets
    /// its parameter as its expected type once that type is fully known, then is checked against it.
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

        var bound = ImmutableArray.CreateBuilder<BoundExpr>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var parameter = parameterTypes[i];
            var argument = BindValue(arguments[i], inference.IsKnown(parameter) ? TypeChecker.Substitute(parameter, map) : null);
            inference.Unify(parameter, argument.Type, fromArgument: arguments[i].Span);
            bound.Add(argument);
        }

        foreach (var parameter in typeParameters.Where(p => !map.ContainsKey(p)))
        {
            _diagnostics.ReportCannotInferTypeArgument(nameSpan, parameter.Name, name);
            map[parameter] = TaipanType.Error;
        }
        foreach (var parameter in typeParameters.Where(p => !p.Constraints.IsEmpty))
            map[parameter] = CheckConstraint(parameter, map[parameter], nameSpan);
        for (var i = 0; i < bound.Count; i++)
            CheckAssignable(bound[i], TypeChecker.Substitute(parameterTypes[i], map), arguments[i].Span);
        return (bound.ToImmutable(), map);
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
            StructType s => s.TypeArguments.All(IsKnown),
            EnumType e => e.TypeArguments.All(IsKnown),
            _ => true,
        };

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
                case (StructType p, StructType a) when p.Symbol == a.Symbol:
                    foreach (var (pa, aa) in p.TypeArguments.Zip(a.TypeArguments))
                        Unify(pa, aa, fromArgument);
                    break;
                case (EnumType p, EnumType a) when p.Symbol == a.Symbol:
                    foreach (var (pa, aa) in p.TypeArguments.Zip(a.TypeArguments))
                        Unify(pa, aa, fromArgument);
                    break;
            }
        }

        private void Bind(TypeParameterSymbol parameter, TaipanType actual, TextSpan? fromArgument)
        {
            if (actual is NoneType)
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
            diagnostics.ReportConflictingTypeArgument(span, parameter.Name, existing, actual);
            map[parameter] = TaipanType.Error;
        }
    }

    private void ReportAmbiguous(AmbiguousVariantSymbol ambiguous, TextSpan span) =>
        _diagnostics.ReportAmbiguousVariant(span, ambiguous.Name, ambiguous.Variants.Select(v => v.Enum.Name));

    private ImmutableArray<BoundExpr> BindArguments(CallExpr call) =>
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

    private void ReportKeywordArguments(CallExpr call)
    {
        if (call.Arguments.FirstOrDefault(a => a.Name is not null) is { } keyword)
            _diagnostics.ReportKeywordArguments(keyword.Span);
    }

    private BoundExpr BindConstruct(CallExpr call, StructSymbol structSymbol, TaipanType? expected)
    {
        var (arguments, isKeyword, map) = BindFieldArguments(call, structSymbol.Name, structSymbol.Fields, structSymbol.TypeParameters, structSymbol.Type, expected);
        return arguments is { } valid
            ? new BoundConstruct((StructType)TypeChecker.Substitute(structSymbol.Type, map), valid, isKeyword, call.Span)
            : new BoundErrorExpr(call.Span);
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
    /// Constructor arguments for a struct or variant (SPEC §4.6), inferring a generic owner's type
    /// arguments (§3.6). Positional: one per field, in order. Keyword: every field exactly once.
    /// The arguments are null if any of them is wrong.
    /// </summary>
    private (ImmutableArray<(FieldSymbol, BoundExpr)>? Arguments, bool IsKeyword, Dictionary<TypeParameterSymbol, TaipanType> Map) BindFieldArguments(
        CallExpr call, string owner, ImmutableArray<FieldSymbol> fields, ImmutableArray<TypeParameterSymbol> typeParameters,
        TaipanType ownerType, TaipanType? expected)
    {
        var isKeyword = call.Arguments.Any(a => a.Name is not null);
        var valid = true;
        var chosen = new List<(FieldSymbol Field, Expr Value)>();
        if (!isKeyword)
        {
            if (call.Arguments.Length != fields.Length)
            {
                BindParameterArguments(call, owner, [.. fields.Select(f => f.Type)]);
                return (null, false, []);
            }
            chosen.AddRange(fields.Zip(call.Arguments.Select(a => a.Value)));
        }
        else
        {
            var given = new HashSet<string>();
            foreach (var argument in call.Arguments)
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
            foreach (var missing in fields.Where(f => !given.Contains(f.Name)))
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

        var target = BindValue(member.Target);
        var name = member.Member.Text;
        if (target.Type == TaipanType.Py)
            return BindPyCall(call, new BoundPyGetAttr(target, name, member.Span));
        if (ReportMixedArguments(call))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }
        ReportKeywordArguments(call);
        if (target.Type.ContainsError || ReportIfOptional(target, member.Target.Span))
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }

        if (target.Type is StructType { Symbol: var structSymbol })
        {
            if (structSymbol.Methods.TryGetValue(name, out var method))
            {
                var map = TypeChecker.MapOf(target.Type);
                var arguments = BindParameterArguments(call, name, [.. method.Parameters.Select(p => TypeChecker.Substitute(p.Type, map))]);
                return new BoundMethodCall(target, method, arguments, TypeChecker.Substitute(method.ReturnType, map), call.Span);
            }
            BindArguments(call);
            if (structSymbol.FindField(name) is not null)
                _diagnostics.ReportFieldNotMethod(member.Member.Span, name);
            else
                _diagnostics.ReportNoMethod(member.Member.Span, target.Type, name);
            return new BoundErrorExpr(call.Span);
        }

        if (BuiltinMethodOf(target.Type, name) is not { } builtin)
        {
            BindArguments(call);
            _diagnostics.ReportNoMethod(member.Member.Span, target.Type, name);
            return new BoundErrorExpr(call.Span);
        }

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

    private BoundExpr BindBuiltinCall(CallExpr call, FunctionSymbol function, BuiltinFunction builtin)
    {
        switch (builtin)
        {
            case BuiltinFunction.Print:
                return new BoundCall(function, BindArguments(call), TaipanType.Nothing, call.Span);
            case BuiltinFunction.Range:
                BindArguments(call);
                _diagnostics.ReportRangeOutsideFor(call.Callee.Span);
                return new BoundErrorExpr(call.Span);
        }

        var arguments = BindArguments(call);
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
        _diagnostics.ReportOptionalUse(span, value.Type);
        return true;
    }
}
