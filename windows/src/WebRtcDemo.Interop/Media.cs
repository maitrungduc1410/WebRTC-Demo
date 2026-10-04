using WebRtcDemo.Interop.Native;

namespace WebRtcDemo.Interop;

/// <summary>A local or remote audio/video track.</summary>
public sealed unsafe class MediaTrack : IDisposable
{
    internal TrackHandle Handle { get; }

    public MediaKind Kind { get; }

    public string Id => NativeMethods.ReadString((buffer, capacity) => NativeMethods.rtc_track_id(Handle, buffer, capacity));

    /// <summary>
    /// A disabled local track sends silence / black frames; a disabled remote track stops playout
    /// (audio) or frame delivery (video) on this device only.
    /// </summary>
    public bool Enabled
    {
        get => NativeMethods.rtc_track_is_enabled(Handle) != 0;
        set => NativeMethods.rtc_track_set_enabled(Handle, value ? 1 : 0);
    }

    internal MediaTrack(TrackHandle handle, MediaKind kind)
    {
        Handle = handle;
        Kind = kind;
    }

    /// <summary>Delivers this video track's frames as BGRA to <paramref name="handler"/> on a WebRTC thread.</summary>
    /// <param name="maxWidth">Larger frames are downscaled (0 = no limit), keeping the aspect ratio.</param>
    public VideoSink AddSink(VideoFrameHandler handler, int maxWidth = 0, int maxHeight = 0)
    {
        if (Kind != MediaKind.Video) throw new InvalidOperationException("Only video tracks have frames.");
        return new VideoSink(this, handler, maxWidth, maxHeight);
    }

    public void Dispose() => Handle.Dispose();
}

public sealed unsafe class VideoSink : IDisposable
{
    private readonly VideoFrameHandler _handler;
    private readonly nint _id;
    private readonly VideoSinkHandle _handle;

    internal VideoSink(MediaTrack track, VideoFrameHandler handler, int maxWidth, int maxHeight)
    {
        _handler = handler;
        _id = CallbackRegistry.Register(this);
        _handle = NativeMethods.rtc_video_sink_create(track.Handle, maxWidth, maxHeight, Callbacks.VideoFrame, _id);
        if (_handle.IsInvalid)
        {
            CallbackRegistry.Unregister(_id);
            throw WebRtcException.FromLastError("Attaching a video sink failed");
        }
    }

    public void SetMaxSize(int maxWidth, int maxHeight) => NativeMethods.rtc_video_sink_set_max_size(_handle, maxWidth, maxHeight);

    internal void Deliver(VideoFrame frame) => _handler(frame);

    /// <summary>No frame is delivered after this returns.</summary>
    public void Dispose()
    {
        _handle.Dispose();
        CallbackRegistry.Unregister(_id);
    }
}

public enum VideoSourceKind { Camera, Custom, Desktop, File }

public sealed unsafe class VideoSource : IDisposable
{
    private readonly Action<CaptureState>? _onState;
    private nint _id;

    internal VideoSourceHandle Handle { get; private set; } = null!;

    public VideoSourceKind Kind { get; }

    private VideoSource(VideoSourceKind kind, Action<CaptureState>? onState)
    {
        Kind = kind;
        _onState = onState;
    }

    internal static VideoSource Create(VideoSourceKind kind, Action<CaptureState>? onState,
        Func<nint, VideoSourceHandle> create, string failure)
    {
        var source = new VideoSource(kind, onState);
        source._id = CallbackRegistry.Register(source);
        var handle = create(source._id);
        if (handle.IsInvalid)
        {
            CallbackRegistry.Unregister(source._id);
            throw WebRtcException.FromLastError(failure);
        }
        source.Handle = handle;
        return source;
    }

    /// <summary>
    /// Stops or restarts a camera or desktop capturer without destroying the source. A camera cannot
    /// restart once stopped (false): open a new camera source instead.
    /// </summary>
    public bool SetCapturing(bool capturing) => NativeMethods.rtc_video_source_set_capturing(Handle, capturing ? 1 : 0) != 0;

    public bool PushI420(int width, int height, ReadOnlySpan<byte> y, int strideY, ReadOnlySpan<byte> u, int strideU, ReadOnlySpan<byte> v, int strideV)
    {
        fixed (byte* py = y)
        fixed (byte* pu = u)
        fixed (byte* pv = v)
        {
            return NativeMethods.rtc_custom_source_push_i420(Handle, width, height, py, strideY, pu, strideU, pv, strideV) != 0;
        }
    }

    internal void RaiseCaptureState(CaptureState state) => _onState?.Invoke(state);

    /// <summary>Stops capturing; no state callback runs after this returns.</summary>
    public void Dispose()
    {
        Handle.Dispose();
        CallbackRegistry.Unregister(_id);
    }
}

public sealed class RtpSender : IDisposable
{
    internal SenderHandle Handle { get; }

    internal RtpSender(SenderHandle handle) => Handle = handle;

    /// <summary>Switches what is sent without renegotiation (camera, screen, file); null sends nothing.</summary>
    public bool SetTrack(MediaTrack? track)
    {
        if (track == null) return NativeMethods.rtc_sender_set_track(Handle, 0) != 0;
        var added = false;
        try
        {
            track.Handle.DangerousAddRef(ref added);
            return NativeMethods.rtc_sender_set_track(Handle, track.Handle.DangerousGetHandle()) != 0;
        }
        finally
        {
            if (added) track.Handle.DangerousRelease();
        }
    }

    public void Dispose() => Handle.Dispose();
}

public sealed unsafe class KeyProvider : IDisposable
{
    internal KeyProviderHandle Handle { get; }

    public KeyProvider(KeyProviderOptions options)
    {
        fixed (byte* salt = options.RatchetSalt)
        fixed (byte* magic = options.UncryptedMagicBytes)
        {
            var native = new KeyProviderOptionsNative
            {
                SharedKey = options.SharedKey ? 1 : 0,
                RatchetSalt = salt,
                RatchetSaltLength = options.RatchetSalt.Length,
                UncryptedMagicBytes = magic,
                UncryptedMagicBytesLength = options.UncryptedMagicBytes?.Length ?? 0,
                RatchetWindowSize = options.RatchetWindowSize,
                FailureTolerance = options.FailureTolerance,
                KeyRingSize = options.KeyRingSize,
                DiscardFrameWhenCryptorNotReady = options.DiscardFrameWhenCryptorNotReady ? 1 : 0,
                KeyDerivation = (int)options.KeyDerivation,
            };
            Handle = NativeMethods.rtc_key_provider_create(&native);
        }
        if (Handle.IsInvalid) throw WebRtcException.FromLastError("Creating the key provider failed");
    }

    public bool SetSharedKey(int index, ReadOnlySpan<byte> key)
    {
        fixed (byte* data = key)
        {
            return NativeMethods.rtc_key_provider_set_shared_key(Handle, index, data, key.Length) != 0;
        }
    }

    public void Dispose() => Handle.Dispose();
}

public sealed record DataChannelMessage(byte[] Data, bool IsBinary)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Data);
}

public sealed unsafe class DataChannel : IDisposable
{
    private readonly DataChannelHandle _handle;
    private readonly nint _id;

    /// <summary>Raised on the WebRTC signaling thread.</summary>
    public event Action<DataChannelState>? StateChanged;

    /// <summary>Raised on the WebRTC signaling thread.</summary>
    public event Action<DataChannelMessage>? MessageReceived;

    internal DataChannel(DataChannelHandle handle)
    {
        _handle = handle;
        _id = CallbackRegistry.Register(this);
        var observer = Callbacks.DataChannelObserver;
        NativeMethods.rtc_data_channel_set_observer(_handle, &observer, _id);
    }

    public DataChannelState State => (DataChannelState)NativeMethods.rtc_data_channel_state(_handle);

    public string Label => NativeMethods.ReadString((buffer, capacity) => NativeMethods.rtc_data_channel_label(_handle, buffer, capacity));

    /// <summary>False unless the channel is open.</summary>
    public bool Send(string text) => Send(System.Text.Encoding.UTF8.GetBytes(text), binary: false);

    public bool Send(ReadOnlySpan<byte> data, bool binary)
    {
        fixed (byte* bytes = data)
        {
            return NativeMethods.rtc_data_channel_send(_handle, bytes, data.Length, binary ? 1 : 0) != 0;
        }
    }

    public void Close() => NativeMethods.rtc_data_channel_close(_handle);

    internal void RaiseState(DataChannelState state) => StateChanged?.Invoke(state);

    internal void RaiseMessage(DataChannelMessage message) => MessageReceived?.Invoke(message);

    /// <summary>
    /// Stops the events (none is raised after this returns) and frees the handle, but leaves the
    /// channel open: call <see cref="Close"/> to close it, or close the peer connection, which
    /// closes its channels and is what the remote sees as hang-up.
    /// </summary>
    public void Dispose()
    {
        _handle.Dispose();
        CallbackRegistry.Unregister(_id);
    }
}

/// <summary>Screens or windows that can be shared.</summary>
public sealed unsafe class DesktopMediaList : IDisposable
{
    private readonly PeerConnectionFactory _factory;
    private readonly Action<DesktopListEvent, string>? _onChanged;
    private readonly DesktopListHandle _handle;
    private readonly nint _id;

    public DesktopSourceType Type { get; }

    internal DesktopMediaList(PeerConnectionFactory factory, DesktopSourceType type, Action<DesktopListEvent, string>? onChanged)
    {
        _factory = factory;
        _onChanged = onChanged;
        Type = type;
        _id = CallbackRegistry.Register(this);
        _handle = NativeMethods.rtc_desktop_list_create(factory.Handle, (int)type, onChanged == null ? null : Callbacks.DesktopList, _id);
        if (_handle.IsInvalid)
        {
            CallbackRegistry.Unregister(_id);
            throw WebRtcException.FromLastError("Listing screens and windows failed");
        }
    }

    /// <summary>
    /// Blocking: call it off the UI thread. Returns new wrappers on every call;
    /// <paramref name="forceReload"/> makes libwebrtc rebuild its sources instead of updating
    /// them. Thumbnails arrive later as <see cref="DesktopListEvent.ThumbnailChanged"/>.
    /// </summary>
    public IReadOnlyList<DesktopMediaSource> Refresh(bool forceReload, bool captureThumbnails)
    {
        var count = NativeMethods.rtc_desktop_list_update(_handle, forceReload ? 1 : 0, captureThumbnails ? 1 : 0);
        var sources = new List<DesktopMediaSource>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
        {
            var handle = NativeMethods.rtc_desktop_list_source(_handle, i);
            if (handle.IsInvalid) continue;
            sources.Add(new DesktopMediaSource(handle));
        }
        return sources;
    }

    internal void Raise(DesktopListEvent evt, string sourceId) => _onChanged?.Invoke(evt, sourceId);

    /// <summary>Dispose the sources first; no event is raised after this returns.</summary>
    public void Dispose()
    {
        _handle.Dispose();
        CallbackRegistry.Unregister(_id);
        GC.KeepAlive(_factory);
    }
}

public sealed unsafe class DesktopMediaSource : IDisposable
{
    internal MediaSourceHandle Handle { get; }

    public string Id { get; }
    public string Name { get; }
    public DesktopSourceType Type { get; }

    internal DesktopMediaSource(MediaSourceHandle handle)
    {
        Handle = handle;
        Id = NativeMethods.ReadString((buffer, capacity) => NativeMethods.rtc_media_source_id(handle, buffer, capacity));
        Name = NativeMethods.ReadString((buffer, capacity) => NativeMethods.rtc_media_source_name(handle, buffer, capacity));
        Type = (DesktopSourceType)NativeMethods.rtc_media_source_type(handle);
    }

    /// <summary>The latest JPEG thumbnail, or empty before the first one was captured.</summary>
    public byte[] GetThumbnail()
    {
        // The capture thread can replace the thumbnail between the two calls.
        while (true)
        {
            var size = NativeMethods.rtc_media_source_thumbnail(Handle, null, 0);
            if (size <= 0) return [];
            var data = new byte[size];
            int actual;
            fixed (byte* buffer = data)
            {
                actual = NativeMethods.rtc_media_source_thumbnail(Handle, buffer, data.Length);
            }
            if (actual == data.Length) return data;
            if (actual < data.Length) return actual <= 0 ? [] : data.AsSpan(0, actual).ToArray();
        }
    }

    public void Dispose() => Handle.Dispose();
}