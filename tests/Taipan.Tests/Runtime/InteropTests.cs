using Taipan.Runtime;

namespace Taipan.Tests.Runtime;

// Runs only where a Python is installed (SPEC §10.6); passes trivially otherwise, unless
// TAIPAN_REQUIRE_PYTHON=1.
public class InteropTests
{
    [Fact]
    public void ImportsCallsAndConverts()
    {
        if (!PythonAvailability.Available())
            return;
        var json = Interop.Import("json");
        var parsed = Interop.Call(Interop.GetAttr(json, "loads"), ["{\"n\": 42, \"ok\": true, \"xs\": [1, null]}"], []);
        Assert.Equal(42, Interop.As(Interop.GetItem(parsed, "n"), Interop.ToInt, "int"));
        Assert.True(Interop.As(Interop.GetItem(parsed, "ok"), Interop.ToBool, "bool"));
        Assert.Null(Interop.AsOptionalValue(Interop.GetItem(parsed, "ok"), Interop.ToInt));
        Assert.Equal([1L, null], Interop.As(Interop.GetItem(parsed, "xs"), v => Interop.ToList(v, e => Interop.NoneOrValue(e, Interop.ToInt)), "list[int?]"));
        Assert.Equal("cannot convert Python bool to int",
            Assert.Throws<TypeError>(() => Interop.As(Interop.GetItem(parsed, "ok"), Interop.ToInt, "int")).Message);
        var error = Assert.Throws<PythonError>(() => Interop.Call(Interop.GetAttr(json, "loads"), ["{"], []));
        Assert.Equal("JSONDecodeError", error.type);
        Assert.StartsWith("Expecting property name", error.message);
        Assert.Equal("[1, 2]", Interop.Str(Interop.From(new List<long> { 1, 2 })));
        Assert.Equal("7", Interop.Str(Interop.Binary("+", 3L, Interop.From(4L))));
        Assert.Equal("True", Interop.Str(Interop.Binary("in", 2L, Interop.From(new List<long> { 1, 2 }))));
    }
}
