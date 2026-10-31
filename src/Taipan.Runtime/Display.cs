using System.Collections;
using System.Globalization;

namespace Taipan.Runtime;

/// <summary>Display form, used by <c>print</c>, <c>str()</c> and f-strings (SPEC §6).</summary>
public static class Display
{
    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Format(bool value) => value ? "True" : "False";

    public static string Format(string? value) => value ?? "none";

    public static string Format(object? value) => value switch
    {
        null => "none",
        long l => Format(l),
        double d => Format(d),
        bool b => Format(b),
        string s => s,
        IVariant { Fields.Count: 0 } v => v.TypeName,
        IStruct s => $"{s.TypeName}({string.Join(", ", s.Fields.Select(f => $"{f.Name}={Repr(f.Value)}"))})",
        IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => $"{Repr(k)}: {Repr(d[k])}")) + "}",
        IList l => "[" + string.Join(", ", l.Cast<object?>().Select(Repr)) + "]",
        _ => throw new ArgumentException($"no display form for {value.GetType()}"),
    };

    /// <summary>
    /// The form of a value inside a list, dict or struct: a str is written as a literal, in
    /// quotes with <c>\ \" \n \t</c> escaped; everything else as <see cref="Format(object?)"/>.
    /// </summary>
    public static string Repr(object? value) => value is string s
        ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\""
        : Format(value);

    /// <summary>Python's <c>repr(float)</c>: shortest round-trip digits, fixed notation for decimal exponents in [-4, 16).</summary>
    public static string Format(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";

        var sign = double.IsNegative(value) ? "-" : "";
        if (value == 0.0)
            return sign + "0.0";

        // "R" gives the shortest round-trip digits, as "1.5", "1E+16" or "1.5E-07".
        var roundTrip = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = roundTrip.IndexOf('E');
        var mantissa = exponentAt < 0 ? roundTrip : roundTrip[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(roundTrip[(exponentAt + 1)..], CultureInfo.InvariantCulture);

        // Normalize to digits d1 d2 ... with the decimal point after `pointAt` digits.
        var pointAt = mantissa.IndexOf('.') is var dot and >= 0 ? dot : mantissa.Length;
        var digits = mantissa.Replace(".", "");
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        digits = digits[leadingZeros..].TrimEnd('0');
        pointAt += exponent - leadingZeros;

        var decimalExponent = pointAt - 1;
        if (decimalExponent is >= -4 and < 16)
        {
            if (pointAt <= 0)
                return $"{sign}0.{new string('0', -pointAt)}{digits}";
            if (digits.Length <= pointAt)
                return $"{sign}{digits}{new string('0', pointAt - digits.Length)}.0";
            return $"{sign}{digits[..pointAt]}.{digits[pointAt..]}";
        }

        var fraction = digits.Length > 1 ? "." + digits[1..] : "";
        var exponentSign = decimalExponent < 0 ? "-" : "+";
        return $"{sign}{digits[0]}{fraction}e{exponentSign}{Math.Abs(decimalExponent):00}";
    }
}
