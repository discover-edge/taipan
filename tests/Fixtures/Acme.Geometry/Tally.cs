using System;
using System.Collections.Generic;
using System.Linq;

namespace Acme.Geometry;

/// <summary>Dictionaries and delegates across the boundary (SPEC §13.3, §13.9, M33).</summary>
public static class Tally
{
    public static Dictionary<string, int> Doubled(Dictionary<string, int> counts) => counts.ToDictionary(p => p.Key, p => p.Value * 2);

    public static int Total(IReadOnlyDictionary<string, int> counts) => counts.Values.Sum();

    public static void Bump(IDictionary<string, long> counts, string key) => counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    public static Func<int, int> Adder(int amount) => x => x + amount;

    public static int Apply(Func<int, int> f, int x) => f(x);

    public static string Describe(Action<string> log)
    {
        log("one");
        log("two");
        return "done";
    }
}

/// <summary>A two-argument indexer (SPEC §13.10, M33).</summary>
public sealed class Grid(int rows, int columns)
{
    private readonly int[,] _cells = new int[rows, columns];

    public int this[int row, int column]
    {
        get => _cells[row, column];
        set => _cells[row, column] = value;
    }
}
