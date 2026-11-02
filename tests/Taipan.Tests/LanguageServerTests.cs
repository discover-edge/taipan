using System.Text;
using System.Text.Json.Nodes;
using Taipan.Cli;

namespace Taipan.Tests;

/// <summary>
/// M24 acceptance (SPEC §16): scripted <c>taipan lsp</c> sessions. Each script is the client's
/// messages, ending with <c>shutdown</c> and <c>exit</c>; the server's replies are read back.
/// </summary>
public class LanguageServerTests
{
    private static readonly string Root = RepoPaths.Root;

    /// <summary>The golden cases that <c>taipan check</c> rejects, or warns about, without running.</summary>
    public static TheoryData<string> CheckedCases()
    {
        var data = new TheoryData<string>();
        foreach (string path in GoldenTests.Cases())
        {
            string[] other = [".out", ".library", ".native", ".python-extension"];
            if (File.Exists(Path.Combine(Root, Path.ChangeExtension(path, ".err"))) && !other.Any(s => File.Exists(Path.Combine(Root, Path.ChangeExtension(path, s)))))
                data.Add(path);
        }
        return data;
    }

    /// <summary>
    /// A document's published diagnostics, written back in <c>taipan check</c>'s format (lines and
    /// code-point columns from 1), are the lines of its <c>.err</c> about that file (SPEC §16.2).
    /// </summary>
    [Theory]
    [MemberData(nameof(CheckedCases))]
    public void DiagnosticsMatchCheck(string casePath)
    {
        var path = Path.Combine(Root, casePath);
        var text = File.ReadAllText(path);
        var session = new Session().Open(path, text).Run();

        var published = session.Diagnostics(path);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var actual = new StringBuilder();
        foreach (var diagnostic in published)
        {
            // The server checks a document by its absolute path, where `taipan check` was given a relative one.
            var message = diagnostic!["message"]!.GetValue<string>().Replace(Root + Path.DirectorySeparatorChar, "");
            if (diagnostic["code"] is null)
            {
                actual.Append($"taipan: {message}\n");
                continue;
            }
            var start = diagnostic["range"]!["start"]!;
            var line = start["line"]!.GetValue<int>();
            var column = CodePoints(lines[line], start["character"]!.GetValue<int>()) + 1;
            var severity = diagnostic["severity"]!.GetValue<int>() == 1 ? "error" : "warning";
            var parts = message.Split("\nhelp: ");
            actual.Append($"{casePath}:{line + 1}:{column}: {severity} {diagnostic["code"]}: {parts[0]}\n");
            if (parts.Length > 1)
                actual.Append($"  help: {parts[1]}\n");
        }
        Assert.Equal(ForFile(File.ReadAllText(Path.ChangeExtension(path, ".err")), casePath), actual.ToString());
    }

    // The .err lines (with their help lines) about the case's own file, and any `taipan:` failure.
    private static string ForFile(string err, string casePath)
    {
        var kept = new StringBuilder();
        var keeping = false;
        foreach (var line in err.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("  help: ", StringComparison.Ordinal))
                keeping = line.StartsWith($"{casePath}:", StringComparison.Ordinal) || line.StartsWith("taipan: ", StringComparison.Ordinal);
            if (keeping)
                kept.Append(line).Append('\n');
        }
        return kept.ToString();
    }

    private static int CodePoints(string line, int utf16) => Enumerable.Range(0, utf16).Count(i => !char.IsLowSurrogate(line[i]));

    [Fact]
    public void DiagnosticsFollowUnsavedChanges()
    {
        var path = Path.Combine(Root, "tests", "cases", "m24", "navigation", "main.tpn");
        var text = File.ReadAllText(path);
        var changed = text + "let bad: int = \"x\"\n";
        var session = new Session().Open(path, text).Change(path, changed).Close(path).Run();

        var published = session.All("textDocument/publishDiagnostics");
        Assert.Equal(3, published.Count);
        Assert.Empty(published[0]["diagnostics"]!.AsArray());
        Assert.Empty(published[2]["diagnostics"]!.AsArray());
        var error = Assert.Single(published[1]["diagnostics"]!.AsArray())!;
        Assert.Equal("TP0200", error["code"]!.GetValue<string>());
        Assert.Equal(text.Count(c => c == '\n'), error["range"]!["start"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public void DiagnosticRangesCountUtf16()
    {
        // A document that's open but not saved, with a name after an emoji: two UTF-16 units, but
        // one code point, so `taipan check` would say column 18 and the server says character 18.
        var path = Path.Combine(Root, "tests", "cases", "m24", "unsaved.tpn");
        var session = new Session().Open(path, "let pair = (\"🐍\", nope)\n").Run();
        var error = Assert.Single(session.Diagnostics(path))!;
        Assert.Equal("TP0201", error["code"]!.GetValue<string>());
        Assert.Equal(0, error["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Equal(18, error["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(22, error["range"]!["end"]!["character"]!.GetValue<int>());
    }

    [Fact]
    public void HoverShowsInferredTypes()
    {
        var path = Path.Combine(Root, "tests", "cases", "m24", "navigation", "main.tpn");
        var text = File.ReadAllText(path);
        var session = new Session().Open(path, text)
            .Hover(path, text, "found =")
            .Hover(path, text, "sum += area")
            .Hover(path, text, "area(item)")
            .Hover(path, text, "moved()")
            .Hover(path, text, "SIDES)")
            .Hover(path, text, "shapes.Shape.Square")
            .Hover(path, text, "Point(0.0)")
            .Hover(path, text, "print(")
            .Run();
        Assert.Equal(
            [
                "let found: float",
                "var sum: float",
                "def area(shape: Shape) -> float",
                "def Point.moved(self, by: float = …) -> Point",
                "const SIDES: int",
                "module shapes",
                "@dataclass class Point",
                null,
            ],
            session.Responses().Select(r => r["result"]?["contents"]?["value"]?.GetValue<string>()?.Split('\n')[1]));
    }

    [Fact]
    public void HoverShowsClassMembers()
    {
        var path = Path.Combine(Root, "tests", "cases", "m28", "members.tpn");
        var text = File.ReadAllText(path);
        var session = new Session().Open(path, text)
            .Hover(path, text, "fahrenheit)")
            .Hover(path, text, "freezing(), t")
            .Hover(path, text, "parse(\"21.5\")")
            .Hover(path, text, "created, Reading")
            .Run();
        Assert.Equal(
            [
                "@property def Temperature.fahrenheit(self) -> float",
                "@staticmethod def Temperature.freezing() -> Temperature",
                "@classmethod def Temperature.parse(cls, text: str) -> Temperature",
                "Temperature.created: ClassVar[int]",
            ],
            session.Responses().Select(r => r["result"]?["contents"]?["value"]?.GetValue<string>()?.Split('\n')[1]));
    }

    [Fact]
    public void HoverShowsDotNetSignatures()
    {
        var path = Path.Combine(Root, "tests", "cases", "m21", "strings.tpn");
        var text = File.ReadAllText(path);
        var session = new Session().Open(path, text)
            .Hover(path, text, "StringBuilder()")
            .Hover(path, text, "Append(")
            .Definition(path, text, "Append(")
            .Run();
        var hovers = session.Responses().Take(2).Select(r => r["result"]!["contents"]!["value"]!.GetValue<string>().Split('\n')[1]).ToList();
        Assert.Equal(".NET System.Text.StringBuilder", hovers[0]);
        Assert.StartsWith(".NET System.Text.StringBuilder.Append(", hovers[1]);
        Assert.Null(session.Responses()[2]["result"]);
    }

    [Fact]
    public void DefinitionReachesOtherModules()
    {
        var path = Path.Combine(Root, "tests", "cases", "m24", "navigation", "main.tpn");
        var shapes = Path.Combine(Root, "tests", "cases", "m24", "navigation", "shapes.tpn");
        var text = File.ReadAllText(path);
        var session = new Session().Open(path, text)
            .Definition(path, text, "area(item)")
            .Definition(path, text, "shapes\nfrom")
            .Definition(path, text, "item in items")
            .Definition(path, text, "item)")
            .Definition(path, text, "SIDES)")
            .Definition(path, text, "print(")
            .Run();
        var results = session.Responses().Select(r => r["result"]).ToList();
        Assert.Equal((shapes, "area"), Located(results[0]));
        Assert.Equal((shapes, ""), Located(results[1]));
        Assert.Equal((path, "item"), Located(results[2]));
        Assert.Equal(results[2]!.ToJsonString(), results[3]!.ToJsonString());
        Assert.Equal((shapes, "SIDES"), Located(results[4]));
        Assert.Null(results[5]);
    }

    // A definition's file, and the text its range covers.
    private static (string Path, string Text) Located(JsonNode? location)
    {
        var path = new Uri(location!["uri"]!.GetValue<string>()).LocalPath;
        var lines = File.ReadAllText(path).Split('\n');
        var start = location["range"]!["start"]!;
        var end = location["range"]!["end"]!;
        var line = lines[start["line"]!.GetValue<int>()];
        return (path, line[start["character"]!.GetValue<int>()..end["character"]!.GetValue<int>()]);
    }

    [Fact]
    public void LifecycleAndUnsupportedRequests()
    {
        var session = new Session().Request("textDocument/completion", new JsonObject()).Run();
        Assert.Equal(0, session.ExitCode);
        var initialize = session.Initialize()["capabilities"]!;
        Assert.Equal(1, initialize["textDocumentSync"]!["change"]!.GetValue<int>());
        Assert.True(initialize["hoverProvider"]!.GetValue<bool>());
        Assert.True(initialize["definitionProvider"]!.GetValue<bool>());
        Assert.Equal(-32601, session.Responses()[0]["error"]!["code"]!.GetValue<int>());

        Assert.Equal(1, new Session().Run(shutdown: false).ExitCode);
    }

    /// <summary>The real command, over its process's stdin and stdout.</summary>
    [Fact]
    public async Task ServesOverStdio()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", [Path.Combine(AppContext.BaseDirectory, "taipan.dll"), "lsp", "--stdio"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        foreach (var message in (string[])[
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""",
            """{"jsonrpc":"2.0","method":"exit"}"""])
        {
            await process.StandardInput.WriteAsync($"Content-Length: {Encoding.UTF8.GetByteCount(message)}\r\n\r\n{message}");
        }
        await process.StandardInput.FlushAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await process.StandardError.ReadToEndAsync());
        Assert.StartsWith("Content-Length: ", output);
        Assert.Contains("\"hoverProvider\":true", output);
        Assert.Contains("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":null}", output);
    }

    /// <summary>A client's messages, sent all at once, and what the server sent back.</summary>
    private sealed class Session
    {
        private readonly List<JsonObject> _messages = [];
        private readonly List<JsonObject> _received = [];
        private int _id;

        public int ExitCode { get; private set; }

        public Session() => Request("initialize", new JsonObject { ["capabilities"] = new JsonObject() }).Notify("initialized", new JsonObject());

        public Session Open(string path, string text) => Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri, ["languageId"] = "taipan", ["version"] = 1, ["text"] = text },
        });

        public Session Change(string path, string text) => Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });

        public Session Close(string path) => Notify("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
        });

        // A request at the start of `marker` in `text`.
        public Session Hover(string path, string text, string marker) => Request("textDocument/hover", At(path, text, marker));

        public Session Definition(string path, string text, string marker) => Request("textDocument/definition", At(path, text, marker));

        private static JsonObject At(string path, string text, string marker)
        {
            var offset = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(offset >= 0, marker);
            var before = text[..offset];
            var line = before.Count(c => c == '\n');
            return new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri },
                ["position"] = new JsonObject { ["line"] = line, ["character"] = offset - (before.LastIndexOf('\n') + 1) },
            };
        }

        public Session Request(string method, JsonObject parameters)
        {
            _messages.Add(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = method, ["params"] = parameters });
            return this;
        }

        public Session Notify(string method, JsonObject parameters)
        {
            _messages.Add(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
            return this;
        }

        public Session Run(bool shutdown = true)
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
}
