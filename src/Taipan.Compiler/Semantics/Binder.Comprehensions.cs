using System.Collections.Immutable;
using Taipan.Compiler.Syntax;

namespace Taipan.Compiler.Semantics;

// Comprehensions and generator expressions (SPEC §3.16, M36).
public sealed partial class Binder
{
    // Inside a comprehension, where `await` can't be (TP0215).
    private int _comprehensionDepth;

    // Declaring a comprehension's targets, which may reuse an outer name (SPEC §3.16).
    private bool _mayShadow;

    private BoundExpr BindComprehension(ComprehensionExpr comprehension, TaipanType? expected) =>
        // A generator expression runs later, so it doesn't see narrowed vars or paths, as a lambda doesn't (SPEC §3.9).
        comprehension.Kind == ComprehensionKind.Generator
            ? InDeferredRegion(() => BindComprehensionNow(comprehension, expected))
            : BindComprehensionNow(comprehension, expected);

    private BoundExpr BindComprehensionNow(ComprehensionExpr comprehension, TaipanType? expected)
    {
        var (outerScope, outerNarrowing) = (_scope, _narrowing);
        _scope = new Scope(outerScope);
        _comprehensionDepth++;
        var clauses = ImmutableArray.CreateBuilder<BoundComprehensionClause>();
        foreach (var clause in comprehension.Clauses)
        {
            switch (clause)
            {
                case ComprehensionFor loop:
                    clauses.Add(BindComprehensionFor(loop));
                    break;
                case ComprehensionIf test:
                    var condition = BindCondition(new ExprCondition(test.Condition, test.Condition.Span));
                    // It narrows the clauses after it and the element, as an `if` does (SPEC §3.16).
                    _narrowing = _narrowing.With(Facts(condition, whenTrue: true));
                    clauses.Add(new BoundComprehensionIf(condition));
                    break;
            }
        }

        var target = expected is null ? null : TypeChecker.Unwrap(expected);
        TaipanType? ExpectedElement() => (comprehension.Kind, target) switch
        {
            (ComprehensionKind.List, ListOfType list) => list.Element,
            (ComprehensionKind.Set, SetOfType set) => set.Element,
            (ComprehensionKind.Generator, IteratorOfType iterator) => iterator.Element,
            (ComprehensionKind.Generator, IterableOfType iterable) => iterable.Element,
            (ComprehensionKind.Dict, DictOfType dict) => dict.Key,
            _ => null,
        };
        var elementType = ExpectedElement();
        var element = elementType is null ? BindValue(comprehension.Element) : BindExpected(comprehension.Element, elementType);
        BoundExpr? value = null;
        if (comprehension.Value is { } valueSyntax)
            value = target is DictOfType dict ? BindExpected(valueSyntax, dict.Value) : BindValue(valueSyntax);
        _comprehensionDepth--;
        (_scope, _narrowing) = (outerScope, outerNarrowing);

        var type = ComprehensionType(comprehension, elementType ?? element.Type, value is null ? null : target is DictOfType d ? d.Value : value.Type);
        return new BoundComprehension(comprehension.Kind, clauses.ToImmutable(), element, value, type, comprehension.Span);
    }

    private TaipanType ComprehensionType(ComprehensionExpr comprehension, TaipanType element, TaipanType? value)
    {
        if (element.ContainsError || value is { ContainsError: true })
            return TaipanType.Error;
        if (element is NoneType || value is NoneType)
        {
            _diagnostics.ReportCannotInferNone(element is NoneType ? comprehension.Element.Span : comprehension.Value!.Span);
            return TaipanType.Error;
        }
        switch (comprehension.Kind)
        {
            case ComprehensionKind.List:
                return new ListOfType(element);
            case ComprehensionKind.Set:
                CheckSetElement(comprehension.Element.Span, element);
                return new SetOfType(element);
            case ComprehensionKind.Dict:
                CheckDictKey(comprehension.Element.Span, element);
                return new DictOfType(element, value!);
            default:
                return new IteratorOfType(element);
        }
    }

    /// <summary>A <c>for</c> clause: its iterable (a <c>range</c> too, as in a loop), and its targets in the comprehension's scope.</summary>
    private BoundComprehensionFor BindComprehensionFor(ComprehensionFor loop)
    {
        BoundExpr iterable;
        TaipanType element;
        if (loop.Iterable is CallExpr { Callee: NameExpr { Name: "range" } } range && _scope.Lookup("range") is FunctionSymbol { Builtin: BuiltinFunction.Range })
        {
            var (start, stop, step) = BindRangeArguments(range);
            (iterable, element) = (new BoundRangeValues(start, stop, step, range.Span), TaipanType.Int);
        }
        else
        {
            (iterable, element) = BindLoopIterable(loop.Iterable);
        }

        _mayShadow = true;
        try
        {
            if (loop.Variables is [UnpackedName { Starred: false } single])
            {
                var variable = new VariableSymbol(single.Name.Text, element, VariableKind.LoopVariable);
                DeclareLocal(single.Name, variable);
                return new BoundComprehensionFor(variable, iterable, []);
            }
            var temporary = UnpackTemp(element);
            var unpack = Unpack(new BoundVariable(temporary, loop.Iterable.Span), loop.Iterable.Span, loop.Variables,
                (item, type) => DeclareUnpacked(item, type, VariableKind.LoopVariable), loop.Span);
            return new BoundComprehensionFor(temporary, iterable, [unpack]);
        }
        finally
        {
            _mayShadow = false;
        }
    }
}
