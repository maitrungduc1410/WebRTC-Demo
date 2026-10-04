namespace WebRtcDemo.Core;

/// <summary>The UI thread. View models are only touched on it; everything else posts to it.</summary>
public interface IDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/>; never runs it inline.</summary>
    void Post(Action action);
}

public static class DispatcherExtensions
{
    public static void Run(this IDispatcher dispatcher, Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.Post(action);
    }
}
