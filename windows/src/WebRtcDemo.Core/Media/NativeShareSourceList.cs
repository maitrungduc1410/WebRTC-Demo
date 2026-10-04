using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

internal sealed class NativeShareSourceList : IShareSourceList
{
    private readonly IDispatcher _dispatcher;
    private readonly DesktopMediaList _list;
    // Every wrapper handed out in a ShareSource: libwebrtc's sources point at the list, so they go
    // first. A source that disappears or is renamed keeps its wrapper until then, because the
    // picker may still hold it; unchanged sources are reused, so this only grows with those.
    private readonly List<DesktopMediaSource> _natives = [];
    private readonly Lock _gate = new();
    private Dictionary<string, ShareSource> _current = [];
    private Task _refresh = Task.CompletedTask;
    private bool _disposed;

    public DesktopSourceType Type { get; }

    public event Action<ShareSource>? ThumbnailChanged;

    public NativeShareSourceList(PeerConnectionFactory factory, DesktopSourceType type, IDispatcher dispatcher)
    {
        Type = type;
        _dispatcher = dispatcher;
        _list = factory.CreateDesktopMediaList(type, (evt, id) =>
        {
            if (evt == DesktopListEvent.ThumbnailChanged) _dispatcher.Post(() => UpdateThumbnail(id));
        });
    }

    public async Task<IReadOnlyList<ShareSource>> RefreshAsync()
    {
        Task<IReadOnlyList<DesktopMediaSource>> refresh;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // UpdateSourceList blocks while it enumerates; never twice at once.
            // Not forced: a forced reload rebuilds every source and recaptures every thumbnail.
            refresh = _refresh.ContinueWith(_ => _list.Refresh(forceReload: false, captureThumbnails: true), TaskScheduler.Default);
            _refresh = refresh;
        }
        var natives = await refresh.ConfigureAwait(true);
        var sources = new List<ShareSource>(natives.Count);
        var current = new Dictionary<string, ShareSource>(natives.Count);
        lock (_gate)
        {
            if (_disposed)
            {
                foreach (var native in natives) native.Dispose();
                return [];
            }
            foreach (var native in natives)
            {
                if (current.ContainsKey(native.Id))
                {
                    native.Dispose();
                    continue;
                }
                if (_current.TryGetValue(native.Id, out var source) && source.Name == native.Name)
                {
                    native.Dispose();
                }
                else
                {
                    _natives.Add(native);
                    source = new ShareSource(native.Id, native.Name, native.Type, native) { Thumbnail = native.GetThumbnail() };
                }
                current[source.Id] = source;
                sources.Add(source);
            }
        }
        _current = current;
        return sources;
    }

    private void UpdateThumbnail(string id)
    {
        if (_disposed || !_current.TryGetValue(id, out var source) || source.Native is not DesktopMediaSource native) return;
        source.Thumbnail = native.GetThumbnail();
        ThumbnailChanged?.Invoke(source);
    }

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _refresh;
        }
        // A refresh still running owns the list until it returns.
        pending.ContinueWith(_ =>
        {
            lock (_gate)
            {
                foreach (var native in _natives) native.Dispose();
                _natives.Clear();
            }
            _list.Dispose();
        }, TaskScheduler.Default);
    }
}
