using System.Text;
using Taipan.Compiler.Semantics;

namespace Taipan.Compiler.Emit;

// Protocols as types (SPEC §3.18, §8, M40): each protocol type a program uses is a C# interface,
// and each class that satisfies it implements it. Which classes satisfy which protocols is known
// only once everything is emitted, so class headers and bodies hold markers filled in at the end.
public sealed partial class CSharpEmitter
{
    private readonly Dictionary<ClassType, string> _protocolNames = [];
    private readonly HashSet<ClassType> _emittedProtocols = [];
    private readonly List<(ClassType Type, string Name)> _emittedClasses = [];
    private readonly Dictionary<string, (List<string> Interfaces, StringBuilder Members)> _implementations = [];

    private static string InterfacesMarker(string className) => $"/*@interfaces:{className}@*/";

    private static string ImplementationsMarker(string className) => $"/*@implementations:{className}@*/";

    /// <summary>The C# interface for a protocol type, at concrete type arguments.</summary>
    private string ProtocolName(ClassType protocol)
    {
        if (!_protocolNames.TryGetValue(protocol, out var name))
        {
            name = UniqueGlobal(protocol.TypeArguments.IsEmpty
                ? protocol.Symbol.Name
                : $"{protocol.Symbol.Name}_{string.Join("_", protocol.TypeArguments.Select(Mangle))}");
            _globalNames.Add(name);
            _protocolNames[protocol] = name;
        }
        return name;
    }

    /// <summary>
    /// Emits the interfaces, then fills in each class's markers with the interfaces it satisfies
    /// and their explicit implementations, repeating while that brings in new types.
    /// </summary>
    private void EmitProtocols()
    {
        var done = new HashSet<(ClassType, ClassType)>();
        while (true)
        {
            var progress = false;
            foreach (var protocol in _protocolNames.Keys.Where(p => !_emittedProtocols.Contains(p)).ToList())
            {
                _emittedProtocols.Add(protocol);
                EmitInterface(protocol);
                progress = true;
            }
            foreach (var (classType, className) in _emittedClasses.ToList())
            {
                foreach (var protocol in _protocolNames.Keys.ToList())
                {
                    if (!done.Add((classType, protocol)) || Protocols.Unsatisfied(classType, protocol) is not null)
                        continue;
                    Implement(classType, className, protocol);
                    progress = true;
                }
            }
            while (_pending.TryDequeue(out var instance))
            {
                EmitInstance(instance);
                progress = true;
            }
            if (!progress)
                return;
        }
    }

    private void EmitInterface(ClassType protocol)
    {
        (_out, _indent, _map) = (_typesOut, 0, []);
        var self = Protocols.Required(protocol, protocol);
        Line();
        Line($"public interface @{_protocolNames[protocol]}");
        Line("{");
        _indent++;
        foreach (var field in protocol.Symbol.Fields)
            Line($"{TypeName(TypeChecker.Substitute(field.Type, self))} @{field.Name} {{ get; set; }}");
        foreach (var method in protocol.Symbol.Methods.Values)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{TypeName(TypeChecker.Substitute(p.Type, self))} @{p.Name}"));
            Line($"{TypeName(TypeChecker.Substitute(method.ReturnType, self))} @{method.Name}({parameters});");
        }
        _indent--;
        Line("}");
    }

    // The explicit implementations of a protocol's members in a class, each calling the class's own.
    private void Implement(ClassType classType, string className, ClassType protocol)
    {
        _map = [];
        if (!_implementations.TryGetValue(className, out var implementation))
            _implementations[className] = implementation = ([], new StringBuilder());
        var name = TypeName(protocol);
        implementation.Interfaces.Add(name);
        var self = Protocols.Required(protocol, protocol);
        foreach (var field in protocol.Symbol.Fields)
        {
            var property = Member(classType.Symbol.FindField(field.Name)!, classType);
            implementation.Members.Append($"    {TypeName(TypeChecker.Substitute(field.Type, self))} {name}.@{field.Name} {{ get => this.@{property}; set => this.@{property} = value; }}\n");
        }
        foreach (var method in protocol.Symbol.Methods.Values)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{TypeName(TypeChecker.Substitute(p.Type, self))} @{p.Name}"));
            var own = classType.Symbol.FindMethod(method.Name) is { } found ? MethodName(found) : method.Name;
            var call = $"this.@{own}({string.Join(", ", method.Parameters.Select(p => $"@{p.Name}"))})";
            var result = TypeChecker.Substitute(method.ReturnType, self);
            var body = result is NothingType ? $"{{ {call}; }}" : $"=> {call};";
            implementation.Members.Append($"    {TypeName(result)} {name}.@{method.Name}({parameters}) {body}\n");
        }
    }

    /// <summary>Fills in the markers in emitted code.</summary>
    private void PatchProtocols(StringBuilder code)
    {
        foreach (var (_, className) in _emittedClasses)
        {
            var found = _implementations.TryGetValue(className, out var implementation);
            code.Replace(InterfacesMarker(className), found ? string.Concat(implementation.Interfaces.Select(i => $", {i}")) : "");
            code.Replace(ImplementationsMarker(className), found ? $"\n{implementation.Members}" : "");
        }
    }
}
