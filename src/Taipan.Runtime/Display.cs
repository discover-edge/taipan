using System.Runtime.CompilerServices;
using System.Collections;
using System.Globalization;

namespace Taipan.Runtime;

/// <summary>Display form, used by <c>print</c>, <c>str()</c> and f-strings (SPEC §6).</summary>
public static class Display
{
    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    // An f-string hole's format spec (SPEC §6, M26), checked at compile time. Parsed specs are cached.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FormatSpec> Specs = new();

    private static FormatSpec SpecOf(string spec) => Specs.GetOrAdd(spec, s => FormatSpec.Parse(s)!);

    public static string Format(long value, string spec) => SpecOf(spec).Format(value);

    public static string Format(double value, string spec) => SpecOf(spec).Format(value);

    public static string Format(string value, string spec) => SpecOf(spec).Format(value);

    public static string Format(bool value) => value ? "True" : "False";

    public static string Format(string? value) => value ?? "None";

    public static string Format(object? value) => value switch
    {
        null => "None",
        long l => Format(l),
        double d => Format(d),
        bool b => Format(b),
        string s => s,
        IFunction f => $"<function {f.Name}>",
        // An async function's result, or a .NET task (SPEC §3.14).
        System.Threading.Tasks.Task => "<awaitable>",
        ITuple t => "(" + string.Join(", ", Enumerable.Range(0, t.Length).Select(i => Repr(t[i]))) + ")",
        IVariant { Fields.Count: 0 } v => v.TypeName,
        // A class's __str__ (SPEC §4.8).
        IClass c => c.__str__(),
        IStruct s => StructForm(s),
        IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => $"{Repr(k)}: {Repr(d[k])}")) + "}",
        IList l => "[" + string.Join(", ", l.Cast<object?>().Select(Repr)) + "]",
        // Python objects (SPEC §10.2), else a .NET value's own ToString (SPEC §13.3).
        _ => ExternalFormat?.Invoke(value, false) ?? value.ToString() ?? "",
    };

    /// <summary>
    /// Formats values from outside Taipan, given whether they're inside a container: Python
    /// objects (SPEC §10.2). Set by <see cref="Interop"/> once Python starts, so Display never
    /// refers to pythonnet's types.
    /// </summary>
    public static Func<object, bool, string?>? ExternalFormat { get; set; }

    /// <summary>
    /// The form of a value inside a list, dict or dataclass: a str is written as a literal, in
    /// quotes with <c>\ \" \n \t</c> escaped; everything else as <see cref="Format(object?)"/>.
    /// </summary>
    public static string Repr(object? value) => value switch
    {
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"",
        null => Format(value),
        // A class's __repr__ (SPEC §4.8); an exception's shows its class and message, as Python's does (SPEC §6).
        IClass c => c.__repr__(),
        _ => ExternalFormat?.Invoke(value, true) ?? Format(value),
    };

    /// <summary>A class's form without <c>__repr__</c>: a dataclass's fields, or <c>&lt;Dog object&gt;</c> (SPEC §4.3).</summary>
    public static string DefaultRepr(IClass value) => value switch
    {
        IStruct s => StructForm(s),
        IObject o => $"<{o.TypeName} object>",
        _ => value.ToString() ?? "",
    };

    /// <summary>A dataclass's or variant's generated form: <c>Point(x=1, y=2)</c>.</summary>
    public static string StructForm(IStruct value) =>
        $"{value.TypeName}({string.Join(", ", value.Fields.Select(f => $"{f.Name}={Repr(f.Value)}"))})";

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
