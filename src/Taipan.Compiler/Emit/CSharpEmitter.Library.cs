using System.Text;
using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// A program emitted as a .NET library (SPEC §14, M22). The program class becomes an internal
// TaipanLibrary holding the implementation; each module is a public static class of wrappers
// that initialize the module and forward to it, with the module's classes and enums nested inside.
public sealed partial class CSharpEmitter
{
    private readonly BoundLibrary? _library;
    private readonly Dictionary<Symbol, BoundModule> _moduleOf = [];
    private readonly Dictionary<BoundModule, (string Init, string State)> _initNames = [];
    private readonly Dictionary<BoundModule, StringBuilder> _moduleTypes = [];
    private string _lockName = "";
    private string _hiddenName = "";

    // The module whose code is being emitted, for its initializer, and the accessibility of the type being emitted.
    private BoundModule? _currentModule;
    private string _typeAccess = "public";

    private void SetUpLibrary(BoundLibrary library)
    {
        foreach (var module in library.Modules)
        {
            IEnumerable<Symbol> definitions =
                [.. module.Functions, .. module.Classes, .. module.Enums, .. module.Constants.Select(c => c.Symbol)];
            foreach (var definition in definitions)
                _moduleOf[definition] = module;
            _initNames[module] = (UniqueGlobal($"__init_{module.Name}"), UniqueGlobal($"__state_{module.Name}"));
            _globalNames.Add(_initNames[module].Init);
            _globalNames.Add(_initNames[module].State);
            _moduleTypes[module] = new StringBuilder();
        }
        // An exported class or enum is nested in its module's class, by its own name (SPEC §14.1).
        foreach (var symbol in library.Exported.Where(s => s is ClassSymbol or EnumSymbol))
            _names[symbol] = symbol.Name;
        _lockName = UniqueGlobal("__lock");
        _globalNames.Add(_lockName);
        _hiddenName = UniqueGlobal("TaipanHidden");
        _globalNames.Add(_hiddenName);
    }

    private bool IsNetGeneric(Symbol symbol) => _library?.Generic.Contains(symbol) == true;

    private bool IsExported(Symbol symbol) => _library?.Exported.Contains(symbol) == true;

    /// <summary>A class or enum's C# type, without type arguments.</summary>
    private string TypePath(Symbol symbol)
    {
        if (_library is null || symbol is ClassSymbol { RuntimeName: not null })
            return $"global::@{_names[symbol]}";
        return IsExported(symbol)
            ? $"global::{_library.Namespace}.@{_moduleOf[symbol].Name}.@{_names[symbol]}"
            : $"global::{_library.Namespace}.@{_names[symbol]}";
    }

    private string InstancePath(string name) => _library is null ? $"global::@{name}" : $"global::{_library.Namespace}.@{name}";

    /// <summary>A field or method's accessibility: in a library, a name starting with <c>_</c> is internal (SPEC §14.1).</summary>
    private string MemberAccess(string name) => _library is not null && name.StartsWith('_') ? "internal" : "public";

    // Default helpers, __new__ and the like aren't a library's API.
    private string HelperAccess => _library is null ? "public" : "internal";

    private string TypeParameterList(ClassSymbol symbol) => TypeParameterList(symbol.TypeParameters, IsNetGeneric(symbol));

    private static string TypeParameterList(IEnumerable<TypeParameterSymbol> parameters, bool generic = true) =>
        generic && parameters.Any() ? $"<{string.Join(", ", parameters.Select(p => $"@{p.Name}"))}>" : "";

    private string InitCall(BoundModule module) => $"{_programPath}.@{_initNames[module].Init}()";

    private string HiddenType => $"global::{_library!.Namespace}.@{_hiddenName}";

    private string HiddenArgument => $"default({HiddenType})";

    private string EmitLibrary()
    {
        var library = _library!;
        _indent = 1;
        Line($"private static readonly object @{_lockName} = new();");
        foreach (var module in library.Modules)
            Line($"private static int @{_initNames[module].State};");
        foreach (var import in _program.PythonImports)
            Line($"public static {PyObject} @{_names[import.Symbol]} = default!;");
        foreach (var constant in _program.Constants)
            Line($"public static {TypeName(constant.Symbol.Type)} @{_names[constant.Symbol]} = default!;");
        foreach (var module in library.Modules)
            EmitInit(module);

        foreach (var function in _program.Functions.Where(f => f.Symbol.TypeParameters.IsEmpty || IsNetGeneric(f.Symbol)))
        {
            _currentModule = _moduleOf[function.Symbol];
            Line();
            EmitFunction(function, isMethod: false, _names[function.Symbol]);
        }

        foreach (var module in library.Modules)
        {
            _currentModule = module;
            foreach (var classDef in _program.Classes.Where(c => module.Classes.Contains(c.Symbol)))
            {
                if (classDef.Symbol.TypeParameters.IsEmpty || IsNetGeneric(classDef.Symbol))
                    EmitLibraryType(classDef.Symbol, () => EmitClass(classDef, _names[classDef.Symbol]));
            }
            foreach (var enumSymbol in module.Enums)
            {
                if (enumSymbol.TypeParameters.IsEmpty || IsNetGeneric(enumSymbol))
                    EmitLibraryType(enumSymbol, () => EmitEnum(enumSymbol, _names[enumSymbol]));
            }
        }

        while (_pending.TryDequeue(out var instance))
            EmitInstance(instance);
        EmitProtocols();
        PatchProtocols(_typesOut);
        foreach (var types in _moduleTypes.Values)
            PatchProtocols(types);

        _out = _functionsOut;
        _indent = 1;
        if (_functionValues.Count > 0 || _cachedLambdaFields.Count > 0)
            Line();
        foreach (var (function, (field, type)) in _functionValues)
            Line($"public static readonly {TypeName(type)} @{field} = new(\"{function.Name}\", @{_names[function]});");
        foreach (var (field, type, value) in _cachedLambdaFields)
            Line($"public static readonly {type} @{field} = {value};");

        var code = new StringBuilder();
        code.Append($"// Generated by taipan from {_source.Path}. Do not edit.\n");
        code.Append("#nullable enable\n#line hidden\n\n");
        code.Append($"namespace {library.Namespace};\n\n");
        code.Append($"internal readonly struct @{_hiddenName};\n\n");
        code.Append($"internal static class {_programClass}\n{{\n");
        code.Append(_functionsOut);
        code.Append("}\n");
        foreach (var module in library.Modules)
        {
            code.Append($"\npublic static class @{module.Name}\n{{\n");
            var wrappers = Wrappers(module);
            code.Append(wrappers);
            if (wrappers.Length > 0 && _moduleTypes[module].Length > 0)
                code.Append('\n');
            code.Append(_moduleTypes[module]);
            code.Append("}\n");
        }
        code.Append(_typesOut);
        return code.ToString();
    }

    // An exported class or enum goes in its module's class; any other is internal, beside it.
    private void EmitLibraryType(Symbol symbol, Action emit)
    {
        var exported = IsExported(symbol);
        _out = exported ? _moduleTypes[_currentModule!] : _typesOut;
        _indent = exported ? 1 : 0;
        _typeAccess = exported ? "public" : "internal";
        if (_out.Length > 0)
            Line();
        emit();
        _typeAccess = "public";
    }

    /// <summary>
    /// A module's initializer (SPEC §14.3): once, it initializes the modules it imports, imports its
    /// Python modules and evaluates its constants. A module being initialized on this thread (a
    /// cycle) counts as done; a failure leaves it to be tried again.
    /// </summary>
    private void EmitInit(BoundModule module)
    {
        var (init, state) = _initNames[module];
        _locals = LocalNames.For([], new BoundBlock([], default), _globalNames);
        Line();
        Line($"public static void @{init}()");
        Line("{");
        _indent++;
        Line($"if (global::System.Threading.Volatile.Read(ref @{state}) == 2)");
        Line("    return;");
        Line($"lock (@{_lockName})");
        Line("{");
        _indent++;
        Line($"if (@{state} != 0)");
        Line("    return;");
        Line($"@{state} = 1;");
        Line("try");
        Line("{");
        _indent++;
        foreach (var imported in module.Imports)
            Line($"@{_initNames[_library!.Modules.First(m => m.Name == imported)].Init}();");
        foreach (var import in module.PythonImports)
        {
            _lineSource = import.Source;
            LineDirective(import.Span);
            Line($"@{_names[import.Symbol]} = {Interop}.Import(\"{import.Module}\");");
        }
        foreach (var constant in module.Constants)
        {
            _lineSource = constant.Source ?? _source;
            LineDirective(constant.Span);
            Line($"@{_names[constant.Symbol]} = {Expr(constant.Value)};");
        }
        HiddenDirective();
        _lineSource = _source;
        Line($"global::System.Threading.Volatile.Write(ref @{state}, 2);");
        _indent--;
        Line("}");
        Line("catch");
        Line("{");
        Line($"    @{state} = 0;");
        Line("    throw;");
        Line("}");
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    /// <summary>
    /// A module's public API (SPEC §14.2): each exported constant as a property and each exported
    /// function as a method, with an overload for each trailing default, all initializing the
    /// module before forwarding to the implementation.
    /// </summary>
    private string Wrappers(BoundModule module)
    {
        var saved = (_out, _indent);
        _out = new StringBuilder();
        _indent = 1;
        var init = InitCall(module);
        foreach (var constant in module.Constants.Where(c => IsExported(c.Symbol)))
            Line($"public static {TypeName(constant.Symbol.Type)} @{constant.Symbol.Name} {{ get {{ {init}; return {_programPath}.@{_names[constant.Symbol]}; }} }}");
        foreach (var function in module.Functions.Where(IsExported))
        {
            var typeParameters = IsNetGeneric(function) ? TypeParameterList(function.TypeParameters) : "";
            var name = $"{_programPath}.@{_names[function]}";
            for (var count = function.Parameters.Length; count >= FirstTrailingDefault(function); count--)
            {
                var parameters = function.Parameters.Take(count);
                var arguments = parameters.Select((p, i) => LibraryArgument(function, i))
                    .Concat(Enumerable.Range(count, function.Parameters.Length - count).Select(i => $"{_programPath}.@{DefaultName(_names[function], function, i)}{typeParameters}()"));
                Line();
                Line($"public static {TypeName(function.ReturnType)} @{function.Name}{typeParameters}({LibraryParameters(function, count)})");
                Line("{");
                Line($"    {init};");
                Line($"    {(function.ReturnType is NothingType ? "" : "return ")}{name}{typeParameters}({string.Join(", ", arguments)});");
                Line("}");
            }
        }
        var code = _out.ToString();
        (_out, _indent) = saved;
        return code;
    }

    /// <summary>
    /// A function's first <paramref name="count"/> parameters in a library's signature (SPEC §14.2):
    /// <c>*args</c> is a <c>List&lt;T&gt;</c>, <c>params</c> when it's last; <c>**kwargs</c> a dict, where null means none.
    /// </summary>
    private string LibraryParameters(FunctionSymbol function, int count) =>
        string.Join(", ", function.Parameters.Take(count).Select((p, i) =>
            $"{(i == count - 1 && function.Kinds[i] == Syntax.ParamKind.VarArgs ? "params " : "")}{TypeName(p.Type)}{(function.Kinds[i] == Syntax.ParamKind.VarKeywords ? "?" : "")} @{p.Name}"));

    // A library caller's argument as Taipan passes it: a null **kwargs is an empty dict.
    private static string LibraryArgument(FunctionSymbol function, int parameter) =>
        function.Kinds[parameter] == Syntax.ParamKind.VarKeywords ? $"(@{function.Parameters[parameter].Name} ?? new())" : $"@{function.Parameters[parameter].Name}";

    // A public method's overloads for its trailing defaults, which an override inherits (SPEC §14.2).
    private void EmitMethodOverloads(FunctionSymbol method)
    {
        if (MemberAccess(method.Name) != "public")
            return;
        for (var count = method.Parameters.Length - 1; count >= FirstTrailingDefault(method); count--)
        {
            var given = method.Parameters.Take(count);
            var arguments = given.Select(p => $"@{p.Name}")
                .Concat(Enumerable.Range(count, method.Parameters.Length - count).Select(i => $"this.@{MethodDefaultName(method, i)}()"));
            Line();
            Line($"public {TypeName(method.ReturnType)} @{method.Name}({string.Join(", ", given.Select(p => $"{TypeName(p.Type)} @{p.Name}"))}) => this.@{method.Name}({string.Join(", ", arguments)});");
        }
    }

    // Parameters from this one on all have defaults.
    private static int FirstTrailingDefault(FunctionSymbol function)
    {
        var first = function.Parameters.Length;
        while (first > 0 && function.DefaultFor(first - 1) is not null)
            first--;
        return first;
    }

    /// <summary>
    /// A plain class's hidden constructor, for <c>__new__</c> and subclasses. In a library it takes
    /// a marker, so a public constructor without parameters doesn't clash with it.
    /// </summary>
    private void EmitHiddenConstructor(ClassSymbol symbol, string className)
    {
        var access = symbol.HasSubclasses ? "private protected" : "private";
        var baseCall = symbol.Base switch
        {
            null => "",
            { RuntimeName: not null } => " : base()",
            _ => " : base(hidden)",
        };
        Line($"{access} @{className}({HiddenType} hidden){baseCall} {{ }}");
    }

    // A plain class's public constructors run __init__, with an overload for each trailing default (SPEC §14.2).
    private void EmitPublicConstructors(ClassSymbol symbol, string className, string parameters, string arguments)
    {
        Line();
        Line($"public @{className}({parameters}) : this({HiddenArgument})");
        Line("{");
        Line($"    {InitCall(_currentModule!)};");
        Line($"    this.@__init__({arguments});");
        Line("}");
        var constructor = symbol.Constructor;
        for (var count = constructor.Parameters.Length - 1; count >= FirstTrailingDefault(constructor); count--)
        {
            var given = constructor.Parameters.Take(count);
            var all = given.Select(p => $"@{p.Name}")
                .Concat(Enumerable.Range(count, constructor.Parameters.Length - count).Select(i => InitDefault(constructor, i, symbol.Type)));
            Line();
            Line($"public @{className}({string.Join(", ", given.Select(p => $"{TypeName(p.Type)} @{p.Name}"))}) : this({string.Join(", ", all)}) {{ }}");
        }
    }

    // A dataclass's constructor overloads, for its trailing fields with defaults (SPEC §14.2).
    private void EmitDataclassOverloads(ClassSymbol symbol, string className)
    {
        var fields = symbol.AllFields;
        var first = fields.Length;
        while (first > 0 && fields[first - 1].HasDefault)
            first--;
        for (var count = fields.Length - 1; count >= first; count--)
        {
            var given = fields.Take(count);
            var all = given.Select(f => $"@{Member(f, symbol.Type)}")
                .Concat(fields.Skip(count).Select(f => $"{TypeName(OwnerType(f, symbol.Type))}.@{FieldDefaultName(ClassName(OwnerType(f, symbol.Type)), f)}()"));
            Line();
            Line($"public @{className}({string.Join(", ", given.Select(f => $"{TypeName(f.Type)} @{Member(f, symbol.Type)}"))}) : this({string.Join(", ", all)}) {{ }}");
        }
    }
}
