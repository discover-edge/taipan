using System.Collections.Immutable;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

// Unpacking (SPEC §3.8) and match (§5.1, §5.1.1): nested, list and starred unpacking, and nested
// patterns, checked for exhaustiveness through all their nesting (M29).
public sealed partial class Binder
{
    // ---- Unpacking (SPEC §3.8) ----

    // The value is held in a temporary named `_`, which the emitter always renames.
    private static VariableSymbol UnpackTemp(TaipanType type) => new("_", type, VariableKind.Let);

    private BoundUnpack BindUnpack(UnpackStmt unpack)
    {
        var value = BindValue(unpack.Value);
        var kind = unpack.IsVar ? VariableKind.Var : VariableKind.Let;
        return Unpack(value, unpack.Value.Span, unpack.Names, (item, element) => DeclareUnpacked(item, element, kind), unpack.Span);
    }

    /// <summary>
    /// <c>a, b = e</c>: the value first, then each target in turn, as for <c>=</c>. A tuple
    /// literal on the right is bound without expected types; its elements are checked against
    /// the targets as they're stored.
    /// </summary>
    private BoundUnpack BindTupleAssign(TupleAssignStmt assign)
    {
        var value = BindValue(assign.Value);
        return Unpack(value, assign.Value.Span, assign.Targets, (item, element) =>
        {
            var target = ((UnpackedTarget)item).Target;
            return [BindAssign(new AssignStmt(target, AssignOp.Assign, assign.Value, assign.Span), element)];
        }, assign.Span);
    }

    /// <summary>A name that unpacking declares, <c>_</c> declaring nothing.</summary>
    private IEnumerable<BoundStmt> DeclareUnpacked(Unpacked item, BoundExpr element, VariableKind kind)
    {
        var name = ((UnpackedName)item).Name;
        if (name.Text == "_")
            return [];
        var symbol = new VariableSymbol(name.Text, element.Type, kind);
        DeclareLocal(name, symbol);
        return [new BoundVariableDecl(symbol, element, item.Span)];
    }

    /// <summary>
    /// Unpacks a tuple or list into <paramref name="items"/> (SPEC §3.8): each gets its element, a
    /// starred one a new list of the rest, and a group is unpacked in turn. A list's length is
    /// checked at run time. Anything else is reported, and the items get the error type.
    /// </summary>
    private BoundUnpack Unpack(BoundExpr value, TextSpan span, ImmutableArray<Unpacked> items,
        Func<Unpacked, BoundExpr, IEnumerable<BoundStmt>> store, TextSpan statementSpan)
    {
        // Any other iterable is taken into a list first, and checked as one (SPEC §3.13).
        if (value.Type is not (TupleOfType or ListOfType) && !value.Type.ContainsError && value.Type is not OptionalOf
            && TypeChecker.ElementOf(value.Type) is { } iterated)
            value = new BoundToList(Convert(value, new IterableOfType(iterated)), new ListOfType(iterated), value.Span);
        var temp = UnpackTemp(value.Type);
        var source = new BoundVariable(temp, span);
        var star = items.IndexOf(items.FirstOrDefault(IsStarred)!);
        var after = star < 0 ? 0 : items.Length - star - 1;
        var fixedCount = star < 0 ? items.Length : items.Length - 1;
        var elements = new BoundExpr[items.Length];
        (int Count, bool Starred)? length = null;

        switch (value.Type)
        {
            case { ContainsError: true }:
                Array.Fill(elements, new BoundErrorExpr(span));
                break;
            case var optional when ReportIfOptional(value, span):
                Array.Fill(elements, new BoundErrorExpr(span));
                break;
            case TupleOfType tuple when star < 0 ? tuple.Elements.Length != items.Length : tuple.Elements.Length < fixedCount:
                _diagnostics.ReportUnpackCount(span, tuple, tuple.Elements.Length, items.Length);
                Array.Fill(elements, new BoundErrorExpr(span));
                break;
            case TupleOfType tuple:
                for (var i = 0; i < items.Length; i++)
                {
                    if (i == star)
                        elements[i] = TupleRest(source, tuple, star, tuple.Elements.Length - after, (UnpackedName?)(items[i] as UnpackedName), items[i].Span);
                    else
                    {
                        var index = star >= 0 && i > star ? tuple.Elements.Length - (items.Length - i) : i;
                        elements[i] = new BoundTupleElement(source, index, tuple.Elements[index], span);
                    }
                }
                break;
            case ListOfType list:
                length = (fixedCount, star >= 0);
                for (var i = 0; i < items.Length; i++)
                {
                    elements[i] = i == star
                        ? new BoundListRest(source, star, after, list, span)
                        : new BoundListElement(source, star >= 0 && i > star ? i - items.Length : i, list.Element, span);
                }
                break;
            default:
                _diagnostics.ReportCannotUnpack(span, value.Type);
                Array.Fill(elements, new BoundErrorExpr(span));
                break;
        }

        var stores = ImmutableArray.CreateBuilder<BoundStmt>();
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] is UnpackedGroup group)
                stores.Add(Unpack(elements[i], group.Span, group.Items, store, statementSpan));
            else
                stores.AddRange(store(items[i], elements[i]));
        }
        return new BoundUnpack(temp, value, stores.ToImmutable(), statementSpan) { Length = length };
    }

    private static bool IsStarred(Unpacked item) => item is UnpackedName { Starred: true } or UnpackedTarget { Starred: true };

    /// <summary>A tuple's elements from <paramref name="start"/> to <paramref name="end"/>, as a list of their joined type.</summary>
    private BoundExpr TupleRest(BoundExpr tuple, TupleOfType type, int start, int end, UnpackedName? name, TextSpan span)
    {
        var parts = type.Elements[start..end];
        TaipanType? element = parts.IsEmpty ? null : parts[0];
        foreach (var part in parts.Skip(1))
        {
            if ((part == element ? part : part.IsNumeric && element!.IsNumeric ? TaipanType.Float : Join(element!, part)) is { } joined)
            {
                element = joined;
                continue;
            }
            _diagnostics.ReportStarredJoin(span, name?.Name.Text ?? "_", element!, part);
            return new BoundErrorExpr(span);
        }
        if (element is null)
        {
            _diagnostics.ReportCannotInferEmpty(span, "list");
            return new BoundErrorExpr(span);
        }
        return new BoundTupleRest(tuple, start, end, new ListOfType(element), span);
    }

    // ---- match (SPEC §5.1) ----

    private BoundMatch BindMatch(MatchStmt match)
    {
        var (scrutinee, type) = BindScrutinee(match.Scrutinee);
        // After a match statement, what holds at the end of every arm that can finish (SPEC §3.9).
        var ends = new List<Narrowing>();
        var arms = BindArms(scrutinee, type, match.Span, [.. match.Arms.Select(a => a.Pattern)], (i, pattern, reachable) =>
        {
            var arm = new BoundArm(pattern, BindBlock(match.Arms[i].Body), reachable);
            if (reachable && !Exits(arm.Body))
                ends.Add(_narrowing);
            return arm;
        });
        if (ends.Count > 0)
            _narrowing = Join(ends);
        return new BoundMatch(scrutinee, arms, match.Span);
    }

    // While a match expression's block arm is bound: the function and loop depth it can't leave (SPEC §5.1.1).
    private (FunctionSymbol? Function, int LoopDepth)? _matchExpressionArm;

    /// <summary>
    /// A match expression (SPEC §5.1.1): each arm is checked against the expected type, or the
    /// arms' types join as list elements do. Raising and unreachable arms give no type. A block
    /// arm's last expression is its value.
    /// </summary>
    private BoundExpr BindMatchExpr(MatchExpr match, TaipanType? expected)
    {
        var (scrutinee, type) = BindScrutinee(match.Scrutinee);
        var arms = BindArms(scrutinee, type, match.Span, [.. match.Arms.Select(a => a.Pattern)], (i, pattern, reachable) =>
        {
            var arm = match.Arms[i];
            if (arm.Block is { } block)
                return BindBlockArm(pattern, block, reachable, expected);
            if (arm.Raise is { } raise)
                return new BoundValueArm(pattern, null, BindRaise(raise), reachable);
            var value = expected is null ? BindValue(arm.Value!) : BindExpected(arm.Value!, expected);
            return new BoundValueArm(pattern, value, null, reachable);
        });

        var values = arms.Where(a => a.IsReachable && a.Value is not null).Select(a => a.Value!).ToImmutableArray();
        if (values.IsEmpty)
        {
            _diagnostics.ReportEveryArmRaises(MatchKeyword(match.Span));
            return new BoundMatchExpr(scrutinee, arms, TaipanType.Error, match.Span);
        }
        return new BoundMatchExpr(scrutinee, arms, expected ?? InferElementType(values), match.Span);
    }

    /// <summary>
    /// A block arm (SPEC §5.1.1): its statements, then its last one's value; or none, if the last
    /// one terminates. It can't return, break or continue out of the match expression (TP0215).
    /// </summary>
    private BoundValueArm BindBlockArm(BoundPattern pattern, Block block, bool reachable, TaipanType? expected)
    {
        var outerArm = _matchExpressionArm;
        _matchExpressionArm = (_function, _loopDepth);
        var outerScope = _scope;
        _scope = new Scope(outerScope);
        BoundExpr? value = null;
        ImmutableArray<BoundStmt> statements;
        if (block.Statements is [.. var leading, ExprStmt last])
        {
            statements = BindStatements([.. leading]);
            value = expected is null ? BindValue(last.Expr) : BindExpected(last.Expr, expected);
        }
        else
        {
            statements = BindStatements(block.Statements);
            // A break or continue there was reported as leaving the match expression.
            if (!TypeChecker.Terminates(new BoundBlock(statements, block.Span)) && statements.LastOrDefault() is not (BoundBreak or BoundContinue))
                _diagnostics.ReportArmGivesNoValue(block.Statements.LastOrDefault()?.Span ?? block.Span);
        }
        _scope = outerScope;
        _matchExpressionArm = outerArm;
        return new BoundValueArm(pattern, value, null, reachable) { Body = new BoundBlock(statements, block.Span) };
    }

    /// <summary>Whether a <c>return</c> here, or a <c>break</c> or <c>continue</c> of this loop depth, would leave a match expression.</summary>
    private bool LeavesMatchExpression(bool isReturn) =>
        _matchExpressionArm is var (function, loopDepth) && function == _function && (isReturn || loopDepth == _loopDepth);

    private static TextSpan MatchKeyword(TextSpan match) => new(match.Start, "match".Length);

    /// <summary>A match's scrutinee, and the type its patterns are checked against (an error type if it can't be matched).</summary>
    private (BoundExpr Scrutinee, TaipanType Type) BindScrutinee(Expr syntax)
    {
        var scrutinee = BindValue(syntax);
        var type = scrutinee.Type;
        if (ReportIfOptional(scrutinee, syntax.Span))
            type = TaipanType.Error;
        else if (!type.ContainsError && type is not (EnumType or ClassType or TupleOfType or ListOfType)
            && type != TaipanType.Int && type != TaipanType.Str && type != TaipanType.Bool)
        {
            _diagnostics.ReportCannotMatch(syntax.Span, type);
            type = TaipanType.Error;
        }
        return (scrutinee, type);
    }

    /// <summary>
    /// Binds each arm's pattern and then, with its bindings in scope and a class pattern's narrowing
    /// applied, its body; then checks reachability and exhaustiveness (SPEC §5.1).
    /// </summary>
    private ImmutableArray<TArm> BindArms<TArm>(
        BoundExpr scrutinee, TaipanType type, TextSpan matchSpan, ImmutableArray<Pattern> patterns, Func<int, BoundPattern, bool, TArm> bindBody)
    {
        var coverage = new Coverage(type);
        var arms = ImmutableArray.CreateBuilder<TArm>();
        var allValid = true;
        for (var i = 0; i < patterns.Length; i++)
        {
            var bindings = new PatternBindings();
            var pattern = BindPattern(patterns[i], type, bindings);
            allValid &= bindings.Valid;
            var reachable = !bindings.Valid || coverage.Add(pattern);
            if (!reachable)
                _diagnostics.ReportUnreachableArm(patterns[i].Span);

            var outer = _scope;
            _scope = new Scope(outer);
            foreach (var (identifier, symbol) in bindings.Names)
                DeclareLocal(identifier, symbol);
            // A class pattern narrows the scrutinee in its arm (SPEC §5.1).
            var (outerNarrowing, mark) = (_narrowing, _endings.Count);
            if (pattern is BoundClassPattern classPattern)
                _narrowing = _narrowing.With(Narrow(scrutinee, classPattern.Class.Symbol.DerivesFrom(((ClassType)type).Symbol) ? classPattern.Class : type));
            arms.Add(bindBody(i, pattern, reachable));
            // Each arm starts from the state before the match; a statement collects the arms' ends.
            _narrowing = Replay(outerNarrowing, mark);
            _scope = outer;
        }

        // An invalid pattern was already reported; don't also call its case missing.
        if (allValid && !type.ContainsError && coverage.Missing() is { Count: > 0 } missing)
            _diagnostics.ReportNonExhaustiveMatch(MatchKeyword(matchSpan), missing);
        return arms.ToImmutable();
    }

    /// <summary>The names one arm's pattern binds, and whether it's valid.</summary>
    private sealed class PatternBindings
    {
        public List<(Identifier, VariableSymbol)> Names { get; } = [];

        public bool Valid { get; set; } = true;
    }

    /// <summary>
    /// Binds a pattern against the type of its position (SPEC §5.1), adding its bindings. An invalid
    /// pattern is reported, marks the bindings invalid, and is bound as a wildcard.
    /// </summary>
    private BoundPattern BindPattern(Pattern pattern, TaipanType type, PatternBindings bindings)
    {
        BoundPattern Invalid()
        {
            bindings.Valid = false;
            return new BoundWildcardPattern();
        }

        // An optional position takes only '_' and bindings: None patterns are deferred.
        if (type is OptionalOf && pattern is not (WildcardPattern or VariantPattern { Module: null, Enum: null, Bindings: null }))
        {
            _diagnostics.ReportOptionalPattern(pattern.Span, type);
            return Invalid();
        }
        switch (pattern)
        {
            case WildcardPattern:
                return new BoundWildcardPattern();
            case IntPattern intPattern:
                if (intPattern.Value < long.MinValue || intPattern.Value > long.MaxValue)
                {
                    _diagnostics.ReportIntegerOutOfRange(intPattern.Span);
                    return Invalid();
                }
                return LiteralPattern(pattern, TaipanType.Int, type, (long)intPattern.Value, bindings);
            case StringPattern stringPattern:
                return LiteralPattern(pattern, TaipanType.Str, type, stringPattern.Value, bindings);
            case BoolPattern boolPattern:
                return LiteralPattern(pattern, TaipanType.Bool, type, boolPattern.Value, bindings);
            case VariantPattern { Module: null, Enum: null, Bindings: null } bare when IsBinding(bare.Variant.Text, type):
                return BindName(bare.Variant, type, bindings);
            case VariantPattern classPattern when type is ClassType classType:
                return BindClassPattern(classPattern, classType, bindings);
            case VariantPattern variantPattern:
                return BindVariantPattern(variantPattern, type, bindings);
            case TuplePattern tuplePattern:
                return BindTuplePattern(tuplePattern, type, bindings);
            case ListPattern listPattern:
                return BindListPattern(listPattern, type, bindings);
            default:
                return Invalid();
        }
    }

    /// <summary>
    /// Whether a bare name binds (SPEC §5.1): not when it's a variant of its position's enum. Nor
    /// when it names a variant or class in scope, which a binding couldn't shadow (TP0207): it's
    /// then checked as that pattern, and reported as one of the wrong type.
    /// </summary>
    private bool IsBinding(string name, TaipanType type) =>
        !(type is EnumType e && e.Symbol.FindVariant(name) is not null)
        && _scope.Lookup(name) is not (VariantSymbol or AmbiguousVariantSymbol or ClassSymbol);

    /// <summary>
    /// A bare name that isn't a variant of its position's enum binds the value (SPEC §5.1). In an
    /// enum position, a capitalized one is taken for a misspelled variant (TP0210).
    /// </summary>
    private BoundPattern BindName(Identifier name, TaipanType type, PatternBindings bindings)
    {
        if (type is EnumType && char.IsUpper(name.Text[0]))
        {
            _diagnostics.ReportNoVariant(name.Span, type, name.Text);
            bindings.Valid = false;
            return new BoundWildcardPattern();
        }
        var symbol = new VariableSymbol(name.Text, type, VariableKind.Let);
        if (bindings.Names.Any(b => b.Item1.Text == name.Text))
            _diagnostics.ReportDuplicateDefinition(name.Span, name.Text);
        else
            bindings.Names.Add((name, symbol));
        return new BoundBindingPattern(symbol);
    }

    private BoundPattern LiteralPattern(Pattern pattern, TaipanType patternType, TaipanType type, object value, PatternBindings bindings)
    {
        // A position whose type is an error says nothing about coverage, so the match isn't checked.
        if (type.ContainsError)
        {
            bindings.Valid = false;
            return new BoundWildcardPattern();
        }
        if (type != patternType)
        {
            _diagnostics.ReportTypeMismatch(pattern.Span, type, patternType);
            bindings.Valid = false;
            return new BoundWildcardPattern();
        }
        return new BoundLiteralPattern(value);
    }

    /// <summary>A variant pattern: each argument a sub-pattern for a payload field, by position (SPEC §5.1).</summary>
    private BoundPattern BindVariantPattern(VariantPattern pattern, TaipanType type, PatternBindings bindings)
    {
        var variant = ResolveVariantPattern(pattern, type);
        if (variant is null)
        {
            bindings.Valid = false;
            BindSubPatterns(pattern, bindings);
            return new BoundWildcardPattern();
        }
        if (pattern.Bindings is not { } arguments || arguments.IsEmpty && variant.Fields.IsEmpty)
            return new BoundVariantPattern(variant, type, []);
        if (arguments.Length != variant.Fields.Length)
        {
            _diagnostics.ReportBindingCount(pattern.Span, variant.Name, variant.Fields.Length, arguments.Length);
            bindings.Valid = false;
            BindSubPatterns(pattern, bindings);
            return new BoundWildcardPattern();
        }
        var fields = arguments.Zip(variant.Fields, (argument, field) => BindPattern(argument.Pattern, FieldType(type, field), bindings));
        return new BoundVariantPattern(variant, type, [.. fields]);
    }

    // The bindings of a pattern that can't be checked, with error types, so its arm still binds.
    private void BindSubPatterns(VariantPattern pattern, PatternBindings bindings)
    {
        foreach (var argument in pattern.Bindings ?? [])
            BindPattern(argument.Pattern, TaipanType.Error, bindings);
    }

    private BoundPattern BindTuplePattern(TuplePattern pattern, TaipanType type, PatternBindings bindings)
    {
        if (type is TupleOfType tuple)
        {
            if (tuple.Elements.Length == pattern.Elements.Length)
                return new BoundTuplePattern(tuple, [.. pattern.Elements.Zip(tuple.Elements, (p, t) => BindPattern(p, t, bindings))]);
            _diagnostics.ReportTuplePatternCount(pattern.Span, tuple, tuple.Elements.Length, pattern.Elements.Length);
        }
        else if (!type.ContainsError)
        {
            _diagnostics.ReportCannotMatchPattern(pattern.Span, type, "tuple");
        }
        bindings.Valid = false;
        foreach (var element in pattern.Elements)
            BindPattern(element, TaipanType.Error, bindings);
        return new BoundWildcardPattern();
    }

    private BoundPattern BindListPattern(ListPattern pattern, TaipanType type, PatternBindings bindings)
    {
        var list = type as ListOfType;
        if (list is null)
        {
            if (!type.ContainsError)
                _diagnostics.ReportCannotMatchPattern(pattern.Span, type, "list");
            bindings.Valid = false;
        }
        var element = list?.Element ?? TaipanType.Error;
        var before = ImmutableArray.CreateBuilder<BoundPattern>();
        var after = ImmutableArray.CreateBuilder<BoundPattern>();
        StarPattern? star = null;
        VariableSymbol? rest = null;
        foreach (var part in pattern.Elements)
        {
            if (part is StarPattern starPattern)
            {
                star ??= starPattern;
                if (starPattern.Name.Text != "_" && BindName(starPattern.Name, list ?? (TaipanType)TaipanType.Error, bindings) is BoundBindingPattern binding)
                    rest = binding.Symbol;
                continue;
            }
            (star is null ? before : after).Add(BindPattern(part, element, bindings));
        }
        if (list is null)
            return new BoundWildcardPattern();
        return new BoundListPattern(list, before.ToImmutable(), star is not null, rest, after.ToImmutable());
    }

    /// <summary>
    /// <c>C(…)</c> on a class (SPEC §5.1): it matches instances of <c>C</c>, which must derive from the
    /// position's class. Each argument is a sub-pattern for a field, by name, or for a dataclass also
    /// by position.
    /// </summary>
    private BoundPattern BindClassPattern(VariantPattern pattern, ClassType scrutinee, PatternBindings bindings)
    {
        BoundPattern Invalid()
        {
            bindings.Valid = false;
            BindSubPatterns(pattern, bindings);
            return new BoundWildcardPattern();
        }

        if (pattern.Module is not null)
        {
            _diagnostics.ReportNotAClass(pattern.Span, _source.ToString(pattern.Span));
            return Invalid();
        }
        Symbol? found;
        if (pattern.Enum is { } moduleName)
        {
            if (_scope.Lookup(moduleName.Text) is not ModuleSymbol module)
            {
                _diagnostics.ReportUndefinedName(moduleName.Span, moduleName.Text);
                return Invalid();
            }
            if (!module.Definitions.TryGetValue(pattern.Variant.Text, out found))
            {
                _diagnostics.ReportNoDefinition(pattern.Variant.Span, module.Name, pattern.Variant.Text);
                return Invalid();
            }
        }
        else if ((found = LookupName(pattern.Variant.Text, pattern.Variant.Span)) is null)
        {
            return Invalid();
        }
        if (found is TypeAliasSymbol alias)
        {
            _diagnostics.ReportAliasAsValue(pattern.Variant.Span, pattern.Variant.Text, alias.TargetName);
            return Invalid();
        }
        var classSymbol = found as ClassSymbol;
        var name = _source.ToString(new TextSpan(pattern.Span.Start, pattern.Variant.Span.End - pattern.Span.Start));
        if (classSymbol is null)
        {
            _diagnostics.ReportNotAClass(pattern.Span, name);
            return Invalid();
        }
        if (classSymbol.IsProtocol)
        {
            _diagnostics.ReportProtocolAsType(pattern.Variant.Span, classSymbol.Name);
            return Invalid();
        }
        if (pattern.Bindings is not { } arguments)
        {
            _diagnostics.ReportBareClassPattern(pattern.Span, name);
            return Invalid();
        }
        if (!classSymbol.DerivesFrom(scrutinee.Symbol))
        {
            _diagnostics.ReportNeverInstance(pattern.Span, scrutinee, classSymbol.Name);
            return Invalid();
        }

        // Generic classes don't derive, so the pattern's class is the scrutinee's type or isn't generic.
        var classType = classSymbol == scrutinee.Symbol ? scrutinee : new ClassType(classSymbol);
        var positional = arguments.TakeWhile(n => n.Field is null).Count();
        var fields = classSymbol.AllFields;
        if (positional > 0 && !classSymbol.IsDataclass)
        {
            _diagnostics.ReportPositionalOnPlainClass(pattern.Span, classSymbol.Name);
            return Invalid();
        }
        if (positional > fields.Length)
        {
            _diagnostics.ReportBindingCount(pattern.Span, classSymbol.Name, fields.Length, positional);
            return Invalid();
        }
        var bound = ImmutableArray.CreateBuilder<(FieldSymbol, BoundPattern)>();
        var given = new HashSet<FieldSymbol>();
        var valid = true;
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            FieldSymbol? field;
            if (argument.Field is null)
            {
                field = fields[i];
            }
            else
            {
                field = classSymbol.FindField(argument.Field.Text);
                if (field is not null)
                    Index(argument.Field.Span, field);
                if (field is null)
                {
                    _diagnostics.ReportNoField(argument.Field.Span, classType, argument.Field.Text);
                    valid = false;
                    BindPattern(argument.Pattern, TaipanType.Error, bindings);
                    continue;
                }
            }
            if (!given.Add(field))
            {
                _diagnostics.ReportRepeatedField((argument.Field ?? (Node)argument.Pattern).Span, field.Name);
                valid = false;
                BindPattern(argument.Pattern, TaipanType.Error, bindings);
                continue;
            }
            bound.Add((field, BindPattern(argument.Pattern, FieldType(classType, field), bindings)));
        }
        if (!valid)
        {
            bindings.Valid = false;
            return new BoundWildcardPattern();
        }
        return new BoundClassPattern(classType, bound.ToImmutable());
    }

    /// <summary>Variant patterns resolve against their position's enum, so they're never ambiguous (SPEC §5.1).</summary>
    private VariantSymbol? ResolveVariantPattern(VariantPattern pattern, TaipanType type)
    {
        if (type.ContainsError)
            return null;
        if (type is not EnumType { Symbol: var enumSymbol })
        {
            var found = pattern.Enum?.Text ?? (_global.Lookup(pattern.Variant.Text) switch
            {
                VariantSymbol v => v.Enum.Name,
                AmbiguousVariantSymbol a => a.Variants[0].Enum.Name,
                _ => $"variant '{pattern.Variant.Text}'",
            });
            _diagnostics.ReportTypeMismatch(pattern.Span, type, found);
            return null;
        }
        if (pattern.Enum is { } qualifier)
        {
            EnumSymbol? named;
            if (pattern.Module is { } moduleName)
            {
                if (_scope.Lookup(moduleName.Text) is not ModuleSymbol module)
                {
                    _diagnostics.ReportUndefinedName(moduleName.Span, moduleName.Text);
                    return null;
                }
                named = module.Enums.GetValueOrDefault(qualifier.Text);
                if (module.Definitions.GetValueOrDefault(qualifier.Text) is TypeAliasSymbol alias)
                {
                    _diagnostics.ReportAliasAsValue(qualifier.Span, qualifier.Text, alias.TargetName);
                    return null;
                }
                if (named is null)
                {
                    _diagnostics.ReportNoDefinition(qualifier.Span, module.Name, qualifier.Text);
                    return null;
                }
            }
            else
            {
                named = ResolveTypeName(qualifier.Text) as EnumSymbol;
                if (_global.Lookup(qualifier.Text) is TypeAliasSymbol alias)
                {
                    _diagnostics.ReportAliasAsValue(qualifier.Span, qualifier.Text, alias.TargetName);
                    return null;
                }
                if (named is null)
                {
                    _diagnostics.ReportUndefinedName(qualifier.Span, qualifier.Text);
                    return null;
                }
            }
            if (named != enumSymbol)
            {
                _diagnostics.ReportTypeMismatch(pattern.Span, type, named.Name);
                return null;
            }
        }
        var variant = enumSymbol.FindVariant(pattern.Variant.Text);
        if (variant is null)
            _diagnostics.ReportNoVariant(pattern.Variant.Span, type, pattern.Variant.Text);
        return variant;
    }
}
