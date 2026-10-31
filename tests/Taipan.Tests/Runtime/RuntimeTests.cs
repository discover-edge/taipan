using Taipan.Runtime;

namespace Taipan.Tests.Runtime;

public class RuntimeTests
{
    // Expected values are Python's repr(float).
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(100.0, "100.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00012, "0.00012")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(123456789.125, "123456789.125")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(9999999999999998.0, "9999999999999998.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.2345678901234568e17, "1.2345678901234568e+17")]
    [InlineData(-1e100, "-1e+100")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NegativeInfinity, "-inf")]
    [InlineData(double.NaN, "nan")]
    public void FloatDisplay(double value, string expected) =>
        Assert.Equal(expected, Display.Format(value));

    [Fact]
    public void ObjectDisplay() =>
        Assert.Equal(["42", "2.0", "True", "False", "s", "None"],
            new object?[] { 42L, 2.0, true, false, "s", null }.Select(Display.Format));

    [Theory]
    [InlineData(7L, 2L, 3L, 1L)]
    [InlineData(-7L, 2L, -4L, 1L)]
    [InlineData(7L, -2L, -4L, -1L)]
    [InlineData(-7L, -2L, 3L, -1L)]
    [InlineData(6L, -3L, -2L, 0L)]
    [InlineData(0L, 5L, 0L, 0L)]
    [InlineData(long.MinValue, 1L, long.MinValue, 0L)]
    [InlineData(long.MinValue, -1L, null, 0L)]
    public void FloorDivAndModFollowPython(long a, long b, long? quotient, long remainder)
    {
        if (quotient is { } q)
            Assert.Equal(q, Ops.FloorDiv(a, b));
        else
            Assert.Throws<OverflowException>(() => Ops.FloorDiv(a, b));
        Assert.Equal(remainder, Ops.Mod(a, b));
    }

    [Fact]
    public void DivisionByZeroPanics()
    {
        Assert.Equal("division by zero", Assert.Throws<TaipanPanic>(() => Ops.FloorDiv(1, 0)).Message);
        Assert.Equal("division by zero", Assert.Throws<TaipanPanic>(() => Ops.Mod(1, 0)).Message);
        Assert.Equal("division by zero", Assert.Throws<TaipanPanic>(() => Ops.Div(1.0, -0.0)).Message);
    }

    [Theory]
    [InlineData(2.9, 2)]
    [InlineData(-2.9, -2)]
    [InlineData(-9223372036854775808.0, long.MinValue)]
    [InlineData(9223372036854774784.0, 9223372036854774784)]
    public void ToIntTruncates(double value, long expected) =>
        Assert.Equal(expected, Ops.ToInt(value));

    [Theory]
    [InlineData(9223372036854775808.0, "cannot convert 9.223372036854776e+18 to int")]
    [InlineData(double.NaN, "cannot convert nan to int")]
    [InlineData(double.NegativeInfinity, "cannot convert -inf to int")]
    public void ToIntPanicsOutOfRange(double value, string message) =>
        Assert.Equal(message, Assert.Throws<TaipanPanic>(() => Ops.ToInt(value)).Message);

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("🐍", 1)]
    [InlineData("é🐍x", 3)]
    public void LenCountsCodePoints(string value, long expected) =>
        Assert.Equal(expected, Builtins.Len(value));
}
