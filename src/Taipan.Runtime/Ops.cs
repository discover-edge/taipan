namespace Taipan.Runtime;

/// <summary>Operators whose semantics differ from C#'s (SPEC §5.3).</summary>
public static class Ops
{
    /// <summary><c>/</c>: always true division; dividing by zero panics, for floats too.</summary>
    public static double Div(double left, double right)
    {
        if (right == 0.0)
            throw Panic.Create("division by zero");
        return left / right;
    }

    /// <summary><c>//</c>: floor division, as in Python.</summary>
    public static long FloorDiv(long left, long right)
    {
        if (right == 0)
            throw Panic.Create("division by zero");
        if (left == long.MinValue && right == -1)
            throw new OverflowException();
        var quotient = left / right;
        if (left % right != 0 && (left < 0) != (right < 0))
            quotient--;
        return quotient;
    }

    /// <summary><c>%</c>: the result takes the divisor's sign, as in Python.</summary>
    public static long Mod(long left, long right)
    {
        if (right == 0)
            throw Panic.Create("division by zero");
        if (right == -1)
            return 0;
        var remainder = left % right;
        if (remainder != 0 && (remainder < 0) != (right < 0))
            remainder += right;
        return remainder;
    }

    // Checked int arithmetic for operands the C# compiler would fold at compile time, where an
    // overflow would be a C# error instead of a Taipan panic. Other arithmetic is emitted as C#
    // operators in a checked compilation.

    public static long Add(long left, long right) => checked(left + right);

    public static long Subtract(long left, long right) => checked(left - right);

    public static long Multiply(long left, long right) => checked(left * right);

    public static long Negate(long value) => checked(-value);

    /// <summary><c>item in container</c> for strings, evaluating operands in source order.</summary>
    public static bool Contains(string item, string container) =>
        container.Contains(item, StringComparison.Ordinal);

    /// <summary><c>int(x)</c> for a float: truncates toward zero (SPEC §6).</summary>
    public static long ToInt(double value)
    {
        // 2^63 is exactly representable; every double below it and at or above -2^63 fits.
        if (double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0)
            throw Panic.Create($"cannot convert {Display.Format(value)} to int");
        return (long)value;
    }
}
