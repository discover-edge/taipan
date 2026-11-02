namespace Taipan.Runtime;

/// <summary>Implemented by every compiled dataclass, for display and structural equality (SPEC §6, §5.3).</summary>
public interface IStruct
{
    /// <summary>The runtime class's name.</summary>
    string TypeName { get; }

    /// <summary>Fields in constructor order, inherited ones first.</summary>
    IReadOnlyList<(string Name, object? Value)> Fields { get; }
}

/// <summary>
/// Implemented by every compiled class, through <see cref="IObject"/> or <see cref="IDataclass"/>:
/// the special methods display, equality, ordering, hashing and arithmetic call (SPEC §4.8, M28).
/// The defaults are what a class gets without them. A class that defines one implements it
/// explicitly, calling its method; each class lists its interface again, so its own implementation
/// is found first.
/// </summary>
public interface IClass
{
    /// <summary>Operations, as the hooks take them: the comparison methods, then the arithmetic ones.</summary>
    public const int Lt = 0, Le = 1, Gt = 2, Ge = 3;
    public const int Add = 0, Sub = 1, Mul = 2, TrueDiv = 3, FloorDiv = 4, Mod = 5;

    /// <summary><c>str()</c>: <c>__repr__</c>, unless the class has <c>__str__</c>.</summary>
    string __str__() => __repr__();

    /// <summary>The form inside a container: <c>&lt;Dog object&gt;</c>, or a dataclass's fields.</summary>
    string __repr__() => Display.DefaultRepr(this);

    /// <summary>
    /// The class's <c>__eq__</c> on <paramref name="other"/>, if it applies (SPEC §4.8). A dataclass's
    /// generated one applies to an object of the same runtime class.
    /// </summary>
    bool TryEq(object other, out bool result)
    {
        if (this is IStruct self && other is IStruct that && GetType() == other.GetType())
        {
            result = Equality.FieldsEqual(self, that);
            return true;
        }
        result = false;
        return false;
    }

    /// <summary>The class's <c>__lt__</c>, <c>__le__</c>, <c>__gt__</c> or <c>__ge__</c>, by <see cref="Lt"/> and so on, if it applies.</summary>
    bool TryCompare(int op, object other, out bool result)
    {
        result = false;
        return false;
    }

    /// <summary>The class's <c>__hash__</c>, if it has one.</summary>
    bool TryHash(out long hash)
    {
        hash = 0;
        return false;
    }

    /// <summary>The class's reflected arithmetic method (<c>__radd__</c>, …) by <see cref="Add"/> and so on, if it applies.</summary>
    bool TryReflected(int op, object other, out object? result)
    {
        result = null;
        return false;
    }

    /// <summary>The class that defines the reflected method the class has, for Python's subclass priority (SPEC §4.8).</summary>
    Type? ReflectedOwner(int op) => null;
}

/// <summary>Implemented by every compiled dataclass (SPEC §4.3).</summary>
public interface IDataclass : IStruct, IClass;

/// <summary>Implemented by every compiled plain class (SPEC §4.3): it displays by class name and compares by identity.</summary>
public interface IObject : IClass
{
    /// <summary>The runtime class's name.</summary>
    string TypeName { get; }
}

/// <summary>Implemented by every enum variant. A variant without a payload displays as its bare name.</summary>
public interface IVariant : IStruct;
