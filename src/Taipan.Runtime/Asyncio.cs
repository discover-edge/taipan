namespace Taipan.Runtime;

/// <summary>An argument of <c>gather</c>: an awaitable, and how to read its result as the type the results join to (SPEC §3.20).</summary>
public readonly struct Awaited<T>
{
    internal Task Task { get; }

    private readonly Func<Task, T> _result;

    private Awaited(Task task, Func<Task, T> result) => (Task, _result) = (task, result);

    public Awaited(Task<T> task) : this(task, static t => ((Task<T>)t).Result)
    {
    }

    /// <summary>An <c>Awaitable[None]</c>, whose result is None.</summary>
    public Awaited(Task task) : this(task, static _ => default!)
    {
    }

    /// <summary>One whose result converts, such as an int to a float.</summary>
    public static Awaited<T> From<TResult>(Task<TResult> task, Func<TResult, T> convert) => new(task, t => convert(((Task<TResult>)t).Result));

    /// <summary>The awaitables of <c>*xs</c>.</summary>
    public static IEnumerable<Awaited<T>> Each(List<Task<T>> tasks) => tasks.Select(t => new Awaited<T>(t));

    public static IEnumerable<Awaited<T>> Each(List<Task> tasks) => tasks.Select(t => new Awaited<T>(t));

    internal T Result => _result(Task);
}

/// <summary>A <c>gather</c> (SPEC §3.20): cancelling it cancels everything it waits for.</summary>
internal sealed class Gathering(Task[] tasks) : Owner
{
    internal Task? Task;

    internal bool CancelRequested;

    internal override bool IsDone => Task is { IsCompleted: true };

    // As a Python future, it wakes what awaits it on the loop's next turn.
    internal override bool ResumesInline => false;

    internal override bool Cancel()
    {
        if (IsDone)
            return false;
        CancelRequested = true;
        foreach (var task in tasks)
        {
            if (!task.IsCompleted)
                Async.OwnerOf(task)?.Cancel();
        }
        return true;
    }
}

/// <summary>
/// <c>asyncio.timeout(delay)</c> (SPEC §3.20): an async context manager that cancels the code
/// inside it once the delay passes, then raises <c>TimeoutError</c> from the <c>CancelledError</c>.
/// </summary>
public sealed class Timeout(double? delay)
{
    private Owner? _owner;
    private EventLoop.Timer? _timer;
    private bool _expired;
    private bool _exited;

    public Task<Timeout> @__aenter__()
    {
        _owner = Async.Current;
        if (delay is { } seconds && EventLoop.Instance is { } loop)
            _timer = loop.Schedule(seconds, Expire);
        return Task.FromResult(this);
    }

    private void Expire()
    {
        if (_exited)
            return;
        _expired = true;
        _owner?.Cancel();
    }

    public Task<bool> @__aexit__(ClassValue? excType, TaipanBaseException? exc, Traceback? tb)
    {
        _exited = true;
        if (_timer is not null)
            _timer.Cancelled = true;
        if (_expired && exc is CancelledError cancelled)
        {
            var timeout = new TimeoutError("") { __cause__ = cancelled };
            return Task.FromException<bool>(Exceptions.Raise(timeout));
        }
        return Task.FromResult(false);
    }

    public override string ToString() => "<Timeout>";
}

/// <summary>The built-in module <c>asyncio</c> (SPEC §3.20, M42).</summary>
public static class Asyncio
{
    /// <summary>
    /// <c>gather(*aws)</c>: every result, in order. The first exception is raised at once, while the
    /// others keep running; cancelling it cancels them all.
    /// </summary>
    public static Task<List<T>> gather<T>(params Awaited<T>[] items)
    {
        var done = new TaskCompletionSource<List<T>>();
        var tasks = items.Select(i => i.Task).ToArray();
        var gathering = new Gathering(tasks) { Task = done.Task };
        Async.Own(done.Task, gathering);
        var remaining = items.Length;
        if (remaining == 0)
            done.SetResult([]);
        foreach (var task in tasks)
        {
            // As in Python, a child's end is seen on the loop's next turn.
            task.ContinueWith(_ => Later(() =>
            {
                if (done.Task.IsCompleted)
                    return;
                if (task.IsFaulted)
                {
                    done.SetException(task.Exception!.InnerException!);
                    return;
                }
                if (task.IsCanceled)
                {
                    done.SetException(new CancelledError(""));
                    return;
                }
                if (--remaining > 0)
                    return;
                if (gathering.CancelRequested)
                    done.SetException(new CancelledError(""));
                else
                    done.SetResult([.. items.Select(i => i.Result)]);
            }), TaskContinuationOptions.ExecuteSynchronously);
        }
        return done.Task;
    }

    private static void Later(Action action)
    {
        if (EventLoop.Instance is { } loop)
            loop.Post(action);
        else
            action();
    }

    /// <summary><c>sleep(delay)</c>: done after <c>delay</c> seconds; <c>sleep(0)</c> lets everything else that's ready run first.</summary>
    public static Task sleep(double delay)
    {
        if (EventLoop.Instance is not { } loop)
            return delay <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(delay));
        var done = new TaskCompletionSource();
        Async.Own(done.Task, Wakeup.Instance);
        if (delay <= 0)
            loop.Post(() => done.TrySetResult());
        else
            loop.Schedule(delay, () => done.TrySetResult());
        return done.Task;
    }

    /// <summary>
    /// <c>wait_for(aw, timeout)</c>, as Python 3.12's: <c>aw</c>'s result, or once <c>timeout</c>
    /// seconds pass, <c>aw</c> is cancelled, and if it ends with <c>CancelledError</c>, <c>TimeoutError</c>.
    /// </summary>
    public static Task<T> wait_for<T>(Task<T> aw, double? timeout) => Async.Start(async () =>
    {
        var scope = new Timeout(timeout);
        await Async.Await(scope.@__aenter__());
        T result;
        try
        {
            result = await Async.Await(aw);
        }
        catch (CancelledError cancelled)
        {
            await Async.Await(scope.@__aexit__(ClassValue.Of(cancelled), cancelled, cancelled.@__traceback__));
            throw;
        }
        await Async.Await(scope.@__aexit__(null, null, null));
        return result;
    });

    public static Task wait_for(Task aw, double? timeout) => wait_for(Unit(aw), timeout);

    private static Task<object?> Unit(Task task) => Async.Start<object?>(async () =>
    {
        await Async.Await(task);
        return null;
    });

    /// <summary><c>timeout(delay)</c>: an async context manager; None never expires.</summary>
    public static Timeout timeout(double? delay) => new(delay);

    /// <summary><c>run(aw)</c>: runs the event loop until <c>aw</c> finishes, and gives its result.</summary>
    public static T run<T>(Task<T> aw)
    {
        EventLoop.Instance?.RunUntil(aw);
        return aw.GetAwaiter().GetResult();
    }

    public static void run(Task aw)
    {
        EventLoop.Instance?.RunUntil(aw);
        aw.GetAwaiter().GetResult();
    }
}
