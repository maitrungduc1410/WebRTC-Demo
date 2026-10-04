namespace WebRtcDemo.Core.Tests.Fakes;

/// <summary>
/// A single-threaded "UI thread" for tests: posted work and await continuations queue up and run
/// when the test calls <see cref="RunPending"/>.
/// </summary>
public sealed class ManualDispatcher : IDispatcher, IDisposable
{
    private readonly Queue<Action> _queue = new();
    private readonly Lock _gate = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

    public ManualDispatcher() => SynchronizationContext.SetSynchronizationContext(new Context(this));

    public bool HasThreadAccess => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action)
    {
        lock (_gate) _queue.Enqueue(action);
    }

    /// <summary>Runs queued work, including work it queues, until nothing is left.</summary>
    public int RunPending(int limit = 10_000)
    {
        var ran = 0;
        while (ran < limit)
        {
            Action? next;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next)) break;
            }
            next();
            ran++;
        }
        return ran;
    }

    /// <summary>Pumps until <paramref name="condition"/> holds, for work that hops through the thread pool first.</summary>
    public void RunUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            RunPending();
            if (condition()) return;
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition not reached");
            Thread.Sleep(5);
        }
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);

    private sealed class Context(ManualDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => dispatcher.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;
    }
}
