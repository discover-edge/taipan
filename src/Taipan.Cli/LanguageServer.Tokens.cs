using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Taipan.Compiler.Semantics;

namespace Taipan.Cli;

// Semantic tokens (SPEC §16.7, M32): a token for each name the binder resolved.
public sealed partial class LanguageServer
{
    private static readonly string[] TokenTypes =
        ["namespace", "class", "enum", "interface", "struct", "typeParameter", "type", "parameter", "variable", "property", "enumMember", "function", "method"];

    private static readonly string[] TokenModifiers = ["declaration", "readonly", "static", "defaultLibrary"];

    [Flags]
    private enum Modifier { None = 0, Declaration = 1, Readonly = 2, Static = 4, DefaultLibrary = 8 }

    private JsonNode? SemanticTokens(JsonNode parameters)
    {
        if (PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>()) is not { } path
            || !_documents.TryGetValue(path, out var document) || document.Result.Index is not { } index)
            return null;
        var data = new JsonArray();
        var (line, character) = (0, 0);
        foreach (var occurrence in index.In(path))
        {
            if (TokenOf(occurrence.Target) is not var (type, modifiers))
                continue;
            if (index.DefinitionOf(occurrence.Target) is { } definition && Path.GetFullPath(definition.Source.Path) == path && definition.Span == occurrence.Span)
                modifiers |= Modifier.Declaration;
            var start = document.Source.GetUtf16Position(occurrence.Span.Start);
            var end = document.Source.GetUtf16Position(occurrence.Span.End);
            // LSP's relative encoding: each token's line and start from the one before.
            data.Add(start.Line - line);
            data.Add(start.Line == line ? start.Character - character : start.Character);
            data.Add(end.Character - start.Character);
            data.Add(Array.IndexOf(TokenTypes, type));
            data.Add((int)modifiers);
            (line, character) = start;
        }
        return new JsonObject { ["data"] = data };
    }

    /// <summary>A name's token type and modifiers (SPEC §16.7); null for one that gets no token.</summary>
    private static (string Type, Modifier Modifiers)? TokenOf(object target) => target switch
    {
        ModuleSymbol or INamespaceSymbol => ("namespace", Modifier.None),
        ClassSymbol { IsProtocol: true } => ("interface", Modifier.None),
        ClassSymbol => ("class", Modifier.None),
        EnumSymbol => ("enum", Modifier.None),
        TypeParameterSymbol => ("typeParameter", Modifier.None),
        TypeAliasSymbol => ("type", Modifier.None),
        SemanticIndex.BuiltinType => ("type", Modifier.DefaultLibrary),
        // A Python name isn't one the binder resolves (SPEC §10).
        VariableSymbol { Type: PrimitiveType { Name: "py" }, Kind: VariableKind.Constant } => null,
        VariableSymbol { Kind: VariableKind.Parameter } => ("parameter", Modifier.None),
        VariableSymbol { Kind: VariableKind.Var } => ("variable", Modifier.None),
        VariableSymbol { Kind: VariableKind.Constant } => ("variable", Modifier.Readonly | Modifier.Static),
        VariableSymbol { Kind: VariableKind.Function } => ("function", Modifier.None),
        VariableSymbol => ("variable", Modifier.Readonly),
        FieldSymbol => ("property", Modifier.None),
        ClassAttributeSymbol => ("property", Modifier.Static),
        FunctionSymbol { Builtin: not null } => ("function", Modifier.DefaultLibrary),
        FunctionSymbol { Kind: MemberKind.Property } => ("property", Modifier.None),
        FunctionSymbol { Owner: not null } method => ("method", method.IsClassLevel ? Modifier.Static : Modifier.None),
        FunctionSymbol => ("function", Modifier.None),
        VariantSymbol => ("enumMember", Modifier.None),
        DotNetTypeSymbol { Type: var type } => (DotNetTokenType(type), Modifier.None),
        INamedTypeSymbol type => (DotNetTokenType(type), Modifier.None),
        IMethodSymbol method => ("method", method.IsStatic ? Modifier.Static : Modifier.None),
        IPropertySymbol or IFieldSymbol => ("property", ((ISymbol)target).IsStatic ? Modifier.Static : Modifier.None),
        _ => null,
    };

    private static string DotNetTokenType(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Struct => "struct",
        _ => "class",
    };
}
