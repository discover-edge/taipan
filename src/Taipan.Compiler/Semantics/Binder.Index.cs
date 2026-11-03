using System.Collections.Immutable;

namespace Taipan.Compiler.Semantics;

// The names the binder resolves, recorded for the language server (SPEC §16.3), and what
// completion needs (SPEC §16.4, M32).
public sealed partial class Binder
{
    private readonly SemanticIndex _index = new();

    /// <summary>Records the name at <paramref name="span"/> in the current file as naming <paramref name="target"/>.</summary>
    private void Index(Text.TextSpan span, object target, bool isDefinition = false)
    {
        if (_speculating > 0)
            return;
        if (isDefinition)
            _index.Define(_source, span, target);
        else
            _index.Refer(_source, span, target);
    }

    /// <summary>Records what's before the <c>.</c> of the member at <paramref name="member"/>.</summary>
    private void RecordReceiver(Text.TextSpan member, object receiver)
    {
        if (_speculating == 0)
            _index.Receiver(_source, member, receiver);
    }

    /// <summary>Records the names in scope where the name at <paramref name="span"/> didn't resolve.</summary>
    private void Unresolved(Text.TextSpan span, bool inType)
    {
        if (_speculating > 0)
            return;
        var names = ImmutableArray.CreateBuilder<SemanticIndex.ScopedName>();
        var seen = new HashSet<string>();
        if (inType)
        {
            foreach (var (name, type) in _typeParameters)
            {
                if (type is TypeParameterType parameter && seen.Add(name))
                    names.Add(new(name, parameter.Symbol, 0));
            }
        }
        var distance = 0;
        for (var scope = _scope; scope is not null; scope = scope.Parent)
        {
            var at = scope == _builtins ? SemanticIndex.ScopedName.Builtins : scope == _global || scope.Parent == _global && _function is null ? SemanticIndex.ScopedName.File : distance++;
            foreach (var (name, symbol) in scope.Symbols.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (seen.Add(name))
                    names.Add(new(name, symbol, at));
            }
        }
        _index.Unresolved(_source, span, new SemanticIndex.NameScope(names.ToImmutable(), inType));
    }
}
