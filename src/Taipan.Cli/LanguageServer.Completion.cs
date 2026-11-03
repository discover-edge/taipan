using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Taipan.Compiler.Lexing;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Cli;

// Completion and signature help (SPEC §16.4, §16.5, M32), on a copy of the document patched at the cursor.
public sealed partial class LanguageServer
{
    // The name put where the cursor is, and the alias a `from m import` line is checked through.
    private const string Placeholder = "__taipan_cursor__";
    private const string ModulePlaceholder = "__taipan_module__";

    private static readonly string[] BuiltinTypes = ["bool", "Callable", "dict", "float", "int", "Iterable", "Iterator", "list", "py", "str", "tuple"];

    private static readonly string[] StatementKeywords =
        ["async", "await", "break", "class", "const", "continue", "def", "del", "elif", "else", "enum", "except", "False", "finally", "for", "from",
         "if", "import", "lambda", "let", "match", "None", "not", "pass", "raise", "return", "True", "try", "type", "var", "while", "yield"];

    private static readonly string[] ExpressionKeywords = ["await", "False", "lambda", "match", "None", "not", "True"];

    // §6's methods of list, dict and str.
    private static readonly Dictionary<string, string[]> BuiltinMethods = new()
    {
        ["list"] = ["append", "contains", "insert", "pop", "sort"],
        ["dict"] = ["get", "items", "keys", "remove", "values"],
        ["str"] = ["endswith", "lower", "replace", "split", "startswith", "strip", "upper"],
    };

    private enum Place { Code, Comment, String }

    /// <summary>A bracket open before the cursor on its logical line, or an f-string's hole.</summary>
    private sealed class OpenBracket(char bracket, int position, char quote = '\0')
    {
        public char Bracket { get; } = bracket;
        public int Position { get; } = position;
        public char Quote { get; } = quote;

        // Where each argument starts, when it's a call's parenthesis.
        public List<int> Arguments { get; } = [position + 1];
    }

    /// <summary>The cursor's logical line up to the cursor: where it starts, what's open, and whether the cursor is in code.</summary>
    private sealed record LinePrefix(int Start, int End, Place Place, List<OpenBracket> Open, char? Quote);

    private static LinePrefix Scan(string text, int cursor)
    {
        // The logical line starts after the last NEWLINE before the cursor (SPEC §1.2).
        var tokens = Lexer.Lex(SourceText.From(text)).Tokens;
        var start = 0;
        foreach (var token in tokens)
        {
            if (token.Span.Start >= cursor)
                break;
            if (token.Kind == TokenKind.Newline && token.Span.Length > 0)
                start = token.Span.End;
        }
        // Blank and comment-only lines before it aren't part of it.
        while (text.IndexOf('\n', start, cursor - start) is var end and >= 0 && text[start..end].TrimStart() is "" or ['#', ..])
            start = end + 1;
        var stack = new List<OpenBracket>();
        var place = Place.Code;
        char? quote = null;
        var isF = false;
        for (var i = start; i < cursor; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == '\\')
                    i++;
                else if (c == q || c == '\n')
                    quote = null;
                else if (isF && c == '{' && i + 1 < cursor && text[i + 1] == '{')
                    i++;
                else if (isF && c == '{')
                {
                    stack.Add(new OpenBracket('{', i, q));
                    quote = null;
                }
                continue;
            }
            switch (c)
            {
                case '#':
                    var end = text.IndexOf('\n', i, cursor - i);
                    if (end < 0)
                        return new LinePrefix(start, cursor, Place.Comment, stack, null);
                    i = end;
                    break;
                case '"' or '\'':
                    isF = i > 0 && text[i - 1] == 'f' && (i < 2 || !IsNameChar(text[i - 2]));
                    quote = c;
                    break;
                case '(' or '[' or '{':
                    stack.Add(new OpenBracket(c, i));
                    break;
                case ')' or ']' or '}' when stack.Count > 0:
                    var top = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    // The end of a hole goes back into its f-string.
                    if (top.Quote != '\0')
                        (quote, isF) = (top.Quote, true);
                    break;
                case ',' when stack.Count > 0:
                    stack[^1].Arguments.Add(i + 1);
                    break;
            }
        }
        if (quote is not null)
            place = Place.String;
        return new LinePrefix(start, cursor, place, stack, quote);
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    // Where the name being typed at the cursor starts.
    private static int WordStart(string text, int cursor)
    {
        var start = cursor;
        while (start > 0 && IsNameChar(text[start - 1]))
            start--;
        return start;
    }

    private static readonly Regex BlockHeader = new(@"^\s*(if|elif|else|while|for|def|class|enum|match|try|except|finally)\b");
    private static readonly Regex Binding = new(@"^\s*(let|var|const)\b");

    /// <summary>
    /// The document with the cursor's logical line cut at <paramref name="at"/>, the placeholder
    /// there, its brackets closed, and a block header's <c>:</c> and body (SPEC §16.4).
    /// </summary>
    private static string Patch(string text, LinePrefix prefix, int at, string inserted = Placeholder)
    {
        var code = text[prefix.Start..at];
        var patched = new System.Text.StringBuilder(text[..at]).Append(inserted);
        if (prefix.Quote is { } quote)
            patched.Append(quote);
        for (var i = prefix.Open.Count - 1; i >= 0; i--)
        {
            var open = prefix.Open[i];
            patched.Append(open.Quote != '\0' ? $"}}{open.Quote}" : open.Bracket switch { '(' => ")", '[' => "]", _ => "}" });
        }

        var lineStart = prefix.Start + (code.Length - code.TrimStart('\n', ' ').Length);
        lineStart = text.LastIndexOf('\n', Math.Max(lineStart - 1, 0)) + 1;
        var indent = text[lineStart..].TakeWhile(c => c == ' ').Count();
        var rest = text[LogicalLineEnd(text, prefix, indent)..];
        if (Binding.IsMatch(code) && !HasAssignment(code))
            patched.Append(" = None");
        if (BlockHeader.Match(code) is { Success: true } header)
        {
            patched.Append(':');
            var next = rest.Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
            if (next is null || next.TakeWhile(c => c == ' ').Count() <= indent)
                patched.Append('\n').Append(' ', indent + 4).Append(header.Groups[1].Value == "match" ? "_: pass" : "pass");
        }
        return patched.Append('\n').Append(rest).ToString();
    }

    /// <summary>
    /// Where the rest of the document starts after the cursor's logical line. Its brackets may not
    /// be closed, since it's being typed, so it ends at a line indented no more than it, unless that
    /// line closes a bracket.
    /// </summary>
    private static int LogicalLineEnd(string text, LinePrefix prefix, int indent)
    {
        var depth = prefix.Open.Count;
        char? quote = prefix.Quote;
        for (var i = prefix.End; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                quote = null;
                var next = text[(i + 1)..].Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
                if (depth <= 0 || next is null || next.TakeWhile(ch => ch == ' ').Count() <= indent && next.TrimStart()[0] is not (')' or ']' or '}'))
                    return i + 1;
                continue;
            }
            if (quote is { } q)
            {
                if (c == '\\')
                    i++;
                else if (c == q)
                    quote = null;
                continue;
            }
            switch (c)
            {
                case '#':
                    i = text.IndexOf('\n', i) is var end and >= 0 ? end - 1 : text.Length;
                    break;
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
            }
        }
        return text.Length;
    }

    // Whether a binding's text has its `=` already, outside brackets.
    private static bool HasAssignment(string code)
    {
        var depth = 0;
        for (var i = 0; i < code.Length; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case '=' when depth == 0 && (i + 1 == code.Length || code[i + 1] != '=') && (i == 0 || code[i - 1] is not ('=' or '!' or '<' or '>')):
                    return true;
            }
        }
        return false;
    }

    private (string Path, Document Document, int Cursor)? Cursor(JsonNode parameters)
    {
        if (PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>()) is not { } path || !_documents.TryGetValue(path, out var document))
            return null;
        var position = document.Source.GetPosition(parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>());
        return (path, document, position);
    }

    // ---- Completion (SPEC §16.4) ----

    private static readonly Regex ImportModule = new(@"^\s*(import\s+([\w.]+\s*(as\s+\w+\s*)?,\s*)*|from\s+)$");
    private static readonly Regex FromImport = new(@"^\s*from\s+([\w.]+)\s+import\s+([\w\s,]*,\s*)?$");

    private JsonNode Completion(JsonNode parameters)
    {
        var items = new JsonArray();
        var result = new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        if (Cursor(parameters) is not var (path, document, cursor))
            return result;
        var text = document.Text;
        var prefix = Scan(text, cursor);
        if (prefix.Place != Place.Code)
            return result;
        var word = WordStart(text, cursor);
        var code = text[prefix.Start..word];

        if (ImportModule.IsMatch(code))
        {
            foreach (var module in ImportableModules(path))
                items.Add(Item(module, 9, $"module {module}", "0000"));
            return result;
        }
        string patched;
        if (FromImport.Match(code) is { Success: true } from)
        {
            var module = from.Groups[1].Value;
            if (module == ModuleLoader.PythonModule || ModuleLoader.IsDotNet(module))
                return result;
            // The module's names, as after `m.` (SPEC §16.4).
            var lineStart = text.LastIndexOf('\n', Math.Max(word - 1, 0)) + 1;
            var line = $"import {module} as {ModulePlaceholder}\n{ModulePlaceholder}.";
            patched = text[..lineStart] + line + Placeholder + "\n" + text[(text.IndexOf('\n', cursor) is var end and >= 0 ? end + 1 : text.Length)..];
            word = lineStart + line.Length;
        }
        else
        {
            patched = Patch(text, prefix, word);
        }

        var index = Check(path, patched).Result.Index;
        if (index is null)
            return result;
        if (index.ReceiverAt(path, word) is { } receiver)
        {
            foreach (var (name, target) in Members(receiver))
                items.Add(Item(name, KindOf(target), Detail(target, name), "0000"));
        }
        else if (index.UnresolvedAt(path, word) is { } scope)
        {
            foreach (var name in scope.Names.Where(n => !scope.InType || IsType(n.Target)))
                items.Add(Item(name.Name, KindOf(name.Target), Detail(name.Target, name.Name), $"{name.Distance:D4}"));
            if (scope.InType)
            {
                foreach (var type in BuiltinTypes)
                    items.Add(Item(type, 7, null, $"{SemanticIndex.ScopedName.Builtins:D4}"));
            }
            else
            {
                var keywords = string.IsNullOrWhiteSpace(code) ? StatementKeywords : ExpressionKeywords;
                foreach (var keyword in keywords)
                    items.Add(Item(keyword, 14, null, "3000"));
            }
        }
        return result;
    }

    private static JsonObject Item(string label, int kind, string? detail, string group)
    {
        var item = new JsonObject { ["label"] = label, ["kind"] = kind, ["sortText"] = group + label };
        if (detail is not null)
            item["detail"] = detail;
        return item;
    }

    private IEnumerable<string> ImportableModules(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var modules = Directory.EnumerateFiles(directory, "*.tpn").Concat(_documents.Keys.Where(d => Path.GetDirectoryName(d) == directory))
            .Select(Path.GetFullPath).Where(f => f != path).Select(Path.GetFileNameWithoutExtension).OfType<string>();
        return modules.Append(ModuleLoader.PythonModule).Append(ModuleLoader.DotNetModule).Distinct().Order(StringComparer.Ordinal);
    }

    private static bool IsType(object target) =>
        target is ClassSymbol or EnumSymbol or TypeAliasSymbol or TypeParameterSymbol or DotNetTypeSymbol or ModuleSymbol;

    /// <summary>The members offered after <c>.</c> on what's before it (SPEC §16.4).</summary>
    private static IEnumerable<(string Name, object Target)> Members(object receiver)
    {
        static bool Special(string name) => name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);
        switch (receiver)
        {
            case ModuleSymbol module:
                return module.Definitions.Select(d => (d.Key, (object)d.Value));
            case EnumSymbol enumSymbol:
                return enumSymbol.Variants.Select(v => (v.Name, (object)v));
            case ClassSymbol classSymbol:
                return Chain(classSymbol).SelectMany(c => c.Methods.Values.Where(m => m.IsClassLevel).Select(m => (m.Name, (object)m))
                    .Concat(c.ClassAttributes.Select(a => (a.Name, (object)a)))).DistinctBy(m => m.Item1);
            case ClassType { Symbol: var classSymbol }:
                return Chain(classSymbol).SelectMany(c => c.Fields.Select(f => (f.Name, (object)f))
                    .Concat(c.Methods.Values.Where(m => !m.IsClassLevel && !Special(m.Name) && m.SetterOf is null).Select(m => (m.Name, (object)m)))
                    .Concat(c.ClassAttributes.Select(a => (a.Name, (object)a)))).DistinctBy(m => m.Item1);
            case ListOfType or DictOfType or PrimitiveType { Name: "str" }:
                var kind = receiver switch { ListOfType => "list", DictOfType => "dict", _ => "str" };
                return BuiltinMethods[kind].Select(m => (m, (object)new BuiltinMethodName(m)));
            case DotNetType { Symbol: var type }:
                return DotNetMembers(type, isStatic: false).DistinctBy(m => m.Item1);
            case INamedTypeSymbol type:
                return DotNetMembers(type, isStatic: true).DistinctBy(m => m.Item1);
            default:
                return [];
        }
    }

    /// <summary>A built-in type's method (SPEC §6), offered after <c>.</c>.</summary>
    private sealed record BuiltinMethodName(string Name);

    private static IEnumerable<ClassSymbol> Chain(ClassSymbol classSymbol)
    {
        for (var c = classSymbol; c is not null; c = c.Base)
            yield return c;
    }

    private static IEnumerable<(string, object)> DotNetMembers(INamedTypeSymbol type, bool isStatic)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || member.IsStatic != isStatic || member.IsImplicitlyDeclared)
                    continue;
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol { IsIndexer: false } or IFieldSymbol)
                    yield return (member.Name, member);
            }
        }
    }

    private static string? Detail(object target, string name) => target switch
    {
        BuiltinMethodName => null,
        _ => SemanticIndex.Describe(target),
    };

    /// <summary>LSP's <c>CompletionItemKind</c> for a name (SPEC §16.4).</summary>
    private static int KindOf(object target) => target switch
    {
        FunctionSymbol { Kind: MemberKind.Property } => 10,
        FunctionSymbol { Owner: not null } or BuiltinMethodName or IMethodSymbol => 2,
        FunctionSymbol => 3,
        FieldSymbol or ClassAttributeSymbol or IFieldSymbol => 5,
        VariableSymbol { Kind: VariableKind.Constant, Type: not PrimitiveType { Name: "py" } } => 21,
        VariableSymbol { Kind: VariableKind.Function } => 3,
        VariableSymbol { Type: PrimitiveType { Name: "py" } } => 9,
        VariableSymbol => 6,
        ClassSymbol { IsProtocol: true } => 8,
        ClassSymbol or TypeAliasSymbol or SemanticIndex.BuiltinType => 7,
        ModuleSymbol => 9,
        IPropertySymbol => 10,
        EnumSymbol => 13,
        VariantSymbol => 20,
        TypeParameterSymbol => 25,
        DotNetTypeSymbol { Type: var type } => DotNetKind(type),
        INamedTypeSymbol type => DotNetKind(type),
        _ => 6,
    };

    private static int DotNetKind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => 8,
        TypeKind.Enum => 13,
        TypeKind.Struct => 22,
        _ => 7,
    };

    // ---- Signature help (SPEC §16.5) ----

    private static readonly Regex KeywordArgument = new(@"^\s*([A-Za-z_]\w*)\s*=(?!=)");

    private JsonNode? SignatureHelp(JsonNode parameters)
    {
        if (Cursor(parameters) is not var (path, document, cursor))
            return null;
        var text = document.Text;
        var prefix = Scan(text, cursor);
        if (prefix.Place == Place.Comment)
            return null;
        // The innermost parenthesis right after a name: a call's.
        var call = prefix.Open.LastOrDefault(o => o.Bracket == '(' && o.Quote == '\0' && CalleeStart(text, o.Position) >= 0);
        if (call is null)
            return null;
        var calleeStart = CalleeStart(text, call.Position);

        // The callee, as the patched copy resolves it, or else as the document's last check did:
        // a .NET overload isn't chosen when the placeholder is its argument.
        var checkedCopy = Check(path, Patch(text, prefix, WordStart(text, cursor)));
        var (index, callee) = (checkedCopy.Result.Index, checkedCopy.Result.Index?.At(path, calleeStart));
        if (callee is null || callee.Span.Start != calleeStart)
            (index, callee) = (document.Result.Index, document.Result.Index?.At(path, calleeStart));
        if (index is null || callee is null || callee.Span.Start != calleeStart)
            return null;
        // A name where it's defined is a `def` or `class` header, not a call.
        if (index.DefinitionOf(callee.Target) is { } definition && definition.Source.Path == path && definition.Span == callee.Span)
            return null;
        var calleeName = checkedCopy.Source.ToString(callee.Span);
        if (Signatures(callee.Target, calleeName) is not { Count: > 0 } signatures)
            return null;

        // The arguments before the cursor's, and the cursor's own.
        var arguments = call.Arguments.Select((start, i) => text[start..(i + 1 < call.Arguments.Count ? call.Arguments[i + 1] - 1 : cursor)]).ToList();
        var active = signatures.Active;
        var parameter = ActiveParameter(signatures.Items[active], arguments);
        var list = new JsonArray();
        foreach (var signature in signatures.Items)
        {
            list.Add(new JsonObject
            {
                ["label"] = signature.Label,
                ["parameters"] = new JsonArray([.. signature.Parameters.Select(p => (JsonNode)new JsonObject { ["label"] = new JsonArray(p.Start, p.End) })]),
            });
        }
        return new JsonObject { ["signatures"] = list, ["activeSignature"] = active, ["activeParameter"] = parameter };
    }

    // Where the name before a `(` starts, or -1 if no name comes right before it.
    private static int CalleeStart(string text, int paren)
    {
        var end = paren;
        while (end > 0 && text[end - 1] == ' ')
            end--;
        var start = end;
        while (start > 0 && IsNameChar(text[start - 1]))
            start--;
        return start < end && !char.IsAsciiDigit(text[start]) ? start : -1;
    }

    /// <summary>A signature: its label, each parameter's place in it, their names and kinds.</summary>
    private sealed record Signature(string Label, ImmutableArray<(int Start, int End)> Parameters, ImmutableArray<string?> Names, ImmutableArray<ParamKind> Kinds);

    private sealed record SignatureList(List<Signature> Items, int Active)
    {
        public int Count => Items.Count;
    }

    private static SignatureList? Signatures(object target, string name)
    {
        switch (target)
        {
            case FunctionSymbol { Builtin: not null }:
                return null;
            case FunctionSymbol function:
                var (label, ranges) = SemanticIndex.SignatureOf(function);
                return One(new Signature(label, ranges, [.. function.Parameters.Select(p => (string?)p.Name)], function.Kinds));
            case ClassSymbol { IsDataclass: true } dataclass:
                return One(Fields(dataclass.Name, dataclass.AllFields));
            case ClassSymbol { IsProtocol: false } classSymbol:
                var constructor = classSymbol.Constructor;
                var parts = constructor.Parameters.Select((p, i) => SemanticIndex.ParameterText(p, constructor.Kinds[i], constructor.DefaultFor(i) is not null)).ToList();
                var (text, places) = SemanticIndex.ParameterList(classSymbol.Name, parts, constructor.Kinds);
                return One(new Signature(text, places, [.. constructor.Parameters.Select(p => (string?)p.Name)], constructor.Kinds));
            case VariantSymbol variant:
                return One(Fields($"{variant.Enum.Name}.{variant.Name}", variant.Fields));
            case VariableSymbol { Type: FunctionType function }:
                var numbered = function.Parameters.Select((p, i) => $"arg{i}: {p}").ToList();
                // Positional only, though written without `/`: a function type has no parameter names.
                var (head, at) = SemanticIndex.ParameterList(name, numbered, [.. numbered.Select(_ => ParamKind.Normal)]);
                var result = function.Result is NothingType ? "" : $" -> {function.Result}";
                return One(new Signature(head + result, at, [.. numbered.Select(_ => (string?)null)], [.. numbered.Select(_ => ParamKind.PositionalOnly)]));
            case IMethodSymbol method:
                var overloads = method.MethodKind == MethodKind.Constructor
                    ? [.. method.ContainingType.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public)]
                    : method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>()
                        .Where(m => m.DeclaredAccessibility == Accessibility.Public && m.IsStatic == method.IsStatic).ToList();
                var chosen = overloads.FindIndex(m => SymbolEqualityComparer.Default.Equals(m, method));
                return new SignatureList([.. overloads.Select(DotNetSignature)], Math.Max(chosen, 0));
            case DotNetTypeSymbol { Type: var type }:
                var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
                return constructors.Count == 0 ? null : new SignatureList([.. constructors.Select(DotNetSignature)], 0);
            default:
                return null;
        }

        static SignatureList One(Signature signature) => new([signature], 0);

        static Signature Fields(string head, ImmutableArray<FieldSymbol> fields)
        {
            var parts = fields.Select(f => $"{f.Name}: {f.Type}{(f.HasDefault ? " = …" : "")}").ToList();
            var kinds = fields.Select(_ => ParamKind.Normal).ToImmutableArray();
            var (label, ranges) = SemanticIndex.ParameterList(head, parts, kinds);
            return new Signature(label, ranges, [.. fields.Select(f => (string?)f.Name)], kinds);
        }
    }

    // A .NET method's C# signature, as hover writes it, with each parameter's place.
    private static Signature DotNetSignature(IMethodSymbol method)
    {
        var label = method.ToDisplayString();
        var open = label.IndexOf('(');
        var ranges = ImmutableArray.CreateBuilder<(int, int)>();
        var (depth, start) = (0, open + 1);
        for (var i = open + 1; i < label.Length && method.Parameters.Length > 0; i++)
        {
            switch (label[i])
            {
                case '<' or '(' or '[':
                    depth++;
                    break;
                case '>' or ']':
                    depth--;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ',' or ')' when depth == 0:
                    ranges.Add((start, i));
                    start = i + 2;
                    break;
            }
        }
        return new Signature(label, ranges.ToImmutable(), [.. method.Parameters.Select(p => (string?)p.Name)],
            [.. method.Parameters.Select(_ => ParamKind.Normal)]);
    }

    /// <summary>
    /// The parameter the cursor's argument fills, as §4.2 binds arguments, or the number of
    /// parameters when it fills none (SPEC §16.5).
    /// </summary>
    private static int ActiveParameter(Signature signature, List<string> arguments)
    {
        var none = signature.Parameters.Length;
        var kinds = signature.Kinds;
        var current = arguments[^1];
        if (current.TrimStart().StartsWith('*'))
            return none;
        if (KeywordArgument.Match(current) is { Success: true } keyword)
        {
            var named = signature.Names.IndexOf(keyword.Groups[1].Value);
            if (named >= 0 && kinds[named] is not (ParamKind.PositionalOnly or ParamKind.VarArgs or ParamKind.VarKeywords))
                return named;
            var varKeywords = kinds.IndexOf(ParamKind.VarKeywords);
            return varKeywords >= 0 ? varKeywords : none;
        }
        var before = arguments[..^1];
        if (before.Any(a => a.TrimStart().StartsWith('*')))
            return none;
        var position = before.Count(a => !KeywordArgument.IsMatch(a));
        var positional = Enumerable.Range(0, kinds.Length).Where(i => kinds[i] is ParamKind.Normal or ParamKind.PositionalOnly).ToList();
        if (position < positional.Count)
            return positional[position];
        var varArgs = kinds.IndexOf(ParamKind.VarArgs);
        return varArgs >= 0 ? varArgs : none;
    }
}
