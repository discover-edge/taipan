using Microsoft.CodeAnalysis;
using Taipan.Compiler.Text;

namespace Taipan.Compiler.Semantics;

/// <summary>
/// The names the binder resolved, for the language server (SPEC §16.3): each name where it's
/// defined or used, with what it names, and where each definition is. A name is a Taipan
/// <see cref="Symbol"/> or a .NET <see cref="ISymbol"/>.
/// </summary>
public sealed class SemanticIndex
{
    public sealed record Occurrence(SourceText Source, TextSpan Span, object Target);

    public sealed record Location(SourceText Source, TextSpan Span);

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
        ClassSymbol classSymbol => $"{(classSymbol.IsDataclass ? "@dataclass " : "")}class {classSymbol.Name}{TypeParameters(classSymbol.TypeParameters)}",
        EnumSymbol enumSymbol => $"enum {enumSymbol.Name}{TypeParameters(enumSymbol.TypeParameters)}",
        VariantSymbol variant => $"{variant.Enum.Name}.{variant.Name}{(variant.Fields.IsEmpty ? "" : $"({string.Join(", ", variant.Fields.Select(f => $"{f.Name}: {f.Type}"))})")}",
        ModuleSymbol module => $"module {module.Name}",
        TypeAliasSymbol alias => $"type {alias.Name}{TypeParameters(alias.TypeParameters)} = {alias.Target}",
        DotNetTypeSymbol dotnet => $".NET {dotnet.Type.ToDisplayString()}",
        ISymbol dotnet => $".NET {dotnet.ToDisplayString()}",
        _ => null,
    };

    private static string Signature(FunctionSymbol function)
    {
        var parameters = function.Parameters.Select((p, i) => $"{p.Name}: {p.Type}{(function.DefaultFor(i) is null ? "" : " = …")}");
        if (function.Self is not null)
            parameters = parameters.Prepend("self");
        var owner = function.Owner is { } classSymbol ? $"{classSymbol.Name}." : "";
        var result = function.ReturnType is NothingType ? "" : $" -> {function.ReturnType}";
        return $"def {owner}{function.Name}{TypeParameters(function.TypeParameters)}({string.Join(", ", parameters)}){result}";
    }

    private static string TypeParameters(IEnumerable<TypeParameterSymbol> parameters) =>
        parameters.Any() ? $"[{string.Join(", ", parameters.Select(p => p.Bound is { } bound ? $"{p.Name}: {bound.Name}" : p.Name))}]" : "";
}
