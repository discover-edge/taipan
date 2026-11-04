using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Formatting;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Cli;

// Formatting and code actions (SPEC §16.10, §16.11, M44).
public sealed partial class LanguageServer
{
    /// <summary><c>textDocument/formatting</c>: the whole document as <c>taipan fmt</c> writes it, or no edits (SPEC §16.10).</summary>
    private JsonNode? Formatting(JsonNode parameters)
    {
        if (PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>()) is not { } path || !_documents.TryGetValue(path, out var document))
            return null;
        var edits = new JsonArray();
        if (Formatter.Format(SourceText.From(document.Text, path)).Text is { } formatted && formatted != document.Source.Text)
            edits.Add(new JsonObject { ["range"] = Range(document.Source, new TextSpan(0, document.Source.Length)), ["newText"] = formatted });
        return edits;
    }

    private static readonly Regex UndefinedName = new("^undefined name '(.+)'$");
    private static readonly Regex MissingOverride = new("^'(.+)' overrides '.+'; mark it '@override'$");
    private static readonly Regex NeedlessOverride = new("^'(.+)' does not override a method of ");

    /// <summary><c>textDocument/codeAction</c>: quick fixes for the diagnostics in the range (SPEC §16.11).</summary>
    private JsonNode? CodeActions(JsonNode parameters)
    {
        var actions = new JsonArray();
        if (PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>()) is not { } path || !_documents.TryGetValue(path, out var document))
            return actions;
        if (parameters["context"]?["only"] is JsonArray only && !only.Any(k => k?.GetValue<string>() is "quickfix" or "quickfix."))
            return actions;
        var source = document.Source;
        var range = parameters["range"]!;
        var start = source.GetPosition(range["start"]!["line"]!.GetValue<int>(), range["start"]!["character"]!.GetValue<int>());
        var end = source.GetPosition(range["end"]!["line"]!.GetValue<int>(), range["end"]!["character"]!.GetValue<int>());
        foreach (var diagnostic in document.Result.Diagnostics.Where(d => d.Source is { } s && Path.GetFullPath(s.Path) == path
            && d.Span.Start <= end && d.Span.End >= start))
        {
            foreach (var (title, edits) in Fixes(path, document, diagnostic))
            {
                actions.Add(new JsonObject
                {
                    ["title"] = title,
                    ["kind"] = "quickfix",
                    ["diagnostics"] = new JsonArray(ToLsp(diagnostic, source)),
                    ["edit"] = new JsonObject { ["changes"] = new JsonObject { [UriOf(path)] = new JsonArray([.. edits]) } },
                });
            }
        }
        return actions;
    }

    // The fixes for one diagnostic, each a title and its edits to the document (SPEC §16.11).
    private IEnumerable<(string Title, List<JsonNode> Edits)> Fixes(string path, Document document, Diagnostic diagnostic)
    {
        var source = document.Source;
        switch (diagnostic.Code)
        {
            case "TP0201" when UndefinedName.Match(diagnostic.Message) is { Success: true } undefined:
            {
                var name = undefined.Groups[1].Value;
                var at = ImportPosition(source);
                if (BuiltinModules.IsBuiltin(name) || RootFiles(path).Any(f => f != path && Path.GetFileNameWithoutExtension(f) == name))
                    yield return ($"Import '{name}'", [Insert(source, at, $"import {name}\n")]);
                foreach (var module in RootFiles(path).Where(f => f != path && Exports(f).Contains(name)).Select(Path.GetFileNameWithoutExtension))
                    yield return ($"Import '{name}' from '{module}'", [Insert(source, at, $"from {module} import {name}\n")]);
                break;
            }
            case "TP0227" when MissingOverride.Match(diagnostic.Message) is { Success: true } missing:
            {
                var lineStart = LineStart(source.Text, diagnostic.Span.Start);
                var indent = source.Text[lineStart..].TakeWhile(c => c == ' ').Count();
                yield return ($"Mark '{missing.Groups[1].Value}' '@override'", [Insert(source, lineStart, new string(' ', indent) + "@override\n")]);
                break;
            }
            case "TP0227" when NeedlessOverride.Match(diagnostic.Message) is { Success: true } needless:
            {
                // The decorator lines above the def, nearest first.
                var lineStart = LineStart(source.Text, diagnostic.Span.Start);
                for (var line = lineStart; line > 0;)
                {
                    var previous = LineStart(source.Text, line - 1);
                    var text = source.Text[previous..(line - 1)].Trim();
                    if (!text.StartsWith('@'))
                        break;
                    if (text == "@override")
                    {
                        yield return ($"Remove '@override' from '{needless.Groups[1].Value}'",
                            [new JsonObject { ["range"] = Range(source, new TextSpan(previous, line - previous)), ["newText"] = "" }]);
                        break;
                    }
                    line = previous;
                }
                break;
            }
            case "TP0200" when diagnostic.Help == "convert it with 'list(…)' first":
                yield return ("Convert to 'list(…)'", [Insert(source, diagnostic.Span.Start, "list("), Insert(source, diagnostic.Span.End, ")")]);
                break;
        }
    }

    private static JsonObject Insert(SourceText source, int position, string text) =>
        new() { ["range"] = Range(source, new TextSpan(position, 0)), ["newText"] = text };

    private static int LineStart(string text, int position) => position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;

    /// <summary>
    /// Where a new import goes (SPEC §16.11): after the last top-level import, or before the first
    /// line of code, after the comments at the file's start.
    /// </summary>
    private static int ImportPosition(SourceText source)
    {
        var parsed = Parser.Parse(source, Lexer.Lex(source).Tokens).Unit;
        if (parsed.Items.LastOrDefault(i => i is ImportStmt or FromImportStmt) is { } last)
        {
            var end = source.Text.IndexOf('\n', last.Span.End);
            return end < 0 ? source.Length : end + 1;
        }
        return parsed.Items.FirstOrDefault() is { } first ? LineStart(source.Text, first.Span.Start) : source.Length;
    }

    /// <summary>The names a module exports (SPEC §4.7): what it defines at its top level, and its enums' variants.</summary>
    private HashSet<string> Exports(string file)
    {
        var text = _documents.TryGetValue(file, out var open) ? open.Text : File.Exists(file) ? File.ReadAllText(file) : "";
        var source = SourceText.From(text, file);
        var unit = Parser.Parse(source, Lexer.Lex(source).Tokens).Unit;
        var names = new HashSet<string>();
        foreach (var item in unit.Items)
        {
            switch (item)
            {
                case FuncDef function:
                    names.Add(function.Name.Text);
                    break;
                case ClassDef classDef:
                    names.Add(classDef.Name.Text);
                    break;
                case EnumDef enumDef:
                    names.Add(enumDef.Name.Text);
                    names.UnionWith(enumDef.Variants.Select(v => v.Name.Text));
                    break;
                case ConstDef constant:
                    names.Add(constant.Name.Text);
                    break;
                case TypeAliasDef alias:
                    names.Add(alias.Name.Text);
                    break;
            }
        }
        return names;
    }
}
