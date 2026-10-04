using System.Collections.Concurrent;

namespace WebRtcDemo.Interop.Native;

/// <summary>
/// Maps the opaque `user`/`ctx` pointers handed to the shim to managed objects. An id instead of
/// a GCHandle means a late callback for an object that is already gone finds nothing rather than
/// a freed handle.
/// </summary>
internal static class CallbackRegistry
{
    private static readonly ConcurrentDictionary<nint, object> Targets = new();
    private static long _next;

    public static nint Register(object target)
    {
        var id = (nint)Interlocked.Increment(ref _next);
        Targets[id] = target;
        return id;
    }

    public static T? Get<T>(nint id) where T : class =>
        Targets.TryGetValue(id, out var target) ? target as T : null;

    public static T? Take<T>(nint id) where T : class =>
        Targets.TryRemove(id, out var target) ? target as T : null;

    public static void Unregister(nint id) => Targets.TryRemove(id, out _);

    internal static int Count => Targets.Count;
}

internal interface IPendingOperation
{
    void Cancel();
}

/// <summary>An asynchronous shim call; completes once, from whichever thread finishes first.</summary>
internal sealed class PendingOperation<T> : IPendingOperation
{
    private readonly ConcurrentDictionary<nint, IPendingOperation>? _owner;

    // Continuations must not run on the WebRTC thread that completes the operation.
    public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public nint Id { get; }

    public PendingOperation(ConcurrentDictionary<nint, IPendingOperation>? owner)
    {
        _owner = owner;
        Id = CallbackRegistry.Register(this);
        _owner?.TryAdd(Id, this);
    }

    public static PendingOperation<T>? Complete(nint id)
    {
        var operation = CallbackRegistry.Take<PendingOperation<T>>(id);
        operation?._owner?.TryRemove(id, out _);
        return operation;
    }

    public void Cancel()
    {
        CallbackRegistry.Unregister(Id);
        _owner?.TryRemove(Id, out _);
        Completion.TrySetCanceled();
    }
}
