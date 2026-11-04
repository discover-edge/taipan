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

    // M45 acceptance (SPEC §17.5, §17.6): Python creates instances of a module's classes and calls
    // their methods, uses its enums, and passes callables and other objects in.
    private const string TypesScript = """
        import shapes
        p = shapes.Point(1.0)
        print(p, repr(p), p.x, p.y)
        q = p.moved(2.0)
        print(q, q.length(), type(q).__name__, type(q).__module__)
        p.x = 5.0
        print(p.x, p == shapes.Point(5.0), p != q, p == 5)
        for bad in [lambda: setattr(p, "x", "no"), lambda: hash(p), lambda: shapes.Shape(), lambda: shapes.area(5)]:
            try:
                bad()
            except TypeError as e:
                print("TypeError:", e)
        d = shapes.Dog("rex")
        print(d.speak(), d.name, d.legs, str(d), isinstance(d, shapes.Animal), hash(d) == hash(d))
        print(shapes.loudest([shapes.Animal("cat"), d]).speak())
        class Puppy(shapes.Animal):
            pass
        try:
            Puppy("x")
        except TypeError as e:
            print("TypeError:", e)
        c = shapes.Shape.Circle(2.0)
        print(c, c.name, c.r, shapes.area(c), shapes.area(shapes.Shape.Rect(w=2.0, h=3.0)), shapes.area(shapes.Shape.Empty))
        print(shapes.Shape.Empty.name, shapes.Shape.Empty == shapes.Shape.Empty, {c: 1}[shapes.Shape.Circle(2.0)])
        try:
            c.w
        except AttributeError as e:
            print("AttributeError:", e)
        print(shapes.origin(), shapes.apply(lambda n: n * 10, 4))
        def boom(n):
            raise ValueError(f"bad {n}")
        print(shapes.safely(boom, 3), shapes.safely(lambda n: n, 3))
        try:
            shapes.apply(boom, 1)
        except ValueError as e:
            print("ValueError back in Python:", e)
        try:
            shapes.apply(lambda n: "x", 1)
        except TypeError as e:
            print("TypeError:", e)
        print(shapes.describe([1, "a"]), shapes.keep([{"k": 1}, 2]))
        """;

    private const string TypesExpected = """
        Point(x=1.0, y=0.0) Point(x=1.0, y=0.0) 1.0 0.0
        Point(x=3.0, y=0.0) 3.0 Point shapes
        5.0 True True False
        TypeError: 'Point.x' must be float, not str
        TypeError: unhashable type: 'shapes.Point'
        TypeError: cannot create 'shapes.Shape' instances
        TypeError: area() argument 's' must be Shape, not int
        rex says woof rex 4 Animal(rex) True True
        rex says woof
        TypeError: a Python class can't derive from a Taipan class
        Circle(r=2.0) Circle 2.0 12.0 6.0 0.0
        Empty True 1
        AttributeError: 'Shape' value Circle has no attribute 'w'
        Point(x=0.0, y=0.0) 41
        ValueError: bad 3 3
        ValueError back in Python: bad 1
        TypeError: a Python function returned str, not int
        got [1, 'a'] {'k': 1}

        """;

    [Fact]
    public async Task PythonUsesTheModulesTypes()
    {
        if (Environment.GetEnvironmentVariable("TAIPAN_TEST_NATIVE") != "1" || !PythonAvailability.Available())
        {
            Console.WriteLine("skipped the extension module build: set TAIPAN_TEST_NATIVE=1, with Python installed, to run it");
            return;
        }
        var (exit, text, error) = await BuildAndRun("tests/cases/m45/shapes.tpn", "shapes", TypesScript);
        Assert.Equal("", error);
        Assert.Equal(TypesExpected.Replace("\r\n", "\n"), text);
        Assert.Equal(0, exit);
    }

    // Builds a module into a temporary directory and runs a script that imports it.
    private static async Task<(int Exit, string Stdout, string Stderr)> BuildAndRun(string casePath, string name, string script)
    {
        var output = Directory.CreateTempSubdirectory("taipan-extension-test-").FullName;
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Directory.SetCurrentDirectory(RepoPaths.Root);
            var built = Cli.Cli.Run(["build", casePath, "--python-extension", "-o", output], stdout, stderr);
            Assert.True(built == 0, stderr.ToString());
            Assert.Equal("", stderr.ToString());
            var path = Path.Combine(output, "check.py");
            await File.WriteAllTextAsync(path, script);
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3", ["-u", path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PYTHONPATH"] = output;
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, text.Replace("\r\n", "\n"), (await error).Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

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
