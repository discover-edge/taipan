using System.Collections.Immutable;
using System.Numerics;
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

    /// <summary>Adds the diagnostics another bag collected.</summary>
    public void AddFrom(DiagnosticBag other) => _items.AddRange(other._items);

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

    public void ReportLambdaDefault(TextSpan span) =>
        Add("TP0100", span, "lambda parameters can't have defaults",
            "each loop iteration has its own variable, so 'lambda: i' already sees this iteration's value");

    public void ReportInvalidDeleteTarget(TextSpan span) =>
        Add("TP0100", span, "cannot delete this expression");

    public void ReportInvalidConversion(TextSpan span, string conversion) =>
        Add("TP0006", span, $"invalid conversion '!{conversion}'", "use '!r' or '!s'");

    public void ReportNestedFormatSpec(TextSpan span) =>
        Add("TP0006", span, "format specs can't be nested");

    public void ReportInvalidFormatSpec(TextSpan span, string spec) =>
        Add("TP0006", span, $"invalid format spec '{spec}'");

    public void ReportInvalidNumber(TextSpan span) =>
        Add("TP0007", span, "invalid number literal");

    public void ReportExpected(TextSpan span, string expected, string found) =>
        Add("TP0100", span, $"expected {expected}, found {found}");

    public void ReportInvalidAssignmentTarget(TextSpan span) =>
        Add("TP0100", span, "cannot assign to this expression");

    public void ReportMixedArguments(TextSpan span) =>
        Add("TP0100", span, "positional argument follows keyword argument");

    public void ReportParameterOrder(TextSpan span, string message) =>
        Add("TP0100", span, message);

    public void ReportRequiredAfterDefault(TextSpan span) =>
        Add("TP0100", span, "parameter without a default follows parameter with a default");

    public void ReportAliasNotTopLevel(TextSpan span) =>
        Add("TP0100", span, "a type alias must be at the top level");

    public void ReportMisplacedMatch(TextSpan span) =>
        Add("TP0100", span, "'match' can only be the whole value of 'let', 'var', an assignment or 'return'");

    public void ReportBareExceptNotLast(TextSpan span) =>
        Add("TP0100", span, "a bare 'except' must be the last except clause");

    public void ReportChainedComparison(TextSpan span) =>
        Add("TP0212", span, "chained comparison not supported", "write 'a < b and b < c'");

    // ---- Type checking (SPEC §9.1). Types are passed pre-rendered as source text. ----

    public void ReportTypeMismatch(TextSpan span, object expected, object found) =>
        Add("TP0200", span, $"expected {expected}, found {found}");

    public void ReportBinaryOperatorTypes(TextSpan span, string op, object left, object right, string? help = null) =>
        Add("TP0200", span, $"operator '{op}' cannot be applied to {left} and {right}", help);

    public void ReportUnaryOperatorType(TextSpan span, string op, object operand, string? help = null) =>
        Add("TP0200", span, $"operator '{op}' cannot be applied to {operand}", help);

    public void ReportCannotCompare(TextSpan span, object left, object right) =>
        Add("TP0200", span, $"cannot compare {left} and {right}");

    public void ReportNoValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' does not return a value");

    public void ReportUnexpectedReturnValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' has no return type, so 'return' cannot have a value");

    public void ReportMissingReturnValue(TextSpan span, string function, object returnType) =>
        Add("TP0200", span, $"missing return value; '{function}' returns {returnType}");

    public void ReportGenericAsValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' is generic, so it cannot be used as a value", "wrap it in a lambda");

    public void ReportBuiltinAsValue(TextSpan span, string function) =>
        Add("TP0200", span, $"built-in '{function}' cannot be used as a value", "wrap it in a lambda");

    public void ReportCannotCall(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot call {type}");

    public void ReportCannotSort(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot sort {type}", "pass a key, e.g. 'key=lambda x: ...'");

    public void ReportCannotSortBy(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot sort by {type}");

    public void ReportCannotInferLambdaParameter(TextSpan span, string parameter) =>
        Add("TP0206", span, $"cannot infer the type of lambda parameter '{parameter}'", "add a type annotation");

    public void ReportFunctionArgumentCount(TextSpan span, string expected, int found) =>
        Add("TP0209", span, $"function takes {expected}, found {found}");

    public void ReportLambdaParameterCount(TextSpan span, int expected, int found) =>
        Add("TP0209", span, $"expected a lambda with {expected} {(expected == 1 ? "parameter" : "parameters")}, found {found}");

    public void ReportRepeatedArgument(TextSpan span, string name) =>
        Add("TP0209", span, $"argument '{name}' is given more than once");

    public void ReportBareCallable(TextSpan span) =>
        Add("TP0209", span, "'Callable' takes a parameter list and a result type");

    public void ReportNoKeywordArgument(TextSpan span, string function, string name) =>
        Add("TP0218", span, $"'{function}' has no parameter '{name}'");

    public void ReportMissingArgument(TextSpan span, string name) =>
        Add("TP0209", span, $"missing argument '{name}'");

    public void ReportTupleTooShort(TextSpan span) =>
        Add("TP0209", span, "a tuple needs at least two elements");

    public void ReportUnpackCount(TextSpan span, object type, int elements, int names) =>
        Add("TP0209", span, $"{type} has {elements} elements, found {names} names");

    public void ReportCannotUnpack(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot unpack {type}");

    public void ReportTupleIndexNotLiteral(TextSpan span) =>
        Add("TP0200", span, "tuple index must be an integer literal");

    public void ReportTupleIndexOutOfRange(TextSpan span, BigInteger index, object type) =>
        Add("TP0200", span, $"index {index} out of range for {type}");

    public void ReportCannotSlice(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot slice {type}");

    public void ReportCannotAssignTupleElement(TextSpan span) =>
        Add("TP0202", span, "cannot assign to an element of a tuple");

    public void ReportNestedTypeParameters(TextSpan span) =>
        Add("TP0223", span, "nested functions cannot have type parameters");

    public void ReportNotAFunction(TextSpan span, string text) =>
        Add("TP0200", span, $"'{text}' is not a function");

    public void ReportIntegerOutOfRange(TextSpan span) =>
        Add("TP0200", span, "integer literal is out of range");

    public void ReportRangeOutsideFor(TextSpan span) =>
        Add("TP0200", span, "'range' can only be used in a for loop");

    public void ReportCannotIterate(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot iterate over {type}");

    public void ReportOptionalNotNarrowed(TextSpan span, object expected, object found, string? whyNotNarrowed = null) =>
        Add("TP0203", span, $"expected {expected}, found {found}", whyNotNarrowed ?? NarrowHelp);

    /// <summary><paramref name="unnarrowable"/>: a var or field that a condition would have narrowed (SPEC §3.9).</summary>
    // A help saying why a name or path isn't narrowed replaces the usual one (SPEC §3.9).
    public void ReportOptionalUse(TextSpan span, object type, string? whyNotNarrowed = null) =>
        Add("TP0203", span, $"{type} must be narrowed before use", whyNotNarrowed ?? NarrowHelp);

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

    // ---- Protocols (SPEC §3.11, M17) ----

    public void ReportBoundNotOnFunction(TextSpan span) =>
        Add("TP0220", span, "bounds are only supported on functions");

    public void ReportNotProtocol(TextSpan span, object type, bool isClass) =>
        Add("TP0220", span, $"'{type}' is not a protocol", isClass ? "class bounds are not supported" : null);

    public void ReportProtocolAsType(TextSpan span, string protocol) =>
        Add("TP0229", span, $"'{protocol}' is a protocol, so it can only be a type parameter bound",
            $"use a bounded type parameter, e.g. 'def f[T: {protocol}](x: T)'");

    /// <summary><paramref name="why"/> is "it has no method 'm'" or "'m' must be 'def …'".</summary>
    public void ReportUnsatisfied(TextSpan span, object type, string protocol, string? declaredBy, string why) =>
        Add("TP0200", span, $"'{type}' does not satisfy '{protocol}'{(declaredBy is null ? "" : $" (declared by '{declaredBy}')")}: {why}");

    public void ReportMisplacedEllipsis(TextSpan span) =>
        Add("TP0200", span, "'...' can only be the body of a protocol method");

    public void ReportSpecialMethodCalled(TextSpan span, string name, string form) =>
        Add("TP0200", span, $"'{name}' cannot be called directly", $"use '{form}'");

    public void ReportLessThanSignature(TextSpan span, string className) =>
        Add("TP0225", span, $"'__lt__' must take one parameter of type '{className}' (or a base class) and return bool");

    public void ReportProtocolRepeated(TextSpan span, string protocol, string? declaredBy) =>
        Add("TP0225", span, declaredBy is null ? $"'{protocol}' is already declared" : $"'{protocol}' is already declared by '{declaredBy}'");

    public void ReportProtocolMember(TextSpan span) =>
        Add("TP0225", span, "a protocol can only declare methods");

    public void ReportProtocolBody(TextSpan span) =>
        Add("TP0225", span, "a protocol method's body must be '...'");

    public void ReportProtocolDefault(TextSpan span) =>
        Add("TP0225", span, "protocol method parameters cannot have defaults");

    public void ReportGenericProtocol(TextSpan span) =>
        Add("TP0225", span, "protocols cannot be generic");

    public void ReportProtocolBase(TextSpan span) =>
        Add("TP0225", span, "a protocol can only derive from 'Protocol'");

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

    // ---- Native executables (SPEC §15, M23) ----

    public void ReportNativePython(TextSpan span, bool extension = false)
    {
        if (extension)
            Add("TP0233", span, "a Python extension can't use Python interop itself");
        else
            Add("TP0233", span, "a native program can't use Python", "build it without '--native'");
    }

    public void ReportNativeDotNet(TextSpan span, string member, string reason) =>
        Add("TP0233", span, $"'{member}' can't be used in a native program: {reason}");

    // ---- Libraries (SPEC §14, M22) ----

    public void ReportStatementInLibrary(TextSpan span) =>
        Add("TP0222", span, "a library can't have top-level statements");

    public void ReportStatementInExtension(TextSpan span) =>
        Add("TP0222", span, "a Python extension can't have top-level statements");

    public void ReportNotExported(TextSpan span, string name, string reason) =>
        Warn("TP0302", span, $"'{name}' is not exported: {reason}", $"rename it '_{name}' to keep it private");

    public void ReportNoDefinition(TextSpan span, string module, string name) =>
        Add("TP0201", span, $"module '{module}' has no definition '{name}'");

    public void ReportModuleAsValue(TextSpan span, string module) =>
        Add("TP0200", span, $"'{module}' is a module, not a value");

    public void ReportInstantiationTooDeep(TextSpan span, string function, string instance) =>
        Add("TP0219", span, $"instantiating '{function}' needs '{instance}', which is too deep");

    public void ReportCannotInferNone(TextSpan span) =>
        Add("TP0206", span, "cannot infer the type of None", "add a type annotation");

    public void ReportCannotMatch(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot match on {type}");

    /// <summary><paramref name="target"/> is the class or enum the alias names, if it names one (SPEC §3.12).</summary>
    public void ReportAliasAsValue(TextSpan span, string name, string? target) =>
        Add("TP0200", span, $"'{name}' is a type alias, not a value", target is null ? null : $"use '{target}'");

    /// <summary><paramref name="through"/> is the cycle's other aliases, in the order it reaches them.</summary>
    public void ReportRecursiveAlias(TextSpan span, string name, IReadOnlyList<string> through)
    {
        var names = through.Select(n => $"'{n}'").ToList();
        var path = names.Count switch
        {
            0 => "",
            1 => $" through {names[0]}",
            _ => $" through {string.Join(", ", names[..^1])} and {names[^1]}",
        };
        Add("TP0230", span, $"type alias '{name}' refers to itself{path}", "use a class for a recursive type");
    }

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

    public void ReportEveryArmRaises(TextSpan span) =>
        Add("TP0200", span, "every arm of this 'match' raises", "use a 'match' statement");

    public void ReportUnreachableArm(TextSpan span) =>
        Warn("TP0300", span, "unreachable match arm");

    public void ReportUnreachableHandler(TextSpan span, string caughtBy) =>
        Warn("TP0300", span, "unreachable except clause", $"'{caughtBy}' is already caught above");

    public void ReportUnreachableCode(TextSpan span) =>
        Warn("TP0300", span, "unreachable code");

    public void ReportBroadHandler(TextSpan span) =>
        Warn("TP0301", span, "this clause catches every exception and discards it",
            "catch a specific class, re-raise with 'raise', or bind it with 'as e' and use it");

    public void ReportMethodAsValue(TextSpan span, string method) =>
        Add("TP0200", span, $"'{method}' is a method; call it");

    public void ReportFieldNotMethod(TextSpan span, string field) =>
        Add("TP0200", span, $"'{field}' is a field, not a method");

    public void ReportCannotIndex(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot index {type}");

    public void ReportInvalidDictKey(TextSpan span, object type, bool isDataclass) =>
        Add("TP0200", span, $"dict keys must be int, str, bool, tuples of those, or plain class instances, found {type}",
            isDataclass ? "a dataclass compares by value, so it cannot be a key" : null);

    public void ReportCompoundSliceAssign(TextSpan span, string op) =>
        Add("TP0202", span, $"cannot use '{op}' on a slice");

    public void ReportCannotDeleteName(TextSpan span, string name) =>
        Add("TP0202", span, $"cannot delete '{name}'", "assign it a new value instead");

    public void ReportCannotDeleteField(TextSpan span) =>
        Add("TP0202", span, "cannot delete a field");

    public void ReportCannotDeleteElement(TextSpan span, object type) =>
        Add("TP0202", span, $"cannot delete an element of {type}");

    public void ReportCannotAssignStrElement(TextSpan span) =>
        Add("TP0202", span, "cannot assign to an element of str");

    public void ReportCannotInferEmpty(TextSpan span, string collection) =>
        Add("TP0206", span, $"cannot infer the type of an empty {collection}", "add a type annotation");

    public void ReportMissingField(TextSpan span, string field, string structName) =>
        Add("TP0209", span, $"missing field '{field}' in '{structName}'");

    public void ReportRepeatedField(TextSpan span, string field) =>
        Add("TP0209", span, $"field '{field}' is given more than once");

    public void ReportNoField(TextSpan span, object type, string name, string? whyNotNarrowed = null) =>
        Add("TP0210", span, $"{type} has no field '{name}'", whyNotNarrowed);

    public void ReportNoMethod(TextSpan span, object type, string name, string? whyNotNarrowed = null) =>
        Add("TP0210", span, $"{type} has no method '{name}'", whyNotNarrowed);

    public void ReportUndefinedName(TextSpan span, string name) =>
        Add("TP0201", span, $"undefined name '{name}'", NoneHelp(name));

    public void ReportUndefinedType(TextSpan span, string name) =>
        Add("TP0201", span, $"undefined type '{name}'", name == "Self" ? "'Self' can only be used in a protocol" : NoneHelp(name));

    // Python's spelling is None; a lowercase none is just an undefined name (SPEC §3.4).
    private static string? NoneHelp(string name) => name == "none" ? "use 'None'" : null;

    public void ReportCannotAssign(TextSpan span, string name, string what, string? help) =>
        Add("TP0202", span, $"cannot assign to '{name}': it is a {what}", help);

    public void ReportConditionNotBool(TextSpan span, object type) =>
        Add("TP0205", span, $"condition must be bool, found {type}", type.ToString() == "py" ? "convert it with 'as bool'" : null);

    public void ReportCannotPassToPython(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot pass {type} to Python");

    public void ReportCastNeedsPy(TextSpan span, object type) =>
        Add("TP0200", span, $"'as' converts py values; found {type}");

    public void ReportCannotCastTo(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot convert to {type}");

    public void ReportPythonNotModule(TextSpan span) =>
        Add("TP0221", span, "'python' is not a module", "use 'from python import <module>'");

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

    public void ReportBareRaiseOutsideHandler(TextSpan span) =>
        Add("TP0215", span, "'raise' without an exception outside an except block");

    /// <summary><paramref name="keyword"/> is <c>return</c>, <c>break</c> or <c>continue</c> (SPEC §3.10).</summary>
    public void ReportLeavesFinally(TextSpan span, string keyword) =>
        Add("TP0215", span, $"'{keyword}' cannot be used in a finally block");

    public void ReportNotConstant(TextSpan span) =>
        Add("TP0217", span, "const initializer must be a constant expression");

    /// <summary>A keyword argument to a built-in, or (with no name) to a function value.</summary>
    public void ReportKeywordArguments(TextSpan span, string? function) =>
        Add("TP0218", span, function is null ? "a function value takes no keyword arguments" : $"'{function}' takes no keyword arguments");

    // ---- Classes (SPEC §4.3, M15) ----

    public void ReportUnknownDecorator(TextSpan span, string name) =>
        Add("TP0224", span, $"unknown decorator '@{name}'");

    public void ReportMisplacedDecorator(TextSpan span, string name, string? help = "'@dataclass' goes on a class, '@override' on a method") =>
        Add("TP0224", span, $"'@{name}' cannot be used here", help);

    public void ReportDuplicateDecorator(TextSpan span, string name) =>
        Add("TP0224", span, $"duplicate decorator '@{name}'");

    public void ReportMultipleBases(TextSpan span) =>
        Add("TP0225", span, "a class can have only one base class");

    /// <summary><paramref name="aliasOf"/> is the class or enum a type alias names, for help pointing at it (M18).</summary>
    public void ReportInvalidBase(TextSpan span, object type, string? aliasOf = null) =>
        Add("TP0225", span, $"'{type}' cannot be a base class", aliasOf is null ? null : $"use '{aliasOf}'");

    public void ReportMixedBase(TextSpan span, bool isDataclass, bool isException = false) =>
        Add("TP0225", span, isDataclass ? "a dataclass can only derive from a dataclass" : "a plain class can only derive from a plain class",
            isException ? "exceptions are plain classes; give it an '__init__'" : null);

    public void ReportGenericBase(TextSpan span) =>
        Add("TP0225", span, "generic classes cannot be derived from");

    public void ReportGenericWithBase(TextSpan span) =>
        Add("TP0225", span, "generic classes cannot have a base class");

    public void ReportInheritanceCycle(TextSpan span, string name) =>
        Add("TP0225", span, $"'{name}' derives from itself");

    public void ReportSpecialMethod(TextSpan span, string name) =>
        Add("TP0225", span, $"special method '{name}' is not supported");

    public void ReportDataclassInit(TextSpan span) =>
        Add("TP0225", span, "a dataclass cannot define '__init__'");

    public void ReportFieldOrder(TextSpan span) =>
        Add("TP0225", span, "field without a default follows field with a default");

    public void ReportFieldNotSet(TextSpan span, string field) =>
        Add("TP0226", span, $"'__init__' does not set '{field}'");

    public void ReportFieldWithoutInit(TextSpan span, string field, string className) =>
        Add("TP0226", span, $"'{field}' has no default, and '{className}' has no '__init__'");

    public void ReportFieldReadEarly(TextSpan span, string field) =>
        Add("TP0226", span, $"'{field}' is read before it is set");

    /// <summary><paramref name="field"/> is null when only <c>super().__init__</c> is still to come.</summary>
    public void ReportSelfUsedEarly(TextSpan span, string? field) =>
        Add("TP0226", span, field is null ? "'self' is used before 'super().__init__' is called" : $"'self' is used before '{field}' is set");

    public void ReportInheritedFieldEarly(TextSpan span, string field) =>
        Add("TP0226", span, $"'{field}' is inherited, so it is set by 'super().__init__'");

    public void ReportSuperInitEarly(TextSpan span, string field) =>
        Add("TP0226", span, $"'super().__init__' is called before '{field}' is set");

    public void ReportMissingSuperInit(TextSpan span, string className) =>
        Add("TP0226", span, $"'{className}.__init__' must call 'super().__init__(…)'");

    public void ReportMisplacedSuperInit(TextSpan span) =>
        Add("TP0226", span, "'super().__init__' must be called once, directly in '__init__'");

    public void ReportSuperInitOutsideInit(TextSpan span) =>
        Add("TP0226", span, "'super().__init__' can only be called in '__init__'");

    /// <summary><paramref name="field"/> is null when only <c>super().__init__</c> is still to come.</summary>
    public void ReportEarlyReturn(TextSpan span, string? field) =>
        Add("TP0226", span, field is null ? "'__init__' returns before 'super().__init__' is called" : $"'__init__' returns before '{field}' is set");

    public void ReportMissingOverride(TextSpan span, string method, string baseClass) =>
        Add("TP0227", span, $"'{method}' overrides '{baseClass}.{method}'; mark it '@override'");

    /// <summary><paramref name="baseClass"/> is null for a class without a base.</summary>
    public void ReportNothingToOverride(TextSpan span, string method, string? baseClass) =>
        Add("TP0227", span, $"'{method}' does not override a method of {(baseClass is null ? "a base class" : $"'{baseClass}'")}");

    public void ReportInitOverride(TextSpan span) =>
        Add("TP0227", span, "'__init__' cannot be marked '@override'");

    public void ReportOverrideParameters(TextSpan span, string method, string baseClass) =>
        Add("TP0228", span, $"'{method}' must take the same parameters as '{baseClass}.{method}'");

    public void ReportOverrideParameterType(TextSpan span, string parameter, string method, object type, string baseClass) =>
        Add("TP0228", span, $"parameter '{parameter}' of '{method}' must have type {type}, as in '{baseClass}.{method}'");

    public void ReportOverrideDefault(TextSpan span, string parameter, string method, string baseClass) =>
        Add("TP0228", span, $"parameter '{parameter}' of '{method}' needs a default, as in '{baseClass}.{method}'");

    /// <summary><paramref name="type"/> is null when the base method returns nothing.</summary>
    public void ReportOverrideReturn(TextSpan span, string method, object? type, string baseClass) =>
        Add("TP0228", span, $"'{method}' must return {type?.ToString() ?? "nothing"}, as '{baseClass}.{method}' does");

    public void ReportIsInstanceNeedsClass(TextSpan span, object type) =>
        Add("TP0200", span, $"isinstance needs a class, found {type}");

    public void ReportNotAClass(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' is not a class");

    public void ReportGenericClassTest(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' is generic");

    public void ReportNeverInstance(TextSpan span, object type, string className) =>
        Add("TP0200", span, $"{type} can never be a {className}");

    public void ReportBareClassPattern(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' is a class; write '{name}()'");

    public void ReportSuperMisuse(TextSpan span) =>
        Add("TP0200", span, "'super()' can only be used as 'super().<method>(…)'");

    public void ReportSuperOutsideMethod(TextSpan span) =>
        Add("TP0200", span, "'super()' can only be used in a method");

    public void ReportNoBaseClass(TextSpan span, string className) =>
        Add("TP0200", span, $"'{className}' has no base class");

    public void ReportInitCalled(TextSpan span) =>
        Add("TP0200", span, "'__init__' cannot be called directly");

    public void ReportInitReturnType(TextSpan span) =>
        Add("TP0200", span, "'__init__' cannot have a return type");

    public void ReportPositionalOnPlainClass(TextSpan span, string className) =>
        Add("TP0209", span, $"'{className}' is not a dataclass, so its fields can only be bound by name");

    // ---- Exceptions (SPEC §3.10, M16) ----

    /// <summary>A class name, which Python would let you raise, gets help to construct it.</summary>
    public void ReportCannotRaise(TextSpan span, object type, string? className = null) =>
        Add("TP0200", span, $"cannot raise {type}", className is null ? null : $"write '{className}(…)'");

    public void ReportNotExceptionClass(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' is not an exception class");

    public void ReportMixedExceptionBinding(TextSpan span) =>
        Add("TP0200", span, "cannot bind both Taipan and .NET exceptions", "write separate clauses");

    public void ReportFormatSpecNotValid(TextSpan span, string spec, object type, string help) =>
        Add("TP0200", span, $"format spec '{spec}' isn't valid for {type}", help);

    public void ReportFormatSpecType(TextSpan span, object type, string help) =>
        Add("TP0200", span, $"format specs apply to int, float and str, found {type}", help);

    public void ReportCannotConstruct(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' cannot be constructed");

    // ---- .NET interop (SPEC §13, M21) ----

    public void ReportDotNetNotModule(TextSpan span) =>
        Add("TP0221", span, "'dotnet' is not a module", "use 'from dotnet.<namespace> import <type>'");

    public void ReportDotNetNamespaceNotFound(TextSpan span, string ns) =>
        Add("TP0221", span, $".NET namespace '{ns}' not found");

    public void ReportNoDotNetType(TextSpan span, string ns, string name) =>
        Add("TP0201", span, $".NET namespace '{ns}' has no type '{name}'");

    public void ReportGenericDotNetImport(TextSpan span, string name) =>
        Add("TP0231", span, $"'{name}' can't be imported: generic .NET types aren't supported yet");

    public void ReportDotNetUnsupported(TextSpan span, string member, string type) =>
        Add("TP0231", span, $"'{member}' uses {type}, which Taipan can't use yet");

    public void ReportAmbiguousDotNetCall(TextSpan span, string member, string first, string second) =>
        Add("TP0232", span, $"ambiguous call to '{member}': it could be '{first}' or '{second}'");

    public void ReportNoOverload(TextSpan span, string member, IEnumerable<object> arguments) =>
        Add("TP0200", span, $"no overload of '{member}' takes ({string.Join(", ", arguments)})");

    public void ReportStaticClass(TextSpan span, string name) =>
        Add("TP0200", span, $"'{name}' is a static class");

    public void ReportOutOfRangeFor(TextSpan span, string value, string type) =>
        Add("TP0200", span, $"{value} is out of range for {type}");

    // ---- Special methods and class members (SPEC §4.8, M28) ----

    public void ReportSpecialSignature(TextSpan span, string method, string signature) =>
        Add("TP0225", span, $"'{method}' must be '{signature}'");

    public void ReportReceiver(TextSpan span, string method, string? receiver) =>
        Add("TP0225", span, receiver is null ? "a static method takes neither 'self' nor 'cls'" : $"'{method}' must take '{receiver}' first");

    public void ReportPropertySignature(TextSpan span, bool hasParameters) =>
        Add("TP0225", span, hasParameters ? "a property takes no parameters other than 'self'" : "a property must have a return type");

    public void ReportSetterPlacement(TextSpan span) =>
        Add("TP0225", span, "a setter must follow its property");

    public void ReportSetterSignature(TextSpan span, object type) =>
        Add("TP0225", span, $"a setter must take one parameter of type {type} and return nothing");

    public void ReportClassAttributeValue(TextSpan span, string name) =>
        Add("TP0225", span, $"class attribute '{name}' needs a value");

    public void ReportGenericClassMembers(TextSpan span, string what) =>
        Add("TP0225", span, $"generic classes cannot have {what}");

    public void ReportAbstractBody(TextSpan span) =>
        Add("TP0225", span, "an abstract method's body must be '...'");

    public void ReportUnhashableSubclass(TextSpan span, string className, string baseClass) =>
        Add("TP0225", span, $"'{className}' would not be hashable, but its base '{baseClass}' is", "define '__hash__'");

    public void ReportDecoratorCombination(TextSpan span, string first, string second) =>
        Add("TP0224", span, $"'@{first}' cannot be combined with '@{second}'");

    public void ReportAbstractNeedsAbc(TextSpan span) =>
        Add("TP0224", span, "'@abstractmethod' needs a class deriving from 'ABC'");

    public void ReportSetterWithoutProperty(TextSpan span, string property) =>
        Add("TP0224", span, $"'@{property}.setter' needs a property '{property}' just above it");

    public void ReportMemberKind(TextSpan span, string member, string kind, string baseClass) =>
        Add("TP0228", span, $"'{member}' is a {kind} in '{baseClass}'");

    public void ReportSetterRequired(TextSpan span, string property, string baseClass) =>
        Add("TP0228", span, $"'{property}' must have a setter, as '{baseClass}.{property}' does");

    public void ReportReflectedReturn(TextSpan span, string className, string method, object type, string baseClass, string forward) =>
        Add("TP0228", span, $"'{className}.{method}' must return {type}, since it can run in place of '{baseClass}.{forward}'");

    public void ReportClsConstructor(TextSpan span, string className, string baseClass, string method) =>
        Add("TP0228", span, $"'{className}' must be constructed as '{baseClass}' is, since '{baseClass}.{method}' calls 'cls(…)'");

    public void ReportAbstractInstantiation(TextSpan span, string className, IEnumerable<string> missing) =>
        Add("TP0234", span, $"cannot instantiate abstract class '{className}'", $"it does not implement {JoinAnd(missing.Select(m => $"'{m}'"))}");

    public void ReportClsInAbstract(TextSpan span, string className) =>
        Add("TP0234", span, $"'cls(…)' may construct the abstract class '{className}'");

    public void ReportAbstractBelowCls(TextSpan span, string className, string method, string baseClass, string classMethod) =>
        Add("TP0234", span, $"'{className}' cannot add abstract method '{method}', since '{baseClass}.{classMethod}' calls 'cls(…)'");

    /// <summary><paramref name="definer"/> is the class whose <c>__eq__</c> removes the hash; null for a dataclass.</summary>
    public void ReportUnhashableKey(TextSpan span, object type, string? definer) =>
        Add("TP0200", span, $"dict keys must be hashable, found {type}",
            definer is null ? "a dataclass needs its own '__hash__' to be a key" : $"'{definer}' defines '__eq__' without '__hash__'");

    public void ReportSumOfNonNumbers(TextSpan span, object element) =>
        Add("TP0200", span, $"sum() takes numbers, found {element}", element is Semantics.PrimitiveType { Name: "str" } ? "use ''.join(…)" : null);

    public void ReportAnyAllNotBool(TextSpan span, string function, object element) =>
        Add("TP0200", span, $"'{function}' takes bools, found {element}", "test each value, as in any(x > 0 for x in xs)");

    public void ReportCannotOrder(TextSpan span, string function, object element) =>
        Add("TP0200", span, $"'{function}' needs values that can be ordered, found {element}", "pass a key, e.g. 'key=lambda x: ...'");

    public void ReportNegativeIntExponent(TextSpan span) =>
        Add("TP0200", span, "an int can't be raised to a negative power", "use a float base, as in 2.0 ** -1");

    public void ReportInvalidSetElement(TextSpan span, object type) =>
        Add("TP0200", span, $"set elements must be hashable, found {type}");

    public void ReportCannotHash(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot hash {type}");

    public void ReportAmbiguousOperator(TextSpan span, string op, object left, object right, string method) =>
        Add("TP0200", span, $"'{op}' on {left} and {right} is ambiguous: '{method}' would run when it fits");

    public void ReportPropertyThroughClass(TextSpan span, string property) =>
        Add("TP0200", span, $"'{property}' is a property; read it from an instance");

    public void ReportNeedsInstance(TextSpan span, string member, string className) =>
        Add("TP0200", span, $"'{member}' needs an instance of '{className}'");

    public void ReportClsMisuse(TextSpan span) =>
        Add("TP0200", span, "'cls' can only be called, or used to reach static and class methods and class attributes");

    public void ReportClassVarMisplaced(TextSpan span) =>
        Add("TP0200", span, "'ClassVar' can only annotate a class attribute");

    public void ReportAbstractSuper(TextSpan span, string baseClass, string method) =>
        Add("TP0200", span, $"'{baseClass}.{method}' is abstract");

    public void ReportClassAttributeThrough(TextSpan span, string attribute, string through, string owner) =>
        Add("TP0202", span, $"cannot assign to '{attribute}' through '{through}'", $"assign '{owner}.{attribute}'");

    public void ReportClassAttributeNotConstant(TextSpan span) =>
        Add("TP0217", span, "class attribute value must be a constant expression");

    private static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }

    // ---- Iterators and generators (SPEC §3.13, M31) ----

    public void ReportBareYield(TextSpan span) =>
        Add("TP0100", span, "'yield' needs a value");

    public void ReportYieldOutsideFunction(TextSpan span) =>
        Add("TP0215", span, "'yield' outside a function");

    public void ReportAwaitOutsideAsync(TextSpan span) =>
        Add("TP0215", span, "'await' outside an async function");

    public void ReportAwaitInComprehension(TextSpan span) =>
        Add("TP0215", span, "'await' can't be inside a comprehension");

    public void ReportYieldInAsync(TextSpan span) =>
        Add("TP0215", span, "'yield' in an async function");

    public void ReportUnmetConstraint(TextSpan span, Semantics.TaipanType argument, string constraint, string parameter, string member) =>
        Add("TP0200", span, $"'{argument}' doesn't satisfy the constraint '{constraint}' on '{parameter}' of '{member}'");

    public void ReportCannotAwait(TextSpan span, Semantics.TaipanType type) =>
        Add("TP0200", span, $"cannot await {type}");

    public void ReportAwaitableDiscarded(TextSpan span) =>
        Add("TP0235", span, "this awaitable is never awaited", "write 'await …'");

    public void ReportYieldInTry(TextSpan span) =>
        Add("TP0215", span, "'yield' can't be inside a 'try' with 'except', or in 'finally'");

    public void ReportGeneratorReturnType(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' yields, so it must return 'Iterator[T]'");

    public void ReportGeneratorReturnValue(TextSpan span) =>
        Add("TP0200", span, "a generator can't return a value");

    public void ReportNotIterableFromList(TextSpan span, string message) =>
        Add("TP0200", span, message, "convert it with 'list(…)' first");

    // ---- Parameters (SPEC §4.2, M30) ----

    public void ReportPositionalOnlyByKeyword(TextSpan span, string parameter) =>
        Add("TP0218", span, $"'{parameter}' is positional-only, so it can't be passed by keyword");

    public void ReportMissingKeywordOnly(TextSpan span, string parameter) =>
        Add("TP0209", span, $"missing keyword-only argument '{parameter}'");

    public void ReportListOnlyFillsArgs(TextSpan span, string unpacked, string args) =>
        Add("TP0209", span, $"'*{unpacked}' can only fill '*{args}'");

    public void ReportNoVarArgs(TextSpan span, string function, string unpacked, bool keywords) =>
        Add("TP0209", span, keywords ? $"'{function}' has no '**kwargs' for '**{unpacked}'" : $"'{function}' has no '*args' for '*{unpacked}'");

    public void ReportKeywordsCantFill(TextSpan span, string unpacked, string parameter) =>
        Add("TP0209", span, $"'**{unpacked}' can't fill '{parameter}'");

    public void ReportCannotUnpackArguments(TextSpan span, object type) =>
        Add("TP0200", span, $"cannot unpack {type} into arguments");

    public void ReportVariadicAsValue(TextSpan span, string function) =>
        Add("TP0200", span, $"'{function}' has '*args' or '**kwargs', so it cannot be used as a value", "wrap it in a lambda");

    // ---- Destructuring (SPEC §3.8, §5.1, M29) ----

    public void ReportTwoStars(TextSpan span, string where) =>
        Add("TP0100", span, where == "a list pattern" ? "a list pattern can have only one '*'" : "an unpacking can have only one '*' at each level");

    public void ReportLoneStar(TextSpan span) =>
        Add("TP0100", span, "a starred name can't be unpacked to alone", "add another name, or drop the '*'");

    public void ReportMisplacedStar(TextSpan span) =>
        Add("TP0100", span, "'*' can only mark a name or target being unpacked");

    public void ReportOneElementTuplePattern(TextSpan span) =>
        Add("TP0209", span, "a tuple needs at least two elements");

    public void ReportTuplePatternCount(TextSpan span, object type, int elements, int patterns) =>
        Add("TP0209", span, $"{type} has {elements} elements, found {patterns} {(patterns == 1 ? "pattern" : "patterns")}");

    public void ReportCannotMatchPattern(TextSpan span, object type, string kind) =>
        Add("TP0200", span, $"{type} cannot match a {kind} pattern");

    public void ReportOptionalPattern(TextSpan span, object type) =>
        Add("TP0203", span, $"{type} can only be matched by '_' or a binding", "'None' patterns aren't supported yet; bind it and test it with 'is some'");

    public void ReportArmGivesNoValue(TextSpan span) =>
        Add("TP0200", span, "this arm gives no value", "end it with an expression");

    public void ReportLeavesMatchExpression(TextSpan span, string keyword) =>
        Add("TP0215", span, $"'{keyword}' can't leave a 'match' expression", "use a 'match' statement");

    public void ReportStarredJoin(TextSpan span, string name, object first, object second) =>
        Add("TP0200", span, $"'*{name}' would hold {first} and {second}");

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

    private void Add(string code, TextSpan span, string message, string? help = null) =>
        _items.Add(new Diagnostic(code, span, message, help) { Source = Source });

    private void Warn(string code, TextSpan span, string message, string? help = null) =>
        _items.Add(new Diagnostic(code, span, message, help, IsWarning: true) { Source = Source });

    private static string Describe(Rune character) =>
        Rune.IsControl(character) || Rune.IsWhiteSpace(character)
            ? $"U+{character.Value:X4}"
            : $"'{character}'";
}
