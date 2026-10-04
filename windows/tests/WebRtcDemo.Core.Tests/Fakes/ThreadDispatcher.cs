using System.Collections.Concurrent;

namespace WebRtcDemo.Core.Tests.Fakes;

/// <summary>A real "UI thread" for end-to-end tests, with its own synchronization context.</summary>
public sealed class ThreadDispatcher : IDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public ThreadDispatcher(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public ConcurrentQueue<Exception> Errors { get; } = new();

    public bool HasThreadAccess => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        if (!_queue.IsAddingCompleted) _queue.Add(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() =>
    {
        action();
        return true;
    });

    public Task InvokeAsync(Func<Task> action) => InvokeAsync<Task>(action).Unwrap();

    /// <summary>Polls <paramref name="condition"/> on the dispatcher thread.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await InvokeAsync(condition).ConfigureAwait(false))
        {
            if (!Errors.IsEmpty) throw new AggregateException(Errors);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Errors.Enqueue(e);
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private sealed class Context(ThreadDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => dispatcher.Post(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}
