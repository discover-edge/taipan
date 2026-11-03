using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

/// <summary>
/// The names the binder resolved, for the language server (SPEC §16.3): each name where it's
/// defined or used, with what it names, and where each definition is. A name is a Taipan
/// <see cref="Symbol"/>, a .NET <see cref="ISymbol"/>, or a <see cref="BuiltinType"/>. For
/// completion (SPEC §16.4, M32), it also keeps what's before each <c>.</c> and the names in scope
/// where a name didn't resolve.
/// </summary>
public sealed class SemanticIndex
{
    public sealed record Occurrence(SourceText Source, TextSpan Span, object Target);

    public sealed record Location(SourceText Source, TextSpan Span);

    /// <summary>A built-in type's name, such as <c>int</c> or <c>list</c> (SPEC §3).</summary>
    public sealed record BuiltinType(string Name);

    /// <summary>A name in scope, at a distance: 0 for the innermost scope, <see cref="File"/> for the file's own and imported names, <see cref="Builtins"/> for built-ins.</summary>
    public sealed record ScopedName(string Name, object Target, int Distance)
    {
        public const int File = 1000;
        public const int Builtins = 2000;
    }

    /// <summary>The names in scope where a name didn't resolve, in a type or a value.</summary>
    public sealed record NameScope(ImmutableArray<ScopedName> Names, bool InType);

    private readonly Dictionary<(string, int), object> _receivers = [];
    private readonly Dictionary<(string, int), NameScope> _unresolved = [];

    /// <summary>
    /// Records what's before the <c>.</c> of the member at <paramref name="member"/>: a value's
    /// <see cref="TaipanType"/>, or the module, class, enum or .NET type it names.
    /// </summary>
    public void Receiver(SourceText source, TextSpan member, object receiver) => _receivers.TryAdd((source.Path, member.Start), receiver);

    public object? ReceiverAt(string path, int start) => _receivers.GetValueOrDefault((path, start));

    public void Unresolved(SourceText source, TextSpan span, NameScope scope) => _unresolved.TryAdd((source.Path, span.Start), scope);

    public NameScope? UnresolvedAt(string path, int start) => _unresolved.GetValueOrDefault((path, start));

    /// <summary>Every name the binder resolved in the file at <paramref name="path"/>, in order.</summary>
    public IEnumerable<Occurrence> In(string path) => _occurrences.TryGetValue(path, out var byStart) ? byStart.Values : [];

    // By file, then by start; a generic body bound more than once records each name once.
    private readonly Dictionary<string, SortedDictionary<int, Occurrence>> _occurrences = [];
    private readonly Dictionary<object, Location> _definitions = new(ReferenceEqualityComparer.Instance);

    public void Refer(SourceText source, TextSpan span, object target)
    {
        if (!_occurrences.TryGetValue(source.Path, out var byStart))
            _occurrences[source.Path] = byStart = [];
        byStart.TryAdd(span.Start, new Occurrence(source, span, target));
    }

    public void Define(SourceText source, TextSpan span, object target)
    {
        _definitions.TryAdd(target, new Location(source, span));
        Refer(source, span, target);
    }

    /// <summary>Where a name is defined, without an occurrence there: a module, at the start of its file.</summary>
    public void Locate(object target, SourceText source, TextSpan span) => _definitions.TryAdd(target, new Location(source, span));

    /// <summary>The name at <paramref name="position"/> in the file at <paramref name="path"/>, if it's one the binder resolved.</summary>
    public Occurrence? At(string path, int position)
    {
        if (!_occurrences.TryGetValue(path, out var byStart))
            return null;
        // The last name starting at or before the position, if it reaches it.
        Occurrence? found = null;
        foreach (var (start, occurrence) in byStart)
        {
            if (start > position)
                break;
            found = occurrence;
        }
        return found is not null && position <= found.Span.End ? found : null;
    }

    /// <summary>Where a name is defined; null for built-in, Python and .NET names.</summary>
    public Location? DefinitionOf(object target) => _definitions.GetValueOrDefault(target);

    /// <summary>What hover shows for a name (SPEC §16.3), or null for one it says nothing about.</summary>
    public static string? Describe(object target) => target switch
    {
        VariableSymbol { Kind: VariableKind.Constant } constant => $"const {constant.Name}: {constant.Type}",
        VariableSymbol { Kind: VariableKind.Var } variable => $"var {variable.Name}: {variable.Type}",
        VariableSymbol { Kind: VariableKind.Parameter } parameter => $"{parameter.Name}: {parameter.Type}",
        VariableSymbol variable => $"let {variable.Name}: {variable.Type}",
        FunctionSymbol { Builtin: not null } => null,
        FunctionSymbol function => Signature(function),
        FieldSymbol field => $"{field.Owner?.Name}.{field.Name}: {field.Type}",
        ClassAttributeSymbol attribute => $"{attribute.Owner.Name}.{attribute.Name}: ClassVar[{attribute.Type}]",
        ClassSymbol classSymbol => $"{(classSymbol.IsDataclass ? "@dataclass " : "")}class {classSymbol.Name}{TypeParameters(classSymbol.TypeParameters)}",
        EnumSymbol enumSymbol => $"enum {enumSymbol.Name}{TypeParameters(enumSymbol.TypeParameters)}",
        VariantSymbol variant => $"{variant.Enum.Name}.{variant.Name}{(variant.Fields.IsEmpty ? "" : $"({string.Join(", ", variant.Fields.Select(f => $"{f.Name}: {f.Type}"))})")}",
        ModuleSymbol module => $"module {module.Name}",
        TypeAliasSymbol alias => $"type {alias.Name}{TypeParameters(alias.TypeParameters)} = {alias.Target}",
        DotNetTypeSymbol dotnet => $".NET {dotnet.Type.ToDisplayString()}",
        ISymbol dotnet => $".NET {dotnet.ToDisplayString()}",
        _ => null,
    };

    private static string Signature(FunctionSymbol function) => SignatureOf(function).Label;

    /// <summary>
    /// A function's or method's signature as hover writes it (SPEC §16.3), with where each
    /// parameter is in it, for signature help (SPEC §16.5). <c>self</c> and <c>cls</c> are written
    /// but aren't parameters.
    /// </summary>
    public static (string Label, ImmutableArray<(int Start, int End)> Parameters) SignatureOf(FunctionSymbol function)
    {
        var owner = function.Owner is { } classSymbol ? $"{classSymbol.Name}." : "";
        var decorator = function.Kind switch
        {
            MemberKind.Property => "@property ",
            MemberKind.Static => "@staticmethod ",
            MemberKind.Class => "@classmethod ",
            _ => function.IsAbstract ? "@abstractmethod " : "",
        };
        var receiver = function.Self is not null ? "self" : function.Cls is not null ? "cls" : null;
        var parts = function.Parameters.Select((p, i) => ParameterText(p, function.Kinds[i], function.DefaultFor(i) is not null)).ToList();
        var label = $"{decorator}def {owner}{function.Name}{TypeParameters(function.TypeParameters)}";
        var (text, ranges) = ParameterList(label, parts, function.Kinds, receiver);
        var result = function.ReturnType is NothingType ? "" : $" -> {function.ReturnType}";
        return (text + result, ranges);
    }

    /// <summary>A parameter as a signature writes it: <c>by: float = …</c>, <c>*args: int</c>, <c>**kwargs: str</c>.</summary>
    public static string ParameterText(VariableSymbol parameter, Syntax.ParamKind kind, bool hasDefault) => kind switch
    {
        Syntax.ParamKind.VarArgs => $"*{parameter.Name}: {(parameter.Type is ListOfType list ? list.Element : parameter.Type)}",
        Syntax.ParamKind.VarKeywords => $"**{parameter.Name}: {(parameter.Type is DictOfType dict ? dict.Value : parameter.Type)}",
        _ => $"{parameter.Name}: {parameter.Type}{(hasDefault ? " = …" : "")}",
    };

    /// <summary>
    /// <paramref name="head"/> followed by the parameters in parentheses, with <c>/</c> after the
    /// positional-only ones and <c>*</c> before keyword-only ones without <c>*args</c> (SPEC §4.2).
    /// </summary>
    public static (string Label, ImmutableArray<(int Start, int End)> Parameters) ParameterList(
        string head, IReadOnlyList<string> parameters, IReadOnlyList<Syntax.ParamKind> kinds, string? receiver = null)
    {
        var text = new System.Text.StringBuilder(head).Append('(');
        var ranges = ImmutableArray.CreateBuilder<(int, int)>();
        var first = true;
        void Separate()
        {
            if (!first)
                text.Append(", ");
            first = false;
        }
        if (receiver is not null)
        {
            Separate();
            text.Append(receiver);
        }
        for (var i = 0; i < parameters.Count; i++)
        {
            if (kinds[i] == Syntax.ParamKind.KeywordOnly && (i == 0 || kinds[i - 1] is not (Syntax.ParamKind.KeywordOnly or Syntax.ParamKind.VarArgs)))
            {
                Separate();
                text.Append('*');
            }
            Separate();
            ranges.Add((text.Length, text.Length + parameters[i].Length));
            text.Append(parameters[i]);
            if (kinds[i] == Syntax.ParamKind.PositionalOnly && (i + 1 == parameters.Count || kinds[i + 1] != Syntax.ParamKind.PositionalOnly))
            {
                Separate();
                text.Append('/');
            }
        }
        return (text.Append(')').ToString(), ranges.ToImmutable());
    }

    private static string TypeParameters(IEnumerable<TypeParameterSymbol> parameters) =>
        parameters.Any() ? $"[{string.Join(", ", parameters.Select(p => p.Bound is { } bound ? $"{p.Name}: {bound.Name}" : p.Name))}]" : "";
}
