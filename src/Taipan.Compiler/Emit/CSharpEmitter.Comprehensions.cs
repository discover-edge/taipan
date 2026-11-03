using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Emit;

// Comprehensions and generator expressions (SPEC §3.16, M36), as LINQ over lambdas: each `for` a
// SelectMany, each `if` a Where, and the element a Select. LINQ evaluates the first iterable at
// once and the rest as values are asked for, which is a generator expression's laziness, and each
// lambda's parameter is a new variable per iteration, as Taipan's loop variables are (SPEC §3.7).
public sealed partial class CSharpEmitter
{
    private const string Linq = $"{Runtime}.Comprehensions";

    private string Comprehension(BoundComprehension comprehension)
    {
        var values = Sequence(comprehension, 0);
        var type = Concrete(comprehension.Type);
        return comprehension.Kind switch
        {
            ComprehensionKind.List => $"{Runtime}.Iterators.ToList({values})",
            ComprehensionKind.Set => $"new {TypeName(type)}({values})",
            ComprehensionKind.Dict => $"{Runtime}.Ops.ToDict({values})",
            _ => $"new {Runtime}.Iterator<{TypeName(((IteratorOfType)type).Element)}>(({values}).GetEnumerator(), \"generator object <genexpr>\")",
        };
    }

    /// <summary>The values from the <c>for</c> clause at <paramref name="at"/> on, with the <c>if</c>s after it as filters.</summary>
    private string Sequence(BoundComprehension comprehension, int at)
    {
        var loop = (BoundComprehensionFor)comprehension.Clauses[at];
        var parameter = $"({TypeName(Concrete(loop.Variable.Type))} @{_locals[loop.Variable]})";
        var unpack = UnpackText(loop.Unpack);
        var values = Source(loop.Iterable);
        var next = at + 1;
        for (; next < comprehension.Clauses.Length && comprehension.Clauses[next] is BoundComprehensionIf test; next++)
            values = $"{Linq}.Where({values}, {parameter} => {Body(comprehension, test.Condition.Span, $"{unpack}return {Expr(test.Condition)};")})";
        if (next < comprehension.Clauses.Length)
            return $"{Linq}.SelectMany({values}, {parameter} => {Body(comprehension, ClauseSpan(comprehension.Clauses[next]), $"{unpack}return {Sequence(comprehension, next)};")})";
        var (elementType, valueType) = Concrete(comprehension.Type) switch
        {
            ListOfType list => (list.Element, null),
            SetOfType set => (set.Element, null),
            IteratorOfType iterator => (iterator.Element, null),
            DictOfType dict => (dict.Key, (TaipanType?)dict.Value),
            var other => (other, null),
        };
        var element = comprehension.Value is { } value
            ? $"({As(comprehension.Element, elementType)}, {As(value, valueType!)})"
            : As(comprehension.Element, elementType);
        return $"{Linq}.Select({values}, {parameter} => {Body(comprehension, comprehension.Element.Span, $"{unpack}return {element};")})";
    }

    private static Text.TextSpan ClauseSpan(BoundComprehensionClause clause) => clause switch
    {
        BoundComprehensionFor loop => loop.Iterable.Span,
        BoundComprehensionIf test => test.Condition.Span,
        _ => throw new ArgumentOutOfRangeException(nameof(clause)),
    };

    // A clause's lambda body. A generator expression's are its own frame, <genexpr>; the others run
    // in the frame they're in, as since Python 3.12 (SPEC §3.19).
    private string Body(BoundComprehension comprehension, Text.TextSpan span, string statements) =>
        comprehension.Kind == ComprehensionKind.Generator ? $"{{ {TrackedStatement(span, statements, "<genexpr>")} }}" : $"{{ {statements} }}";

    // A value as the comprehension's element type, which an expected type may widen it to (SPEC §3.16).
    private string As(BoundExpr value, TaipanType type) =>
        Concrete(value.Type) == type || type.ContainsError ? Expr(value) : $"({TypeName(type)})({Expr(value)})";

    // A for clause's iterable as a .NET sequence, guarded against changes as a loop's is (SPEC §5.2).
    private string Source(BoundExpr iterable) => Concrete(iterable.Type) switch
    {
        _ when iterable is BoundRangeValues => Expr(iterable),
        PrimitiveType { Name: "py" } => $"{Interop}.Iterate({Expr(iterable)})",
        IteratorOfType or IterableOfType or OpaqueType => Expr(iterable),
        _ => $"{Runtime}.Iter.Over({Expr(iterable)})",
    };

    private string RangeValues(BoundRangeValues range) =>
        $"{Runtime}.Ops.Range({(range.Start is null ? "0L" : Expr(range.Start))}, {Expr(range.Stop)}{(range.Step is null ? "" : $", {Expr(range.Step)}")})";

    // A for clause's unpacking, as statements on one line inside a lambda's body.
    private string UnpackText(System.Collections.Immutable.ImmutableArray<BoundStmt> unpack)
    {
        if (unpack.IsEmpty)
            return "";
        var (outer, outerIndent, outerLoops) = (_out, _indent, _loops);
        _out = new System.Text.StringBuilder();
        _loops = new Stack<LoopContext>();
        _indent = 0;
        EmitStatements(unpack);
        var text = string.Join(" ", _out.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        (_out, _indent, _loops) = (outer, outerIndent, outerLoops);
        return text + " ";
    }
}
