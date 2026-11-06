using System.Text.Json.Nodes;

namespace Taipan.Tests;

/// <summary>
/// M32 acceptance (SPEC §16.4–§16.7): completion, signature help, references, rename and semantic
/// tokens, in scripted <c>taipan lsp</c> sessions over <c>tests/cases/m32/editor</c>.
/// </summary>
public class EditorTests
{
    private static readonly string Directory = Path.Combine(RepoPaths.Root, "tests", "cases", "m32", "editor");
    private static readonly string Main = Path.Combine(Directory, "main.tpn");
    private static readonly string Geometry = Path.Combine(Directory, "geometry.tpn");
    private static readonly string Report = Path.Combine(Directory, "report.tpn");
    private static readonly string MainText = File.ReadAllText(Main);

    // ---- Completion (SPEC §16.4) ----

    // The labels completion offers with `typed` inserted after the line starting with `after`, at its end.
    private static List<JsonNode> Complete(string after, string typed)
    {
        var (text, cursor) = Insert(MainText, after, typed);
        var session = new LspSession().Open(Main, text).Completion(Main, text, cursor).Run();
        return [.. session.Responses().Single()["result"]!["items"]!.AsArray().Select(i => i!)];
    }

    // The text with `typed` on a line of its own after the line starting with `after`, and where it ends.
    private static (string Text, int Cursor) Insert(string text, string after, string typed)
    {
        var start = text.IndexOf(after, StringComparison.Ordinal);
        Assert.True(start >= 0, after);
        var end = text.IndexOf('\n', start) + 1;
        return (text[..end] + typed + "\n" + text[end..], end + typed.Length);
    }

    private static List<string> Labels(List<JsonNode> items) => [.. items.Select(i => i["label"]!.GetValue<string>())];

    [Fact]
    public void CompletesLocalsInScope()
    {
        var items = Complete("    var sum = 0.0", "    s");
        var labels = Labels(items);
        // Locals before globals before built-ins before keywords; `shape` isn't bound yet.
        Assert.DoesNotContain("shape", labels);
        Assert.True(labels.IndexOf("sum") < labels.IndexOf("scale"));
        Assert.True(labels.IndexOf("shapes") < labels.IndexOf("total"));
        Assert.True(labels.IndexOf("scale") < labels.IndexOf("print"));
        Assert.True(labels.IndexOf("print") < labels.IndexOf("while"));
        // Functions can't see top-level variables (SPEC §4.1).
        Assert.DoesNotContain("corner", labels);
        var sum = items.Single(i => i["label"]!.GetValue<string>() == "sum");
        Assert.Equal(6, sum["kind"]!.GetValue<int>());
        Assert.Equal("var sum: float", sum["detail"]!.GetValue<string>());
    }

    [Fact]
    public void CompletesFieldsAfterDot()
    {
        var items = Complete("let moved = ", "print(corner.");
        Assert.Equal(["scaled", "x", "y"], Labels(items).Order());
        Assert.Equal("Point.x: float", items.Single(i => i["label"]!.GetValue<string>() == "x")["detail"]!.GetValue<string>());
        Assert.Equal(5, items.Single(i => i["label"]!.GetValue<string>() == "x")["kind"]!.GetValue<int>());
        Assert.Equal(2, items.Single(i => i["label"]!.GetValue<string>() == "scaled")["kind"]!.GetValue<int>());
    }

    [Fact]
    public void CompletesInheritedMembersButNotSpecialMethods()
    {
        var items = Complete("    for shape in shapes:", "        let first = Square(1.0).");
        Assert.Equal(["area", "side", "sides"], Labels(items).Order());
    }

    [Fact]
    public void CompletesAModulesNames()
    {
        var items = Complete("let moved = ", "geometry.");
        Assert.Equal(["Color", "Custom", "Point", "Red", "Shape", "Square", "UNITS", "scale"], Labels(items).Order(StringComparer.Ordinal));
        Assert.Equal(21, items.Single(i => i["label"]!.GetValue<string>() == "UNITS")["kind"]!.GetValue<int>());
        Assert.Equal(20, items.Single(i => i["label"]!.GetValue<string>() == "Red")["kind"]!.GetValue<int>());
    }

    [Fact]
    public void CompletesImports()
    {
        Assert.Equal(["dotnet", "geometry", "python", "report"], Labels(Complete("from report import", "import ")).Order(StringComparer.Ordinal));
        Assert.Equal(["describe"], Labels(Complete("from report import", "from report import ")));
        Assert.Contains("Color", Labels(Complete("from report import", "from geometry import Point, ")));
    }

    [Fact]
    public void CompletesClassAndEnumMembers()
    {
        Assert.Equal(["sides", "unit"], Labels(Complete("let moved = ", "geometry.Shape.")).Order(StringComparer.Ordinal));
        Assert.Equal(["Custom", "Red"], Labels(Complete("let moved = ", "geometry.Color.")).Order(StringComparer.Ordinal));
        Assert.Contains("upper", Labels(Complete("let moved = ", "print(\"a\".")));
    }

    [Fact]
    public void CompletesTypesInTypes()
    {
        var labels = Labels(Complete("let moved = ", "let p: "));
        Assert.Contains("Point", labels);
        Assert.Contains("int", labels);
        Assert.Contains("geometry", labels);
        Assert.DoesNotContain("corner", labels);
        Assert.DoesNotContain("scale", labels);
        Assert.DoesNotContain("print", labels);
    }

    [Fact]
    public void CompletesNothingInCommentsAndStrings()
    {
        Assert.Empty(Complete("let moved = ", "# geometry."));
        Assert.Empty(Complete("let moved = ", "print(\"geometry."));
    }

    // ---- Signature help (SPEC §16.5) ----

    private static JsonNode? Signature(string typed)
    {
        var (text, cursor) = Insert(MainText, "let moved = ", typed);
        return new LspSession().Open(Main, text).SignatureHelp(Main, text, cursor).Run().Responses().Single()["result"];
    }

    private static (string Label, int Active) Shown(JsonNode? help)
    {
        var signature = help!["signatures"]![help["activeSignature"]?.GetValue<int>() ?? 0]!;
        return (signature["label"]!.GetValue<string>(), help["activeParameter"]?.GetValue<int>() ?? signature["activeParameter"]!.GetValue<int>());
    }

    [Fact]
    public void ShowsSignatures()
    {
        const string scale = "def scale(p: Point, by: float = …, *rest: float) -> Point";
        Assert.Equal((scale, 1), Shown(Signature("scale(corner, ")));
        Assert.Equal((scale, 2), Shown(Signature("scale(corner, 1.0, 2.0, ")));
        Assert.Equal((scale, 1), Shown(Signature("scale(corner, by=")));
        Assert.Equal(("Point(x: float, y: float = …)", 0), Shown(Signature("Point(")));
        Assert.Equal(("def Point.scaled(self, by: float) -> Point", 0), Shown(Signature("corner.scaled(")));
        Assert.Equal(("Color.Custom(code: str)", 0), Shown(Signature("geometry.Color.Custom(")));
        Assert.Null(Signature("print("));
        Assert.Equal(("pick(arg0: Point) -> float", 0), Shown(Signature("let pick: Callable[[Point], float] = lambda p: p.x\npick(")));

        var help = Signature("scale(");
        var parameters = help!["signatures"]![0]!["parameters"]!.AsArray();
        Assert.Equal(3, parameters.Count);
        var first = parameters[0]!["label"]!.AsArray();
        Assert.Equal("p: Point", scale[first[0]!.GetValue<int>()..first[1]!.GetValue<int>()]);
    }

    [Fact]
    public void ShowsDotNetOverloadsAndMembers()
    {
        var strings = Path.Combine(RepoPaths.Root, "tests", "cases", "m21", "strings.tpn");
        var text = File.ReadAllText(strings);
        var session = new LspSession().Open(strings, text)
            .SignatureHelp(strings, text, text.IndexOf("Append(", StringComparison.Ordinal) + "Append(".Length)
            .Completion(strings, text, text.IndexOf("sb.", StringComparison.Ordinal) + "sb.".Length)
            .Run();
        var help = session.Responses()[0]["result"]!;
        Assert.True(help["signatures"]!.AsArray().Count > 1);
        Assert.Equal(("System.Text.StringBuilder.Append(string?)", 0), Shown(help));
        var labels = Labels([.. session.Responses()[1]["result"]!["items"]!.AsArray().Select(i => i!)]);
        Assert.Contains("Append", labels);
        Assert.Single(labels, l => l == "Append");
        Assert.DoesNotContain("Combine", labels);
    }

    [Fact]
    public void HoverShowsAsyncFunctions()
    {
        var path = Path.Combine(RepoPaths.Root, "tests", "cases", "m33", "async.tpn");
        var text = File.ReadAllText(path);
        var session = new LspSession().Open(path, text).Hover(path, text, "double_later(21)").Hover(path, text, "pending = ").Run();
        Assert.Equal(
            ["async def double_later(n: int) -> int", "let pending: Awaitable[str]"],
            session.Responses().Select(r => r["result"]!["contents"]!["value"]!.GetValue<string>().Split('\n')[1]));
    }

    // ---- References and rename (SPEC §16.6) ----

    // Each location as `file:line:column text`, columns from 1.
    private static List<string> Places(JsonNode? locations) =>
        [.. locations!.AsArray().Select(l => Place(new Uri(l!["uri"]!.GetValue<string>()).LocalPath, l["range"]!))];

    private static string Place(string path, JsonNode range)
    {
        var line = range["start"]!["line"]!.GetValue<int>();
        var start = range["start"]!["character"]!.GetValue<int>();
        var text = File.ReadAllText(path).Split('\n')[line][start..range["end"]!["character"]!.GetValue<int>()];
        return $"{Path.GetFileName(path)}:{line + 1}:{start + 1} {text}";
    }

    [Fact]
    public void FindsReferencesAcrossModules()
    {
        var session = new LspSession().Open(Main, MainText)
            .References(Main, MainText, "scale(corner")
            .References(Main, MainText, "scale(corner", includeDeclaration: false)
            .References(Main, MainText, "area()")
            .References(Main, MainText, "y=2.0")
            .References(Main, MainText, "x, geometry.UNITS")
            .References(Main, MainText, "print(describe")
            .Run();
        var results = session.Responses().Select(r => r["result"]).ToList();
        Assert.Equal(
            ["geometry.tpn:37:5 scale", "main.tpn:3:37 scale", "main.tpn:13:13 scale", "report.tpn:2:29 scale", "report.tpn:2:38 resize", "report.tpn:5:18 resize"],
            Places(results[0]));
        Assert.DoesNotContain("geometry.tpn:37:5 scale", Places(results[1]));
        // Overrides are one name (SPEC §16.6).
        Assert.Equal(["geometry.tpn:15:9 area", "geometry.tpn:30:9 area", "main.tpn:9:22 area"], Places(results[2]));
        // Keyword arguments name fields and parameters.
        Assert.Equal(["geometry.tpn:7:5 y", "geometry.tpn:10:40 y", "main.tpn:12:25 y", "report.tpn:6:36 y"], Places(results[3]));
        Assert.Equal(
            ["geometry.tpn:6:5 x", "geometry.tpn:10:27 x", "main.tpn:14:30 x", "main.tpn:17:11 x", "report.tpn:6:22 x"],
            Places(results[4]));
        Assert.Null(results[5]);
    }

    // A rename's edits as `file:line:column old -> new`, sorted.
    private static List<string> Edits(JsonNode? result)
    {
        var edits = new List<string>();
        foreach (var (uri, changes) in result!["changes"]!.AsObject())
        {
            var path = new Uri(uri).LocalPath;
            edits.AddRange(changes!.AsArray().Select(e => $"{Place(path, e!["range"]!)} -> {e["newText"]!.GetValue<string>()}"));
        }
        return [.. edits.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void RenamesAcrossModules()
    {
        var reportText = File.ReadAllText(Report);
        var session = new LspSession().Open(Main, MainText)
            .Rename(Main, MainText, "scale(corner", "grow")
            .Rename(Main, MainText, "y=2.0", "down")
            .Rename(Main, MainText, "area()", "size")
            .Rename(Main, MainText, "corner, 2.0", "corner")
            .Open(Report, reportText)
            .Rename(Report, reportText, "resize(p", "enlarge")
            .Run();
        var results = session.Responses().Select(r => r["result"]).ToList();
        Assert.Equal(
            ["geometry.tpn:37:5 scale -> grow", "main.tpn:13:13 scale -> grow", "main.tpn:3:37 scale -> grow", "report.tpn:2:29 scale -> grow"],
            Edits(results[0]));
        Assert.Equal(4, Edits(results[1]).Count);
        Assert.Equal(3, Edits(results[2]).Count);
        Assert.Empty(results[3]!["changes"]!.AsObject());
        // Renaming an alias edits only it and its uses (SPEC §16.6).
        Assert.Equal(["report.tpn:2:38 resize -> enlarge", "report.tpn:5:18 resize -> enlarge"], Edits(results[4]));
    }

    [Fact]
    public void RefusesRenames()
    {
        var strings = Path.Combine(RepoPaths.Root, "tests", "cases", "m21", "strings.tpn");
        var stringsText = File.ReadAllText(strings);
        var session = new LspSession().Open(Main, MainText)
            .Rename(Main, MainText, "print(describe", "show")
            .Rename(Main, MainText, "geometry.UNITS", "geo")
            .Rename(Main, MainText, "corner, 2.0", "let")
            .Rename(Main, MainText, "corner, 2.0", "2nd")
            .Rename(Main, MainText, "corner, 2.0", "_")
            .Rename(Main, MainText, "corner, 2.0", "__str__")
            .Rename(Main, MainText, "total(shapes", "corner")
            .PrepareRename(Main, MainText, "corner, 2.0")
            .PrepareRename(Main, MainText, "print(describe")
            .PrepareRename(Main, MainText, "# Completion")
            .Open(strings, stringsText)
            .Rename(strings, stringsText, "StringBuilder()", "Builder")
            .Run();
        var responses = session.Responses();
        string Error(int i)
        {
            Assert.Equal(-32803, responses[i]["error"]!["code"]!.GetValue<int>());
            return responses[i]["error"]!["message"]!.GetValue<string>();
        }
        Assert.Equal("cannot rename the built-in 'print'", Error(0));
        Assert.Equal("cannot rename the module 'geometry': the editor can't rename files", Error(1));
        Assert.Equal("'let' is a keyword", Error(2));
        Assert.Equal("'2nd' is not a valid name", Error(3));
        Assert.Equal("'_' is not a valid name", Error(4));
        Assert.Equal("cannot rename to the special name '__str__'", Error(5));
        Assert.StartsWith("renaming 'total' to 'corner' would cause an error: main.tpn:", Error(6));
        Assert.Equal("corner", responses[7]["result"]!["placeholder"]!.GetValue<string>());
        Assert.Equal("cannot rename the built-in 'print'", Error(8));
        Assert.Null(responses[9]["result"]);
        Assert.Equal("cannot rename the .NET name 'StringBuilder'", Error(10));

        var json = Path.Combine(RepoPaths.Root, "tests", "cases", "m10", "json.tpn");
        var jsonText = File.ReadAllText(json);
        var members = Path.Combine(RepoPaths.Root, "tests", "cases", "m28", "members.tpn");
        var membersText = File.ReadAllText(members);
        var special = new LspSession().Open(members, membersText).Rename(members, membersText, "__str__", "show").Run().Responses().Single();
        Assert.Equal("cannot rename the special method '__str__'", special["error"]!["message"]!.GetValue<string>());

        var python = new LspSession().Open(json, jsonText).Rename(json, jsonText, "json.", "codec").Run().Responses().Single();
        Assert.Equal("cannot rename the Python name 'json'", python["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void RefusesRenamesWhileAFileDoesntParse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"taipan-rename-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var main = Path.Combine(directory, "main.tpn");
            File.WriteAllText(main, "def twice(n: int) -> int:\n    return n * 2\n\nprint(twice(2))\n");
            File.WriteAllText(Path.Combine(directory, "broken.tpn"), "def oops(:\n");
            var text = File.ReadAllText(main);
            var session = new LspSession().Open(main, text).Rename(main, text, "twice(2)", "double").Run();
            var error = session.Responses().Single()["error"]!;
            Assert.Equal(-32803, error["code"]!.GetValue<int>());
            Assert.Equal("cannot rename while broken.tpn has syntax errors", error["message"]!.GetValue<string>());
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    // ---- Semantic tokens (SPEC §16.7) ----

    private static readonly string[] TokenTypes =
        ["namespace", "class", "enum", "interface", "struct", "typeParameter", "type", "parameter", "variable", "property", "enumMember", "function", "method"];

    private static readonly string[] Modifiers = ["declaration", "readonly", "static", "defaultLibrary"];

    // Names after a multi-line string, and in a triple-quoted f-string's holes, keep their lines (SPEC §1, M49).
    [Fact]
    public void GivesSemanticTokensAroundMultiLineStrings()
    {
        const string text = "var first = \"\"\"a\nb\"\"\"\nvar second = f\"\"\"\n{first}\nc{first}\"\"\"\nprint(second)\n";
        var session = new LspSession().Open(Main, text).SemanticTokens(Main).Run();
        var data = session.Responses().Single()["result"]!["data"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        var lines = text.Split('\n');
        var names = new List<string>();
        int line = 0, column = 0;
        for (var i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
            names.Add($"{line + 1}:{column + 1} {lines[line].Substring(column, data[i + 2])}");
        }
        Assert.Equal(["1:5 first", "3:5 second", "4:2 first", "5:3 first", "6:7 second"], names.Where(n => !n.EndsWith("print")));
    }

    [Fact]
    public void GivesSemanticTokens()
    {
        var session = new LspSession().Open(Main, MainText).SemanticTokens(Main).Run();
        var legend = session.Initialize()["capabilities"]!["semanticTokensProvider"]!["legend"]!;
        Assert.Equal(TokenTypes, legend["tokenTypes"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(Modifiers, legend["tokenModifiers"]!.AsArray().Select(t => t!.GetValue<string>()));

        var data = session.Responses().Single()["result"]!["data"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();
        var lines = MainText.Split('\n');
        var tokens = new List<string>();
        int line = 0, column = 0;
        for (var i = 0; i < data.Length; i += 5)
        {
            line += data[i];
            column = data[i] == 0 ? column + data[i + 1] : data[i + 1];
            var modifiers = Enumerable.Range(0, Modifiers.Length).Where(b => (data[i + 4] & (1 << b)) != 0).Select(b => Modifiers[b]);
            tokens.Add(string.Join(" ", new[] { $"{line + 1}:{column + 1}", lines[line].Substring(column, data[i + 2]), TokenTypes[data[i + 3]] }.Concat(modifiers)));
        }
        Assert.Equal(
            [
                "2:8 geometry namespace",
                "3:6 geometry namespace",
                "3:22 Point class",
                "3:29 Square class",
                "3:37 scale function",
                "4:6 report namespace",
                "4:20 describe function",
                "6:5 total function declaration",
                "6:11 shapes parameter declaration",
                "6:19 list type defaultLibrary",
                "6:24 geometry namespace",
                "6:33 Shape class",
                "6:44 float type defaultLibrary",
                "7:9 sum variable declaration",
                "8:9 shape variable declaration readonly",
                "8:18 shapes parameter",
                "9:9 sum variable",
                "9:16 shape variable readonly",
                "9:22 area method",
                "10:12 sum variable",
                "12:5 corner variable declaration readonly",
                "12:14 Point class",
                "12:25 y property",
                "13:5 moved variable declaration readonly",
                "13:13 scale function",
                "13:19 corner variable readonly",
                "14:1 print function defaultLibrary",
                "14:7 describe function",
                "14:16 moved variable readonly",
                "14:24 moved variable readonly",
                "14:30 x property",
                "14:33 geometry namespace",
                "14:42 UNITS variable readonly static",
                "15:1 print function defaultLibrary",
                "15:7 total function",
                "15:14 Square class",
                "15:27 geometry namespace",
                "15:36 Shape class",
                "15:42 unit method static",
                "15:52 geometry namespace",
                "15:61 Shape class",
                "15:67 sides property static",
                "16:7 corner variable readonly",
                "17:5 Point class",
                "17:11 x property",
                "17:13 across variable declaration readonly",
                "17:22 print function defaultLibrary",
                "17:38 across variable readonly",
            ],
            tokens);
    }
}
