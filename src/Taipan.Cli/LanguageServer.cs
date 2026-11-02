using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Taipan.Compiler.Diagnostics;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Text;

namespace Taipan.Cli;

/// <summary>
/// <c>taipan lsp</c> (SPEC §16): a Language Server Protocol server over a pair of streams, speaking
/// JSON-RPC 2.0 framed by <c>Content-Length</c> headers. It checks each open document as a main
/// file, as <c>taipan check</c> would, and answers hover and definition from what the binder resolved.
/// </summary>
public sealed class LanguageServer(Stream input, Stream output)
{
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;

    private sealed record Document(string Text, SourceText Source, CheckResult Result);

    // Open documents by path, with their latest check.
    private readonly Dictionary<string, Document> _documents = [];
    private bool _shutdown;

    /// <summary>Serves until <c>exit</c>, or the end of input; returns the exit code (SPEC §16.1).</summary>
    public int Run()
    {
        while (Read() is { } message)
        {
            var method = message["method"]?.GetValue<string>();
            var id = message["id"];
            if (method == "exit")
                return _shutdown ? 0 : 1;
            try
            {
                var result = Handle(method, message["params"], out var handled);
                if (id is null)
                    continue;
                if (handled)
                    Respond(id, result);
                else
                    RespondError(id, MethodNotFound, $"unsupported method '{method}'");
            }
            catch (Exception e) when (id is not null)
            {
                RespondError(id, InternalError, e.Message);
            }
        }
        return _shutdown ? 0 : 1;
    }

    private JsonNode? Handle(string? method, JsonNode? parameters, out bool handled)
    {
        handled = true;
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["positionEncoding"] = "utf-16",
                        ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1 },
                        ["hoverProvider"] = true,
                        ["definitionProvider"] = true,
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = "taipan" },
                };
            case "shutdown":
                _shutdown = true;
                return null;
            case "textDocument/didOpen":
                Open(parameters!["textDocument"]!["uri"]!.GetValue<string>(), parameters["textDocument"]!["text"]!.GetValue<string>());
                return null;
            case "textDocument/didChange":
                // Full sync: the last change is the whole text (SPEC §16.1).
                var changes = parameters!["contentChanges"]!.AsArray();
                Open(parameters["textDocument"]!["uri"]!.GetValue<string>(), changes[^1]!["text"]!.GetValue<string>());
                return null;
            case "textDocument/didClose":
                Close(parameters!["textDocument"]!["uri"]!.GetValue<string>());
                return null;
            case "textDocument/hover":
                return Hover(parameters!);
            case "textDocument/definition":
                return Definition(parameters!);
            default:
                // An unsupported request is MethodNotFound; an unsupported notification is ignored (SPEC §16.1).
                handled = false;
                return null;
        }
    }

    // ---- Documents and diagnostics (SPEC §16.2) ----

    private void Open(string uri, string text)
    {
        if (PathOf(uri) is not { } path)
            return;
        _documents[path] = Check(path, text);
        Publish(path);
        // Another open document may import this one.
        foreach (var other in _documents.Keys.Where(p => p != path).ToList())
        {
            _documents[other] = Check(other, _documents[other].Text);
            Publish(other);
        }
    }

    private Document Check(string path, string text)
    {
        var openText = (string file) => _documents.TryGetValue(Path.GetFullPath(file), out var open) ? open.Text : null;
        // This document's own text, even before it's stored.
        string? Read(string file) => Path.GetFullPath(file) == path ? text : openText(file);
        return new Document(text, SourceText.From(text, path), Checker.Check(path, Target.Program, Read));
    }

    private void Close(string uri)
    {
        if (PathOf(uri) is not { } path || !_documents.Remove(path))
            return;
        Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = UriOf(path), ["diagnostics"] = new JsonArray() });
    }

    private void Publish(string path)
    {
        var document = _documents[path];
        var diagnostics = new JsonArray();
        foreach (var diagnostic in document.Result.Diagnostics.Where(d => d.Source is { } source && Path.GetFullPath(source.Path) == path))
            diagnostics.Add(ToLsp(diagnostic, diagnostic.Source!));
        // A problem that isn't in the source is one error at the document's start.
        if (document.Result.Failure is { } failure)
        {
            diagnostics.Add(new JsonObject
            {
                ["range"] = Range(document.Source, default),
                ["severity"] = 1,
                ["source"] = "taipan",
                ["message"] = failure,
            });
        }
        Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = UriOf(path), ["diagnostics"] = diagnostics });
    }

    private static JsonObject ToLsp(Diagnostic diagnostic, SourceText source) => new()
    {
        ["range"] = Range(source, diagnostic.Span),
        ["severity"] = diagnostic.IsWarning ? 2 : 1,
        ["code"] = diagnostic.Code,
        ["source"] = "taipan",
        ["message"] = diagnostic.Help is null ? diagnostic.Message : $"{diagnostic.Message}\nhelp: {diagnostic.Help}",
    };

    // ---- Hover and definition (SPEC §16.3) ----

    private JsonNode? Hover(JsonNode parameters)
    {
        if (Find(parameters) is not var (_, occurrence) || SemanticIndex.Describe(occurrence.Target) is not { } description)
            return null;
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = $"```taipan\n{description}\n```" },
            ["range"] = Range(occurrence.Source, occurrence.Span),
        };
    }

    private JsonNode? Definition(JsonNode parameters)
    {
        if (Find(parameters) is not var (document, occurrence) || document.Result.Index?.DefinitionOf(occurrence.Target) is not { } location)
            return null;
        return new JsonObject { ["uri"] = UriOf(Path.GetFullPath(location.Source.Path)), ["range"] = Range(location.Source, location.Span) };
    }

    // The name at a request's position, in an open document that parsed.
    private (Document, SemanticIndex.Occurrence)? Find(JsonNode parameters)
    {
        if (PathOf(parameters["textDocument"]!["uri"]!.GetValue<string>()) is not { } path
            || !_documents.TryGetValue(path, out var document) || document.Result.Index is not { } index)
            return null;
        var position = document.Source.GetPosition(parameters["position"]!["line"]!.GetValue<int>(), parameters["position"]!["character"]!.GetValue<int>());
        return index.At(path, position) is { } occurrence ? (document, occurrence) : null;
    }

    private static JsonObject Range(SourceText source, TextSpan span) => new()
    {
        ["start"] = Position(source, span.Start),
        ["end"] = Position(source, span.End),
    };

    private static JsonObject Position(SourceText source, int offset)
    {
        var (line, character) = source.GetUtf16Position(offset);
        return new JsonObject { ["line"] = line, ["character"] = character };
    }

    // Only file:// documents are checked (SPEC §16.1).
    private static string? PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? Path.GetFullPath(parsed.LocalPath) : null;

    private static string UriOf(string path) => new Uri(path).AbsoluteUri;

    // ---- JSON-RPC over Content-Length framing ----

    private JsonNode? Read()
    {
        var length = -1;
        while (ReadHeaderLine() is { } line)
        {
            if (line.Length == 0)
            {
                if (length < 0)
                    continue;
                var body = new byte[length];
                input.ReadExactly(body);
                return JsonNode.Parse(body);
            }
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());
        }
        return null;
    }

    // A header line, without its CRLF; null at the end of input.
    private string? ReadHeaderLine()
    {
        var bytes = new List<byte>();
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0)
                return null;
            if (next == '\n')
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add((byte)next);
        }
    }

    private void Respond(JsonNode id, JsonNode? result) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

    private void RespondError(JsonNode id, int code, string message) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private void Notify(string method, JsonNode parameters) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private void Write(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        output.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        output.Write(body);
        output.Flush();
    }
}
