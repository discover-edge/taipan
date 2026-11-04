using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Programs and I/O (SPEC §6.1, M37): print's keywords, input, open, sys.exit, os.getenv, file
// objects, and with statements (SPEC §3.17).
public sealed partial class Binder
{
    private static readonly string[] Modes = ["r", "w", "a", "x", "rt", "wt", "at", "xt", "tr", "tw", "ta", "tx"];

    private static readonly HashSet<string> Encodings =
        ["utf-8", "utf8", "utf-8-sig", "utf8-sig", "ascii", "us-ascii", "latin-1", "latin1", "iso-8859-1", "iso8859-1", "l1"];

    /// <summary>A call of a built-in function M37 adds, or of <c>print</c>, which takes keywords since M37.</summary>
    private BoundExpr BindIoBuiltin(CallExpr call, FunctionSymbol function, BuiltinFunction builtin) => builtin switch
    {
        BuiltinFunction.Print => BindPrint(call, function),
        BuiltinFunction.Open => BindOpen(call, function),
        BuiltinFunction.Input => BindStrings(call, function, 0, 1, TaipanType.Str),
        BuiltinFunction.GetEnv => BindStrings(call, function, 1, 2, call.Arguments.Length == 1 ? new OptionalOf(TaipanType.Str) : TaipanType.Str),
        _ => BindExit(call, function),
    };

    // print(*values, sep=" ", end="\n", file=sys.stdout, flush=False) (SPEC §6).
    private BoundExpr BindPrint(CallExpr call, FunctionSymbol function)
    {
        var values = BindArgumentValues(call with { Arguments = [.. call.Arguments.Where(a => a.Name is null)] });
        if (call.Arguments.All(a => a.Name is null))
            return new BoundCall(function, values, TaipanType.Nothing, call.Span);
        var keywords = BindKeywords(call, "print", new()
        {
            ["sep"] = TaipanType.Str,
            ["end"] = TaipanType.Str,
            ["file"] = TaipanType.TextIO,
            ["flush"] = TaipanType.Bool,
        });
        return new BoundPrint(values, keywords.GetValueOrDefault("sep"), keywords.GetValueOrDefault("end"),
            keywords.GetValueOrDefault("file"), keywords.GetValueOrDefault("flush"), call.Span);
    }

    /// <summary>A call's keyword arguments, each checked against its type; any other is TP0218.</summary>
    private Dictionary<string, BoundExpr> BindKeywords(CallExpr call, string function, Dictionary<string, TaipanType> accepted)
    {
        var bound = new Dictionary<string, BoundExpr>();
        foreach (var argument in call.Arguments.Where(a => a.Name is not null))
        {
            var name = argument.Name!.Text;
            if (!accepted.TryGetValue(name, out var type))
            {
                _diagnostics.ReportNoKeywordArgument(argument.Name.Span, function, name);
                BindIgnored(argument.Value);
            }
            else if (bound.ContainsKey(name))
            {
                _diagnostics.ReportRepeatedArgument(argument.Name.Span, name);
                BindIgnored(argument.Value);
            }
            else
            {
                bound[name] = BindExpected(argument.Value, type);
            }
        }
        return bound;
    }

    // input(prompt) and os.getenv(name, default): str arguments, positionally.
    private BoundExpr BindStrings(CallExpr call, FunctionSymbol function, int least, int most, TaipanType result)
    {
        ReportKeywordArguments(call, function.Name);
        if (call.Arguments.Length < least || call.Arguments.Length > most)
        {
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, least == most - 1 ? $"{least} or {Arguments(most)}" : Arguments(most), call.Arguments.Length);
            return new BoundCall(function, BindArguments(call), result, call.Span);
        }
        return new BoundCall(function, [.. call.Arguments.Select(a => BindExpected(a.Value, TaipanType.Str))], result, call.Span);
    }

    // sys.exit(), sys.exit(code: int) or sys.exit(message: str) (SPEC §6.1).
    private BoundExpr BindExit(CallExpr call, FunctionSymbol function)
    {
        ReportKeywordArguments(call, function.Name);
        var arguments = BindArgumentValues(call);
        if (arguments.Length > 1)
            _diagnostics.ReportWrongArgumentCount(call.Callee.Span, function.Name, "0 or 1 argument", arguments.Length);
        else if (arguments is [var argument] && argument.Type != TaipanType.Int && argument.Type != TaipanType.Str && !argument.Type.ContainsError)
            CheckAssignable(argument, TaipanType.Int, call.Arguments[0].Value.Span, "int or str");
        return new BoundCall(function, arguments, TaipanType.Nothing, call.Span);
    }

    /// <summary>
    /// <c>open(file, mode="r", *, encoding="utf-8", newline=None)</c> (SPEC §6.1): the mode, encoding
    /// and newline are literals, checked here, and become the call's last three arguments.
    /// </summary>
    private BoundExpr BindOpen(CallExpr call, FunctionSymbol function)
    {
        var slots = new Dictionary<string, Expr>();
        string[] positional = ["file", "mode"];
        var valid = true;
        var position = 0;
        foreach (var argument in call.Arguments)
        {
            var name = argument.Name?.Text ?? (position < positional.Length ? positional[position++] : null);
            if (name is null)
            {
                _diagnostics.ReportWrongArgumentCount(call.Callee.Span, "open", "1 or 2 arguments", call.Arguments.Count(a => a.Name is null));
                valid = false;
            }
            else if (name is not ("file" or "mode" or "encoding" or "newline"))
            {
                _diagnostics.ReportNoKeywordArgument(argument.Name!.Span, "open", name);
                valid = false;
            }
            else if (!slots.TryAdd(name, argument.Value))
            {
                _diagnostics.ReportRepeatedArgument(argument.Name?.Span ?? argument.Span, name);
                valid = false;
            }
        }
        if (!slots.TryGetValue("file", out var file))
        {
            if (valid)
                _diagnostics.ReportWrongArgumentCount(call.Callee.Span, "open", "1 or 2 arguments", 0);
            BindArguments(call);
            return new BoundCall(function, [], TaipanType.TextIO, call.Span);
        }
        if (!valid)
        {
            BindArguments(call);
            return new BoundCall(function, [], TaipanType.TextIO, call.Span);
        }

        var path = BindExpected(file, TaipanType.Str);
        var mode = OpenLiteral(slots.GetValueOrDefault("mode"), "mode", "r", m => Modes.Contains(m), _diagnostics.ReportInvalidMode);
        var encoding = OpenLiteral(slots.GetValueOrDefault("encoding"), "encoding", "utf-8",
            e => Encodings.Contains(e.ToLowerInvariant().Replace('_', '-')), _diagnostics.ReportUnknownEncoding);
        BoundExpr newline = new BoundNone(call.Span);
        if (slots.GetValueOrDefault("newline") is { } newlineSyntax)
        {
            if (newlineSyntax is StringLiteral { Value: "" } empty)
                newline = new BoundLiteral("", TaipanType.Str, empty.Span);
            else if (newlineSyntax is not NoneLiteral)
            {
                var value = BindValue(newlineSyntax);
                if (!value.Type.ContainsError)
                    _diagnostics.ReportInvalidNewline(newlineSyntax.Span);
            }
        }
        return new BoundCall(function, [path, mode, encoding, newline], TaipanType.TextIO, call.Span);
    }

    // A literal argument of open, or its default; anything else is reported.
    private BoundExpr OpenLiteral(Expr? syntax, string name, string fallback, Func<string, bool> allowed, Action<TextSpan, string> reportInvalid)
    {
        if (syntax is null)
            return new BoundLiteral(fallback, TaipanType.Str, default);
        if (syntax is StringLiteral literal)
        {
            if (!allowed(literal.Value))
                reportInvalid(literal.Span, literal.Value);
            return new BoundLiteral(literal.Value, TaipanType.Str, literal.Span);
        }
        var value = BindExpected(syntax, TaipanType.Str);
        if (!value.Type.ContainsError)
            _diagnostics.ReportOpenNeedsLiteral(syntax.Span, name);
        return value;
    }

    /// <summary>The attributes of a file, a class value and a traceback (SPEC §6.1, §3.17, §3.19), or null if the type has none.</summary>
    private static TaipanType? IoAttribute(TaipanType type, string name) => (type, name) switch
    {
        (OpaqueType { Name: "TextIO" }, "name" or "mode" or "encoding") => TaipanType.Str,
        (OpaqueType { Name: "TextIO" }, "closed") => TaipanType.Bool,
        (ClassOfType, "__name__") => TaipanType.Str,
        // A traceback's entry (SPEC §3.19, M41).
        (OpaqueType { Name: "TracebackType" }, "tb_lineno") => TaipanType.Int,
        (OpaqueType { Name: "TracebackType" }, "tb_next") => new OptionalOf(TaipanType.Traceback),
        _ => null,
    };

    /// <summary>
    /// <c>with</c> (SPEC §3.17): its items nest left to right, each a <see cref="BoundWith"/>
    /// whose body is the next.
    /// </summary>
    private BoundStmt BindWith(WithStmt with)
    {
        // `async with` may be only where `await` may (SPEC §3.20).
        if (with.IsAsync)
            CheckAsyncContext(new TextSpan(with.Span.Start, "async".Length), "async with");
        return BindWithItem(with, 0);
    }

    private BoundWith BindWithItem(WithStmt with, int index)
    {
        var item = with.Items[index];
        var manager = BindValue(item.Manager);
        var (enter, suppresses) = with.IsAsync ? AsyncContextManager(manager, item.Manager.Span) : ContextManager(manager, item.Manager.Span);
        var binding = item.Binding is { } bindingName ? new VariableSymbol(bindingName.Text, enter, VariableKind.Let) : null;

        BoundBlock BindBody()
        {
            var outer = _scope;
            _scope = new Scope(outer);
            if (binding is not null)
                DeclareLocal(item.Binding!, binding);
            var body = index + 1 < with.Items.Length
                ? new BoundBlock([BindWithItem(with, index + 1)], with.Body.Span)
                : BindBlock(with.Body);
            _scope = outer;
            return body;
        }

        // A manager that may suppress can end its block after any statement, as a try's except can (SPEC §3.9).
        BoundBlock body;
        if (suppresses)
        {
            var start = _narrowing;
            body = Repeatable(BindBody, b => [b]);
            _narrowing = start;
        }
        else
        {
            body = BindBody();
        }
        return new BoundWith(manager, binding, enter, suppresses, body, item.Span) { IsAsync = with.IsAsync };
    }

    /// <summary>What a manager's <c>__enter__</c> gives, and whether its <c>__exit__</c> may suppress (returns bool).</summary>
    private (TaipanType Enter, bool Suppresses) ContextManager(BoundExpr manager, TextSpan span)
    {
        if (manager.Type.ContainsError || ReportIfOptional(manager, span))
            return (TaipanType.Error, false);
        if (manager.Type == TaipanType.TextIO)
            return (TaipanType.TextIO, false);
        if (SpecialMethods.Method(manager.Type, "__enter__") is var (_, _, enter) && SpecialMethods.Method(manager.Type, "__exit__") is var (_, _, exit))
            return (enter, exit == TaipanType.Bool);
        _diagnostics.ReportNotContextManager(span, manager.Type, isAsync: IsAsyncContextManager(manager.Type));
        return (TaipanType.Error, false);
    }

    /// <summary>
    /// What an async manager's <c>__aenter__</c> gives once awaited, and whether its <c>__aexit__</c>
    /// may suppress (SPEC §3.20): a class's, or <c>asyncio.timeout</c>'s, which never does.
    /// </summary>
    private (TaipanType Enter, bool Suppresses) AsyncContextManager(BoundExpr manager, TextSpan span)
    {
        if (manager.Type.ContainsError || ReportIfOptional(manager, span))
            return (TaipanType.Error, false);
        if (manager.Type == TaipanType.Timeout)
            return (TaipanType.Timeout, false);
        if (SpecialMethods.Method(manager.Type, "__aenter__") is (_, _, AwaitableOfType enter)
            && SpecialMethods.Method(manager.Type, "__aexit__") is (_, _, AwaitableOfType exit))
            return (enter.Result, exit.Result == TaipanType.Bool);
        var isPlain = manager.Type == TaipanType.TextIO
            || SpecialMethods.Method(manager.Type, "__enter__") is not null && SpecialMethods.Method(manager.Type, "__exit__") is not null;
        _diagnostics.ReportNotAsyncContextManager(span, manager.Type, isPlain);
        return (TaipanType.Error, false);
    }

    private static bool IsAsyncContextManager(TaipanType type) =>
        type == TaipanType.Timeout || SpecialMethods.Method(type, "__aenter__") is not null && SpecialMethods.Method(type, "__aexit__") is not null;
}
