using System.Reflection;
using Geometry;
using Taipan.Runtime;

namespace Taipan.LibraryTests;

/// <summary>
/// M22 acceptance (SPEC §14): C# calls the Taipan library built from <c>library/*.tpn</c>, whose
/// modules are the static classes <c>geometry</c> and <c>shapes</c> in the namespace <c>Geometry</c>.
/// </summary>
public class LibraryTests
{
    [Fact]
    public void CallsFunctionsAndReadsConstants()
    {
        Assert.Equal(16.0, geometry.total_area([new shapes.Shape.Circle(2.0), new shapes.Shape.Square(2.0)]));
        Assert.Equal("1.0", geometry.VERSION);
        Assert.Equal(4, shapes.SIDES);
        Assert.Equal(4, shapes.corners("square"));
    }

    [Fact]
    public void ConstructsClasses()
    {
        var counter = new shapes.Counter();
        Assert.Equal(1, counter.bump());
        Assert.Equal(6, new shapes.Counter(5).bump());
        Assert.Equal("<Counter object>", counter.ToString());

        var point = new shapes.Point(1.5, 2.0);
        point.x = 3.0;
        Assert.Equal("Point(x=3.0, y=2.0)", point.ToString());
        Assert.Equal("Circle(r=2.0)", new shapes.Shape.Circle(2.0).ToString());
        Assert.IsType<shapes.Shape.Circle>(shapes.parse("circle"));
    }

    [Fact]
    public void DefaultsAreOverloads()
    {
        Assert.Equal(0.0, new shapes.Point(1.0).y);
        Assert.Equal(2.0, shapes.scale(new shapes.Point(1.0)).x);
        Assert.Equal(3.0, shapes.scale(new shapes.Point(1.0), 3.0).x);
        Assert.Equal(2.0, new shapes.Point(1.0).moved().x);
        Assert.Equal(6.0, new shapes.Point(1.0).moved(5.0).x);
    }

    [Fact]
    public void GenericsAreDotNetGenerics()
    {
        shapes.Box<long> box = shapes.wrap(3L);
        Assert.Equal(3, box.get());
        Assert.Equal("a", new shapes.Box<string>("a").value);
    }

    [Fact]
    public void FunctionValuesAreTaipanFunctions()
    {
        Assert.Equal(8, shapes.apply(new Function<Func<long, long>>("twice", x => x * 2), 4));
    }

    [Fact]
    public void CatchesTaipanExceptions()
    {
        var unknown = Assert.Throws<shapes.UnknownShape>(() => shapes.parse("hexagon"));
        Assert.Equal("hexagon", unknown.name);
        Assert.Equal("unknown shape: hexagon", unknown.Message);
        Assert.IsAssignableFrom<TaipanException>(unknown);

        Assert.Equal("missing key \"circle\"", Assert.Throws<KeyError>(() => shapes.corners("circle")).message);
        Assert.Throws<OverflowException>(() => shapes.doubled(long.MaxValue));
    }

    [Fact]
    public void PrivateNamesAreInternal()
    {
        Assert.Null(typeof(shapes).GetMethod("_helper"));
        Assert.Null(typeof(shapes.Counter).GetProperty("_secret"));
        Assert.Null(typeof(shapes.Counter).GetMethod("__init__", BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(typeof(shapes).Assembly.GetExportedTypes(), t => t.Name == "TaipanLibrary");
    }
}
