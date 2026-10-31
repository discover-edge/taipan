using Taipan.Runtime;

namespace Taipan.Tests;

/// <summary>
/// Python-dependent tests pass trivially where no CPython is installed (SPEC §10.6). Setting
/// <c>TAIPAN_REQUIRE_PYTHON=1</c> (as CI does) turns a missing Python into a failure instead.
/// </summary>
public static class PythonAvailability
{
    public static bool Available()
    {
        if (Interop.FindLibrary() is not null)
            return true;
        if (Environment.GetEnvironmentVariable("TAIPAN_REQUIRE_PYTHON") == "1")
            throw new InvalidOperationException("TAIPAN_REQUIRE_PYTHON=1 but no Python installation was found");
        return false;
    }
}
