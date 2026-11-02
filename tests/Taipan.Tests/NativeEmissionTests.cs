using Taipan.Cli;
using Taipan.Compiler.Emit;
using Taipan.Compiler.Semantics;
using Taipan.Compiler.Syntax;

namespace Taipan.Tests;

/// <summary>
/// The emission for <c>--native</c> (SPEC §15.3), without NativeAOT: every golden case that runs is
/// compiled with native line tracking and without debug information, run in-process, and compared
/// against its sidecars. As in a native build, a panic's location can then come only from what the
/// program recorded, so this checks that line tracking reports the same lines as stack traces. Cases a native build
/// rejects (Python, TP0233) are left out.
/// </summary>
[Collection(RunningPrograms.Name)]
public class NativeEmissionTests
{
    static NativeEmissionTests() => Directory.SetCurrentDirectory(RepoPaths.Root);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string path in GoldenTests.Cases())
        {
            string[] skipped = [".library", ".native", ".requires"];
            if (File.Exists(Path.ChangeExtension(path, ".out")) && !skipped.Any(s => File.Exists(Path.ChangeExtension(path, s))))
                data.Add(path);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RunsWithTheSameOutput(string casePath)
    {
        var exitText = ReadSidecar(casePath, ".exit");
        var expectedErr = ReadSidecar(casePath, ".err");
        var expectedExit = exitText is not null ? int.Parse(exitText.Trim()) : expectedErr is not null ? 1 : 0;
        // Only programs that run: not compile errors or a bad taipan.json.
        if (expectedExit is not (0 or 101) || ProjectFile.Load(casePath, out _) is not { } project)
            return;

        var loaded = ModuleLoader.Load(casePath);
        var bound = Binder.Bind(loaded.Modules, DotNetLibrary.For(project.CompileReferences), asNative: true);
        if (bound.Diagnostics.Any(d => !d.IsWarning))
        {
            Assert.All(bound.Diagnostics.Where(d => !d.IsWarning), d => Assert.Equal("TP0233", d.Code));
            return;
        }
        var emitted = CSharpEmitter.Emit(loaded.Modules[0].Source, bound.Program, native: true);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        foreach (var warning in loaded.Diagnostics.Concat(bound.Diagnostics).Concat(emitted.Diagnostics))
            stderr.WriteLine(warning.Format(warning.Source!));
        var image = RoslynCompiler.Compile(emitted, Path.GetFileNameWithoutExtension(casePath), debugInformation: false);
        var exit = RoslynCompiler.Run(image, project.RuntimeAssemblies, stdout, stderr);

        Assert.Equal(expectedErr ?? "", Normalize(stderr.ToString()));
        Assert.Equal(ReadSidecar(casePath, ".out"), Normalize(stdout.ToString()));
        Assert.Equal(expectedExit, exit);
    }

    private static string? ReadSidecar(string tpnPath, string extension)
    {
        var path = Path.ChangeExtension(tpnPath, extension);
        return File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
