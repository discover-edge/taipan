namespace Taipan.Runtime;

/// <summary>Implemented by every compiled dataclass, for display and structural equality (SPEC §6, §5.3).</summary>
public interface IStruct
{
    /// <summary>The runtime class's name.</summary>
    string TypeName { get; }

    /// <summary>Fields in constructor order, inherited ones first.</summary>
    IReadOnlyList<(string Name, object? Value)> Fields { get; }
}

/// <summary>Implemented by every compiled plain class (SPEC §4.3): it displays by class name and compares by identity.</summary>
public interface IObject
{
    /// <summary>The runtime class's name.</summary>
    string TypeName { get; }
}

/// <summary>Implemented by every enum variant. A variant without a payload displays as its bare name.</summary>
public interface IVariant : IStruct;
