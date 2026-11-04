using System.Text;
using System.Text.Json.Nodes;
using Taipan.Cli;

namespace Taipan.Tests;

/// <summary>A client's messages, sent all at once, and what the server sent back.</summary>
internal sealed class LspSession
{
    private readonly List<JsonObject> _messages = [];
    private readonly List<JsonObject> _received = [];
    private int _id;

    public int ExitCode { get; private set; }

    public LspSession() : this(new JsonObject())
    {
    }

    /// <summary>A session whose client declares <paramref name="capabilities"/>, such as renaming files (SPEC §16.6).</summary>
    public LspSession(JsonObject capabilities) => Request("initialize", new JsonObject { ["capabilities"] = capabilities.DeepClone() }).Notify("initialized", new JsonObject());

    public LspSession Open(string path, string text) => Notify("textDocument/didOpen", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri, ["languageId"] = "taipan", ["version"] = 1, ["text"] = text },
    });

    public LspSession Change(string path, string text) => Notify("textDocument/didChange", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri, ["version"] = 2 },
        ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
    });

    public LspSession Close(string path) => Notify("textDocument/didClose", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
    });

    // A request at the start of `marker` in `text`.
    public LspSession Hover(string path, string text, string marker) => Request("textDocument/hover", At(path, text, marker));

    public LspSession Definition(string path, string text, string marker) => Request("textDocument/definition", At(path, text, marker));

    // Since M32 (SPEC §16.4–§16.7). A `|` in `marker` puts the position there instead of at its start.
    public LspSession Completion(string path, string text, int offset) => Request("textDocument/completion", At(path, text, offset));

    public LspSession SignatureHelp(string path, string text, int offset) => Request("textDocument/signatureHelp", At(path, text, offset));

    public LspSession References(string path, string text, string marker, bool includeDeclaration = true)
    {
        var parameters = At(path, text, marker);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = includeDeclaration };
        return Request("textDocument/references", parameters);
    }

    public LspSession Rename(string path, string text, string marker, string newName)
    {
        var parameters = At(path, text, marker);
        parameters["newName"] = newName;
        return Request("textDocument/rename", parameters);
    }

    public LspSession PrepareRename(string path, string text, string marker) => Request("textDocument/prepareRename", At(path, text, marker));

    public LspSession SemanticTokens(string path) => Request("textDocument/semanticTokens/full", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
    });

    private static JsonObject At(string path, string text, string marker)
    {
        var bar = marker.IndexOf('|');
        var offset = text.IndexOf(bar < 0 ? marker : marker.Remove(bar, 1), StringComparison.Ordinal);
        Assert.True(offset >= 0, marker);
        return At(path, text, offset + Math.Max(bar, 0));
    }

    private static JsonObject At(string path, string text, int offset)
    {
        var before = text[..offset];
        var line = before.Count(c => c == '\n');
        return new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = offset - (before.LastIndexOf('\n') + 1) },
        };
    }

    public LspSession Request(string method, JsonObject parameters)
    {
        _messages.Add(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters });
        return this;
    }

    public LspSession Notify(string method, JsonObject parameters)
    {
        _messages.Add(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
        return this;
    }

    public LspSession Run(bool shutdown = true)
    {
        if (shutdown)
            Request("shutdown", new JsonObject());
        Notify("exit", new JsonObject());
        var input = new MemoryStream();
        foreach (var message in _messages)
        {
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            input.Write(body);
        }
        input.Position = 0;
        var output = new MemoryStream();
        ExitCode = new LanguageServer(input, output).Run();

        // Each message is `Content-Length: n`, a blank line, and n bytes of JSON.
        var bytes = output.ToArray();
        for (var position = 0; position < bytes.Length;)
        {
            var headerEnd = bytes.AsSpan(position).IndexOf("\r\n\r\n"u8) + position;
            var length = int.Parse(Encoding.ASCII.GetString(bytes, position, headerEnd - position)["Content-Length: ".Length..]);
            _received.Add(JsonNode.Parse(bytes.AsSpan(headerEnd + 4, length))!.AsObject());
            position = headerEnd + 4 + length;
        }
        return this;
    }

    /// <summary>The responses to the script's own requests, in order: not initialize's or shutdown's.</summary>
    public List<JsonObject> Responses() =>
        [.. _received.Where(m => m["id"] is not null && MethodOf(m["id"]!.GetValue<int>()) is not ("initialize" or "shutdown"))];

    public JsonNode Initialize() => _received.First(m => m["id"]?.GetValue<int>() == 1)["result"]!;

    private string MethodOf(int id) => _messages.First(m => m["id"]?.GetValue<int>() == id)["method"]!.GetValue<string>();

    public List<JsonObject> All(string method) => [.. _received.Where(m => m["method"]?.GetValue<string>() == method).Select(m => m["params"]!.AsObject())];

    public JsonArray Diagnostics(string path) =>
        All("textDocument/publishDiagnostics").Last(p => new Uri(p["uri"]!.GetValue<string>()).LocalPath == path)["diagnostics"]!.AsArray();
}
