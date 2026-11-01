using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

/// <summary>
/// Chooses C# names for one method's locals (SPEC §8). Taipan allows a name in a nested block
/// and again later in the enclosing block; C# doesn't, because a C# local's scope is its whole
/// block. Such a nested local gets a numeric suffix. So does a local named <c>_</c>, which the
/// emitter uses as the discard.
/// An <c>is some</c> or <c>match</c> binding becomes a C# pattern variable, whose scope C# widens past the
/// guarded block, so it gets a suffix whenever its name is used by any other local in the method.
/// A nested function's body and a lambda's body are nested blocks whose extra locals are their
/// parameters (SPEC §3.7).
/// </summary>
public sealed class LocalNames
{
    private readonly Dictionary<VariableSymbol, string> _names = [];
    private readonly HashSet<string> _used;
    private readonly Dictionary<string, int> _counts = [];
    private readonly Stack<HashSet<string>> _enclosing = new();

    private LocalNames(IEnumerable<string> reserved) => _used = [.. reserved, "_"];

    public string this[VariableSymbol symbol] => _names[symbol];

    public static LocalNames For(IReadOnlyList<VariableSymbol> parameters, BoundBlock body, IEnumerable<string> globalNames)
    {
        var names = new LocalNames(globalNames);
        foreach (var parameter in parameters)
            names.Count(parameter);
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

    private void Count(VariableSymbol symbol)
    {
        _used.Add(symbol.Name);
        _counts[symbol.Name] = _counts.GetValueOrDefault(symbol.Name) + 1;
    }

    private void CollectAll(BoundStmt stmt)
    {
        foreach (var symbol in DirectDeclarations([stmt]).Concat(Bindings(stmt)))
            Count(symbol);
        foreach (var (nested, extra) in NestedBlocks(stmt))
        {
            foreach (var symbol in extra)
                Count(symbol);
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
            foreach (var binding in Bindings(stmt))
                _names[binding] = _counts[binding.Name] > 1 || binding.Name == "_" ? Fresh(binding.Name) : binding.Name;
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
        statements.SelectMany(s => s switch
        {
            BoundVariableDecl decl => [decl.Symbol],
            BoundLocalFunction function => [function.Symbol],
            // An unpacking's temporary and declarations are emitted in the enclosing block.
            BoundUnpack unpack => [unpack.Temp, .. DirectDeclarations(unpack.Stores)],
            _ => (IEnumerable<VariableSymbol>)[],
        });

    /// <summary>The <c>is some</c> bindings in a statement's own conditions.</summary>
    private static IEnumerable<VariableSymbol> Bindings(BoundStmt stmt) => stmt switch
    {
        BoundIf ifStmt => ifStmt.Clauses.Select(c => c.Condition).OfType<BoundIsSome>().Select(c => c.Binding),
        BoundWhile { Condition: BoundIsSome isSome } => [isSome.Binding],
        BoundMatch match => match.Arms.SelectMany(a => a.Pattern switch
        {
            BoundVariantPattern p => p.Bindings.OfType<VariableSymbol>(),
            BoundClassPattern p => p.Bindings.Select(b => b.Binding),
            _ => [],
        }),
        _ => [],
    };

    /// <summary>The C# blocks directly inside a statement, with any extra locals they declare.</summary>
    private static IEnumerable<(IReadOnlyList<BoundStmt> Statements, IReadOnlyList<VariableSymbol> Extra)> NestedBlocks(BoundStmt stmt)
    {
        // A lambda's body is an expression, seen here as a statement so its own lambdas are found.
        foreach (var lambda in BoundTree.Lambdas(stmt))
            yield return ([new BoundExprStmt(lambda.Body, lambda.Span)], lambda.Parameters);
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
            case BoundForEach forEach:
                yield return (forEach.Body.Statements, [forEach.Variable]);
                break;
            case BoundMatch match:
                foreach (var arm in match.Arms)
                    yield return (arm.Body.Statements, []);
                break;
            case BoundTry tryStmt:
                yield return (tryStmt.Body.Statements, []);
                foreach (var handler in tryStmt.Handlers)
                    yield return (handler.Body.Statements, handler.Binding is { } binding ? [binding] : []);
                if (tryStmt.Else is not null)
                    yield return (tryStmt.Else.Statements, []);
                if (tryStmt.Finally is not null)
                    yield return (tryStmt.Finally.Statements, []);
                break;
            case BoundLocalFunction function:
                yield return (function.Body.Statements, function.Parameters);
                break;
        }
    }
}
