using WebRtcDemo.Effects;
using WebRtcDemo.Effects.Imaging;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>
/// A background video played by the shim's file source (Media Foundation on Windows, looping,
/// video only so nothing is heard). Its track is never sent; a sink keeps the newest frame.
/// </summary>
internal sealed class NativeBackgroundVideo : IBackgroundVideo
{
    private const int MaxWidth = 1280;
    private const int MaxHeight = 720;

    private readonly Lock _gate = new();
    private readonly VideoSource _source;
    private readonly MediaTrack _track;
    private readonly VideoSink _sink;
    private BgraBitmap? _latest;
    private long _version;
    private volatile bool _failed;
    private bool _disposed;

    /// <exception cref="WebRtcException">The file can't be opened.</exception>
    public NativeBackgroundVideo(PeerConnectionFactory factory, string path)
    {
        _source = factory.CreateFileSource(path, loop: true, state =>
        {
            if (state is CaptureState.Failed) _failed = true;
        });
        try
        {
            _track = factory.CreateVideoTrack(_source, "effects-background");
            _sink = _track.AddSink(OnFrame, MaxWidth, MaxHeight);
        }
        catch
        {
            _track?.Dispose();
            _source.Dispose();
            throw;
        }
    }

    public bool Failed => _failed;

    private void OnFrame(VideoFrame frame)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest ??= new BgraBitmap(frame.Width, frame.Height);
            _latest.CopyFrom(frame.Data, frame.Width, frame.Height, frame.Stride);
            _version++;
        }
    }

    public bool TryCopyLatest(ref long version, BgraBitmap target)
    {
        lock (_gate)
        {
            if (_latest == null || _version == version) return false;
            target.CopyFrom(_latest.Pixels, _latest.Width, _latest.Height, _latest.Stride);
            version = _version;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _sink.Dispose();
        _track.Dispose();
        _source.Dispose();
    }
}
