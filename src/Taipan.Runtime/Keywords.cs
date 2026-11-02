namespace Taipan.Runtime;

/// <summary>
/// The dict a call builds for a function's <c>**kwargs</c> (SPEC §4.2, M30): its keywords and the
/// dicts unpacked into it, in order. A key naming one of the function's parameters, or given
/// twice, is Python's <c>TypeError</c>.
/// </summary>
public sealed class Keywords<T>(string function, string[] reserved)
{
    public OrderedDictionary<string, T> Dict { get; } = [];

    public Keywords<T> Add(string key, T value)
    {
        if (reserved.Contains(key))
            throw new TypeError($"{function}() got multiple values for argument '{key}'");
        if (Dict.ContainsKey(key))
            throw new TypeError($"{function}() got multiple values for keyword argument '{key}'");
        Dict[key] = value;
        return this;
    }

    public Keywords<T> Spread(OrderedDictionary<string, T> entries)
    {
        foreach (var (key, value) in entries)
            Add(key, value);
        return this;
    }
}
