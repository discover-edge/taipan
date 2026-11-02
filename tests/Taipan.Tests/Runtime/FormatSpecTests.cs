using System.Globalization;
using Taipan.Runtime;

namespace Taipan.Tests;

/// <summary>
/// Format specs (SPEC §6, M26) against CPython: <c>FormatSpecCases.txt</c> holds Python's own
/// <c>format(value, spec)</c> results, written by <c>generate_format_cases.py</c>. Where Python
/// rejects a spec for a type, Taipan rejects it at compile time.
/// </summary>
public class FormatSpecTests
{
    public static TheoryData<string, string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(RepoPaths.Root, "tests", "Taipan.Tests", "Runtime", "FormatSpecCases.txt")))
        {
            var parts = line.Split('\t');
            data.Add(parts[0], parts[1], parts[2], parts[3]);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesPython(string kind, string value, string spec, string expected)
    {
        var parsed = FormatSpec.Parse(spec);
        Assert.NotNull(parsed);
        var formatKind = kind switch { "int" => FormatKind.Int, "float" => FormatKind.Float, _ => FormatKind.Str };
        if (expected == "ERROR")
        {
            Assert.NotNull(parsed.Problem(formatKind));
            return;
        }
        Assert.Null(parsed.Problem(formatKind));
        var actual = formatKind switch
        {
            FormatKind.Int => parsed.Format(long.Parse(value, CultureInfo.InvariantCulture)),
            FormatKind.Float => parsed.Format(ParseFloat(value)),
            _ => parsed.Format(value),
        };
        Assert.Equal(expected, actual);
    }

    private static double ParseFloat(string text) => text switch
    {
        "inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        "nan" => double.NaN,
        _ => double.Parse(text, CultureInfo.InvariantCulture),
    };

    [Theory]
    [InlineData("10.2q")]
    [InlineData(".")]
    [InlineData("5.2.1f")]
    [InlineData("<<<5")]
    public void RejectsWhatIsntTheMiniLanguage(string spec) => Assert.Null(FormatSpec.Parse(spec));
}
