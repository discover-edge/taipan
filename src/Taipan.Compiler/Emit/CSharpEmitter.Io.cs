using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// Programs and I/O (SPEC §6.1, M37): print's keywords, sys.exit, and with statements (SPEC §3.17).
public sealed partial class CSharpEmitter
{
    // sys.exit's SystemExit, which the caller throws.
    private string Exit(BoundCall exit) => $"{Runtime}.Sys.@exit({string.Join(", ", exit.Arguments.Select(Expr))})";

    private string Print(BoundPrint print)
    {
        // A bare `null` would bind to the params array itself, not be an element.
        var values = print.Values.Select(v => v is BoundNone ? "(object?)null" : Expr(v));
        string[] options =
        [
            print.Sep is null ? "\" \"" : Expr(print.Sep),
            print.End is null ? "\"\\n\"" : Expr(print.End),
            print.File is null ? "null" : Expr(print.File),
            print.Flush is null ? "false" : Expr(print.Flush),
        ];
        return $"{Runtime}.Builtins.Print({string.Join(", ", options.Concat(values))})";
    }

    /// <summary>
    /// SPEC §8: the manager and <c>__enter__</c>'s result in locals, then a try whose catch passes the
    /// exception to <c>__exit__</c>, rethrowing unless it suppresses, and whose finally calls
    /// <c>__exit__(None, None, None)</c> when the block ended any other way.
    /// </summary>
    private void EmitWith(BoundWith with)
    {
        var manager = $"@{_locals.Fresh("manager")}";
        var raised = $"@{_locals.Fresh("raised")}";
        var caught = $"@{_locals.Fresh("caught")}";
        var exception = $"@{_locals.Fresh("exception")}";
        LineDirective(with.Span);
        Line("{");
        _indent++;
        Line($"var {manager} = {Expr(with.Manager)};");
        // `async with` awaits __aenter__ and __aexit__ (SPEC §3.20, M42).
        string Call(string method, string arguments) =>
            with.IsAsync ? $"await {Runtime}.Async.Await({manager}.@__a{method}__({arguments}))" : $"{manager}.@__{method}__({arguments})";
        Line(with.Binding is { } binding
            ? $"{TypeName(binding.Type)} @{_locals[binding]} = {Call("enter", "")};"
            : $"{Call("enter", "")};");
        Line($"var {raised} = false;");
        Line("try");
        EmitBody(with.Body);
        LineDirective(with.Span, statement: false);
        Line($"catch (global::System.Exception {caught}) when ({Runtime}.Tracebacks.Catch({caught}, {FrameArguments()}))");
        Line("{");
        _indent++;
        Line($"{raised} = true;");
        Line($"var {exception} = {Runtime}.Exceptions.From({caught});");
        // __exit__ handles the exception, for the context of one it raises (SPEC §3.19).
        var exit = Call("exit", $"{Runtime}.ClassValue.Of({exception}), {exception}, {exception}.@__traceback__");
        var suppressed = with.Suppresses ? $"@{_locals.Fresh("suppressed")}" : null;
        if (suppressed is not null)
            Line($"bool {suppressed};");
        Line($"{Runtime}.Tracebacks.Handle({exception});");
        Line("try");
        Line("{");
        Line(suppressed is null ? $"    {exit};" : $"    {suppressed} = {exit};");
        Line("}");
        Line("finally");
        Line("{");
        Line($"    {Runtime}.Tracebacks.Unhandle();");
        Line("}");
        if (suppressed is not null)
            Line($"if (!{suppressed})");
        Line("{");
        Line($"    {Runtime}.Tracebacks.Again({caught});");
        Line("    throw;");
        Line("}");
        _indent--;
        Line("}");
        Line("finally");
        Line("{");
        Line($"    if (!{raised})");
        Line($"        {Call("exit", "null, null, null")};");
        Line("}");
        _indent--;
        Line("}");
    }
}
