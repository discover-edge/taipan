using System;

namespace Acme.Geometry;

public sealed class Point(double x, double y)
{
    public double X { get; } = x;

    public double Y { get; } = y;

    public double DistanceTo(Point other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));

    public override string ToString() => $"({X}, {Y})";
}

public class GeometryException(string message) : Exception(message);

public static class Shapes
{
    public static double Area(double width, double height) =>
        width < 0 || height < 0 ? throw new GeometryException($"negative size {width} x {height}") : width * height;

    // Neither fits two ints better than the other (SPEC §13.5).
    public static string Describe(long a, int b) => "long, int";

    public static string Describe(int a, long b) => "int, long";
}
