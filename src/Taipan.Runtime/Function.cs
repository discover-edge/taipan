namespace Taipan.Runtime;

/// <summary>A function value, for display (SPEC §3.7).</summary>
public interface IFunction
{
    /// <summary>The function's name, or <c>&lt;lambda&gt;</c>.</summary>
    string Name { get; }
}

/// <summary>
/// A function value: a delegate with its display name. Equality is identity, so each evaluation of
/// a lambda or nested <c>def</c> creates a new object (SPEC §3.7, §8).
/// </summary>
public sealed class Function<TDelegate>(string name, TDelegate invoke) : IFunction where TDelegate : Delegate
{
    public string Name { get; } = name;

    public TDelegate Invoke { get; } = invoke;

    public override string ToString() => Display.Format(this);
}
