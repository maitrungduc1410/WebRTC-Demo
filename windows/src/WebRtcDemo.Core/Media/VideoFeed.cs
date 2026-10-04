using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>Frames of one video track; handlers run on a WebRTC thread and must return quickly.</summary>
public interface IVideoFeed
{
    IDisposable Subscribe(VideoFrameHandler handler);
}

/// <summary>
/// Fans one native sink out to every subscriber (renderer, snapshotter), so each frame is
/// converted to BGRA once. The sink exists only while someone is subscribed.
/// </summary>
public sealed class TrackVideoFeed(MediaTrack track) : IVideoFeed, IDisposable
{
    private readonly Lock _gate = new();
    private VideoFrameHandler[] _handlers = [];
    private VideoSink? _sink;
    private bool _disposed;

    public MediaTrack Track { get; } = track;

    public IDisposable Subscribe(VideoFrameHandler handler)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _handlers = [.. _handlers, handler];
            _sink ??= Track.AddSink(Deliver);
        }
        return new Subscription(this, handler);
    }

    private void Deliver(VideoFrame frame)
    {
        foreach (var handler in Volatile.Read(ref _handlers)) handler(frame);
    }

    private void Unsubscribe(VideoFrameHandler handler)
    {
        VideoSink? release = null;
        lock (_gate)
        {
            _handlers = [.. _handlers.Where(h => h != handler)];
            if (_handlers.Length == 0)
            {
                release = _sink;
                _sink = null;
            }
        }
        // Outside the lock: releasing waits for a frame in flight, whose handlers may take it.
        release?.Dispose();
    }

    /// <summary>Stops frame delivery; the track itself is owned by the caller.</summary>
    public void Dispose()
    {
        VideoSink? release;
        lock (_gate)
        {
            _disposed = true;
            _handlers = [];
            release = _sink;
            _sink = null;
        }
        release?.Dispose();
    }

    private sealed class Subscription(TrackVideoFeed feed, VideoFrameHandler handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) feed.Unsubscribe(handler);
        }
    }
}
