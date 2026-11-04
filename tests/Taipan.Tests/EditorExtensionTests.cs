using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Taipan.Tests;

/// <summary>
/// M32 and M47 acceptance (SPEC §16.8): the VS Code extension in <c>editors/vscode</c>. Its grammar
/// highlights the sample as the golden token list says, it packages with <c>vsce</c> at the
/// compiler's version with its icon and commands, and its scripted test passes.
/// </summary>
public class EditorExtensionTests
{
    private static readonly string Extension = Path.Combine(RepoPaths.Root, "editors", "vscode");

    [Fact]
    public void ManifestContributesTheLanguage()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Extension, "package.json")))!;
        var contributes = manifest["contributes"]!;
        var language = contributes["languages"]!.AsArray().Single()!;
        Assert.Equal("taipan", language["id"]!.GetValue<string>());
        Assert.Equal([".tpn"], language["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        var grammar = contributes["grammars"]!.AsArray().Single()!;
        Assert.Equal("source.taipan", grammar["scopeName"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(Extension, grammar["path"]!.GetValue<string>())));
        Assert.Equal("taipan", contributes["configuration"]!["properties"]!["taipan.path"]!["default"]!.GetValue<string>());
        // Its one runtime dependency (SPEC §16.8).
        Assert.Equal(["vscode-languageclient"], manifest["dependencies"]!.AsObject().Select(d => d.Key));
        Assert.Contains("'lsp', '--stdio'", File.ReadAllText(Path.Combine(Extension, "extension.js")));
    }

    [Fact]
    public void ManifestNamesTheExtension()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Extension, "package.json")))!;
        Assert.Equal("discover-edge", manifest["publisher"]!.GetValue<string>());
        Assert.Equal("taipan", manifest["name"]!.GetValue<string>());
        // The compiler's version, which a release's tag names (SPEC §12.2).
        var props = File.ReadAllText(Path.Combine(RepoPaths.Root, "Directory.Build.props"));
        Assert.Equal(Regex.Match(props, "<Version>(.*)</Version>").Groups[1].Value, manifest["version"]!.GetValue<string>());
        // The Taipan logo, copied into the extension's directory.
        Assert.Equal("icon.png", manifest["icon"]!.GetValue<string>());
        Assert.Equal(File.ReadAllBytes(Path.Combine(RepoPaths.Root, "assets", "icon.png")), File.ReadAllBytes(Path.Combine(Extension, "icon.png")));
    }

    [Fact]
    public void ManifestContributesTheCommands()
    {
        var contributes = JsonNode.Parse(File.ReadAllText(Path.Combine(Extension, "package.json")))!["contributes"]!;
        var commands = contributes["commands"]!.AsArray().ToDictionary(c => c!["command"]!.GetValue<string>(), c => c!["title"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["taipan.runFile"] = "Run File", ["taipan.restartServer"] = "Restart Language Server" }, commands);
        Assert.All(contributes["commands"]!.AsArray(), c => Assert.Equal("Taipan", c!["category"]!.GetValue<string>()));
        var palette = contributes["menus"]!["commandPalette"]!.AsArray();
        Assert.All(palette, m => Assert.Equal("editorLangId == taipan", m!["when"]!.GetValue<string>()));
        Assert.Equal(commands.Keys.Order(), palette.Select(m => m!["command"]!.GetValue<string>()).Order());
        var run = contributes["menus"]!["editor/title/run"]!.AsArray().Single()!;
        Assert.Equal("taipan.runFile", run["command"]!.GetValue<string>());
    }

    [Fact]
    public void ScriptedTestPasses()
    {
        if (!NodeAvailability.Available(Extension))
            return;
        var (exitCode, output, error) = Node(Extension, "test/extension.test.mjs");
        Assert.True(exitCode == 0, output + error);
        Assert.Equal("ok\n", output);
    }

    [Fact]
    public void GrammarGivesTheGoldenTokens()
    {
        if (!NodeAvailability.Available(Extension))
            return;
        var (exitCode, output, error) = Node(Extension, "test/tokenize.mjs", "test/sample.tpn");
        Assert.True(exitCode == 0, error);
        Assert.Equal(File.ReadAllText(Path.Combine(Extension, "test", "sample.tokens")), output);
    }

    [Fact]
    public void PackagesWithVsce()
    {
        if (!NodeAvailability.Available(Extension))
            return;
        var directory = Path.Combine(Path.GetTempPath(), $"taipan-vsix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var version = JsonNode.Parse(File.ReadAllText(Path.Combine(Extension, "package.json")))!["version"]!.GetValue<string>();
            var vsix = Path.Combine(directory, $"taipan-{version}.vsix");
            var (exitCode, output, error) = Node(Extension, "node_modules/@vscode/vsce/vsce", "package", "--out", vsix);
            Assert.True(exitCode == 0, output + error);

            using var archive = ZipFile.OpenRead(vsix);
            var entries = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("extension/extension.js", entries);
            Assert.Contains("extension/syntaxes/taipan.tmLanguage.json", entries);
            Assert.Contains("extension/language-configuration.json", entries);
            Assert.Contains("extension/icon.png", entries);
            Assert.Contains(entries, e => e.StartsWith("extension/node_modules/vscode-languageclient/", StringComparison.Ordinal));
            // Its own files and its runtime dependency only: no tests, no development tools.
            Assert.DoesNotContain(entries, e => e.StartsWith("extension/test/", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.StartsWith("extension/node_modules/vscode-textmate/", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.StartsWith("extension/node_modules/@vscode/", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.StartsWith("extension/node_modules/ovsx/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (int ExitCode, string Output, string Error) Node(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("node", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }
}

/// <summary>
/// The extension's tests need Node.js and the extension's packages (<c>npm ci</c> in
/// <c>editors/vscode</c>), and pass trivially without them. Setting <c>TAIPAN_REQUIRE_NODE=1</c>
/// turns their absence into a failure instead, as <see cref="PythonAvailability"/> does for Python.
/// </summary>
public static class NodeAvailability
{
    public static bool Available(string extension)
    {
        var missing = !Directory.Exists(Path.Combine(extension, "node_modules")) ? "the extension's packages (run `npm ci` in editors/vscode)"
            : !OnPath("node") ? "Node.js"
            : null;
        if (missing is null)
            return true;
        if (Environment.GetEnvironmentVariable("TAIPAN_REQUIRE_NODE") == "1")
            throw new InvalidOperationException($"TAIPAN_REQUIRE_NODE=1 but {missing} wasn't found");
        return false;
    }

    internal static bool OnPath(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
