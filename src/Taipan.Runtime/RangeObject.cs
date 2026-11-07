using System.Collections;

namespace Taipan.Runtime;

/// <summary>A value of the type <c>range</c>, which goes through its numbers as often as asked (SPEC §6, M50).</summary>
public sealed class RangeObject : IEnumerable<long>
{
    private readonly long _start, _stop, _step;

    private RangeObject(long start, long stop, long step)
    {
        (_start, _stop, _step) = (start, stop, Ops.RangeStep(step));
    }

    public static RangeObject Of(long stop) => new(0, stop, 1);

    public static RangeObject Of(long start, long stop) => new(start, stop, 1);

    public static RangeObject Of(long start, long stop, long step) => new(start, stop, step);

    /// <summary>How many numbers it gives; too many for an <c>int</c> is an <c>OverflowError</c>, as in Python.</summary>
    public long Length
    {
        get
        {
            var span = _step > 0 ? (Int128)_stop - _start : (Int128)_start - _stop;
            var step = _step > 0 ? (Int128)_step : -(Int128)_step;
            var length = span <= 0 ? 0 : (span - 1) / step + 1;
            return length <= long.MaxValue ? (long)length : throw new OverflowError("integer overflow");
        }
    }

    private long Last => (long)(_start + (Int128)(Length - 1) * _step);

    public long At(long index)
    {
        var length = Length;
        var at = index < 0 ? index + length : index;
        if (at < 0 || at >= length)
            throw new IndexError($"index {index} out of range for length {length}");
        return (long)(_start + (Int128)at * _step);
    }

    public bool Contains(long value)
    {
        if (Length == 0 || (_step > 0 ? value < _start || value > Last : value > _start || value < Last))
            return false;
        return ((Int128)value - _start) % _step == 0;
    }

    public IEnumerator<long> GetEnumerator()
    {
        var value = _start;
        for (var left = Length; left > 0; left--)
        {
            yield return value;
            if (left > 1)
                value += _step;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IEnumerable<long> Backward()
    {
        var length = Length;
        for (var i = length - 1; i >= 0; i--)
            yield return (long)(_start + (Int128)i * _step);
    }

    // Two ranges are equal when they give the same numbers, as in Python: what they have in common is
    // the length, then the first number, then the step when there's more than one.
    public override bool Equals(object? other)
    {
        if (other is not RangeObject range)
            return false;
        var length = Length;
        return length == range.Length && (length == 0 || (_start == range._start && (length == 1 || _step == range._step)));
    }

    public override int GetHashCode()
    {
        var length = Length;
        return HashCode.Combine(length, length == 0 ? 0 : _start, length <= 1 ? 0 : _step);
    }

    public static bool operator ==(RangeObject left, RangeObject right) => left.Equals(right);

    public static bool operator !=(RangeObject left, RangeObject right) => !left.Equals(right);

    public override string ToString() => _step == 1 ? $"range({_start}, {_stop})" : $"range({_start}, {_stop}, {_step})";
}
