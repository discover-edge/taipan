using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Text;

namespace Taipan.Cli;

// Find references and rename (SPEC §16.6, M32), across the module root's files.
public sealed partial class LanguageServer
{
    /// <summary>Where a name is defined, which is what makes two occurrences in separate checks the same name.</summary>
    private readonly record struct NameKey(string Path, int Start, int Length);

    /// <summary>An occurrence of a name in a file of the module root.</summary>
    private sealed record Reference(string Path, SourceText Source, TextSpan Span, bool IsDefinition)
    {
        public string Text => Source.ToString(Span);
    }

    /// <summary>
    /// The key of what a name names: its definition's place, or, for a method, the place of the
    /// method it overrides at the top of its chain (SPEC §16.6). Null for built-in, Python and .NET names.
    /// </summary>
    private static NameKey? KeyOf(object target, SemanticIndex index)
    {
        if (target is FunctionSymbol function)
        {
            while (function.Overrides is { } overridden)
                function = overridden;
            target = function;
        }
        return index.DefinitionOf(target) is { } location ? new NameKey(Path.GetFullPath(location.Source.Path), location.Span.Start, location.Span.Length) : null;
    }

    // The module root's files (SPEC §16.6): every .tpn file in the document's directory, open or not.
    private List<string> RootFiles(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        return [.. Directory.EnumerateFiles(directory, "*.tpn").Select(Path.GetFullPath)
            .Concat(_documents.Keys.Where(d => Path.GetDirectoryName(d) == directory)).Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>Each file checked as a main file, with the open documents' text, and <paramref name="edited"/>'s over both.</summary>
    private Dictionary<string, CheckResult> CheckRoot(List<string> files, Dictionary<string, string>? edited = null)
    {
        string? Read(string file)
        {
            var full = Path.GetFullPath(file);
            return edited?.GetValueOrDefault(full) ?? (_documents.TryGetValue(full, out var open) ? open.Text : null);
        }
        return files.ToDictionary(f => f, f => edited is null && _documents.TryGetValue(f, out var open)
            ? open.Result
            : Checker.Check(f, Target.Program, Read));
    }

    // Every occurrence of the name `key` in the module root, in file and then position order.
    private static List<Reference> Occurrences(NameKey key, Dictionary<string, CheckResult> checks)
    {
        var found = new Dictionary<(string, int), Reference>();
        foreach (var (file, result) in checks)
        {
            if (result.Index is not { } index)
                continue;
            foreach (var occurrence in index.In(file))
            {
                if (KeyOf(occurrence.Target, index) != key)
                    continue;
                var definition = index.DefinitionOf(occurrence.Target);
                var isDefinition = definition is not null && Path.GetFullPath(definition.Source.Path) == file && definition.Span == occurrence.Span;
                found.TryAdd((file, occurrence.Span.Start), new Reference(file, occurrence.Source, occurrence.Span, isDefinition));
            }
        }
        return [.. found.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Span.Start)];
    }

    private JsonNode? References(JsonNode parameters)
    {
        if (Find(parameters) is not var (document, occurrence) || KeyOf(occurrence.Target, document.Result.Index!) is not { } key)
            return null;
        var path = Path.GetFullPath(document.Source.Path);
        var includeDeclaration = parameters["context"]?["includeDeclaration"]?.GetValue<bool>() ?? true;
        var locations = new JsonArray();
        foreach (var reference in Occurrences(key, CheckRoot(RootFiles(path))).Where(r => includeDeclaration || !r.IsDefinition))
            locations.Add(new JsonObject { ["uri"] = UriOf(reference.Path), ["range"] = Range(reference.Source, reference.Span) });
        return locations;
    }

    // ---- Rename ----

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    private static bool IsSpecial(string name) => name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);

    /// <summary>Throws the refusal for a name rename won't change (SPEC §16.6), and gives its key otherwise.</summary>
    private static NameKey Renamable(Document document, SemanticIndex.Occurrence occurrence)
    {
        var index = document.Result.Index!;
        var text = document.Source.ToString(occurrence.Span);
        switch (occurrence.Target)
        {
            case ISymbol or DotNetTypeSymbol:
                throw new RequestFailedException($"cannot rename the .NET name '{text}'");
            case ModuleSymbol:
                throw new RequestFailedException($"cannot rename the module '{text}'; rename its file instead");
            case VariableSymbol { Type: PrimitiveType { Name: "py" } } when index.DefinitionOf(occurrence.Target) is null:
                throw new RequestFailedException($"cannot rename the Python name '{text}'");
            case FunctionSymbol { Name: var name } when IsSpecial(name):
                throw new RequestFailedException($"cannot rename the special method '{name}'");
        }
        return KeyOf(occurrence.Target, index) ?? throw new RequestFailedException($"cannot rename the built-in '{text}'");
    }

    private JsonNode? PrepareRename(JsonNode parameters)
    {
        if (Find(parameters) is not var (document, occurrence))
            return null;
        Renamable(document, occurrence);
        return new JsonObject { ["range"] = Range(occurrence.Source, occurrence.Span), ["placeholder"] = document.Source.ToString(occurrence.Span) };
    }

    private JsonNode? Rename(JsonNode parameters)
    {
        if (Find(parameters) is not var (document, occurrence))
            return null;
        var key = Renamable(document, occurrence);
        var newName = parameters["newName"]!.GetValue<string>();
        if (!Identifier.IsMatch(newName) || newName == "_")
            throw new RequestFailedException($"'{newName}' is not a valid name");
        if (TokenFacts.Keywords.ContainsKey(newName))
            throw new RequestFailedException($"'{newName}' is a keyword");
        if (IsSpecial(newName))
            throw new RequestFailedException($"cannot rename to the special name '{newName}'");

        var path = Path.GetFullPath(document.Source.Path);
        var directory = Path.GetDirectoryName(path)!;
        var files = RootFiles(path);
        var before = CheckRoot(files);
        if (files.FirstOrDefault(f => before[f].Index is null) is { } broken)
            throw new RequestFailedException($"cannot rename while {Path.GetRelativePath(directory, broken)} has syntax errors");

        // An alias isn't spelled as what it imports: renaming the alias edits it in its own file only.
        var name = document.Source.ToString(occurrence.Span);
        var isAlias = occurrence.Target is Symbol symbol && symbol.Name != name;
        var edits = Occurrences(key, before).Where(r => r.Text == name && (!isAlias || r.Path == path)).ToList();
        var changes = new JsonObject();
        if (newName == name)
            return new JsonObject { ["changes"] = changes };

        // Checked again with the changes, no file may get an error it didn't have.
        var edited = new Dictionary<string, string>();
        foreach (var group in edits.GroupBy(e => e.Path))
        {
            var text = group.First().Source.Text;
            foreach (var edit in group.OrderByDescending(e => e.Span.Start))
                text = text[..edit.Span.Start] + newName + text[edit.Span.End..];
            edited[group.Key] = text;
            changes[UriOf(group.Key)] = new JsonArray([.. group.Select(e => (JsonNode)new JsonObject
            {
                ["range"] = Range(e.Source, e.Span),
                ["newText"] = newName,
            })]);
        }
        var after = CheckRoot(files, edited);
        foreach (var file in files)
        {
            var counts = Errors(before[file], file).CountBy(d => d.Code).ToDictionary();
            foreach (var error in Errors(after[file], file))
            {
                if (counts.GetValueOrDefault(error.Code) > 0)
                {
                    counts[error.Code]--;
                    continue;
                }
                var position = error.Source!.GetLinePosition(error.Span.Start);
                throw new RequestFailedException(
                    $"renaming '{name}' to '{newName}' would cause an error: {Path.GetRelativePath(directory, file)}:{position.Line}:{position.Column}: {error.Code}: {error.Message}");
            }
        }
        return new JsonObject { ["changes"] = changes };
    }

    // A file's own errors, from its check as a main file.
    private static IEnumerable<Compiler.Diagnostics.Diagnostic> Errors(CheckResult result, string file) =>
        result.Diagnostics.Where(d => !d.IsWarning && d.Source is { } source && Path.GetFullPath(source.Path) == file);
}
