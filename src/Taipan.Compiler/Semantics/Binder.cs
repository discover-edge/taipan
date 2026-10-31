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
/// Resolves names and checks types (SPEC §3–§6), producing the bound tree. Works in passes:
/// struct names; then the hoisted declarations (constants, function signatures, struct fields
/// and method signatures); then function and method bodies and top-level statements.
/// </summary>
public sealed class Binder
{
    private static readonly BigInteger MaxInt = long.MaxValue;
    private static readonly HashSet<string> BuiltinTypeNames = ["int", "float", "bool", "str", "list", "dict", "py"];

    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Scope _global = new(null);
    private readonly Dictionary<string, StructSymbol> _structs = [];
    private readonly HashSet<string> _scriptNames = [];
    private Scope _scope;
    private FunctionSymbol? _function;
    private int _loopDepth;
    private string? _unsupported;

    private Binder(SourceText source)
    {
        _source = source;
        _scope = _global;
    }

    public static BindResult Bind(SourceText source, CompilationUnit unit)
    {
        var binder = new Binder(source);
        var program = binder.BindProgram(unit);
        return new BindResult(program, binder._diagnostics.ToImmutable(), binder._unsupported);
    }

    // ---- Program ----

    private BoundProgram BindProgram(CompilationUnit unit)
    {
        foreach (var builtin in FunctionSymbol.CreateBuiltins())
            _global.Declare(builtin);

        var structDefs = new List<(StructDef Syntax, StructSymbol Symbol)>();
        foreach (var structDef in unit.Items.OfType<StructDef>())
        {
            var symbol = new StructSymbol(structDef.Name.Text);
            if (BuiltinTypeNames.Contains(symbol.Name))
                _diagnostics.ReportDuplicateDefinition(structDef.Name.Span, symbol.Name);
            else if (DeclareGlobal(structDef.Name, symbol))
                _structs[symbol.Name] = symbol;
            structDefs.Add((structDef, symbol));
        }

        var constants = ImmutableArray.CreateBuilder<BoundConstant>();
        var functions = new List<(FuncDef Syntax, FunctionSymbol Symbol)>();
        var methods = new List<(MethodDef Syntax, FunctionSymbol Symbol)>();
        foreach (var item in unit.Items)
        {
            switch (item)
            {
                case ConstDef constDef:
                    constants.Add(BindConstDef(constDef));
                    break;
                case FuncDef funcDef:
                    functions.Add((funcDef, BindFunctionSignature(funcDef)));
                    break;
                case StructDef structDef:
                    var symbol = structDefs.First(s => s.Syntax == structDef).Symbol;
                    methods.AddRange(BindStructMembers(structDef, symbol));
                    break;
                case EnumDef:
                    Unsupported("enums");
                    break;
            }
        }

        foreach (var stmt in unit.Items.OfType<Stmt>())
            CollectScriptNames(stmt);

        var boundFunctions = functions.Select(f => BindBody(f.Symbol, f.Syntax.Params, f.Syntax.Name, f.Syntax.Body, f.Syntax.Span)).ToImmutableArray();
        var boundStructs = structDefs
            .Select(s => new BoundStruct(
                s.Symbol,
                [.. methods.Where(m => m.Symbol.Self!.Type == s.Symbol.Type)
                    .Select(m => BindBody(m.Symbol, m.Syntax.Params, m.Syntax.Name, m.Syntax.Body, m.Syntax.Span))],
                s.Syntax.Span))
            .ToImmutableArray();

        _scope = new Scope(_global);
        var topLevel = unit.Items.OfType<Stmt>().Select(BindStmt).ToImmutableArray();
        return new BoundProgram(boundStructs, constants.ToImmutable(), boundFunctions, new BoundBlock(topLevel, unit.Span));
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
        var symbol = new FunctionSymbol(funcDef.Name.Text, BindParams(funcDef.Params), BindReturnType(funcDef.ReturnType));
        DeclareGlobal(funcDef.Name, symbol);
        return symbol;
    }

    private ImmutableArray<VariableSymbol> BindParams(ImmutableArray<Param> parameters) =>
        [.. parameters.Select(p => new VariableSymbol(p.Name.Text, BindType(p.Type), VariableKind.Parameter))];

    private TaipanType BindReturnType(TypeSyntax? type) => type is null ? TaipanType.Nothing : BindType(type);

    private List<(MethodDef, FunctionSymbol)> BindStructMembers(StructDef structDef, StructSymbol symbol)
    {
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
        return methods;
    }

    private BoundFunction BindBody(FunctionSymbol symbol, ImmutableArray<Param> parameters, Identifier name, Block body, TextSpan span)
    {
        _function = symbol;
        _loopDepth = 0;
        _scope = new Scope(_global);
        if (symbol.Self is { } self)
            _scope.Declare(self);
        for (var i = 0; i < symbol.Parameters.Length; i++)
            DeclareLocal(parameters[i].Name, symbol.Parameters[i]);

        var boundBody = BindBlock(body);
        if (symbol.ReturnType != TaipanType.Nothing && !TypeChecker.Terminates(boundBody))
            _diagnostics.ReportNotAllPathsReturn(name.Span, symbol.Name);

        _function = null;
        _scope = _global;
        return new BoundFunction(symbol, boundBody, span);
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
                    CollectScriptNames(clause.Body);
                if (ifStmt.Else is not null)
                    CollectScriptNames(ifStmt.Else);
                break;
            case WhileStmt whileStmt:
                CollectScriptNames(whileStmt.Body);
                break;
            case ForStmt forStmt:
                _scriptNames.Add(forStmt.Variable.Text);
                CollectScriptNames(forStmt.Body);
                break;
        }
    }

    private void CollectScriptNames(Block block)
    {
        foreach (var stmt in block.Statements)
            CollectScriptNames(stmt);
    }

    // ---- Declarations and lookup ----

    private bool DeclareGlobal(Identifier name, Symbol symbol)
    {
        if (_global.LookupLocal(name.Text) is not null)
        {
            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
            return false;
        }
        _global.Declare(symbol);
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
                return Unsupported("the py type");
            case NamedType named when _structs.TryGetValue(named.Name.Text, out var structSymbol):
                return structSymbol.Type;
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
            case OptionalType:
                return Unsupported("optionals");
            default:
                return TaipanType.Error;
        }
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
        MatchStmt match => UnsupportedStmt("match", match.Span),
        _ => throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}"),
    };

    private BoundBlock UnsupportedStmt(string feature, TextSpan span)
    {
        Unsupported(feature);
        return new BoundBlock([], span);
    }

    private BoundVariableDecl BindVariableDecl(
        Identifier name, TypeSyntax? annotation, Expr valueSyntax, VariableKind kind, TextSpan span)
    {
        var declared = annotation is null ? null : BindType(annotation);
        var value = declared is null ? BindValue(valueSyntax) : BindExpected(valueSyntax, declared);
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
            if (symbol is FunctionSymbol or StructSymbol)
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
        if (target.Type.ContainsError)
            return new BoundExprStmt(BindValue(assign.Value), assign.Span);

        if (target.Type is StructType { Symbol: var structSymbol })
        {
            if (structSymbol.FindField(member.Member.Text) is { } field)
            {
                var value = BindAssignedValue(assign, new BoundFieldAccess(target, field, member.Span), field.Type);
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

        var returnType = _function.ReturnType;
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
            .Select(c => new BoundIfClause(BindCondition(c.Condition), BindBlock(c.Body)))
            .ToImmutableArray();
        var elseBlock = ifStmt.Else is null ? null : BindBlock(ifStmt.Else);
        return new BoundIf(clauses, elseBlock, ifStmt.Span);
    }

    private BoundWhile BindWhile(WhileStmt whileStmt)
    {
        var condition = BindCondition(whileStmt.Condition);
        _loopDepth++;
        var body = BindBlock(whileStmt.Body);
        _loopDepth--;
        return new BoundWhile(condition, body, whileStmt.Span);
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
                { ContainsError: true } => TaipanType.Error,
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

    private BoundExpr BindCondition(Condition condition)
    {
        if (condition is not ExprCondition exprCondition)
        {
            Unsupported("optionals");
            return new BoundErrorExpr(condition.Span);
        }

        var value = BindValue(exprCondition.Expr);
        if (!value.Type.ContainsError && value.Type != TaipanType.Bool)
            _diagnostics.ReportConditionNotBool(exprCondition.Expr.Span, value.Type);
        return value;
    }

    // ---- Expressions ----

    /// <summary>Binds against an expected type (SPEC §4.6) and checks the result is assignable to it.</summary>
    private BoundExpr BindExpected(Expr expr, TaipanType expected)
    {
        var value = BindValue(expr, expected);
        CheckAssignable(value, expected, expr.Span);
        return value;
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
        NameExpr name => BindName(name),
        UnaryExpr unary => BindUnary(unary),
        BinaryExpr binary => BindBinaryOperation(BindValue(binary.Left), binary.Op, BindValue(binary.Right), binary.Span),
        CallExpr call => BindCall(call),
        ListExpr list => BindList(list, expected),
        DictExpr dict => BindDict(dict, expected),
        MemberExpr member => BindMember(member),
        IndexExpr index => BindIndexOf(BindValue(index.Target), index),
        NoneLiteral => UnsupportedExpr("optionals", expr.Span),
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

    private BoundExpr BindName(NameExpr name)
    {
        switch (LookupName(name.Name, name.Span))
        {
            case VariableSymbol variable:
                return new BoundVariable(variable, name.Span);
            case FunctionSymbol or StructSymbol:
                _diagnostics.ReportFunctionAsValue(name.Span, name.Name);
                return new BoundErrorExpr(name.Span);
            default:
                return new BoundErrorExpr(name.Span);
        }
    }

    private BoundExpr BindUnary(UnaryExpr unary)
    {
        // -9223372036854775808 is in range only with the minus applied to the literal (SPEC §4.5).
        if (unary is { Op: UnaryOp.Negate, Operand: IntLiteral literal } && literal.Value == MaxInt + 1)
            return new BoundLiteral(long.MinValue, TaipanType.Int, unary.Span);

        var operand = BindValue(unary.Operand);
        if (operand.Type.ContainsError)
            return new BoundUnary(unary.Op, operand, TaipanType.Error, unary.Span);

        if (unary.Op == UnaryOp.Not)
        {
            if (operand.Type != TaipanType.Bool)
                _diagnostics.ReportConditionNotBool(unary.Operand.Span, operand.Type);
            return new BoundUnary(unary.Op, operand, TaipanType.Bool, unary.Span);
        }

        if (!operand.Type.IsNumeric)
        {
            _diagnostics.ReportUnaryOperatorType(unary.Span, "-", operand.Type);
            return new BoundUnary(unary.Op, operand, TaipanType.Error, unary.Span);
        }
        return new BoundUnary(unary.Op, operand, operand.Type, unary.Span);
    }

    private BoundExpr BindBinaryOperation(BoundExpr left, BinaryOp op, BoundExpr right, TextSpan span)
    {
        if (op == BinaryOp.Coalesce)
            return UnsupportedExpr("optionals", span);
        if (left.Type.ContainsError || right.Type.ContainsError)
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

    /// <summary>The common type of literal elements: all equal, or int and float mixing to float (SPEC §4.6).</summary>
    private TaipanType InferElementType(ImmutableArray<BoundExpr> elements)
    {
        var type = elements[0].Type;
        foreach (var element in elements.Skip(1))
        {
            if (element.Type.ContainsError || type.ContainsError || element.Type == type)
                continue;
            if (element.Type.IsNumeric && type.IsNumeric)
            {
                type = TaipanType.Float;
                continue;
            }
            _diagnostics.ReportTypeMismatch(element.Span, type, element.Type);
            return TaipanType.Error;
        }
        return type;
    }

    private BoundExpr BindMember(MemberExpr member)
    {
        var target = BindValue(member.Target);
        var name = member.Member.Text;
        if (target.Type.ContainsError)
            return new BoundErrorExpr(member.Span);

        if (target.Type is StructType { Symbol: var structSymbol })
        {
            if (structSymbol.FindField(name) is { } field)
                return new BoundFieldAccess(target, field, member.Span);
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

    private BoundExpr BindIndexOf(BoundExpr target, IndexExpr indexExpr)
    {
        switch (target.Type)
        {
            case { ContainsError: true }:
                BindValue(indexExpr.Index);
                return new BoundErrorExpr(indexExpr.Span);
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

    private BoundExpr BindCall(CallExpr call)
    {
        if (call.Callee is MemberExpr member)
            return BindMethodCall(call, member);

        if (call.Callee is not NameExpr calleeName)
        {
            var callee = BindValue(call.Callee);
            BindArguments(call);
            if (!callee.Type.ContainsError)
                _diagnostics.ReportNotAFunction(call.Callee.Span, _source.ToString(call.Callee.Span));
            return new BoundErrorExpr(call.Span);
        }

        var symbol = LookupName(calleeName.Name, calleeName.Span);
        switch (symbol)
        {
            case StructSymbol structSymbol:
                return BindConstruct(call, structSymbol);
            case FunctionSymbol { Builtin: { } builtin } function:
                ReportKeywordArguments(call);
                return BindBuiltinCall(call, function, builtin);
            case FunctionSymbol function:
                ReportKeywordArguments(call);
                var arguments = BindParameterArguments(call, function.Name, [.. function.Parameters.Select(p => p.Type)]);
                return new BoundCall(function, arguments, function.ReturnType, call.Span);
            default:
                BindArguments(call);
                if (symbol is VariableSymbol)
                    _diagnostics.ReportNotAFunction(calleeName.Span, calleeName.Name);
                return new BoundErrorExpr(call.Span);
        }
    }

    private ImmutableArray<BoundExpr> BindArguments(CallExpr call) =>
        [.. call.Arguments.Select(a => BindValue(a.Value))];

    /// <summary>
    /// Binds arguments against parameter types: each one gets its parameter as its expected type.
    /// A count mismatch is TP0209, reported at <paramref name="countSpan"/> (the callee).
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

    // Positional: one argument per field in order. Keyword: every field exactly once (SPEC §4.6).
    private BoundExpr BindConstruct(CallExpr call, StructSymbol structSymbol)
    {
        var fields = structSymbol.Fields;
        var isKeyword = call.Arguments.Any(a => a.Name is not null);
        if (!isKeyword)
        {
            var positional = BindParameterArguments(call, structSymbol.Name, [.. fields.Select(f => f.Type)]);
            if (positional.Length != fields.Length)
                return new BoundErrorExpr(call.Span);
            return new BoundConstruct(structSymbol, [.. fields.Zip(positional)], IsKeyword: false, call.Span);
        }

        var arguments = ImmutableArray.CreateBuilder<(FieldSymbol, BoundExpr)>();
        var given = new HashSet<string>();
        var valid = true;
        foreach (var argument in call.Arguments)
        {
            var name = argument.Name!;
            var field = structSymbol.FindField(name.Text);
            if (field is null)
            {
                _diagnostics.ReportNoField(name.Span, structSymbol.Type, name.Text);
                BindValue(argument.Value);
                valid = false;
                continue;
            }
            if (!given.Add(name.Text))
            {
                _diagnostics.ReportRepeatedField(name.Span, name.Text);
                valid = false;
            }
            arguments.Add((field, BindExpected(argument.Value, field.Type)));
        }

        foreach (var missing in fields.Where(f => !given.Contains(f.Name)))
        {
            _diagnostics.ReportMissingField(call.Callee.Span, missing.Name, structSymbol.Name);
            valid = false;
        }
        return valid
            ? new BoundConstruct(structSymbol, arguments.ToImmutable(), IsKeyword: true, call.Span)
            : new BoundErrorExpr(call.Span);
    }

    private BoundExpr BindMethodCall(CallExpr call, MemberExpr member)
    {
        var target = BindValue(member.Target);
        var name = member.Member.Text;
        ReportKeywordArguments(call);
        if (target.Type.ContainsError)
        {
            BindArguments(call);
            return new BoundErrorExpr(call.Span);
        }

        if (target.Type is StructType { Symbol: var structSymbol })
        {
            if (structSymbol.Methods.TryGetValue(name, out var method))
            {
                var arguments = BindParameterArguments(call, name, [.. method.Parameters.Select(p => p.Type)]);
                return new BoundMethodCall(target, method, arguments, call.Span);
            }
            BindArguments(call);
            if (structSymbol.FindField(name) is not null)
                _diagnostics.ReportFieldNotMethod(member.Member.Span, name);
            else
                _diagnostics.ReportNoMethod(member.Member.Span, target.Type, name);
            return new BoundErrorExpr(call.Span);
        }

        if (target.Type is DictOfType && name == "get")
            return UnsupportedExpr("optionals", call.Span);

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
        switch (builtin)
        {
            case BuiltinFunction.Len when argument.Type is not (ListOfType or DictOfType) && !argument.Type.ContainsError:
                CheckAssignable(argument, TaipanType.Str, argumentSpan, "str, list or dict");
                break;
            case BuiltinFunction.Int or BuiltinFunction.Float when argument.Type == TaipanType.Str:
                return UnsupportedExpr($"{function.Name}(str)", call.Span);
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
        if (!TypeChecker.IsAssignable(value.Type, target))
            _diagnostics.ReportTypeMismatch(span, expectedText ?? target.ToString(), value.Type);
    }
}
