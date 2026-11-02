namespace Taipan.Compiler.Semantics;

// The names the binder resolves, recorded for the language server (SPEC §16.3).
public sealed partial class Binder
{
    private readonly SemanticIndex _index = new();

    /// <summary>Records the name at <paramref name="span"/> in the current file as naming <paramref name="target"/>.</summary>
    private void Index(Text.TextSpan span, object target, bool isDefinition = false)
    {
        if (isDefinition)
            _index.Define(_source, span, target);
        else
            _index.Refer(_source, span, target);
    }
}
