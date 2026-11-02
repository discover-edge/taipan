#nullable disable

namespace Acme.Geometry;

// Without nullable annotations, as older assemblies are (SPEC §13.3).
public static class Legacy
{
    public static string Name() => "legacy";

    public static string Missing() => null;
}
