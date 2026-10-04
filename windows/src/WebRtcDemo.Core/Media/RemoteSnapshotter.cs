using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>
/// Keeps a tiny copy of the last non-black remote frame for the blurred "camera off" backdrop
/// (every 500 ms, 36 px wide, skipped when darker than luma 16), like the web client.
/// </summary>
public sealed class RemoteSnapshotter : IDisposable
{
    public const int SnapshotWidth = 36;
    public const double MinimumLuma = 16;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Action<BgraImage> _onSnapshot;
    private IDisposable? _subscription;
    private long _lastTimestamp;
    private volatile bool _active = true;

    public RemoteSnapshotter(IDispatcher dispatcher, Action<BgraImage> onSnapshot, TimeProvider? time = null)
    {
        _dispatcher = dispatcher;
        _onSnapshot = onSnapshot;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Off while the placeholder shows: the track then delivers black or frozen frames.</summary>
    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public void Attach(IVideoFeed? feed)
    {
        _subscription?.Dispose();
        _subscription = feed?.Subscribe(OnFrame);
        _lastTimestamp = 0;
    }

    /// <summary>Runs on the decoder thread.</summary>
    internal void OnFrame(VideoFrame frame)
    {
        if (!_active || frame.Width <= 0 || frame.Height <= 0) return;
        var now = _time.GetTimestamp();
        if (_lastTimestamp != 0 && _time.GetElapsedTime(_lastTimestamp, now) < Interval) return;
        _lastTimestamp = now;

        var image = ImageOps.Downscale(frame.Data, frame.Width, frame.Height, frame.Stride, SnapshotWidth);
        if (ImageOps.MeanLuma(image) < MinimumLuma) return;
        _dispatcher.Post(() => _onSnapshot(image));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
