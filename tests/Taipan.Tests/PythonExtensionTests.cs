using System.Diagnostics;

namespace Taipan.Tests;

/// <summary>
/// M25 acceptance (SPEC §17): <c>taipan build --python-extension</c> builds
/// <c>tests/cases/m25/geometry.tpn</c> into a real CPython extension module, which Python scripts
/// import and call. It's a NativeAOT build that needs Python to run, so it runs only with
/// <c>TAIPAN_TEST_NATIVE=1</c> (as CI sets) and a Python installation, and otherwise passes with a note.
/// </summary>
// It sets the working directory, as the tests that run programs do, so it runs apart from them.
[Collection(RunningPrograms.Name)]
public class PythonExtensionTests
{
    // The roadmap's acceptance command, then calls covering conversions, defaults, keywords,
    // errors and exceptions. Python runs unbuffered (-u), so its output and the module's print
    // come out in call order.
    private const string Script = """
        import geometry
        print(geometry.area(2.0))
        print(geometry.area(r=1), geometry.PI, geometry.SIDES)
        print(geometry.scale([1.0, 2.5]), geometry.scale([1.0], by=3), geometry.scale([1, 2], 0.5))
        print(geometry.describe("circle", None), geometry.describe(sides=3, name="square"))
        print(geometry.bounds([3, -1, 7]), geometry.first([]), geometry.first(["a", "b"]), geometry.lookup({"a": 1}, "a"))
        print(geometry.report("hi"))
        print(geometry.resize(2.0, by=3.0, round=False), geometry.stack(), geometry.stack(1, 2, top="a"))
        for call in [
            lambda: geometry.area(-1.0),
            lambda: geometry.area("x"),
            lambda: geometry.area(),
            lambda: geometry.area(1.0, 2.0),
            lambda: geometry.area(radius=1.0),
            lambda: geometry.area(1.0, r=2.0),
            lambda: geometry.scale([1.0, "x"]),
            lambda: geometry.describe("hexagon", 6),
            lambda: geometry.lookup({}, "missing"),
            lambda: geometry.square(2**62),
            lambda: geometry.square(2**70),
            lambda: geometry.bounds([]),
            lambda: geometry.resize(2.0, 3.0, round=True),
            lambda: geometry.resize(w=2.0, round=True),
            lambda: geometry.resize(2.0),
            lambda: geometry.stack(1, "x"),
            lambda: geometry.stack(top=1),
        ]:
            try:
                call()
            except Exception as e:
                print(f"{type(e).__module__}.{type(e).__name__}: {e.args[0]}")
        print(issubclass(geometry.NegativeRadius, geometry.GeometryError), issubclass(geometry.BadShape, ValueError))
        try:
            geometry.area(-2.0)
        except geometry.GeometryError as e:
            print("caught by its base:", e)
        """;

    private const string Expected = """
        12.56636
        3.14159 3.14159 4
        [2.0, 5.0] [3.0] [0.5, 1.0]
        circle with 0 sides square with 3 sides
        (-1, 7) None a 1
        report: hi
        None
        6.0 [] {} [1, 2] {"top": "a"}
        geometry.NegativeRadius: radius -1.0 is negative
        builtins.TypeError: area() argument 'r' must be float, not str
        builtins.TypeError: area() missing required argument 'r'
        builtins.TypeError: area() takes 1 positional argument but 2 were given
        builtins.TypeError: area() got an unexpected keyword argument 'radius'
        builtins.TypeError: area() got multiple values for argument 'r'
        builtins.TypeError: scale() argument 'xs' must be list[float], not str
        geometry.BadShape: unknown shape hexagon
        builtins.KeyError: missing key "missing"
        builtins.OverflowError: integer overflow
        builtins.OverflowError: int too big to convert
        builtins.IndexError: index 0 out of range for length 0
        builtins.TypeError: resize() takes 1 positional argument but 2 were given
        builtins.TypeError: resize() got some positional-only arguments passed as keyword arguments: 'w'
        builtins.TypeError: resize() missing required keyword-only argument 'round'
        builtins.TypeError: stack() argument 'heights' must be int, not str
        builtins.TypeError: stack() argument 'labels' must be str, not int
        True True
        caught by its base: radius -2.0 is negative

        """;

    [Fact]
    public async Task PythonImportsAndCallsTheModule()
    {
        if (Environment.GetEnvironmentVariable("TAIPAN_TEST_NATIVE") != "1" || !PythonAvailability.Available())
        {
            Console.WriteLine("skipped the extension module build: set TAIPAN_TEST_NATIVE=1, with Python installed, to run it");
            return;
        }

        var output = Directory.CreateTempSubdirectory("taipan-extension-test-").FullName;
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Directory.SetCurrentDirectory(RepoPaths.Root);
            var built = Cli.Cli.Run(["build", "tests/cases/m25/geometry.tpn", "--python-extension", "-o", output], stdout, stderr);
            Assert.True(built == 0, stderr.ToString());
            Assert.Equal("", stderr.ToString());
            Assert.True(File.Exists(Path.Combine(output, OperatingSystem.IsWindows() ? "geometry.pyd" : "geometry.abi3.so")));

            var script = Path.Combine(output, "check.py");
            await File.WriteAllTextAsync(script, Script);
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3", ["-u", script])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PYTHONPATH"] = output;
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal("", await error);
            Assert.Equal(Expected.Replace("\r\n", "\n"), text.Replace("\r\n", "\n"));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}
