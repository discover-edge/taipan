using System.Collections.Immutable;
using System.Text;
using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// A program emitted as a CPython extension module (SPEC §17, M25): the program class as usual,
// and a TaipanExtension class whose PyInit_<name> builds the module, with one entry point per
// exported function that binds and converts its arguments, calls it and converts the result.
public sealed partial class CSharpEmitter
{
    private const string PythonExtension = "global::Taipan.Runtime.PythonExtension";
    private const string EntryPoint = "(nint)(delegate* unmanaged<nint, nint, nint, nint>)&";

    private string ExtensionClass(BoundExtension extension)
    {
        var code = new StringBuilder();
        var className = UniqueGlobal("TaipanExtension");
        var entries = new StringBuilder();
        code.Append($"\npublic static unsafe class {className}\n{{\n");
        code.Append($"    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = \"PyInit_{extension.Name}\")]\n");
        code.Append("    public static nint __init()\n    {\n        try\n        {\n");
        var functions = string.Join(", ", extension.Functions.Select(f => $"(\"{f.Name}\", {EntryPoint}@{f.Name})"));
        code.Append($"            var @module = {PythonExtension}.CreateModule(\"{extension.Name}\", [{functions}]);\n");
        foreach (var exception in extension.Exceptions)
            code.Append($"            {PythonExtension}.AddException(@module, \"{extension.Name}\", \"{exception.Name}\", typeof({TypeName(exception.Type)}));\n");
        // Classes and enums as Python types (SPEC §17.5, M45).
        foreach (var exported in extension.Classes)
            code.Append(AddClass(extension.Name, exported, entries));
        foreach (var enumSymbol in extension.Enums)
            code.Append(AddEnum(extension.Name, enumSymbol, entries));
        code.Append($"            {_programPath}.{_topLevelMethod}();\n");
        foreach (var constant in extension.Constants)
            code.Append($"            {PythonExtension}.AddConstant(@module, \"{constant.Symbol.Name}\", {ToPython(constant.Symbol.Type, $"{_programPath}.@{_names[constant.Symbol]}", 0)});\n");
        code.Append($"            return {PythonExtension}.Return(@module);\n");
        code.Append($"        }}\n        catch (global::System.Exception @e)\n        {{\n            return {PythonExtension}.Raise(@e);\n        }}\n    }}\n");
        foreach (var function in extension.Functions)
        {
            code.Append(EntryPointFor($"@{function.Name}", function.Name, "@self", function.Parameters, function.Kinds,
                i => function.DefaultFor(i) is null ? null : $"{_programPath}.@{DefaultName(_names[function], function, i)}()",
                locals => $"{_programPath}.@{_names[function]}({string.Join(", ", locals)})", function.ReturnType));
        }
        code.Append(entries);
        code.Append("}\n");
        return code.ToString();
    }

    /// <summary>
    /// A class's Python type (SPEC §17.5): its constructor's entry point, its fields and properties
    /// as attributes, and an entry point per method, added to <paramref name="entries"/>.
    /// </summary>
    private string AddClass(string module, ExtensionClass exported, StringBuilder entries)
    {
        var symbol = exported.Symbol;
        var type = symbol.Type;
        var csType = TypeName(type);
        var construct = "0";
        if (!symbol.IsAbstract)
        {
            var entry = $"@__new_{symbol.Name}";
            construct = $"{EntryPoint}{entry}";
            if (symbol.IsDataclass)
            {
                // A dataclass's constructor takes its fields, inherited ones first (SPEC §4.3).
                var fields = symbol.AllFields;
                var parameters = fields.Select(f => new VariableSymbol(f.Name, TypeChecker.Substitute(f.Type, TypeChecker.MapOf(type)), VariableKind.Parameter)).ToImmutableArray();
                entries.Append(EntryPointFor(entry, symbol.Name, "@type", parameters, [.. parameters.Select(_ => Syntax.ParamKind.Normal)],
                    i => fields[i].Default is null ? null : $"{TypeName(OwnerType(fields[i], type))}.@{FieldDefaultName(ClassName(OwnerType(fields[i], type)), fields[i])}()",
                    locals => $"new {csType}({string.Join(", ", locals)})", type, $"{PythonExtension}.CheckConstructed(@type, typeof({csType}));"));
            }
            else
            {
                var constructor = symbol.Constructor;
                entries.Append(EntryPointFor(entry, symbol.Name, "@type", constructor.Parameters, constructor.Kinds,
                    i => constructor.DefaultFor(i) is null ? null : InitDefault(constructor, i, type),
                    locals => $"{csType}.@__new__({string.Join(", ", locals)})", type, $"{PythonExtension}.CheckConstructed(@type, typeof({csType}));"));
            }
        }
        var attributes = new List<string>();
        foreach (var field in exported.Fields)
        {
            var read = $"(({csType})@o).@{Member(field, type)}";
            var setter = symbol.IsFrozen ? "null" : $"static (object @o, nint @v) => {read} = {Assigned(symbol.Name, field.Name, field.Type, "@v")}";
            attributes.Add($"new(\"{field.Name}\", static (object @o) => {ToPython(field.Type, read, 1)}, {setter})");
        }
        foreach (var property in exported.Properties)
        {
            var target = $"(({csType})@o)";
            var setter = property.Setter is null ? "null"
                : $"static (object @o, nint @v) => {target}.@{property.Name}__set({Assigned(symbol.Name, property.Name, property.ReturnType, "@v")})";
            attributes.Add($"new(\"{property.Name}\", static (object @o) => {ToPython(property.ReturnType, $"{target}.@{property.Name}__get()", 1)}, {setter})");
        }
        var methods = new List<string>();
        foreach (var method in exported.Methods)
        {
            var entry = $"@{symbol.Name}_{method.Name}";
            methods.Add($"(\"{method.Name}\", {EntryPoint}{entry})");
            entries.Append(EntryPointFor(entry, method.Name, "@self", method.Parameters, method.Kinds,
                i => method.DefaultFor(i) is null ? null : $"@target.@{MethodDefaultName(method, i)}()",
                locals => $"@target.@{MethodName(method)}({string.Join(", ", locals)})", method.ReturnType,
                $"var @target = {PythonExtension}.Self<{csType}>(@self);"));
        }
        return $"            {PythonExtension}.AddClass(@module, \"{module}\", \"{symbol.Name}\", typeof({csType}), {construct}, "
            + $"[{string.Join(", ", attributes)}], [{string.Join(", ", methods)}], {(symbol.IsHashable ? "true" : "false")}, {(symbol.HasSubclasses ? "true" : "false")});\n";
    }

    /// <summary>An enum's Python type (SPEC §17.5): <c>name</c> and its variants' fields as attributes, and its variants as class attributes.</summary>
    private string AddEnum(string module, EnumSymbol symbol, StringBuilder entries)
    {
        var type = symbol.Type;
        var csType = TypeName(type);
        var attributes = new List<string> { $"new(\"name\", static (object @o) => {PythonExtension}.FromStr(@o.GetType().Name), null)" };
        foreach (var name in symbol.Variants.SelectMany(v => v.Fields).Select(f => f.Name).Distinct())
        {
            var arms = symbol.Variants.Where(v => v.Fields.Any(f => f.Name == name)).Select(v =>
            {
                var field = v.Fields.First(f => f.Name == name);
                return $"{VariantTypeName(v, type)} @value => {ToPython(field.Type, $"@value.@{FieldName(v.Name, field)}", 1)}";
            });
            attributes.Add($"new(\"{name}\", static (object @o) => @o switch {{ {string.Join(", ", arms)}, _ => throw {PythonExtension}.NoAttribute(\"{symbol.Name}\", @o, \"{name}\") }}, null)");
        }
        var variants = new List<string>();
        foreach (var variant in symbol.Variants)
        {
            if (variant.Fields.IsEmpty)
            {
                variants.Add($"(\"{variant.Name}\", new {VariantTypeName(variant, type)}(), 0)");
                continue;
            }
            var entry = $"@{symbol.Name}_{variant.Name}";
            variants.Add($"(\"{variant.Name}\", null, {EntryPoint}{entry})");
            var parameters = variant.Fields.Select(f => new VariableSymbol(f.Name, f.Type, VariableKind.Parameter)).ToImmutableArray();
            entries.Append(EntryPointFor(entry, variant.Name, "@self", parameters, [.. parameters.Select(_ => Syntax.ParamKind.Normal)], _ => null,
                locals => $"new {VariantTypeName(variant, type)}({string.Join(", ", locals)})", type));
        }
        return $"            {PythonExtension}.AddEnum(@module, \"{module}\", \"{symbol.Name}\", typeof({csType}), [{string.Join(", ", attributes)}], [{string.Join(", ", variants)}]);\n";
    }

    // An attribute's new value, converted to its type, or a TypeError naming both (SPEC §17.5).
    private string Assigned(string owner, string attribute, TaipanType type, string value) =>
        $"{PythonExtension}.Assigned(\"{owner}\", \"{attribute}\", \"{Escape(TypeChecker.Unaliased(type).ToString())}\", {value}, static @x => {ToTaipan(type, "@x", 2)})";

    /// <summary>
    /// An entry point taking <c>(first, args, kwargs)</c>, as <c>METH_VARARGS | METH_KEYWORDS</c> and
    /// <c>tp_new</c> do (SPEC §17.3): it binds and converts the arguments, calls, and converts the result.
    /// </summary>
    private string EntryPointFor(string entry, string name, string first, ImmutableArray<VariableSymbol> parameters, ImmutableArray<Syntax.ParamKind> kinds,
        Func<int, string?> defaultFor, Func<List<string>, string> call, TaipanType result, string? prologue = null)
    {
        var names = string.Join(", ", parameters.Select(p => $"\"{p.Name}\""));
        var kindCodes = string.Join(", ", kinds.Select(k => k switch
        {
            Syntax.ParamKind.PositionalOnly => "1",
            Syntax.ParamKind.KeywordOnly => "2",
            Syntax.ParamKind.VarArgs => "3",
            Syntax.ParamKind.VarKeywords => "4",
            _ => "0",
        }));
        var defaults = string.Join(", ", parameters.Select((_, i) => defaultFor(i) is null ? "false" : "true"));
        string Convert(TaipanType type, string parameter, string value) =>
            $"{PythonExtension}.Argument(\"{name}\", \"{parameter}\", \"{Escape(TypeChecker.Unaliased(type).ToString())}\", {value}, static @v => {ToTaipan(type, "@v", 1)})";
        var arguments = parameters.Select((p, i) => kinds[i] switch
        {
            // Each collected argument converts to the element type (SPEC §17.3, M30).
            Syntax.ParamKind.VarArgs => $"global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Select(@extra, @x => {Convert(((ListOfType)p.Type).Element, p.Name, "@x")}))",
            Syntax.ParamKind.VarKeywords => $"{PythonExtension}.Keywords(@extraKeywords, @x => {Convert(((DictOfType)p.Type).Value, p.Name, "@x")})",
            _ when defaultFor(i) is not { } fallback => Convert(p.Type, p.Name, $"@arguments[{i}]"),
            _ => $"@arguments[{i}] == 0 ? {defaultFor(i)} : {Convert(p.Type, p.Name, $"@arguments[{i}]")}",
        });
        // Each argument in its own local, named after its parameter, in parameter order.
        var locals = parameters.Select(p => $"@p_{p.Name}").ToList();
        var code = new StringBuilder();
        code.Append("\n    [global::System.Runtime.InteropServices.UnmanagedCallersOnly]\n");
        code.Append($"    public static nint {entry}(nint {first}, nint @args, nint @kwargs)\n    {{\n        try\n        {{\n");
        if (prologue is not null)
            code.Append($"            {prologue}\n");
        code.Append($"            var @arguments = {PythonExtension}.Arguments(@args, @kwargs, \"{name}\", [{names}], [{kindCodes}], [{defaults}], out var @extra, out var @extraKeywords);\n");
        foreach (var (local, argument) in locals.Zip(arguments))
            code.Append($"            var {local} = {argument};\n");
        if (result is NothingType)
        {
            code.Append($"            {call(locals)};\n");
            code.Append($"            return {PythonExtension}.Return({PythonExtension}.None());\n");
        }
        else
        {
            code.Append($"            return {PythonExtension}.Return({ToPython(result, call(locals), 0)});\n");
        }
        code.Append($"        }}\n        catch (global::System.Exception @e)\n        {{\n            return {PythonExtension}.Raise(@e);\n        }}\n    }}\n");
        return code.ToString();
    }

    /// <summary>A Python object (<paramref name="value"/>, a borrowed reference) as a Taipan value of <paramref name="type"/> (SPEC §17.3).</summary>
    private string ToTaipan(TaipanType type, string value, int depth)
    {
        var element = $"@e{depth}";
        return type switch
        {
            PrimitiveType { Name: "int" } => $"{PythonExtension}.Int({value})",
            PrimitiveType { Name: "float" } => $"{PythonExtension}.Float({value})",
            PrimitiveType { Name: "bool" } => $"{PythonExtension}.Bool({value})",
            PrimitiveType { Name: "str" } => $"{PythonExtension}.Str({value})",
            OptionalOf optional => $"{PythonExtension}.{(IsValueType(optional.Inner) ? "OptionalValue" : "Optional")}({value}, static {element} => {ToTaipan(optional.Inner, element, depth + 1)})",
            ListOfType list => $"{PythonExtension}.List({value}, static {element} => {ToTaipan(list.Element, element, depth + 1)})",
            DictOfType dict => $"{PythonExtension}.Dict({value}, static {element} => {ToTaipan(dict.Key, element, depth + 1)}, static {element} => {ToTaipan(dict.Value, element, depth + 1)})",
            TupleOfType tuple => $"({string.Join(", ", tuple.Elements.Select((e, i) => ToTaipan(e, $"{PythonExtension}.Item({value}, {i}, {tuple.Elements.Length})", depth + 1)))})",
            // Since M45: the module's Python types, Python objects, and Python callables (SPEC §17.5, §17.6).
            ClassType or EnumType => $"{PythonExtension}.Unwrap<{TypeName(type)}>({value})",
            PrimitiveType { Name: "py" } => $"{PythonExtension}.Handle({value})",
            FunctionType function => PythonCallable(function, value, depth),
            _ => throw new InvalidOperationException($"{type} can't come from Python"),
        };
    }

    /// <summary>A Taipan value of <paramref name="type"/> as a new Python reference (SPEC §17.3).</summary>
    private string ToPython(TaipanType type, string value, int depth)
    {
        var element = $"@e{depth}";
        return type switch
        {
            PrimitiveType { Name: "int" } => $"{PythonExtension}.FromInt({value})",
            PrimitiveType { Name: "float" } => $"{PythonExtension}.FromFloat({value})",
            PrimitiveType { Name: "bool" } => $"{PythonExtension}.FromBool({value})",
            PrimitiveType { Name: "str" } => $"{PythonExtension}.FromStr({value})",
            OptionalOf optional => $"{PythonExtension}.{(IsValueType(optional.Inner) ? "FromOptionalValue" : "FromOptional")}({value}, static {element} => {ToPython(optional.Inner, element, depth + 1)})",
            ListOfType list => $"{PythonExtension}.FromList({value}, static {element} => {ToPython(list.Element, element, depth + 1)})",
            DictOfType dict => $"{PythonExtension}.FromDict({value}, static {element} => {ToPython(dict.Key, element, depth + 1)}, static {element} => {ToPython(dict.Value, element, depth + 1)})",
            // A tuple's value is used once per element, so it's a lambda's parameter.
            TupleOfType tuple => $"((global::System.Func<{TypeName(tuple)}, nint>)(static {element} => {PythonExtension}.FromTuple({string.Join(", ", tuple.Elements.Select((e, i) => ToPython(e, $"{element}.Item{i + 1}", depth + 1)))})))({value})",
            ClassType or EnumType => $"{PythonExtension}.Wrap({value})",
            PrimitiveType { Name: "py" } => $"{PythonExtension}.FromHandle({value})",
            _ => throw new InvalidOperationException($"{type} can't go to Python"),
        };
    }

    /// <summary>
    /// A Python callable as a Taipan function value (SPEC §17.6): calling it converts the arguments to
    /// Python, calls the object, and converts what it returns.
    /// </summary>
    private string PythonCallable(FunctionType function, string value, int depth)
    {
        var handle = $"@h{depth}";
        var parameters = function.Parameters.Select((p, i) => (Name: $"@a{depth}_{i}", Type: p)).ToList();
        var call = $"{PythonExtension}.Call({handle}{string.Concat(parameters.Select(p => $", {ToPython(p.Type, p.Name, depth + 1)}"))})";
        var body = function.Result is NothingType
            ? $"{PythonExtension}.Discard({call})"
            : $"{PythonExtension}.Returned({call}, \"{Escape(TypeChecker.Unaliased(function.Result).ToString())}\", static @r{depth} => {ToTaipan(function.Result, $"@r{depth}", depth + 1)})";
        var lambda = $"({string.Join(", ", parameters.Select(p => $"{TypeName(p.Type)} {p.Name}"))}) => {body}";
        return $"((global::System.Func<{TypeName(TaipanType.Py)}, {TypeName(function)}>)(static {handle} => new {TypeName(function)}(\"<python function>\", {lambda})))({PythonExtension}.Handle({value}))";
    }
}
