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
        Line(with.Binding is { } binding
            ? $"{TypeName(binding.Type)} @{_locals[binding]} = {manager}.@__enter__();"
            : $"{manager}.@__enter__();");
        Line($"var {raised} = false;");
        Line("try");
        EmitBody(with.Body);
        LineDirective(with.Span, statement: false);
        Line($"catch (global::System.Exception {caught})");
        Line("{");
        _indent++;
        Line($"{raised} = true;");
        Line($"if (!{Runtime}.Exceptions.IsCatchable({caught}))");
        Line("    throw;");
        Line($"var {exception} = {Runtime}.Exceptions.From({caught});");
        var exit = $"{manager}.@__exit__({Runtime}.ClassValue.Of({exception}), {exception}, {Runtime}.Traceback.Instance)";
        if (with.Suppresses)
        {
            Line($"if (!{exit})");
            Line("    throw;");
        }
        else
        {
            Line($"{exit};");
            Line("throw;");
        }
        _indent--;
        Line("}");
        Line("finally");
        Line("{");
        Line($"    if (!{raised})");
        Line($"        {manager}.@__exit__(null, null, null);");
        Line("}");
        _indent--;
        Line("}");
    }
}
