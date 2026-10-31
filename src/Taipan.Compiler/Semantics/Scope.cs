namespace Taipan.Compiler.Semantics;

/// <summary>
/// A block of declarations. The chain is: global (functions, constants, built-ins) ← script
/// (top-level let/var) ← top-level blocks; and global ← function (parameters) ← body blocks.
/// Function scopes skip the script scope, so functions can't see top-level variables (SPEC §4.1).
/// </summary>
public sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, Symbol> _symbols = [];

    public Scope? Parent { get; } = parent;

    public Symbol? LookupLocal(string name) => _symbols.GetValueOrDefault(name);

    public Symbol? Lookup(string name) => LookupLocal(name) ?? Parent?.Lookup(name);

    public void Declare(Symbol symbol) => _symbols[symbol.Name] = symbol;
}
