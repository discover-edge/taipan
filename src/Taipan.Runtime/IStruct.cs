namespace Taipan.Runtime;

/// <summary>Implemented by every compiled struct, for display and structural equality (SPEC §6, §5.3).</summary>
public interface IStruct
{
    string TypeName { get; }

    /// <summary>Fields in declaration order.</summary>
    IReadOnlyList<(string Name, object? Value)> Fields { get; }
}
