using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Taipan.Runtime;

/// <summary>Taipan's <c>AsyncIterable[T]</c> (SPEC §3.20, M42): what <c>async for</c> goes over.</summary>
public interface IAsyncIterable<T>
{
    IAsyncIterator<T> @__aiter__();
}

/// <summary>Taipan's <c>AsyncIterator[T]</c>: each value is awaited from <c>__anext__</c>, which raises <c>StopAsyncIteration</c> at the end.</summary>
public interface IAsyncIterator<T> : IAsyncIterable<T>
{
    Task<T> @__anext__();
}

/// <summary>The body's side of an async generator (SPEC §8): <c>yield e</c> is <c>await channel.Yield(e)</c>.</summary>
public sealed class AsyncGeneratorChannel<Y, S>
{
    internal AsyncGenerator<Y, S> Generator = null!;

    public AsyncYieldAwaitable<Y, S> Yield(Y value) => new(Generator, value, used: false);

    public AsyncYieldAwaitable<Y, S> Receive(Y value) => new(Generator, value, used: true);
}

public readonly struct AsyncYieldAwaitable<Y, S>(AsyncGenerator<Y, S> generator, Y value, bool used) : INotifyCompletion
{
    public AsyncYieldAwaitable<Y, S> GetAwaiter() => this;

    public bool IsCompleted => false;

    public void OnCompleted(Action continuation) => generator.Yielded(value, continuation, used);

    public S GetResult() => generator.TakeResumption();
}

/// <summary>
/// An async generator object (SPEC §3.20, §8). Each <c>__anext__</c>, <c>asend</c> or <c>athrow</c>
/// runs the body directly until it yields, raises or finishes, and gives an awaitable that's done
/// then; code awaiting it resumes at once, as in Python. The body's own awaits are its owner's.
/// </summary>
public sealed class AsyncGenerator<Y, S> : Owner, IAsyncIterator<Y>
{
    private enum State { Created, Suspended, Done }

    private readonly Func<AsyncGeneratorChannel<Y, S>, Task> _body;
    private readonly AsyncGeneratorChannel<Y, S> _channel = new();
    private readonly string _name;
    private readonly string? _needs;
    private State _state;
    private Task? _task;
    private Action? _continuation;
    private bool _wantsValue;
    private S _sent = default!;
    private Exception? _thrown;
    private TaskCompletionSource<Y>? _step;

    /// <param name="needs">What it receives, when that doesn't accept None (SPEC §3.20).</param>
    public AsyncGenerator(Func<AsyncGeneratorChannel<Y, S>, Task> body, string name, string? needs = null)
    {
        (_body, _name, _needs) = (body, name, needs);
        _channel.Generator = this;
    }

    internal override bool IsDone => _state == State.Done;

    public IAsyncIterator<Y> @__aiter__() => this;

    public Task<Y> @__anext__() => Step(Resume.Next, default!, null);

    public Task<Y> asend(S value) => Step(Resume.Send, value, null);

    public Task<Y> athrow(TaipanBaseException exception) => Step(Resume.Throw, default!, (TaipanBaseException)Exceptions.Raise(exception));

    /// <summary>Raises <c>GeneratorExit</c> where it's paused, as <c>close</c> does (SPEC §3.20).</summary>
    public Task aclose()
    {
        if (_state != State.Suspended)
        {
            _state = State.Done;
            return Task.CompletedTask;
        }
        var step = Step(Resume.Throw, default!, new GeneratorExit(""));
        return Async.Start(async () =>
        {
            try
            {
                await Async.Await(step);
            }
            catch (GeneratorExit)
            {
                return;
            }
            catch (StopAsyncIteration)
            {
                return;
            }
            throw new RuntimeError("async generator ignored GeneratorExit");
        });
    }

    private Task<Y> Step(Resume how, S value, TaipanBaseException? thrown)
    {
        if (_step is not null)
            return Task.FromException<Y>(new RuntimeError("anext(): asynchronous generator is already running"));
        switch (_state)
        {
            case State.Done:
                return Task.FromException<Y>(thrown ?? new StopAsyncIteration(""));
            case State.Created when thrown is not null:
                _state = State.Done;
                return Task.FromException<Y>(thrown);
            case State.Created when how == Resume.Send && value is not null:
                return Task.FromException<Y>(new TypeError("can't send non-None value to a just-started async generator"));
            case State.Suspended when _wantsValue && how == Resume.Next && _needs is not null:
                return Task.FromException<Y>(new TypeError($"a generator waiting for a {_needs} can't be resumed with next(); use send()"));
        }
        var step = new TaskCompletionSource<Y>();
        _step = step;
        Async.Own(step.Task, this);
        if (_task is null)
        {
            _state = State.Suspended;
            _task = Async.RunAs(this, () => _body(_channel));
            _task.ContinueWith(static (task, state) => ((AsyncGenerator<Y, S>)state!).Finished(task), this, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            (_sent, _thrown) = (how == Resume.Send ? value : default!, thrown);
            var continuation = _continuation!;
            _continuation = null;
            Async.RunAs(this, continuation);
        }
        return step.Task;
    }

    internal void Yielded(Y value, Action continuation, bool used)
    {
        (_continuation, _wantsValue) = (continuation, used);
        var step = _step!;
        _step = null;
        step.SetResult(value);
    }

    internal S TakeResumption()
    {
        if (_thrown is { } thrown)
        {
            _thrown = null;
            ExceptionDispatchInfo.Throw(thrown);
        }
        var sent = _sent;
        _sent = default!;
        return sent;
    }

    // The body ended: the step waiting for it raises StopAsyncIteration, or what the body raised.
    private void Finished(Task task)
    {
        _state = State.Done;
        var step = _step;
        _step = null;
        if (step is null)
            return;
        if (task.IsCompletedSuccessfully)
            step.SetException(new StopAsyncIteration(""));
        else if (task.Exception!.InnerException is StopAsyncIteration stop)
            step.SetException(new RuntimeError("async generator raised StopAsyncIteration") { __cause__ = stop, __context__ = stop, Unchained = false });
        else
            step.SetException(task.Exception.InnerException!);
    }

    public override string ToString() => $"<async_generator object {_name}>";
}

/// <summary><c>async for</c>, <c>anext</c>, and classes as async iterators (SPEC §3.20).</summary>
public static class AsyncIterators
{
    /// <summary>A class with an async <c>__anext__</c> as an async iterator.</summary>
    public static IAsyncIterator<T> FromAnext<TObject, T>(TObject value, Func<TObject, Task<T>> next) where TObject : notnull =>
        new ObjectIterator<TObject, T>(value, next);

    private sealed class ObjectIterator<TObject, T>(TObject value, Func<TObject, Task<T>> next) : IAsyncIterator<T> where TObject : notnull
    {
        public Task<T> @__anext__() => next(value);

        public IAsyncIterator<T> @__aiter__() => this;

        public override string ToString() => Display.Format(value);
    }

    /// <summary>A class with <c>__aiter__</c> as an async iterable: each <c>async for</c> calls it once.</summary>
    public static IAsyncIterable<T> FromAiter<TObject, T>(TObject value, Func<TObject, IAsyncIterator<T>> iter) where TObject : notnull =>
        new ObjectIterable<TObject, T>(value, iter);

    private sealed class ObjectIterable<TObject, T>(TObject value, Func<TObject, IAsyncIterator<T>> iter) : IAsyncIterable<T> where TObject : notnull
    {
        public IAsyncIterator<T> @__aiter__() => iter(value);

        public override string ToString() => Display.Format(value);
    }

    /// <summary><c>anext(it)</c>: the next value's awaitable.</summary>
    public static Task<T> Anext<T>(IAsyncIterator<T> iterator) => iterator.@__anext__();

    /// <summary><c>anext(it, default)</c>: the default once it's finished.</summary>
    public static Task<T> Anext<T>(IAsyncIterator<T> iterator, T fallback) => Async.Start(async () =>
    {
        try
        {
            return await Async.Await(iterator.@__anext__());
        }
        catch (StopAsyncIteration)
        {
            return fallback;
        }
    });

    /// <summary><c>anext(it, None)</c> on an iterator of values, or of references.</summary>
    public static Task<T?> AnextValueOrNone<T>(IAsyncIterator<T> iterator) where T : struct => Async.Start<T?>(async () =>
    {
        try
        {
            return await Async.Await(iterator.@__anext__());
        }
        catch (StopAsyncIteration)
        {
            return null;
        }
    });

    public static Task<T?> AnextOrNone<T>(IAsyncIterator<T> iterator) where T : class => Async.Start<T?>(async () =>
    {
        try
        {
            return await Async.Await(iterator.@__anext__());
        }
        catch (StopAsyncIteration)
        {
            return null;
        }
    });
}
