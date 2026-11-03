using Taipan.Compiler.Text;

namespace Taipan.Compiler.Emit;

// Line tracking (SPEC §15.3, §3.19): each method that's a Python frame keeps its current line and
// file in locals, and an exception filter that never catches adds the frame to an exception's
// traceback as it passes. Before M41 this was only for native builds, to locate a panic.
public sealed partial class CSharpEmitter
{
    // Off only while emitting code that's no frame of its own, such as a cached function value's lambda.
    private bool _tracking = true;

    // The current frame's line and file locals, the file it was last set to, and its Python name;
    // null outside a tracked method.
    private string? _lineVariable;
    private string? _fileVariable;
    private string? _lineFile;
    private string? _frameName;

    private int LineOf(TextSpan span) => _lineSource.GetLinePosition(span.Start).Line;

    /// <summary>A frame's body, which adds the frame to the traceback of an exception passing through it.</summary>
    private void EmitTracked(TextSpan span, string name, Action body)
    {
        if (!_tracking)
        {
            body();
            return;
        }
        var saved = (_lineVariable, _fileVariable, _lineFile, _frameName);
        (_lineVariable, _fileVariable, _lineFile, _frameName) = (_locals.Fresh("line"), _locals.Fresh("file"), _lineSource.Path, name);
        var exception = _locals.Fresh("e");
        Line($"int @{_lineVariable} = {LineOf(span)};");
        Line($"string @{_fileVariable} = \"{Escape(_lineSource.Path)}\";");
        Line("try");
        Line("{");
        _indent++;
        body();
        _indent--;
        Line("}");
        Line($"catch (global::System.Exception @{exception}) when ({Runtime}.Tracebacks.Leave(@{exception}, @{_fileVariable}, @{_lineVariable}, \"{Escape(name)}\"))");
        Line("{");
        Line("    throw;");
        Line("}");
        (_lineVariable, _fileVariable, _lineFile, _frameName) = saved;
    }

    /// <summary>A lambda's one statement, as its own frame with its own filter, since a lambda is its own method.</summary>
    private string TrackedStatement(TextSpan span, string statement, string name)
    {
        if (!_tracking)
            return statement;
        var exception = _locals.Fresh("e");
        return $"try {{ {statement} }} catch (global::System.Exception @{exception}) when ({Runtime}.Tracebacks.Leave(@{exception}, \"{Escape(_lineSource.Path)}\", {LineOf(span)}, \"{Escape(name)}\")) {{ throw; }}";
    }

    /// <summary>The arguments that name the current frame for an <c>except</c> clause's filter: its file, line and name.</summary>
    private string FrameArguments() =>
        _lineVariable is null ? $"\"{Escape(_lineSource.Path)}\", 0, \"<module>\"" : $"@{_fileVariable}, @{_lineVariable}, \"{Escape(_frameName!)}\"";

    /// <summary>A condition evaluated again on its own line, after other statements: an <c>elif</c>'s, or a <c>while</c>'s.</summary>
    private string Condition(TextSpan span, string condition) =>
        _lineVariable is null ? condition : $"{Runtime}.Panic.At(ref @{_lineVariable}, {LineOf(span)}) && ({condition})";
}
