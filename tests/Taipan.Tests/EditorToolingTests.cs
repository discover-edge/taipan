using System.Text.Json.Nodes;

namespace Taipan.Tests;

/// <summary>
/// M44's language server features (SPEC §16.4, §16.6, §16.10, §16.11): formatting, code actions,
/// completing .NET namespaces and types, and renaming a module with its file, in a temporary module root.
/// </summary>
public sealed class EditorToolingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"taipan-editor-{Guid.NewGuid():N}");

    public EditorToolingTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static JsonObject Request(string method, string path, JsonObject? extra = null)
    {
        var parameters = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = new Uri(path).AbsoluteUri } };
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value?.DeepClone();
        return parameters;
    }

    private static JsonObject Whole(string text) => new()
    {
        ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
        ["end"] = new JsonObject { ["line"] = text.Count(c => c == '\n'), ["character"] = 0 },
    };

    // ---- Formatting (SPEC §16.10) ----

    [Fact]
    public void FormatsTheWholeDocument()
    {
        var text = "let x=1\nprint( x )\n";
        var path = Write("main.tpn", text);
        var broken = Write("broken.tpn", "let y = (1\n");
        var responses = new LspSession().Open(path, text).Open(broken, "let y = (1\n")
            .Request("textDocument/formatting", Request("textDocument/formatting", path, new JsonObject { ["options"] = new JsonObject { ["tabSize"] = 2, ["insertSpaces"] = true } }))
            .Request("textDocument/formatting", Request("textDocument/formatting", broken))
            .Run().Responses();
        var edit = responses[0]["result"]!.AsArray().Single()!;
        Assert.Equal("let x = 1\nprint(x)\n", edit["newText"]!.GetValue<string>());
        Assert.Equal(0, edit["range"]!["start"]!["line"]!.GetValue<int>());
        Assert.Equal(2, edit["range"]!["end"]!["line"]!.GetValue<int>());
        Assert.Empty(responses[1]["result"]!.AsArray());
        Assert.True(new LspSession().Run().Initialize()["capabilities"]!["documentFormattingProvider"]!.GetValue<bool>());
    }

    // ---- Code actions (SPEC §16.11) ----

    private List<JsonNode> Actions(string path, string text)
    {
        var responses = new LspSession().Open(path, text)
            .Request("textDocument/codeAction", Request("textDocument/codeAction", path, new JsonObject { ["range"] = Whole(text), ["context"] = new JsonObject { ["diagnostics"] = new JsonArray() } }))
            .Run().Responses();
        return [.. responses.Single()["result"]!.AsArray().Select(a => a!)];
    }

    private static string Apply(string text, JsonNode action)
    {
        var edits = action["edit"]!["changes"]!.AsObject().Single().Value!.AsArray()
            .Select(e => (Start: Offset(text, e!["range"]!["start"]!), End: Offset(text, e!["range"]!["end"]!), Text: e!["newText"]!.GetValue<string>()))
            .OrderByDescending(e => e.Start);
        foreach (var (start, end, newText) in edits)
            text = text[..start] + newText + text[end..];
        return text;
    }

    private static int Offset(string text, JsonNode position)
    {
        var line = position["line"]!.GetValue<int>();
        var offset = 0;
        for (var i = 0; i < line; i++)
            offset = text.IndexOf('\n', offset) + 1;
        return offset + position["character"]!.GetValue<int>();
    }

    [Fact]
    public void ImportsAnUndefinedNameFromAModule()
    {
        Write("shapes.tpn", "def area(r: float) -> float:\n    return r * r\n\nenum Kind:\n    Round\n");
        var text = "# Areas.\nimport sys\n\nprint(area(2.0), Round, len(sys.argv))\n";
        var path = Write("main.tpn", text);
        var actions = Actions(path, text);
        Assert.Equal(["Import 'area' from 'shapes'", "Import 'Round' from 'shapes'"], actions.Select(a => a["title"]!.GetValue<string>()));
        Assert.All(actions, a => Assert.Equal("quickfix", a["kind"]!.GetValue<string>()));
        Assert.Equal("TP0201", actions[0]["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("# Areas.\nimport sys\nfrom shapes import area\n\nprint(area(2.0), Round, len(sys.argv))\n", Apply(text, actions[0]));
    }

    [Fact]
    public void ImportsAModule()
    {
        Write("shapes.tpn", "def area(r: float) -> float:\n    return r * r\n");
        var text = "# Comments first.\nprint(shapes.area(1.0), len(sys.argv))\n";
        var path = Write("main.tpn", text);
        var actions = Actions(path, text);
        Assert.Equal(["Import 'shapes'", "Import 'sys'"], actions.Select(a => a["title"]!.GetValue<string>()));
        Assert.Equal("# Comments first.\nimport shapes\nprint(shapes.area(1.0), len(sys.argv))\n", Apply(text, actions[0]));
    }

    [Fact]
    public void AddsAndRemovesOverride()
    {
        var text = """
            class Animal:
                def speak(self) -> str:
                    return "..."

            class Dog(Animal):
                def speak(self) -> str:
                    return "woof"

                @staticmethod
                @override
                def make() -> Dog:
                    return Dog()

            """;
        var path = Write("main.tpn", text);
        var actions = Actions(path, text);
        Assert.Equal(["Mark 'speak' '@override'", "Remove '@override' from 'make'"], actions.Select(a => a["title"]!.GetValue<string>()));
        Assert.Contains("class Dog(Animal):\n    @override\n    def speak(self) -> str:", Apply(text, actions[0]));
        Assert.Contains("    @staticmethod\n    def make() -> Dog:", Apply(text, actions[1]));
    }

    [Fact]
    public void WrapsAnIteratorInList()
    {
        var text = "let xs = iter([1, 2])\nprint(len(xs))\n";
        var path = Write("main.tpn", text);
        var action = Actions(path, text).Single();
        Assert.Equal("Convert to 'list(…)'", action["title"]!.GetValue<string>());
        Assert.Equal("let xs = iter([1, 2])\nprint(len(list(xs)))\n", Apply(text, action));
    }

    // ---- Completion (SPEC §16.4) ----

    private List<string> Complete(string text)
    {
        var path = Write("main.tpn", text);
        var session = new LspSession().Open(path, text).Completion(path, text, text.Length).Run();
        return [.. session.Responses().Single()["result"]!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>())];
    }

    [Fact]
    public void CompletesDotNetNamespacesAndTypes()
    {
        Assert.Contains("System", Complete("from dotnet."));
        Assert.Contains("Microsoft", Complete("import dotnet."));
        var inSystem = Complete("from dotnet.System.");
        Assert.Contains("IO", inSystem);
        Assert.Contains("Collections", inSystem);
        Assert.DoesNotContain("String", inSystem);
        var types = Complete("from dotnet.System.Collections.Generic import ");
        Assert.Contains("HashSet", types);
        Assert.Contains("List", types);
        Assert.Single(types, t => t == "KeyValuePair");
        Assert.Contains("Path", Complete("from dotnet.System.IO import File, "));
    }

    [Fact]
    public void CompletesPythonModules()
    {
        var modules = Complete("from python import ");
        if (!PythonAvailability.Available())
        {
            Assert.Empty(modules);
            return;
        }
        Assert.Contains("json", modules);
        Assert.Contains("sys", modules);
        Assert.DoesNotContain(modules, m => m.StartsWith('_'));
    }

    // ---- Renaming a module (SPEC §16.6) ----

    private static readonly JsonObject RenamesFiles = new()
    {
        ["workspace"] = new JsonObject
        {
            ["workspaceEdit"] = new JsonObject { ["documentChanges"] = true, ["resourceOperations"] = new JsonArray("create", "rename", "delete") },
        },
    };

    [Fact]
    public void RenamesAModuleAndItsFile()
    {
        var shapes = Write("shapes.tpn", "def area(r: float) -> float:\n    return r * r\n");
        Write("other.tpn", "import shapes\n\ndef twice(r: float) -> float:\n    return 2.0 * shapes.area(r)\n");
        var text = "import shapes\nfrom shapes import area\nprint(shapes.area(1.0), area(2.0))\n";
        var main = Write("main.tpn", text);
        var response = new LspSession(RenamesFiles).Open(main, text).Rename(main, text, "shapes.area(1", "geometry").Run().Responses().Single();
        var changes = response["result"]!["documentChanges"]!.AsArray();
        Assert.Equal("rename", changes[0]!["kind"]!.GetValue<string>());
        Assert.Equal(new Uri(shapes).AbsoluteUri, changes[0]!["oldUri"]!.GetValue<string>());
        Assert.Equal(new Uri(Path.Combine(_directory, "geometry.tpn")).AbsoluteUri, changes[0]!["newUri"]!.GetValue<string>());
        var edited = changes.Skip(1).ToDictionary(c => Path.GetFileName(new Uri(c!["textDocument"]!["uri"]!.GetValue<string>()).LocalPath), c => c!["edits"]!.AsArray().Count);
        Assert.Equal(new Dictionary<string, int> { ["main.tpn"] = 3, ["other.tpn"] = 2 }, edited);
    }

    [Fact]
    public void RefusesAModuleRenameItCantMake()
    {
        Write("shapes.tpn", "def area(r: float) -> float:\n    return r * r\n");
        Write("geometry.tpn", "def perimeter(r: float) -> float:\n    return r\n");
        var text = "import shapes\nimport sys\nprint(shapes.area(1.0), sys.argv)\n";
        var main = Write("main.tpn", text);
        string Error(LspSession session) => session.Run().Responses().Single()["error"]!["message"]!.GetValue<string>();
        Assert.Equal("'geometry.tpn' already exists", Error(new LspSession(RenamesFiles).Open(main, text).Rename(main, text, "shapes.area", "geometry")));
        Assert.Equal("cannot rename the module 'shapes': the editor can't rename files", Error(new LspSession().Open(main, text).Rename(main, text, "shapes.area", "forms")));
        Assert.Equal("cannot rename the built-in 'sys'", Error(new LspSession(RenamesFiles).Open(main, text).Rename(main, text, "sys.argv", "system")));
    }
}
