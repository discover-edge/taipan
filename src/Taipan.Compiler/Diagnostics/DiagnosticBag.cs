using System.Collections.Immutable;
using System.Text;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Diagnostics;

/// <summary>
/// Collects diagnostics for one compilation stage. Each code in SPEC §9 has one Report method,
/// which owns its exact message text.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>The file new diagnostics belong to, when a program has several (SPEC §4.7).</summary>
    public SourceText? Source { get; set; }

    /// <summary>The order to report files in: the main file first, then its modules (SPEC §4.7).</summary>
    public IReadOnlyList<SourceText> FileOrder { get; set; } = [];

    /// <summary>Diagnostics grouped by file in <see cref="FileOrder"/>, then in source order (stable for equal positions).</summary>
    public ImmutableArray<Diagnostic> ToImmutable() =>
        [.. _items.OrderBy(d => d.Source is { } source ? IndexOf(source) : -1).ThenBy(d => d.Span.Start)];

    private int IndexOf(SourceText source)
    {
        for (var i = 0; i < FileOrder.Count; i++)
        {
            if (FileOrder[i] == source)
                return i;
        }
        return FileOrder.Count;
    }

    public void ReportTabInIndentation(TextSpan span) =>
        Add("TP0001", span, "tab in indentation");

    public void ReportInconsistentDedent(TextSpan span) =>
        Add("TP0002", span, "dedent does not match any outer indentation level");

    public void ReportUnterminatedString(TextSpan span) =>
        Add("TP0003", span, "unterminated string");

    public void ReportInvalidCharacter(TextSpan span, Rune character) =>
        Add("TP0004", span, $"invalid character {Describe(character)}");

    public void ReportInvalidEscape(TextSpan span, Rune character) =>
        Add("TP0005", span, $"invalid escape sequence '\\{character}'");

    public void ReportSingleCloseBraceInFString(TextSpan span) =>
        Add("TP0006", span, "single '}' is not allowed in an f-string");

    public void ReportEmptyFStringHole(TextSpan span) =>
        Add("TP0006", span, "empty f-string hole");

    public void ReportNestedFString(TextSpan span) =>
        Add("TP0006", span, "f-strings cannot be nested");

    public void ReportInvalidNumber(TextSpan span) =>
        Add("TP0007", span, "invalid number literal");

    public void ReportExpected(TextSpan span, string expected, string found) =>
        Add("TP0100", span, $"expected {expected}, found {found}");

    public void ReportInvalidAssignmentTarget(TextSpan span) =>
        Add("TP0100", span, "cannot assign to this expression");

    public void ReportMixedArguments(TextSpan span) =>
        Add("TP0100", span, "cannot mix positional and keyword arguments");

    public void ReportChainedComparison(TextSpan span) =>
        Add("TP0212", span, "chained comparison not supported", "write 'a < b and b < c'");

    // ---- Type checking (SPEC §9.1). Types are passed pre-rendered as source text. ----

    public void ReportTypeMismatch(TextSpan span, object expected, object found) =>
        Add("TP0200", span, $"expected {expected}, found {found}");

    public void ReportBinaryOperatorTypes(TextSpan span, string op, object left, object right) =>
        Add("TP0200", span, $"operator '{op}' cannot be applied to {left} and {right}");

    public void ReportUnaryOperatorType(TextSpan span, string op, object operand) =>
        Add("TP0200", span, $"operator '{op}' cannot be applied to {operand}");

    public void ReportCannotCompare(TextSpan span, object left, object right) =>
        Add("TP0200", span, $"cannot compare {left} and {right}");

    public void ReportNoValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' does not return a value");

    public void ReportUnexpectedReturnValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' has no return type, so 'return' cannot have a value");

    public void ReportMissingReturnValue(TextSpan span, string function, object returnType) =>
        Add("TP0200", span, $"missing return value; '{function}' returns {returnType}");

    public void ReportFunctionAsValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' is a function; call it");

    public void ReportNotAFunction(TextSpan span, string text) =>
        Add("TP0200", span, $"'{text}' is not a function");

    public void ReportIntegerOutOfRange(TextSpan span) =>
        Add("TP0200", span, "integer literal is out of range");

    public void ReportRangeOutsideFor(TextSpan span) =>
        Add("TP0200", span, "'range' can only be used in a for loop");

    public void ReportCannotIterate(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot iterate over {type}");

    public void ReportOptionalNotNarrowed(TextSpan span, object expected, object found) =>
        Add("TP0203", span, $"expected {expected}, found {found}", NarrowHelp);

    public void ReportOptionalUse(TextSpan span, object type) =>
        Add("TP0203", span, $"{type} must be narrowed before use", NarrowHelp);

    private const string NarrowHelp = "narrow it with 'is some', or give a default with '??'";

    public void ReportNeedsOptional(TextSpan span, string construct, object type) =>
        Add("TP0200", span, $"'{construct}' needs an optional{(construct == "??" ? " on the left" : "")}, found {type}");

    public void ReportNestedOptional(TextSpan span) =>
        Add("TP0213", span, "optional types cannot be nested");

    public void ReportWrongTypeArgumentCount(TextSpan span, string name, int expected, int found) =>
        Add("TP0209", span, $"'{name}' takes {expected} {(expected == 1 ? "type argument" : "type arguments")}, found {found}");

    public void ReportCannotInferTypeArgument(TextSpan span, string parameter, string function) =>
        Add("TP0206", span, $"cannot infer '{parameter}' for '{function}'", "add a type annotation");

    public void ReportConflictingTypeArgument(TextSpan span, string parameter, object first, object second) =>
        Add("TP0200", span, $"'{parameter}' is inferred as both {first} and {second}");

    public void ReportConstraintViolation(TextSpan span, string parameter, IEnumerable<string> allowed, object found) =>
        Add("TP0200", span, $"'{parameter}' must be {Alternatives(allowed)}, found {found}");

    public void ReportConstraintTooShort(TextSpan span) =>
        Add("TP0220", span, "a constraint needs at least two types");

    public void ReportConstraintRepeated(TextSpan span, object type) =>
        Add("TP0220", span, $"'{type}' is listed twice in the constraint");

    public void ReportConstraintNotConcrete(TextSpan span) =>
        Add("TP0220", span, "a constraint cannot be optional or generic");

    public void ReportConstraintNotOnFunction(TextSpan span) =>
        Add("TP0220", span, "constraints are only supported on functions");

    /// <summary>"a or b", "a, b or c".</summary>
    public static string Alternatives(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} or {list[^1]}";
    }

    public void ReportModuleNotFound(TextSpan span, string module, string path) =>
        Add("TP0221", span, $"module '{module}' not found", $"looked for {path}");

    public void ReportStatementInModule(TextSpan span) =>
        Add("TP0222", span, "only the main file can have top-level statements");

    public void ReportNoDefinition(TextSpan span, string module, string name) =>
        Add("TP0201", span, $"module '{module}' has no definition '{name}'");

    public void ReportModuleAsValue(TextSpan span, string module) =>
        Add("TP0200", span, $"'{module}' is a module, not a value");

    public void ReportInstantiationTooDeep(TextSpan span, string function, string instance) =>
        Add("TP0219", span, $"instantiating '{function}' needs '{instance}', which is too deep");

    public void ReportCannotInferNone(TextSpan span) =>
        Add("TP0206", span, "cannot infer the type of none", "add a type annotation");

    public void ReportCannotMatch(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot match on {type}");

    public void ReportTypeAsValue(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' is a type, not a value");

    public void ReportNoVariant(TextSpan span, object enumType, string name) =>
        Add("TP0210", span, $"{enumType} has no variant '{name}'");

    public void ReportBindingCount(TextSpan span, string variant, int fields, int bindings) =>
        Add("TP0209", span, $"'{variant}' has {fields} {(fields == 1 ? "field" : "fields")}, found {bindings} {(bindings == 1 ? "binding" : "bindings")}");

    public void ReportNonExhaustiveMatch(TextSpan span, IEnumerable<string> missing) =>
        Add("TP0204", span, $"non-exhaustive match (missing: {string.Join(", ", missing)})");

    public void ReportAmbiguousVariant(TextSpan span, string variant, IEnumerable<string> enums)
    {
        var names = enums.ToList();
        Add("TP0216", span, $"'{variant}' is ambiguous: it is a variant of {string.Join(" and ", names)}",
            $"qualify it, e.g. '{names[0]}.{variant}'");
    }

    public void ReportUnreachableArm(TextSpan span) =>
        _items.Add(new Diagnostic("TP0300", span, "unreachable match arm", IsWarning: true) { Source = Source });

    public void ReportMethodAsValue(TextSpan span, string method) =>
        Add("TP0200", span, $"'{method}' is a method; call it");

    public void ReportFieldNotMethod(TextSpan span, string field) =>
        Add("TP0200", span, $"'{field}' is a field, not a method");

    public void ReportCannotIndex(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot index {type}");

    public void ReportInvalidDictKey(TextSpan span, object type) =>
        Add("TP0200", span, $"dict keys must be int, str, or bool, found {type}");

    public void ReportCannotAssignStrElement(TextSpan span) =>
        Add("TP0202", span, "cannot assign to an element of str");

    public void ReportCannotInferEmpty(TextSpan span, string collection) =>
        Add("TP0206", span, $"cannot infer the type of an empty {collection}", "add a type annotation");

    public void ReportMissingField(TextSpan span, string field, string structName) =>
        Add("TP0209", span, $"missing field '{field}' in '{structName}'");

    public void ReportRepeatedField(TextSpan span, string field) =>
        Add("TP0209", span, $"field '{field}' is given more than once");

    public void ReportNoField(TextSpan span, object type, string name) =>
        Add("TP0210", span, $"{type} has no field '{name}'");

    public void ReportNoMethod(TextSpan span, object type, string name) =>
        Add("TP0210", span, $"{type} has no method '{name}'");

    public void ReportUndefinedName(TextSpan span, string name) =>
        Add("TP0201", span, $"undefined name '{name}'");

    public void ReportUndefinedType(TextSpan span, string name) =>
        Add("TP0201", span, $"undefined type '{name}'");

    public void ReportCannotAssign(TextSpan span, string name, string what, string? help) =>
        Add("TP0202", span, $"cannot assign to '{name}': it is a {what}", help);

    public void ReportConditionNotBool(TextSpan span, object type) =>
        Add("TP0205", span, $"condition must be bool, found {type}");

    public void ReportShadowing(TextSpan span, string name) =>
        Add("TP0207", span, $"'{name}' shadows an outer binding");

    public void ReportNotAllPathsReturn(TextSpan span, string function) =>
        Add("TP0208", span, $"not all paths in '{function}' return a value");

    public void ReportWrongArgumentCount(TextSpan span, string function, string expected, int found) =>
        Add("TP0209", span, $"'{function}' takes {expected}, found {found}");

    public void ReportTopLevelVariableInFunction(TextSpan span, string function, string name) =>
        Add("TP0211", span, $"'{function}' cannot use top-level variable '{name}'",
            "pass it as a parameter, or make it a const");

    public void ReportDuplicateDefinition(TextSpan span, string name) =>
        Add("TP0214", span, $"duplicate definition of '{name}'");

    public void ReportBreakOutsideLoop(TextSpan span) =>
        Add("TP0215", span, "'break' outside a loop");

    public void ReportContinueOutsideLoop(TextSpan span) =>
        Add("TP0215", span, "'continue' outside a loop");

    public void ReportReturnOutsideFunction(TextSpan span) =>
        Add("TP0215", span, "'return' outside a function");

    public void ReportNotConstant(TextSpan span) =>
        Add("TP0217", span, "const initializer must be a constant expression");

    public void ReportKeywordArguments(TextSpan span) =>
        Add("TP0218", span, "keyword arguments are only allowed in constructors");

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    private void Add(string code, TextSpan span, string message, string? help = null) =>
        _items.Add(new Diagnostic(code, span, message, help) { Source = Source });

    private static string Describe(Rune character) =>
        Rune.IsControl(character) || Rune.IsWhiteSpace(character)
            ? $"U+{character.Value:X4}"
            : $"'{character}'";
}
