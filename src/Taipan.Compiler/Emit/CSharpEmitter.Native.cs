using Taipan.Compiler.Text;

namespace Taipan.Compiler.Emit;

// A program emitted for a native build (SPEC §15.3, M23). Native code has no debug information to
// map its stack to .tpn lines, so each method keeps its current line and file in locals, and an
// exception filter that never catches records them in the innermost Taipan frame.
public sealed partial class CSharpEmitter
{
    private bool _native;

    // The current method's line and file locals, and the file it was last set to; null outside a tracked method.
    private string? _lineVariable;
    private string? _fileVariable;
    private string? _lineFile;

    private int LineOf(TextSpan span) => _lineSource.GetLinePosition(span.Start).Line;

    /// <summary>A method body, which in a native program records where an exception passing through it was raised.</summary>
    private void EmitTracked(TextSpan span, Action body)
    {
        if (!_native)
        {
            body();
            return;
        }
        var saved = (_lineVariable, _fileVariable, _lineFile);
        (_lineVariable, _fileVariable, _lineFile) = (_locals.Fresh("line"), _locals.Fresh("file"), _lineSource.Path);
        var exception = _locals.Fresh("e");
        Line($"int @{_lineVariable} = {LineOf(span)};");
        Line($"string @{_fileVariable} = \"{Escape(_lineSource.Path)}\";");
        Line("try");
        Line("{");
        _indent++;
        body();
        _indent--;
        Line("}");
        Line($"catch (global::System.Exception @{exception}) when ({Runtime}.Panic.Locate(@{exception}, @{_fileVariable}, @{_lineVariable}))");
        Line("{");
        Line("    throw;");
        Line("}");
        (_lineVariable, _fileVariable, _lineFile) = saved;
    }

    /// <summary>A lambda's one statement, with its own filter, since a lambda is its own method.</summary>
    private string TrackedStatement(TextSpan span, string statement)
    {
        var exception = _locals.Fresh("e");
        return $"try {{ {statement} }} catch (global::System.Exception @{exception}) when ({Runtime}.Panic.Locate(@{exception}, \"{Escape(_lineSource.Path)}\", {LineOf(span)})) {{ throw; }}";
    }

    /// <summary>A condition evaluated again on its own line, after other statements: an <c>elif</c>'s, or a <c>while</c>'s.</summary>
    private string Condition(TextSpan span, string condition) =>
        _lineVariable is null ? condition : $"{Runtime}.Panic.At(ref @{_lineVariable}, {LineOf(span)}) && ({condition})";
}
