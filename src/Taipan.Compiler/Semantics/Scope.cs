namespace Taipan.Compiler.Semantics;

/// <summary>
/// A block of declarations. The chain is: built-ins ← a module's global scope (its functions,
/// constants, types and imports) ← script (top-level let/var) ← top-level blocks; and global ←
/// function (parameters) ← body blocks.
/// Function scopes skip the script scope, so functions can't see top-level variables (SPEC §4.1).
/// </summary>
public sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, Symbol> _symbols = [];

    public Scope? Parent { get; } = parent;

    public Symbol? LookupLocal(string name) => _symbols.GetValueOrDefault(name);

    public Symbol? Lookup(string name) => LookupLocal(name) ?? Parent?.Lookup(name);

    public void Declare(Symbol symbol) => _symbols[symbol.Name] = symbol;

    /// <summary>Declares <paramref name="symbol"/> under another name, as an import's alias does.</summary>
    public void Declare(string name, Symbol symbol) => _symbols[name] = symbol;
}
