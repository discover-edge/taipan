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
        code.Append($"\npublic static unsafe class {className}\n{{\n");
        code.Append($"    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = \"PyInit_{extension.Name}\")]\n");
        code.Append("    public static nint __init()\n    {\n        try\n        {\n");
        var functions = string.Join(", ", extension.Functions.Select(f => $"(\"{f.Name}\", {EntryPoint}@{f.Name})"));
        code.Append($"            var @module = {PythonExtension}.CreateModule(\"{extension.Name}\", [{functions}]);\n");
        foreach (var exception in extension.Exceptions)
            code.Append($"            {PythonExtension}.AddException(@module, \"{extension.Name}\", \"{exception.Name}\", typeof({TypeName(exception.Type)}));\n");
        code.Append($"            {_programPath}.{_topLevelMethod}();\n");
        foreach (var constant in extension.Constants)
            code.Append($"            {PythonExtension}.AddConstant(@module, \"{constant.Symbol.Name}\", {ToPython(constant.Symbol.Type, $"{_programPath}.@{_names[constant.Symbol]}", 0)});\n");
        code.Append($"            return {PythonExtension}.Return(@module);\n");
        code.Append($"        }}\n        catch (global::System.Exception @e)\n        {{\n            return {PythonExtension}.Raise(@e);\n        }}\n    }}\n");
        foreach (var function in extension.Functions)
            code.Append(EntryPointFor(function));
        code.Append("}\n");
        return code.ToString();
    }

    // An exported function's entry point: (self, args, kwargs), as METH_VARARGS | METH_KEYWORDS (SPEC §17.3).
    private string EntryPointFor(FunctionSymbol function)
    {
        var parameters = function.Parameters;
        var required = 0;
        while (required < parameters.Length && function.DefaultFor(required) is null)
            required++;
        var names = string.Join(", ", parameters.Select(p => $"\"{p.Name}\""));
        var arguments = parameters.Select((p, i) =>
        {
            var converted = $"{PythonExtension}.Argument(\"{function.Name}\", \"{p.Name}\", \"{Escape(p.Type.ToString())}\", @arguments[{i}], static @v => {ToTaipan(p.Type, "@v", 1)})";
            return function.DefaultFor(i) is null ? converted : $"@arguments[{i}] == 0 ? {_programPath}.@{DefaultName(_names[function], function, i)}() : {converted}";
        });
        // Each argument in its own local, named after its parameter, in parameter order.
        var locals = parameters.Select(p => $"@p_{p.Name}").ToList();
        var call = $"{_programPath}.@{_names[function]}({string.Join(", ", locals)})";
        var code = new StringBuilder();
        code.Append("\n    [global::System.Runtime.InteropServices.UnmanagedCallersOnly]\n");
        code.Append($"    public static nint @{function.Name}(nint @self, nint @args, nint @kwargs)\n    {{\n        try\n        {{\n");
        code.Append($"            var @arguments = {PythonExtension}.Arguments(@args, @kwargs, \"{function.Name}\", [{names}], {required});\n");
        foreach (var (local, argument) in locals.Zip(arguments))
            code.Append($"            var {local} = {argument};\n");
        if (function.ReturnType is NothingType)
        {
            code.Append($"            {call};\n");
            code.Append($"            return {PythonExtension}.Return({PythonExtension}.None());\n");
        }
        else
        {
            code.Append($"            return {PythonExtension}.Return({ToPython(function.ReturnType, call, 0)});\n");
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
            _ => throw new InvalidOperationException($"{type} can't go to Python"),
        };
    }
}
