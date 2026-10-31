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

    // Generic instantiations nested deeper than this are polymorphic recursion (SPEC §3.6, TP0219).
    private const int MaxInstantiationDepth = 32;

    private readonly SourceText _source;
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
    private readonly Stack<LoopContext> _loops = new();
    private int _indent;

    // Monomorphization (SPEC §8): the substitution for the instance being emitted, and the
    // instances still to emit. Each one is emitted once, under a name derived from its type arguments.
    private Dictionary<TypeParameterSymbol, TaipanType> _map = [];
    private readonly Dictionary<string, string> _instanceNames = [];
    private readonly Queue<Instance> _pending = new();
    private readonly Dictionary<FunctionSymbol, List<BoundFunction>> _genericFunctions = [];
    private readonly Dictionary<StructSymbol, BoundStruct> _structs = [];
    private readonly DiagnosticBag _diagnostics = new();
    private int _depth;
    private TextSpan _rootSpan;
    private TextSpan _statementSpan;
    private bool _tooDeep;

    /// <summary>A generic definition to emit with concrete type arguments.</summary>
    private sealed record Instance(Symbol Definition, ImmutableArray<TaipanType> TypeArguments, string Name, int Depth, TextSpan RootSpan);

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
        _program = program;
        _globalNames =
        [
            .. program.Constants.Select(c => c.Symbol.Name),
            .. program.Functions.Select(f => f.Symbol.Name),
            .. program.Structs.Select(s => s.Symbol.Name),
            .. program.Enums.Select(e => e.Name),
            .. program.Enums.SelectMany(e => e.Variants.Select(v => v.Name)),
        ];
        _out = _functionsOut;
        foreach (var function in program.Functions.Where(f => !f.Symbol.TypeParameters.IsEmpty))
        {
            if (!_genericFunctions.TryGetValue(function.Symbol, out var bodies))
                _genericFunctions[function.Symbol] = bodies = [];
            bodies.Add(function);
        }
        foreach (var structDef in program.Structs)
            _structs[structDef.Symbol] = structDef;
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
        foreach (var constant in _program.Constants)
            Line($"public static {TypeName(constant.Symbol.Type)} @{constant.Symbol.Name} = default!;");
        if (!_program.Constants.IsEmpty)
            Line();

        EmitTopLevel();
        foreach (var function in _program.Functions.Where(f => f.Symbol.TypeParameters.IsEmpty))
        {
            Line();
            EmitFunction(function, isMethod: false);
        }

        _out = _typesOut;
        _indent = 0;
        foreach (var structDef in _program.Structs.Where(s => s.Symbol.TypeParameters.IsEmpty))
        {
            Line();
            EmitStruct(structDef, structDef.Symbol.Name);
        }
        foreach (var enumSymbol in _program.Enums.Where(e => e.TypeParameters.IsEmpty))
        {
            Line();
            EmitEnum(enumSymbol, enumSymbol.Name);
        }

        while (_pending.TryDequeue(out var instance))
            EmitInstance(instance);

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
        (_depth, _rootSpan) = (instance.Depth, instance.RootSpan);
        switch (instance.Definition)
        {
            case FunctionSymbol function:
                _map = TypeChecker.Zip(function.TypeParameters, instance.TypeArguments);
                _out = _functionsOut;
                _indent = 1;
                Line();
                EmitFunction(BodyFor(function, instance.TypeArguments), isMethod: false, instance.Name);
                break;
            case StructSymbol structSymbol:
                _map = TypeChecker.Zip(structSymbol.TypeParameters, instance.TypeArguments);
                _out = _typesOut;
                _indent = 0;
                Line();
                EmitStruct(_structs[structSymbol], instance.Name);
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
        if (_depth + 1 > MaxInstantiationDepth)
        {
            if (!_tooDeep)
                _diagnostics.ReportInstantiationTooDeep(root, definition.Name, $"{definition.Name}[{string.Join(", ", typeArguments.Select(a => Abbreviate(a, 3)))}]");
            _tooDeep = true;
            return definition.Name;
        }

        var name = UniqueGlobal($"{definition.Name}_{string.Join("_", typeArguments.Select(Mangle))}");
        _globalNames.Add(name);
        _instanceNames[key] = name;
        _pending.Enqueue(new Instance(definition, typeArguments, name, _depth + 1, root));
        return name;
    }

    // A type for a message, with anything nested deeper than `depth` shown as "...".
    private static string Abbreviate(TaipanType type, int depth) => depth == 0 ? "..." : type switch
    {
        ListOfType list => $"list[{Abbreviate(list.Element, depth - 1)}]",
        DictOfType dict => $"dict[{Abbreviate(dict.Key, depth - 1)}, {Abbreviate(dict.Value, depth - 1)}]",
        OptionalOf optional => $"{Abbreviate(optional.Inner, depth)}?",
        StructType { TypeArguments.IsEmpty: false } s => $"{s.Symbol.Name}[{string.Join(", ", s.TypeArguments.Select(a => Abbreviate(a, depth - 1)))}]",
        EnumType { TypeArguments.IsEmpty: false } e => $"{e.Symbol.Name}[{string.Join(", ", e.TypeArguments.Select(a => Abbreviate(a, depth - 1)))}]",
        _ => type.ToString(),
    };

    private static string Mangle(TaipanType type) => type switch
    {
        PrimitiveType primitive => primitive.Name,
        ListOfType list => $"list_{Mangle(list.Element)}",
        DictOfType dict => $"dict_{Mangle(dict.Key)}_{Mangle(dict.Value)}",
        OptionalOf optional => $"{Mangle(optional.Inner)}_opt",
        StructType structType => string.Join("_", [structType.Symbol.Name, .. structType.TypeArguments.Select(Mangle)]),
        EnumType enumType => string.Join("_", [enumType.Symbol.Name, .. enumType.TypeArguments.Select(Mangle)]),
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
        foreach (var constant in _program.Constants)
        {
            LineDirective(constant.Span);
            Line($"@{constant.Symbol.Name} = {Expr(constant.Value)};");
        }
        EmitStatements(_program.TopLevel.Statements);
        HiddenDirective();
        _indent--;
        Line("}");
    }

    private void EmitFunction(BoundFunction function, bool isMethod, string? name = null)
    {
        var symbol = function.Symbol;
        _locals = LocalNames.For(function.Parameters, function.Body, _globalNames);
        _self = symbol.Self;
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{TypeName(p.Type)} @{_locals[p]}"));
        Line($"public {(isMethod ? "" : "static ")}{TypeName(symbol.ReturnType)} @{name ?? symbol.Name}({parameters})");
        Line("{");
        _indent++;
        EmitStatements(function.Body.Statements);
        HiddenDirective();
        _indent--;
        Line("}");
        _self = null;
    }

    // A struct is a class with mutable properties, a positional constructor and its methods (SPEC §8).
    private void EmitStruct(BoundStruct structDef, string className)
    {
        var symbol = structDef.Symbol;
        Line($"public sealed class @{className} : {Runtime}.IStruct");
        Line("{");
        _indent++;
        foreach (var field in symbol.Fields)
            Line($"public {TypeName(field.Type)} @{field.Name} {{ get; set; }}");
        Line();

        var parameters = string.Join(", ", symbol.Fields.Select(f => $"{TypeName(f.Type)} @{f.Name}"));
        Line($"public @{className}({parameters})");
        Line("{");
        foreach (var field in symbol.Fields)
            Line($"    this.@{field.Name} = @{field.Name};");
        Line("}");
        Line();

        var fields = string.Join(", ", symbol.Fields.Select(f => $"(\"{f.Name}\", (object?)this.@{f.Name})"));
        Line($"string {Runtime}.IStruct.TypeName => \"{symbol.Name}\";");
        Line($"{Collections}.IReadOnlyList<(string Name, object? Value)> {Runtime}.IStruct.Fields => [{fields}];");
        Line($"public override string ToString() => {Runtime}.Display.Format(this);");

        foreach (var method in structDef.Methods)
        {
            Line();
            EmitFunction(method, isMethod: true);
        }
        _indent--;
        Line("}");
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
            var parameters = string.Join(", ", variant.Fields.Select(f => $"{TypeName(f.Type)} @{f.Name}"));
            Line($"public sealed record @{variant.Name}({parameters}) : @{className}, {variantInterface}");
            Line("{");
            _indent++;
            var fields = string.Join(", ", variant.Fields.Select(f => $"(\"{f.Name}\", (object?)this.@{f.Name})"));
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

    private void EmitBody(BoundBlock block)
    {
        Line("{");
        _indent++;
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
                    () => Line($"{Operand(assign.Target)}.@{assign.Field.Name} = {Expr(assign.Value)};"));
                break;
            case BoundIndexAssign assign:
                LineDirective(assign.Span);
                EmitCompound(assign.IsCompound, [assign.Target, assign.Index],
                    () => Line($"{Runtime}.Ops.SetIndex({Expr(assign.Target)}, {Expr(assign.Index)}, {Expr(assign.Value)});"));
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
            case BoundExprStmt { Expr: BoundCall or BoundMethodCall or BoundBuiltinMethodCall } exprStmt:
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
                Line($"foreach ({TypeName(forEach.Variable.Type)} @{_locals[forEach.Variable]} in {Runtime}.Iter.Over({Expr(forEach.Iterable)}))");
                EmitLoopBody(forEach.Body);
                break;
            case BoundMatch match:
                EmitMatch(match);
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

    // range(a, b) evaluates a, then b, once (SPEC §8).
    private void EmitForRange(BoundForRange forStmt)
    {
        var variable = $"@{_locals[forStmt.Variable]}";
        var end = $"@{_locals.Fresh("end")}";
        var start = forStmt.Start is null ? "0L" : Expr(forStmt.Start);
        LineDirective(forStmt.Span);
        Line($"for (long {variable} = {start}, {end} = {Expr(forStmt.Stop)}; {variable} < {end}; {variable}++)");
        EmitLoopBody(forStmt.Body);
    }

    private void EmitLoopBody(BoundBlock body)
    {
        var loop = new LoopContext();
        _loops.Push(loop);
        EmitBody(body);
        _loops.Pop();
        if (loop.BreakLabel is { } label)
            Line($"{label}: ;");
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
            BoundVariable { Symbol.Kind: VariableKind.Constant } variable => Global(variable.Symbol.Name),
            BoundVariable variable => $"@{_locals[variable.Symbol]}",
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
            BoundFieldAccess access => $"{Operand(access.Target)}.@{access.Field.Name}",
            BoundIndex index => $"{Runtime}.Ops.Index({Expr(index.Target)}, {Expr(index.Index)})",
            BoundMethodCall call => $"{Operand(call.Target)}.@{call.Method.Name}({Arguments(call.Arguments)})",
            BoundBuiltinMethodCall call => BuiltinMethodCall(call),
            BoundNone => "null",
            BoundIsSome isSome => $"{Operand(isSome.Operand)} is {{}} @{_locals[isSome.Binding]}",
            BoundIsNone isNone => $"{Operand(isNone.Operand)} is null",
            _ => throw new InvalidOperationException($"unexpected bound expression {expr.GetType().Name}"),
        };
    }

    /// <summary>A function or constant: unqualified in the program class, qualified from a struct's methods.</summary>
    private string Global(string name) => _self is null ? $"@{name}" : $"global::{_programClass}.@{name}";

    private string FunctionReference(BoundCall call) =>
        call.TypeArguments.IsEmpty
            ? Global(call.Function.Name)
            : Global(InstanceName(call.Function, [.. call.TypeArguments.Select(Concrete)]));

    private string Arguments(IEnumerable<BoundExpr> arguments) => string.Join(", ", arguments.Select(Expr));

    /// <summary>An operand of an operator or member access, parenthesized when it's an operator expression.</summary>
    private string Operand(BoundExpr expr) =>
        expr is BoundBinary or BoundUnary && !_hoisted.ContainsKey(expr) ? $"({Expr(expr)})" : Expr(expr);

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
        var left = Operand(binary.Left);
        var right = Operand(binary.Right);
        var compared = TypeChecker.Unwrap(leftType) is NoneType ? TypeChecker.Unwrap(Concrete(binary.Right.Type)) : TypeChecker.Unwrap(leftType);
        var structural = compared is ListOfType or DictOfType or StructType or EnumType;
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
        var arguments = Arguments(call.Arguments);
        var argumentType = call.Arguments.IsEmpty ? TaipanType.Error : Concrete(call.Arguments[0].Type);
        return call.Function.Builtin switch
        {
            null => $"{FunctionReference(call)}({arguments})",
            // `null` alone would bind to the params array itself, not be one of its elements.
            BuiltinFunction.Print => $"{Runtime}.Builtins.Print({string.Join(", ", call.Arguments.Select(a => a is BoundNone ? "(object?)null" : Expr(a)))})",
            BuiltinFunction.Len => $"{Runtime}.Builtins.Len({arguments})",
            BuiltinFunction.Str => $"{Runtime}.Display.Format({arguments})",
            BuiltinFunction.Int when argumentType == TaipanType.Str => $"{Runtime}.Ops.ParseInt({arguments})",
            BuiltinFunction.Float when argumentType == TaipanType.Str => $"{Runtime}.Ops.ParseFloat({arguments})",
            BuiltinFunction.Int when argumentType == TaipanType.Float => $"{Runtime}.Ops.ToInt({arguments})",
            BuiltinFunction.Int => arguments,
            BuiltinFunction.Float when argumentType == TaipanType.Int => $"(double){Operand(call.Arguments[0])}",
            BuiltinFunction.Float => arguments,
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

    // Keyword construction uses C# named arguments, which C# evaluates in source order.
    private string Construct(BoundConstruct construct)
    {
        var arguments = construct.Arguments.Select(a =>
            construct.IsKeyword ? $"@{a.Field.Name}: {Expr(a.Value)}" : Expr(a.Value));
        return $"new {TypeName(construct.StructType)}({string.Join(", ", arguments)})";
    }

    private string ConstructVariant(BoundConstructVariant construct)
    {
        var arguments = construct.Arguments.Select(a =>
            construct.IsKeyword ? $"@{a.Field.Name}: {Expr(a.Value)}" : Expr(a.Value));
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
        NothingType => "void",
        ListOfType list => $"{Collections}.List<{TypeName(list.Element)}>",
        DictOfType dict => $"{Collections}.OrderedDictionary<{TypeName(dict.Key)}, {TypeName(dict.Value)}>",
        StructType { TypeArguments.IsEmpty: true } structType => $"global::@{structType.Symbol.Name}",
        StructType structType => $"global::@{InstanceName(structType.Symbol, structType.TypeArguments)}",
        EnumType { TypeArguments.IsEmpty: true } enumType => $"global::@{enumType.Symbol.Name}",
        EnumType enumType => $"global::@{InstanceName(enumType.Symbol, enumType.TypeArguments)}",
        OptionalOf optional => $"{TypeName(optional.Inner)}?",
        var other => throw new InvalidOperationException($"no C# type for {other}"),
    };

    /// <summary>Whether the C# type is a value type, whose optional is <c>Nullable&lt;T&gt;</c>.</summary>
    private static bool IsValueType(TaipanType type) =>
        type == TaipanType.Int || type == TaipanType.Float || type == TaipanType.Bool;

    // ---- Output ----

    private void LineDirective(TextSpan span)
    {
        _statementSpan = span;
        var line = _source.GetLinePosition(span.Start).Line;
        _out.Append($"#line {line} \"{_source.Path}\"\n");
    }

    private void HiddenDirective() => _out.Append("#line hidden\n");

    private void Line(string text = "")
    {
        if (text.Length > 0)
            _out.Append(' ', _indent * 4);
        _out.Append(text).Append('\n');
    }
}
