using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

/// <summary>
/// Chooses C# names for one method's locals (SPEC §8). Taipan allows a name in a nested block
/// and again later in the enclosing block; C# doesn't, because a C# local's scope is its whole
/// block. Such a nested local gets a numeric suffix. So does a local named <c>_</c>, which the
/// emitter uses as the discard.
/// </summary>
public sealed class LocalNames
{
    private readonly Dictionary<VariableSymbol, string> _names = [];
    private readonly HashSet<string> _used;
    private readonly Stack<HashSet<string>> _enclosing = new();

    private LocalNames(IEnumerable<string> reserved) => _used = [.. reserved, "_"];

    public string this[VariableSymbol symbol] => _names[symbol];

    public static LocalNames For(IReadOnlyList<VariableSymbol> parameters, BoundBlock body, IEnumerable<string> globalNames)
    {
        var names = new LocalNames(globalNames);
        foreach (var parameter in parameters)
            names._used.Add(parameter.Name);
        names.CollectAll(body);

        names.Declare(parameters, []);
        names._enclosing.Push([.. parameters.Select(p => p.Name)]);
        names.VisitBlock(body.Statements, []);
        names._enclosing.Pop();
        return names;
    }

    /// <summary>A name for a compiler temporary, distinct from every local in the method.</summary>
    public string Fresh(string baseName)
    {
        var name = baseName;
        for (var i = 2; _used.Contains(name); i++)
            name = $"{baseName}{i}";
        _used.Add(name);
        return name;
    }

    private void CollectAll(BoundStmt stmt)
    {
        foreach (var symbol in DirectDeclarations([stmt]))
            _used.Add(symbol.Name);
        foreach (var (nested, extra) in NestedBlocks(stmt))
        {
            foreach (var symbol in extra)
                _used.Add(symbol.Name);
            foreach (var inner in nested)
                CollectAll(inner);
        }
    }

    private void VisitBlock(IReadOnlyList<BoundStmt> statements, IReadOnlyList<VariableSymbol> extra)
    {
        var direct = extra.Concat(DirectDeclarations(statements)).ToList();
        Declare(direct, _enclosing);
        _enclosing.Push([.. direct.Select(s => s.Name)]);
        foreach (var stmt in statements)
        {
            foreach (var (nested, nestedExtra) in NestedBlocks(stmt))
                VisitBlock(nested, nestedExtra);
        }
        _enclosing.Pop();
    }

    private void Declare(IEnumerable<VariableSymbol> symbols, IEnumerable<HashSet<string>> enclosing)
    {
        foreach (var symbol in symbols)
        {
            var clashes = symbol.Name == "_" || enclosing.Any(names => names.Contains(symbol.Name));
            _names[symbol] = clashes ? Fresh(symbol.Name) : symbol.Name;
        }
    }

    private static IEnumerable<VariableSymbol> DirectDeclarations(IEnumerable<BoundStmt> statements) =>
        statements.OfType<BoundVariableDecl>().Select(d => d.Symbol);

    /// <summary>The C# blocks directly inside a statement, with any extra locals they declare.</summary>
    private static IEnumerable<(IReadOnlyList<BoundStmt> Statements, IReadOnlyList<VariableSymbol> Extra)> NestedBlocks(BoundStmt stmt)
    {
        switch (stmt)
        {
            case BoundBlock block:
                yield return (block.Statements, []);
                break;
            case BoundIf ifStmt:
                foreach (var clause in ifStmt.Clauses)
                    yield return (clause.Body.Statements, []);
                if (ifStmt.Else is not null)
                    yield return (ifStmt.Else.Statements, []);
                break;
            case BoundWhile whileStmt:
                yield return (whileStmt.Body.Statements, []);
                break;
            case BoundForRange forStmt:
                yield return (forStmt.Body.Statements, [forStmt.Variable]);
                break;
        }
    }
}
