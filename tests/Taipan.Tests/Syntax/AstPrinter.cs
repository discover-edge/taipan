using System.Collections;
using System.Globalization;
using System.Numerics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Tests.Syntax;

/// <summary>
/// Prints an AST as a compact one-line snapshot: <c>Kind(child, child, …)</c>, without spans (any TextSpan property).
/// Children print in declaration order; identifiers print as bare text and strings quoted.
/// Uses reflection so that every node type is covered without a hand-written case per node.
/// </summary>
public static class AstPrinter
{
    public static string Print(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\"",
        Identifier id => id.Text,
        NameExpr name => $"NameExpr({name.Name})",
        TypeParam { Constraints.IsEmpty: true } parameter => parameter.Name.Text,
        Param { Default: null } parameter => $"Param({parameter.Name.Text}, {Print(parameter.Type)})",
        bool b => b ? "True" : "False",
        BigInteger i => i.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        Node node => PrintNode(node),
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Print)) + "]",
        _ => throw new ArgumentException($"cannot print {value.GetType()}"),
    };

    private static string PrintNode(Node node)
    {
        var type = node.GetType();
        var children = type.GetProperties()
            .Where(p => p.PropertyType != typeof(TextSpan))
            .Select(p => Print(p.GetValue(node)))
            .ToList();
        return children.Count == 0 ? type.Name : $"{type.Name}({string.Join(", ", children)})";
    }
}
