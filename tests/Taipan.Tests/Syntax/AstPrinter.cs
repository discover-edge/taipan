using System.Collections.Immutable;
using System.Collections;
using System.Globalization;
using System.Numerics;
using Taipan.Compiler.Syntax;
using Taipan.Compiler.Text;

namespace Taipan.Tests.Syntax;

/// <summary>
/// Prints an AST as a compact one-line snapshot: <c>Kind(child, child, …)</c>, without spans (any TextSpan property).
/// Children print in declaration order; identifiers print as bare text and strings quoted. An empty
/// decorator list and a missing default are left out, since most declarations have neither.
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
        TypeParam { Constraints.IsEmpty: true, Bound: null } parameter => parameter.Name.Text,
        TypeParam { Bound: null } parameter => $"TypeParam({parameter.Name.Text}, {Print(parameter.Constraints)})",
        TypeParam parameter => $"TypeParam({parameter.Name.Text}, {Print(parameter.Bound)})",
        Param { Default: null } parameter => $"Param({parameter.Name.Text}, {Print(parameter.Type)})",
        FieldDef { Default: null } field => $"FieldDef({field.Name.Text}, {Print(field.Type)})",
        PatternBinding { Field: null } binding => binding.Name.Text,
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
            .Where(p => !(p.Name == "Decorators" && p.GetValue(node) is ImmutableArray<Decorator> { IsEmpty: true }))
            .Select(p => Print(p.GetValue(node)))
            .ToList();
        return children.Count == 0 ? type.Name : $"{type.Name}({string.Join(", ", children)})";
    }
}
