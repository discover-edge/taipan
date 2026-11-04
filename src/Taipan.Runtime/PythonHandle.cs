namespace Taipan.Runtime;

/// <summary>
/// A Python object an extension module holds as a <c>py</c> value (SPEC §17.6, M45): it keeps a
/// reference while Taipan holds it, and displays as Python's <c>str()</c>.
/// </summary>
public sealed class PythonHandle
{
    internal PythonHandle(nint pointer)
    {
        Pointer = pointer;
        PythonExtension.Keep(pointer);
    }

    internal nint Pointer { get; }

    ~PythonHandle() => PythonExtension.Release(Pointer);

    public override string ToString() => PythonExtension.Show(Pointer);
}
