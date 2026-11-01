namespace Taipan.Runtime;

/// <summary>A function value, for display (SPEC §3.7).</summary>
public interface IFunction
{
    /// <summary>The function's name, or <c>&lt;lambda&gt;</c>.</summary>
    string Name { get; }

    /// <summary>What equality compares: the function this value is, or was converted from (SPEC §3.3).</summary>
    object Identity { get; }
}

/// <summary>
/// A function value: a delegate with its display name. Equality is identity, so each evaluation of
/// a lambda or nested <c>def</c> creates a new object (SPEC §3.7, §8).
/// </summary>
public sealed class Function<TDelegate> : IFunction where TDelegate : Delegate
{
    public Function(string name, TDelegate invoke)
    {
        Name = name;
        Invoke = invoke;
        Identity = this;
    }

    private Function(string name, TDelegate invoke, object identity)
    {
        Name = name;
        Invoke = invoke;
        Identity = identity;
    }

    public string Name { get; }

    public TDelegate Invoke { get; }

    public object Identity { get; }

    /// <summary>
    /// <paramref name="source"/> as another function type it's assignable to (SPEC §3.3): its
    /// delegate wrapped by <paramref name="adapt"/>, still equal to it and displayed the same.
    /// </summary>
    public static Function<TDelegate> Convert<TSource>(Function<TSource> source, Func<TSource, TDelegate> adapt) where TSource : Delegate =>
        new(source.Name, adapt(source.Invoke), source.Identity);

    public override string ToString() => Display.Format(this);
}
