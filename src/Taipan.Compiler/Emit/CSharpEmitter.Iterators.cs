using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// Iterators and generators (SPEC §3.13, §8, M31).
public sealed partial class CSharpEmitter
{
    // Whether the statements being emitted are a generator's body, where a return ends it.
    private bool _inGenerator;

    /// <summary>
    /// A generator's body: a C# iterator local function, which the generator calls and wraps, so
    /// none of its body runs until a value is asked for (SPEC §8).
    /// </summary>
    private void EmitGeneratorBody(FunctionSymbol generator, IReadOnlyList<BoundStmt> statements)
    {
        var element = TypeName(TypeChecker.ElementOf(Concrete(generator.ReturnType)) ?? TaipanType.Error);
        var body = $"@{_locals.Fresh("body")}";
        var outer = _inGenerator;
        _inGenerator = true;
        Line($"{Collections}.IEnumerable<{element}> {body}()");
        Line("{");
        _indent++;
        EmitStatements(statements);
        // An empty iterator method still needs a yield to be one.
        Line("yield break;");
        _indent--;
        Line("}");
        _inGenerator = outer;
        // The body is its own frame, at the line it last set (SPEC §3.19).
        var frame = _lineVariable is null ? $"\"{Escape(_lineSource.Path)}\", () => 0" : $"@{_fileVariable}, () => @{_lineVariable}";
        Line($"return new {Runtime}.Generator<{element}>({body}(), \"{generator.Name}\", {frame});");
    }

    /// <summary>
    /// A value as an iterable or iterator (SPEC §8): a list, dict or str through the runtime's
    /// guarded loop, a class through its <c>__iter__</c> or <c>__next__</c>.
    /// </summary>
    private string ToIterable(BoundToIterable converted)
    {
        var value = Expr(converted.Value);
        var type = Concrete(converted.Value.Type);
        var target = Concrete(converted.Type);
        if (type is ClassType classType)
        {
            var element = TypeName(target is IteratorOfType iterator ? iterator.Element : ((IterableOfType)target).Element);
            var self = $"@{_locals.Fresh("self")}";
            return target is IteratorOfType || classType.Symbol.FindMethod("__iter__") is null
                ? $"{Runtime}.Iterators.FromNext({value}, static ({TypeName(type)} {self}) => {self}.@__next__())"
                : $"{Runtime}.Iterators.FromIter<{TypeName(type)}, {element}>({value}, static {self} => {self}.@__iter__())";
        }
        return $"{Runtime}.Iter.Over({value})";
    }

    /// <summary>What <c>iter(x)</c>'s iterator displays as: Python's names for a list's, or a general one.</summary>
    private string IteratorName(BoundExpr argument) =>
        (argument is BoundToIterable converted ? Concrete(converted.Value.Type) : Concrete(argument.Type)) switch
        {
            ListOfType => "list_iterator object",
            _ => "iterator object",
        };
}
