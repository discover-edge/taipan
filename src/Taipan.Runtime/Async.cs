using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Taipan.Runtime;

/// <summary>
/// Something that awaits (SPEC §3.20, M42): an async function's call, an async generator's body, or
/// a <c>gather</c>. Cancelling it raises <c>CancelledError</c> at the <c>await</c> it's paused at, or
/// cancels what that awaits, when it's another one, so the innermost <c>finally</c> blocks run first.
/// </summary>
public abstract class Owner
{
    /// <summary>The <c>await</c> it's paused at, if it is.</summary>
    internal Waiting? Waiter;

    /// <summary>A cancellation for its next <c>await</c>, requested while it wasn't paused.</summary>
    internal bool CancelPending;

    internal abstract bool IsDone { get; }

    /// <summary>Whether code awaiting it resumes as soon as it's done, as for a coroutine awaited in Python, rather than on the loop's next turn.</summary>
    internal virtual bool ResumesInline => true;

    // The token .NET calls in its code are given (SPEC §13.20, M43), made when one is first passed.
    private CancellationTokenSource? _tokenSource;

    internal CancellationToken Token => (_tokenSource ??= new CancellationTokenSource()).Token;

    internal virtual bool Cancel()
    {
        if (IsDone)
            return false;
        if (Waiter is { } waiter)
        {
            if (waiter.Scheduled || Async.OwnerOf(waiter.Task) is not { IsDone: false } inner || !inner.Cancel())
                waiter.Cancel();
        }
        else
        {
            CancelPending = true;
        }
        // The .NET work it started stops too; what it starts after catching the cancellation gets a new token.
        if (_tokenSource is { } source)
        {
            _tokenSource = null;
            source.Cancel();
        }
        return true;
    }
}

/// <summary>A call of an async function (SPEC §3.14, §3.20).</summary>
internal sealed class Frame : Owner
{
    internal Task? Task;

    internal override bool IsDone => Task is { IsCompleted: true };
}

/// <summary>A task the event loop completes, such as a sleep's: code awaiting it resumes in the same turn, and it can't be cancelled itself.</summary>
internal sealed class Wakeup : Owner
{
    internal static readonly Wakeup Instance = new();

    internal override bool IsDone => true;
}

/// <summary>Async functions and <c>await</c> (SPEC §3.14, §3.20, §8).</summary>
public static class Async
{
    // The owner whose code is running on this thread, which an await belongs to.
    [ThreadStatic]
    private static Owner? t_current;

    private static readonly ConditionalWeakTable<Task, Owner> s_owners = new();

    internal static Owner? Current => t_current;

    internal static Owner? OwnerOf(Task task) => s_owners.TryGetValue(task, out var owner) ? owner : null;

    internal static void Own(Task task, Owner owner) => s_owners.AddOrUpdate(task, owner);

    /// <summary>The running async function's cancellation token, which a .NET call it makes is given (SPEC §13.20, M43).</summary>
    public static CancellationToken Token => t_current?.Token ?? CancellationToken.None;

    /// <summary>Calls an async function's body, which runs until its first <c>await</c> of something not yet done (SPEC §3.14).</summary>
    public static Task<T> Start<T>(Func<Task<T>> body)
    {
        var frame = new Frame();
        var task = RunAs(frame, body);
        frame.Task = task;
        Own(task, frame);
        return task;
    }

    public static Task Start(Func<Task> body)
    {
        var frame = new Frame();
        var task = RunAs(frame, body);
        frame.Task = task;
        Own(task, frame);
        return task;
    }

    /// <summary>Runs code as its owner's, so the awaits in it are that owner's.</summary>
    internal static T RunAs<T>(Owner? owner, Func<T> code)
    {
        var saved = t_current;
        t_current = owner;
        try
        {
            return code();
        }
        finally
        {
            t_current = saved;
        }
    }

    internal static void RunAs(Owner? owner, Action code)
    {
        var saved = t_current;
        t_current = owner;
        try
        {
            code();
        }
        finally
        {
            t_current = saved;
        }
    }

    /// <summary><c>await e</c>: resumes on the event loop when <c>e</c> is done, or with <c>CancelledError</c> if the owner is cancelled.</summary>
    public static Waiting<T> Await<T>(Task<T> task) => new(task, t_current);

    public static Waiting Await(Task task) => new(task, t_current);
}

/// <summary>An <c>await</c> of a task (SPEC §8): it resumes its owner on the event loop, and can be cancelled.</summary>
public class Waiting(Task task, Owner? owner) : ICriticalNotifyCompletion
{
    private readonly object _gate = new();
    private Action? _continuation;
    private bool _cancelled;

    internal Task Task => task;

    /// <summary>Whether its resumption is decided: the task finished, or it was cancelled.</summary>
    internal bool Scheduled { get; private set; }

    public Waiting GetAwaiter() => this;

    public bool IsCompleted
    {
        get
        {
            // A cancellation requested while the owner ran takes effect at this await.
            if (owner is { CancelPending: true })
            {
                owner.CancelPending = false;
                _cancelled = true;
                return true;
            }
            return task.IsCompleted;
        }
    }

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation)
    {
        _continuation = continuation;
        if (owner is not null)
            owner.Waiter = this;
        task.ContinueWith(static (_, state) => ((Waiting)state!).Finished(), this, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // The awaited task finished: an owner's code awaiting it resumes now, anything else's on the loop's next turn.
    private void Finished()
    {
        lock (_gate)
        {
            if (Scheduled)
                return;
            Scheduled = true;
        }
        if (Async.OwnerOf(task) is { ResumesInline: true } || EventLoop.Instance is null)
            Resume();
        else
            EventLoop.Instance.Post(Resume);
    }

    /// <summary>Resumes the owner with <c>CancelledError</c>, on the loop's next turn.</summary>
    internal void Cancel()
    {
        lock (_gate)
        {
            _cancelled = true;
            if (Scheduled)
                return;
            Scheduled = true;
        }
        if (EventLoop.Instance is { } loop)
            loop.Post(Resume);
        else
            Resume();
    }

    private void Resume()
    {
        if (owner is not null && owner.Waiter == this)
            owner.Waiter = null;
        Async.RunAs(owner, _continuation!);
    }

    protected void ThrowIfCancelled()
    {
        if (_cancelled)
            throw new CancelledError("");
    }

    public void GetResult()
    {
        ThrowIfCancelled();
        task.GetAwaiter().GetResult();
    }
}

public sealed class Waiting<T>(Task<T> task, Owner? owner) : Waiting(task, owner)
{
    public new Waiting<T> GetAwaiter() => this;

    public new T GetResult()
    {
        ThrowIfCancelled();
        return task.GetAwaiter().GetResult();
    }
}

/// <summary>
/// The event loop (SPEC §3.20, §8): one thread runs the code after each <c>await</c>, in the order
/// what it awaited finished, as Python's asyncio does. Each turn runs what was ready when it began,
/// after the timers that are due.
/// </summary>
public sealed class EventLoop
{
    private readonly object _gate = new();
    private readonly Queue<Action> _ready = new();
    private readonly PriorityQueue<Timer, (long Due, long Order)> _timers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _order;

    /// <summary>A program's loop; null in a library, where code after an await runs where the caller's context runs it (SPEC §14).</summary>
    public static EventLoop? Instance { get; private set; }

    /// <summary>Whether it's running, inside a top-level <c>await</c> or <c>asyncio.run</c>.</summary>
    public bool Running { get; private set; }

    internal static void Start() => Instance = new EventLoop();

    /// <summary>Runs code on the loop's next turn; from any thread.</summary>
    public void Post(Action action)
    {
        lock (_gate)
        {
            _ready.Enqueue(action);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>A timer that runs code on the loop after a delay, in seconds, unless it's cancelled first.</summary>
    internal sealed class Timer(Action action)
    {
        internal Action Action { get; } = action;

        internal bool Cancelled { get; set; }
    }

    internal Timer Schedule(double delay, Action action)
    {
        var timer = new Timer(action);
        lock (_gate)
        {
            var due = _clock.ElapsedTicks + (long)(Math.Max(delay, 0) * Stopwatch.Frequency);
            _timers.Enqueue(timer, (due, _order++));
            Monitor.Pulse(_gate);
        }
        return timer;
    }

    /// <summary>Runs the loop until a task finishes.</summary>
    public void RunUntil(Task task)
    {
        if (Running)
            throw new RuntimeError("asyncio.run() cannot be called from a running event loop");
        Running = true;
        // A task that finishes off the loop, such as a .NET one, wakes it.
        task.ContinueWith(_ => Post(static () => { }), TaskContinuationOptions.ExecuteSynchronously);
        try
        {
            while (!task.IsCompleted)
            {
                Action[] turn;
                lock (_gate)
                {
                    while (true)
                    {
                        MoveDueTimers();
                        if (_ready.Count > 0 || task.IsCompleted)
                            break;
                        Monitor.Wait(_gate, WaitTime());
                    }
                    turn = [.. _ready];
                    _ready.Clear();
                }
                foreach (var action in turn)
                    action();
            }
        }
        finally
        {
            Running = false;
        }
    }

    private void MoveDueTimers()
    {
        var now = _clock.ElapsedTicks;
        while (_timers.TryPeek(out var timer, out var at) && at.Due <= now)
        {
            _timers.Dequeue();
            if (!timer.Cancelled)
                _ready.Enqueue(timer.Action);
        }
    }

    private int WaitTime()
    {
        if (!_timers.TryPeek(out _, out var at))
            return System.Threading.Timeout.Infinite;
        var ticks = Math.Max(at.Due - _clock.ElapsedTicks, 0);
        return (int)Math.Min(int.MaxValue, Math.Ceiling(ticks * 1000.0 / Stopwatch.Frequency));
    }

    /// <summary>A main file's top level with an <c>await</c> (SPEC §3.14): it runs the loop until it finishes.</summary>
    public static void Main(Func<Task> topLevel)
    {
        var task = Async.Start(topLevel);
        Instance?.RunUntil(task);
        task.GetAwaiter().GetResult();
    }
}
