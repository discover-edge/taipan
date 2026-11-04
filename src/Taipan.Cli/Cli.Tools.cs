using Taipan.Compiler.Formatting;
using Taipan.Compiler.Text;

namespace Taipan.Cli;

// taipan fmt (SPEC §19) and taipan test (SPEC §20), M44.
public static partial class Cli
{
    /// <summary>
    /// <c>taipan fmt [--check] &lt;path&gt;…</c> (SPEC §19): formats each file, and each <c>.tpn</c>
    /// file under each directory, in place; with <c>--check</c>, names the files it would change.
    /// </summary>
    private static int Format(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var check = args.Contains("--check");
        var paths = args.Where(a => a != "--check").ToList();
        if (paths.Count == 0)
            return UsageError(stderr, "fmt takes at least one file or directory");
        if (paths.FirstOrDefault(p => p.StartsWith('-')) is { } option)
            return UsageError(stderr, $"unknown option '{option}'");
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*.tpn", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
            else if (File.Exists(path))
                files.Add(path);
            else
            {
                stderr.WriteLine($"taipan: file not found: {path}");
                return ExitUsage;
            }
        }
        var exit = ExitSuccess;
        foreach (var file in files)
        {
            var original = File.ReadAllText(file);
            FormatResult result;
            try
            {
                result = Formatter.Format(SourceText.From(original, file));
            }
            catch (InvalidOperationException e)
            {
                // Output whose tokens differ from the input's is the formatter's bug, and isn't written (SPEC §19.1).
                stderr.WriteLine($"taipan: internal error: {e.Message}");
                return ExitUsage;
            }
            if (result.Text is not { } formatted)
            {
                foreach (var diagnostic in result.Diagnostics)
                    stderr.WriteLine(diagnostic.Format(diagnostic.Source ?? SourceText.From(original, file)));
                exit = ExitCompileError;
                continue;
            }
            if (formatted == original.Replace("\r\n", "\n"))
                continue;
            if (check)
            {
                stdout.WriteLine($"would reformat {file}");
                exit = ExitCompileError;
            }
            else
            {
                File.WriteAllText(file, formatted);
            }
        }
        return exit;
    }

    /// <summary>
    /// <c>taipan test [&lt;path&gt;…]</c> (SPEC §20): each test file's tests, run in this process, then one
    /// summary for them all.
    /// </summary>
    private static int Test(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.FirstOrDefault(a => a.StartsWith('-')) is { } option)
            return UsageError(stderr, $"unknown option '{option}'");
        var files = new List<string>();
        foreach (var path in args.Length == 0 ? ["."] : args)
        {
            if (Directory.Exists(path))
                files.AddRange(TestFiles(path));
            else if (File.Exists(path))
                files.Add(path);
            else
            {
                stderr.WriteLine($"taipan: file not found: {path}");
                return ExitUsage;
            }
        }
        var (passed, failed, errors) = (0, 0, 0);
        var summarize = Taipan.Runtime.Testing.Summarize;
        Taipan.Runtime.Testing.Summarize = false;
        try
        {
            foreach (var file in files)
            {
                var result = Checker.Check(file, Target.Test);
                foreach (var diagnostic in result.Diagnostics)
                    stderr.WriteLine(diagnostic.Format(diagnostic.Source!));
                if (result.Failure is not null)
                    stderr.WriteLine($"taipan: {result.Failure}");
                if (result.Program is not { } program)
                {
                    errors++;
                    continue;
                }
                var image = Compiler.Emit.RoslynCompiler.Compile(program, Path.GetFileNameWithoutExtension(file));
                Compiler.Emit.RoslynCompiler.Run(image, result.Project!.RuntimeAssemblies, stdin, stdout, stderr, file, []);
                if (Taipan.Runtime.Testing.Last is { } last)
                    (passed, failed, errors) = (passed + last.Passed, failed + last.Failed, errors + last.Errors);
            }
        }
        finally
        {
            Taipan.Runtime.Testing.Summarize = summarize;
        }
        stdout.WriteLine(Taipan.Runtime.Testing.Summary(new Taipan.Runtime.TestResults(passed, failed, errors)));
        return failed + errors > 0 || passed == 0 ? ExitCompileError : ExitSuccess;
    }

    /// <summary>A directory's test files (SPEC §20.1): <c>test_*.tpn</c> and <c>*_test.tpn</c> at any depth, by path, as found under it.</summary>
    private static IEnumerable<string> TestFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.tpn", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is var name && (name.StartsWith("test_", StringComparison.Ordinal) || name.EndsWith("_test.tpn", StringComparison.Ordinal)))
            .Select(f => directory == "." ? Path.GetRelativePath(".", f) : f)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Takes <c>--runtime &lt;rid&gt;</c>, <c>--invariant-globalization</c> and <c>--optimize-size</c> out of a
    /// build's arguments (SPEC §15.4, §15.5), giving the usage error for a wrong one, or null.
    /// </summary>
    private static string? NativeOptionsOf(ref string[] args, Target target, out NativeOptions options)
    {
        options = new NativeOptions(null, false, false);
        var rest = new List<string>();
        string? runtime = null;
        var (invariant, size, given) = (false, false, false);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--runtime" when i + 1 >= args.Length:
                    return "--runtime needs a runtime identifier";
                case "--runtime":
                    runtime = args[++i];
                    given = true;
                    break;
                case "--invariant-globalization":
                    invariant = given = true;
                    break;
                case "--optimize-size":
                    size = given = true;
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }
        args = [.. rest];
        if (given && target is not (Target.Native or Target.NativeLibrary or Target.PythonExtension))
            return "--runtime, --invariant-globalization and --optimize-size need --native or --python-extension";
        if (runtime is not null && !NativeBuild.RuntimeIdentifiers.Contains(runtime))
            return $"unknown runtime '{runtime}'; use one of {string.Join(", ", NativeBuild.RuntimeIdentifiers)}";
        options = new NativeOptions(runtime, invariant, size);
        return null;
    }
}
