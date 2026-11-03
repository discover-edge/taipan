using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Taipan.Runtime;

/// <summary>How a generator is resumed (SPEC §3.20): by <c>next</c>, <c>send</c>, or <c>throw</c> and <c>close</c>.</summary>
public enum Resume { Next, Send, Throw }

/// <summary>
/// Taipan's <c>Generator[Y, S, R]</c> (SPEC §3.20, M42): an iterator that also receives <c>S</c>s
/// and returns an <c>R</c>. It's covariant in what it yields and returns, as Python's is.
/// </summary>
public interface IGenerator<out Y, in S, out R> : IIterator<Y>
{
    Y send(S value);

    Y @throw(TaipanBaseException exception);

    void close();

    /// <summary>One step, for <c>yield from</c> (PEP 380): true if it yielded <see cref="IIterator{T}.Current"/>, false if it returned <see cref="Result"/>.</summary>
    bool Step(Resume how, S value, TaipanBaseException? thrown);

    /// <summary>What it returned, once a step gives false.</summary>
    R Result { get; }
}

/// <summary>A generator, whose end gives a <c>StopIteration</c> with its return value (SPEC §3.20).</summary>
internal interface IReturning
{
    StopIteration Stop();
}

/// <summary>
/// The body's side of a generator (SPEC §8): <c>yield e</c> is <c>await channel.Yield(e)</c>, whose
/// awaiter hands <c>e</c> to whatever resumed the body and gives back what it's resumed with.
/// </summary>
public sealed class GeneratorChannel<Y, S>
{
    internal Y Yielded = default!;
    internal Action? Continuation;
    internal bool WantsValue;
    internal S Sent = default!;
    internal Exception? Thrown;

    /// <summary>The <c>yield from</c> the body is paused at, which values are passed through to.</summary>
    internal Delegation<Y>? Delegate;
    internal object? DelegateResult;

    /// <summary>A <c>yield</c> whose value isn't used.</summary>
    public YieldAwaitable<Y, S> Yield(Y value) => new(this, value, used: false);

    /// <summary>A <c>yield</c> whose value is used, which <c>next</c> can resume only when <c>S</c> accepts None.</summary>
    public YieldAwaitable<Y, S> Receive(Y value) => new(this, value, used: true);

    /// <summary><c>yield from</c> a generator: gives its values, passing on what this one receives, and is its return value.</summary>
    public DelegateAwaitable<Y, S, R2> From<Y2, S2, R2>(IGenerator<Y2, S2, R2> inner) =>
        new(this, new GeneratorDelegation<Y, Y2, S2, R2>(inner));

    /// <summary><c>yield from</c> any other iterable: gives its values, and receives only None.</summary>
    public DelegateAwaitable<Y, S, object?> From<Y2>(IEnumerable<Y2> values) =>
        new(this, new IterableDelegation<Y, Y2>(values.GetEnumerator()));

    /// <summary>What the body was resumed with: the sent value, or the exception it raises at the <c>yield</c>.</summary>
    internal S TakeResumption()
    {
        if (Thrown is { } thrown)
        {
            Thrown = null;
            ExceptionDispatchInfo.Throw(thrown);
        }
        var sent = Sent;
        Sent = default!;
        return sent;
    }
}

public readonly struct YieldAwaitable<Y, S>(GeneratorChannel<Y, S> channel, Y value, bool used) : INotifyCompletion
{
    public YieldAwaitable<Y, S> GetAwaiter() => this;

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation)
    {
        channel.Yielded = value;
        channel.WantsValue = used;
        channel.Continuation = continuation;
    }

    public S GetResult() => channel.TakeResumption();
}

public readonly struct DelegateAwaitable<Y, S, R>(GeneratorChannel<Y, S> channel, Delegation<Y> delegation) : INotifyCompletion
{
    public DelegateAwaitable<Y, S, R> GetAwaiter() => this;

    // The first value is taken at once, as PEP 380 does, so an empty one needn't pause the body.
    public bool IsCompleted
    {
        get
        {
            if (delegation.Step(Resume.Next, null, null))
                return false;
            channel.DelegateResult = delegation.Result;
            return true;
        }
    }

    public void OnCompleted(Action continuation)
    {
        channel.Yielded = delegation.Current;
        channel.WantsValue = false;
        channel.Delegate = delegation;
        channel.Continuation = continuation;
    }

    public R GetResult()
    {
        channel.TakeResumption();
        var result = channel.DelegateResult;
        channel.DelegateResult = null;
        return (R)result!;
    }
}

/// <summary>What a <c>yield from</c> delegates to, seen through the outer generator's yield type.</summary>
public abstract class Delegation<Y>
{
    public Y Current { get; protected set; } = default!;

    public object? Result { get; protected set; }

    /// <summary>One step; true if it yielded <see cref="Current"/>. Its exceptions propagate.</summary>
    public abstract bool Step(Resume how, object? sent, TaipanBaseException? thrown);

    /// <summary>Closes it, when the outer generator is closed (PEP 380).</summary>
    public abstract void Close();
}

internal sealed class GeneratorDelegation<Y, Y2, S2, R2>(IGenerator<Y2, S2, R2> inner) : Delegation<Y>
{
    public override bool Step(Resume how, object? sent, TaipanBaseException? thrown)
    {
        if (!inner.Step(how, how == Resume.Send ? (S2)sent! : default!, thrown))
        {
            Result = inner.Result;
            return false;
        }
        Current = Values.Widen<Y>(inner.Current);
        return true;
    }

    public override void Close() => inner.close();
}

internal sealed class IterableDelegation<Y, Y2>(IEnumerator<Y2> values) : Delegation<Y>
{
    public override bool Step(Resume how, object? sent, TaipanBaseException? thrown)
    {
        if (thrown is not null)
            throw thrown;
        if (how == Resume.Send && sent is not null)
            throw new TypeError("can't send a value to a 'yield from' over an iterator");
        if (!values.MoveNext())
            return false;
        Current = Values.Widen<Y>(values.Current);
        return true;
    }

    public override void Close()
    {
    }
}

/// <summary>Conversions the runtime makes between a value's own type and one it's assignable to (SPEC §3.3).</summary>
internal static class Values
{
    public static T Widen<T>(object? value) => value switch
    {
        T same => same,
        long integer when typeof(T) == typeof(double) || typeof(T) == typeof(double?) => (T)(object)(double)integer,
        _ => (T)value!,
    };
}

/// <summary>
/// A generator object (SPEC §3.20, §8): it drives its body, a C# async method, as a coroutine.
/// The body runs only inside <c>next</c>, <c>send</c>, <c>throw</c> and <c>close</c>, on their
/// thread, and pauses at each <c>yield</c>.
/// </summary>
public sealed class Generator<Y, S, R> : IGenerator<Y, S, R>, IReturning
{
    private enum State { Created, Suspended, Running, Done }

    private readonly Func<GeneratorChannel<Y, S>, Task<R>> _body;
    private readonly GeneratorChannel<Y, S> _channel = new();
    private readonly string _name;
    private readonly string? _needs;
    private State _state;
    private Task<R>? _task;

    /// <param name="needs">What it receives, when that doesn't accept None, so <c>next</c> can't resume a <c>yield</c> whose value is used.</param>
    public Generator(Func<GeneratorChannel<Y, S>, Task<R>> body, string name, string? needs = null) =>
        (_body, _name, _needs) = (body, name, needs);

    public Y Current { get; private set; } = default!;

    public R Result { get; private set; } = default!;

    public bool MoveNext() => Step(Resume.Next, default!, null);

    public Y send(S value) => Step(Resume.Send, value, null) ? Current : throw Stop();

    // Raised where the generator is paused, as `raise` would (SPEC §3.20).
    public Y @throw(TaipanBaseException exception) =>
        Step(Resume.Throw, default!, (TaipanBaseException)Exceptions.Raise(exception)) ? Current : throw Stop();

    public void close()
    {
        if (_state is State.Created or State.Done)
        {
            _state = State.Done;
            return;
        }
        bool yielded;
        try
        {
            yielded = Step(Resume.Throw, default!, new GeneratorExit(""));
        }
        catch (GeneratorExit)
        {
            return;
        }
        catch (StopIteration)
        {
            return;
        }
        if (yielded)
            throw new RuntimeError("generator ignored GeneratorExit");
    }

    /// <summary>A <c>StopIteration</c> for its end, whose value is what it returned.</summary>
    public StopIteration Stop()
    {
        var stop = new StopIteration("") { @value = Result };
        Result = default!;
        return stop;
    }

    public bool Step(Resume how, S value, TaipanBaseException? thrown)
    {
        switch (_state)
        {
            case State.Running:
                throw new ValueError("generator already executing");
            case State.Done:
                if (thrown is not null)
                    throw thrown;
                Result = default!;
                return false;
            case State.Created:
                if (thrown is not null)
                {
                    _state = State.Done;
                    throw thrown;
                }
                if (how == Resume.Send && value is not null)
                    throw new TypeError("can't send non-None value to a just-started generator");
                break;
            case State.Suspended when _channel.Delegate is null && _channel.WantsValue && how == Resume.Next && _needs is not null:
                throw new TypeError($"a generator waiting for a {_needs} can't be resumed with next(); use send()");
        }
        _state = State.Running;
        try
        {
            if (_task is null)
                _task = _body(_channel);
            else if (_channel.Delegate is { } delegation)
                Delegate(delegation, how, value, thrown);
            else
                Continue(how == Resume.Send ? value : default!, thrown);
        }
        finally
        {
            _state = State.Suspended;
        }
        if (!_task.IsCompleted)
        {
            Current = _channel.Yielded;
            return true;
        }
        _state = State.Done;
        _channel.Continuation = null;
        if (_task.IsCompletedSuccessfully)
        {
            Result = _task.Result;
            return false;
        }
        var exception = _task.Exception!.InnerException!;
        // PEP 479 (SPEC §3.13): a StopIteration escaping the body is a RuntimeError.
        if (exception is StopIteration stop)
            throw Tracebacks.FromStopIteration(stop);
        ExceptionDispatchInfo.Throw(exception);
        return false;
    }

    private void Continue(S value, Exception? thrown)
    {
        (_channel.Sent, _channel.Thrown) = (value, thrown);
        var continuation = _channel.Continuation!;
        _channel.Continuation = null;
        continuation();
    }

    // Passes a step to the generator a `yield from` delegates to, resuming the body once it ends (PEP 380).
    private void Delegate(Delegation<Y> delegation, Resume how, S value, TaipanBaseException? thrown)
    {
        try
        {
            if (thrown is GeneratorExit)
            {
                delegation.Close();
                _channel.Delegate = null;
                Continue(default!, thrown);
                return;
            }
            if (delegation.Step(how, value, thrown))
            {
                _channel.Yielded = delegation.Current;
                return;
            }
        }
        catch (Exception e) when (_channel.Delegate is not null)
        {
            _channel.Delegate = null;
            Continue(default!, e);
            return;
        }
        _channel.Delegate = null;
        _channel.DelegateResult = delegation.Result;
        Continue(default!, null);
    }

    public IEnumerator<Y> GetEnumerator()
    {
        while (MoveNext())
            yield return Current;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"<generator object {_name}>";
}
