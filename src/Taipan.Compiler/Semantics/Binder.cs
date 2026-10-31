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
/// Resolves names and checks types (SPEC §3–§6), producing the bound tree. Works in two passes:
/// first the hoisted globals (constants and function signatures), then function bodies and
/// top-level statements.
/// </summary>
public sealed class Binder
{
    private static readonly BigInteger MaxInt = long.MaxValue;

    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Scope _global = new(null);
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

        var constants = ImmutableArray.CreateBuilder<BoundConstant>();
        var signatures = new List<(FuncDef Syntax, FunctionSymbol Symbol)>();
        foreach (var item in unit.Items)
        {
            switch (item)
            {
                case ConstDef constDef:
                    constants.Add(BindConstDef(constDef));
                    break;
                case FuncDef funcDef:
                    signatures.Add((funcDef, BindSignature(funcDef)));
                    break;
                case StructDef:
                    Unsupported("structs");
                    break;
                case EnumDef:
                    Unsupported("enums");
                    break;
            }
        }

        foreach (var stmt in unit.Items.OfType<Stmt>())
            CollectScriptNames(stmt);

        var functions = signatures.Select(s => BindFunctionBody(s.Syntax, s.Symbol)).ToImmutableArray();

        _scope = new Scope(_global);
        var topLevel = unit.Items.OfType<Stmt>().Select(BindStmt).ToImmutableArray();
        return new BoundProgram(constants.ToImmutable(), functions, new BoundBlock(topLevel, unit.Span));
    }

    private BoundConstant BindConstDef(ConstDef constDef)
    {
        var annotation = constDef.Type is null ? null : BindType(constDef.Type);
        BoundExpr value;
        if (IsConstantExpression(constDef.Value))
        {
            value = BindValue(constDef.Value);
            if (annotation is not null)
                CheckAssignable(value, annotation, constDef.Value.Span);
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

    private FunctionSymbol BindSignature(FuncDef funcDef)
    {
        var parameters = funcDef.Params
            .Select(p => new VariableSymbol(p.Name.Text, BindType(p.Type), VariableKind.Parameter))
            .ToImmutableArray();
        var returnType = funcDef.ReturnType is null ? TaipanType.Nothing : BindType(funcDef.ReturnType);
        var symbol = new FunctionSymbol(funcDef.Name.Text, parameters, returnType);
        DeclareGlobal(funcDef.Name, symbol);
        return symbol;
    }

    private BoundFunction BindFunctionBody(FuncDef funcDef, FunctionSymbol symbol)
    {
        _function = symbol;
        _loopDepth = 0;
        _scope = new Scope(_global);
        for (var i = 0; i < symbol.Parameters.Length; i++)
            DeclareLocal(funcDef.Params[i].Name, symbol.Parameters[i]);

        var body = BindBlock(funcDef.Body);
        if (symbol.ReturnType != TaipanType.Nothing && !TypeChecker.Terminates(body))
            _diagnostics.ReportNotAllPathsReturn(funcDef.Name.Span, symbol.Name);

        _function = null;
        _scope = _global;
        return new BoundFunction(symbol, body, funcDef.Span);
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

    private void DeclareGlobal(Identifier name, Symbol symbol)
    {
        if (_global.LookupLocal(name.Text) is not null)
        {
            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
            return;
        }
        _global.Declare(symbol);
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
            case NamedType named:
                _diagnostics.ReportUndefinedType(named.Span, named.Name.Text);
                return TaipanType.Error;
            case ListType:
                return Unsupported("lists");
            case DictType:
                return Unsupported("dicts");
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
        var value = BindValue(valueSyntax);
        if (declared is not null)
            CheckAssignable(value, declared, valueSyntax.Span);

        var symbol = new VariableSymbol(name.Text, declared ?? value.Type, kind);
        DeclareLocal(name, symbol);
        return new BoundVariableDecl(symbol, value, span);
    }

    private BoundStmt BindAssign(AssignStmt assign)
    {
        if (assign.Target is not NameExpr target)
        {
            Unsupported(assign.Target is MemberExpr ? "member access" : "indexing");
            BindValue(assign.Value);
            return new BoundBlock([], assign.Span);
        }

        var symbol = LookupName(target.Name, target.Span);
        var value = BindValue(assign.Value);
        if (symbol is not VariableSymbol variable)
        {
            if (symbol is FunctionSymbol)
                _diagnostics.ReportCannotAssign(target.Span, target.Name, "function", null);
            return new BoundExprStmt(value, assign.Span);
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

        if (assign.Op != AssignOp.Assign)
        {
            var current = new BoundVariable(variable, target.Span);
            value = BindBinaryOperation(current, TypeChecker.ToBinaryOp(assign.Op), value, assign.Span);
        }
        CheckAssignable(value, variable.Type, assign.Value.Span);
        return new BoundAssign(variable, value, assign.Span);
    }

    private BoundStmt BindReturn(ReturnStmt ret)
    {
        var value = ret.Value is null ? null : BindValue(ret.Value);
        if (_function is null)
        {
            _diagnostics.ReportReturnOutsideFunction(ret.Span);
            return new BoundReturn(value, ret.Span);
        }

        if (value is not null && _function.ReturnType == TaipanType.Nothing)
            _diagnostics.ReportUnexpectedReturnValue(ret.Value!.Span, _function.Name);
        else if (value is null && _function.ReturnType != TaipanType.Nothing)
            _diagnostics.ReportMissingReturnValue(ret.Span, _function.Name, _function.ReturnType);
        else if (value is not null)
            CheckAssignable(value, _function.ReturnType, ret.Value!.Span);
        return new BoundReturn(value, ret.Span);
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
        BoundExpr stop;
        if (forStmt.Iterable is CallExpr { Callee: NameExpr { Name: "range" } } call
            && _scope.Lookup("range") is FunctionSymbol { Builtin: BuiltinFunction.Range })
        {
            (start, stop) = BindRangeArguments(call);
        }
        else
        {
            stop = BindValue(forStmt.Iterable);
            if (stop.Type == TaipanType.Str)
                Unsupported("'for' over str");
            else if (!stop.Type.IsError)
                _diagnostics.ReportCannotIterate(forStmt.Iterable.Span, stop.Type);
        }

        // The loop variable is a let scoped to the body (SPEC §5.2).
        var outer = _scope;
        _scope = new Scope(outer);
        var variable = new VariableSymbol(forStmt.Variable.Text, TaipanType.Int, VariableKind.LoopVariable);
        DeclareLocal(forStmt.Variable, variable);
        _loopDepth++;
        var statements = forStmt.Body.Statements.Select(BindStmt).ToImmutableArray();
        _loopDepth--;
        _scope = outer;

        return new BoundForRange(variable, start, stop, new BoundBlock(statements, forStmt.Body.Span), forStmt.Span);
    }

    private (BoundExpr? Start, BoundExpr Stop) BindRangeArguments(CallExpr call)
    {
        ReportKeywordArguments(call);
        var arguments = call.Arguments.Select(a => BindValue(a.Value)).ToImmutableArray();
        foreach (var (argument, syntax) in arguments.Zip(call.Arguments))
            CheckAssignable(argument, TaipanType.Int, syntax.Value.Span);

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
        if (!value.Type.IsError && value.Type != TaipanType.Bool)
            _diagnostics.ReportConditionNotBool(exprCondition.Expr.Span, value.Type);
        return value;
    }

    // ---- Expressions ----

    /// <summary>Binds an expression whose value is used: a call without a return type is TP0200.</summary>
    private BoundExpr BindValue(Expr expr)
    {
        var bound = BindExpr(expr);
        if (bound is BoundCall { Type: NothingType } call)
        {
            _diagnostics.ReportNoValue(expr.Span, call.Function.Name);
            return new BoundErrorExpr(expr.Span);
        }
        return bound;
    }

    private BoundExpr BindExpr(Expr expr) => expr switch
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
        NoneLiteral => UnsupportedExpr("optionals", expr.Span),
        ListExpr => UnsupportedExpr("lists", expr.Span),
        DictExpr => UnsupportedExpr("dicts", expr.Span),
        MemberExpr => UnsupportedExpr("member access", expr.Span),
        IndexExpr => UnsupportedExpr("indexing", expr.Span),
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
            case FunctionSymbol function:
                _diagnostics.ReportFunctionAsValue(name.Span, function.Name);
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
        if (operand.Type.IsError)
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
        if (left.Type.IsError || right.Type.IsError)
            return new BoundBinary(left, op, right, TaipanType.Error, span);

        var type = op switch
        {
            BinaryOp.And or BinaryOp.Or => CheckLogicalOperands(left, right),
            BinaryOp.Equal or BinaryOp.NotEqual => CheckComparable(left, right, span),
            BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual =>
                CheckOrdered(left, op, right, span),
            BinaryOp.In or BinaryOp.NotIn => CheckOperands(left, op, right, span,
                left.Type == TaipanType.Str && right.Type == TaipanType.Str ? TaipanType.Bool : null),
            _ => CheckOperands(left, op, right, span, TypeChecker.Arithmetic(op, left.Type, right.Type)),
        };
        return new BoundBinary(left, op, right, type, span);
    }

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

    // ---- Calls ----

    private BoundExpr BindCall(CallExpr call)
    {
        if (call.Callee is MemberExpr)
            return UnsupportedExpr("methods", call.Span);

        if (call.Callee is not NameExpr calleeName)
        {
            var callee = BindValue(call.Callee);
            BindArguments(call);
            if (!callee.Type.IsError)
                _diagnostics.ReportNotAFunction(call.Callee.Span, _source.ToString(call.Callee.Span));
            return new BoundErrorExpr(call.Span);
        }

        var symbol = LookupName(calleeName.Name, calleeName.Span);
        if (symbol is not FunctionSymbol function)
        {
            BindArguments(call);
            if (symbol is VariableSymbol)
                _diagnostics.ReportNotAFunction(calleeName.Span, calleeName.Name);
            return new BoundErrorExpr(call.Span);
        }

        ReportKeywordArguments(call);
        var arguments = BindArguments(call);
        return function.Builtin is { } builtin
            ? BindBuiltinCall(call, function, builtin, arguments)
            : BindUserCall(call, function, arguments);
    }

    private ImmutableArray<BoundExpr> BindArguments(CallExpr call) =>
        [.. call.Arguments.Select(a => BindValue(a.Value))];

    private void ReportKeywordArguments(CallExpr call)
    {
        if (call.Arguments.FirstOrDefault(a => a.Name is not null) is { } keyword)
            _diagnostics.ReportKeywordArguments(keyword.Span);
    }

    private BoundCall BindUserCall(CallExpr call, FunctionSymbol function, ImmutableArray<BoundExpr> arguments)
    {
        if (arguments.Length != function.Parameters.Length)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, Arguments(function.Parameters.Length), arguments.Length);
        }
        else
        {
            for (var i = 0; i < arguments.Length; i++)
                CheckAssignable(arguments[i], function.Parameters[i].Type, call.Arguments[i].Value.Span);
        }
        return new BoundCall(function, arguments, function.ReturnType, call.Span);
    }

    private BoundExpr BindBuiltinCall(CallExpr call, FunctionSymbol function, BuiltinFunction builtin, ImmutableArray<BoundExpr> arguments)
    {
        switch (builtin)
        {
            case BuiltinFunction.Print:
                return new BoundCall(function, arguments, TaipanType.Nothing, call.Span);
            case BuiltinFunction.Range:
                _diagnostics.ReportRangeOutsideFor(call.Callee.Span);
                return new BoundErrorExpr(call.Span);
        }

        if (arguments.Length != 1)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, Arguments(1), arguments.Length);
            return new BoundCall(function, arguments, function.ReturnType, call.Span);
        }

        var argument = arguments[0];
        var argumentSpan = call.Arguments[0].Value.Span;
        switch (builtin)
        {
            case BuiltinFunction.Len:
                CheckAssignable(argument, TaipanType.Str, argumentSpan);
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

    private void CheckAssignable(BoundExpr value, TaipanType target, TextSpan span)
    {
        if (!TypeChecker.IsAssignable(value.Type, target))
            _diagnostics.ReportTypeMismatch(span, target, value.Type);
    }
}
