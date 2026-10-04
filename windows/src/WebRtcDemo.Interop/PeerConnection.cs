using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WebRtcDemo.Interop.Native;

namespace WebRtcDemo.Interop;

/// <summary>
/// One RTCPeerConnection. Events are raised on WebRTC threads and must not block or call back
/// into this object synchronously; post to the UI thread instead. Async methods complete on the
/// thread pool, never on a WebRTC thread.
/// </summary>
public sealed unsafe class PeerConnection : IDisposable
{
    private readonly PeerConnectionFactory _factory;
    private readonly PeerConnectionHandle _handle;
    private readonly nint _id;
    private readonly ConcurrentDictionary<nint, IPendingOperation> _pending = new();
    private int _closed;

    public event Action<SignalingState>? SignalingStateChanged;
    public event Action<PeerConnectionState>? ConnectionStateChanged;
    public event Action<IceConnectionState>? IceConnectionStateChanged;
    public event Action<IceGatheringState>? IceGatheringStateChanged;
    public event Action<IceCandidate>? IceCandidateGathered;
    /// <summary>
    /// A remote track arrived; the handler owns it. With a key provider its frame cryptor is
    /// already attached. When nobody handles it, it is released. A receiver whose m-line became
    /// active again is reported again with a new track.
    /// </summary>
    public event Action<MediaTrack, RemoteTrackInfo>? TrackAdded;
    /// <summary>The remote created a data channel; the handler owns it.</summary>
    public event Action<DataChannel>? DataChannelReceived;
    public event Action? RenegotiationNeeded;
    /// <summary>Participant is "local" for sender cryptors and "remote" for receiver ones.</summary>
    public event Action<string, FrameCryptionState>? CryptorStateChanged;

    internal PeerConnection(PeerConnectionFactory factory, IReadOnlyList<IceServer> iceServers, KeyProvider? keyProvider)
    {
        _factory = factory;
        _id = CallbackRegistry.Register(this);
        var strings = new List<nint>();
        var servers = new IceServerNative[iceServers.Count];
        var keyAdded = false;
        try
        {
            for (var i = 0; i < iceServers.Count; i++)
            {
                servers[i].Uri = Utf8(iceServers[i].Uri, strings);
                servers[i].Username = Utf8(iceServers[i].Username, strings);
                servers[i].Password = Utf8(iceServers[i].Password, strings);
            }
            keyProvider?.Handle.DangerousAddRef(ref keyAdded);
            var observer = Callbacks.PeerConnectionObserver;
            fixed (IceServerNative* serverArray = servers)
            {
                _handle = NativeMethods.rtc_pc_create(factory.Handle, serverArray, servers.Length,
                    keyProvider?.Handle.DangerousGetHandle() ?? 0, &observer, _id);
            }
        }
        finally
        {
            if (keyAdded) keyProvider!.Handle.DangerousRelease();
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }
        if (_handle.IsInvalid)
        {
            CallbackRegistry.Unregister(_id);
            throw WebRtcException.FromLastError("Creating the peer connection failed");
        }
    }

    private static byte* Utf8(string? value, List<nint> allocations)
    {
        if (value == null) return null;
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        allocations.Add(pointer);
        return (byte*)pointer;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public RtpSender AddTrack(MediaTrack track, string streamId)
    {
        var handle = NativeMethods.rtc_pc_add_track(_handle, track.Handle, streamId);
        if (handle.IsInvalid) throw WebRtcException.FromLastError("Adding the track failed");
        return new RtpSender(handle);
    }

    /// <summary>
    /// A transceiver with an explicit direction. <paramref name="track"/> may be null (the sender
    /// sends nothing until <see cref="RtpSender.SetTrack"/>).
    /// </summary>
    public RtpSender AddTransceiver(MediaKind kind, TransceiverDirection direction, MediaTrack? track = null, string? streamId = null)
    {
        var added = false;
        try
        {
            track?.Handle.DangerousAddRef(ref added);
            var handle = NativeMethods.rtc_pc_add_transceiver(_handle, (int)kind, (int)direction,
                track?.Handle.DangerousGetHandle() ?? 0, streamId);
            if (handle.IsInvalid) throw WebRtcException.FromLastError("Adding the transceiver failed");
            return new RtpSender(handle);
        }
        finally
        {
            if (added) track!.Handle.DangerousRelease();
        }
    }

    /// <summary>In m-line order; empty once closed.</summary>
    public IReadOnlyList<TransceiverInfo> GetTransceivers()
    {
        var count = NativeMethods.rtc_pc_get_transceivers(_handle, null, 0);
        if (count <= 0) return [];
        var infos = new TransceiverInfoNative[count];
        fixed (TransceiverInfoNative* buffer = infos)
        {
            count = Math.Min(count, NativeMethods.rtc_pc_get_transceivers(_handle, buffer, infos.Length));
            var result = new TransceiverInfo[count];
            for (var i = 0; i < count; i++)
            {
                var info = &buffer[i];
                result[i] = new TransceiverInfo((MediaKind)info->Kind, (TransceiverDirection)info->Direction,
                    (TransceiverDirection)info->CurrentDirection, NativeMethods.Utf8(info->Mid), NativeMethods.Utf8(info->ReceiverId));
            }
            return result;
        }
    }

    public DataChannel CreateDataChannel(string label)
    {
        var handle = NativeMethods.rtc_pc_create_data_channel(_handle, label);
        if (handle.IsInvalid) throw WebRtcException.FromLastError("Creating the data channel failed");
        return new DataChannel(handle);
    }

    /// <summary>Creates an offer and sets it as the local description.</summary>
    public Task<SessionDescription> CreateOfferAsync()
    {
        var operation = new PendingOperation<SessionDescription>(_pending);
        NativeMethods.rtc_pc_create_offer(_handle, Callbacks.Sdp, operation.Id);
        return operation.Completion.Task;
    }

    /// <summary>Creates an answer and sets it as the local description.</summary>
    public Task<SessionDescription> CreateAnswerAsync()
    {
        var operation = new PendingOperation<SessionDescription>(_pending);
        NativeMethods.rtc_pc_create_answer(_handle, Callbacks.Sdp, operation.Id);
        return operation.Completion.Task;
    }

    public Task SetRemoteDescriptionAsync(SessionDescription description)
    {
        var operation = new PendingOperation<bool>(_pending);
        NativeMethods.rtc_pc_set_remote_description(_handle, description.Type, description.Sdp, Callbacks.Result, operation.Id);
        return operation.Completion.Task;
    }

    /// <summary>False when the connection is closed or the candidate was rejected.</summary>
    public bool AddIceCandidate(IceCandidate candidate) =>
        NativeMethods.rtc_pc_add_ice_candidate(_handle, candidate.SdpMid, candidate.SdpMLineIndex, candidate.Candidate) != 0;

    /// <summary>Moves <paramref name="mimeType"/> (e.g. "video/VP8") to the front on every transceiver of that kind.</summary>
    public int PreferCodec(MediaKind kind, string mimeType) => NativeMethods.rtc_pc_prefer_codec(_handle, (int)kind, mimeType);

    /// <summary>Attaches an encrypting frame cryptor to each sender with a track that has none yet.</summary>
    public int AttachSenderCryptors() => NativeMethods.rtc_pc_attach_sender_cryptors(_handle);

    /// <summary>
    /// Attaches a decrypting frame cryptor to each receiver that has none yet; call after setting
    /// a remote offer and before answering, so reused receivers decrypt from their first frame.
    /// </summary>
    public int AttachReceiverCryptors() => NativeMethods.rtc_pc_attach_receiver_cryptors(_handle);

    /// <summary>The loudest inbound audio level in [0, 1], or null when stats have none.</summary>
    public Task<double?> GetRemoteAudioLevelAsync()
    {
        var operation = new PendingOperation<double?>(_pending);
        NativeMethods.rtc_pc_get_remote_audio_level(_handle, Callbacks.AudioLevel, operation.Id);
        return operation.Completion.Task;
    }

    /// <summary>The packets received on all inbound audio streams, or null when stats have none.</summary>
    public Task<long?> GetInboundAudioPacketsAsync()
    {
        var operation = new PendingOperation<double?>(_pending);
        NativeMethods.rtc_pc_get_inbound_audio_packets(_handle, Callbacks.AudioLevel, operation.Id);
        // GetResult rethrows a cancelled operation as OperationCanceledException (Result would wrap it).
        return operation.Completion.Task.ContinueWith(t => t.GetAwaiter().GetResult() is { } packets ? (long?)packets : null,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Our microphone's peak in [0, 1] as WebRTC measures it before encoding (the audio
    /// media-source), or null when no audio track is attached or recording.
    /// </summary>
    public Task<double?> GetLocalAudioLevelAsync()
    {
        var operation = new PendingOperation<double?>(_pending);
        NativeMethods.rtc_pc_get_local_audio_level(_handle, Callbacks.AudioLevel, operation.Id);
        return operation.Completion.Task;
    }

    /// <summary>One receiver's inbound audio level in [0, 1], or null when unknown.</summary>
    public Task<double?> GetReceiverAudioLevelAsync(string receiverId)
    {
        var operation = new PendingOperation<double?>(_pending);
        NativeMethods.rtc_pc_get_receiver_audio_level(_handle, receiverId, Callbacks.AudioLevel, operation.Id);
        return operation.Completion.Task;
    }

    /// <summary>
    /// Closes the connection and its cryptors; pending operations are cancelled and no event is
    /// raised after this returns. Blocks on the signaling thread, so never call it from an event:
    /// from inside one of this connection's events it throws <see cref="InvalidOperationException"/>
    /// and leaves the connection open.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (NativeMethods.rtc_pc_close(_handle) == 0)
        {
            Volatile.Write(ref _closed, 0);
            throw new InvalidOperationException(NativeMethods.LastError());
        }
        CallbackRegistry.Unregister(_id);
        foreach (var operation in _pending.Values) operation.Cancel();
    }

    public void Dispose()
    {
        Close();
        _handle.Dispose();
        GC.KeepAlive(_factory);
    }

    internal void RaiseSignalingState(SignalingState state) => SignalingStateChanged?.Invoke(state);
    internal void RaiseConnectionState(PeerConnectionState state) => ConnectionStateChanged?.Invoke(state);
    internal void RaiseIceConnectionState(IceConnectionState state) => IceConnectionStateChanged?.Invoke(state);
    internal void RaiseIceGatheringState(IceGatheringState state) => IceGatheringStateChanged?.Invoke(state);
    internal void RaiseIceCandidate(IceCandidate candidate) => IceCandidateGathered?.Invoke(candidate);
    internal void RaiseRenegotiationNeeded() => RenegotiationNeeded?.Invoke();
    internal void RaiseCryptorState(string participant, FrameCryptionState state) => CryptorStateChanged?.Invoke(participant, state);

    internal void RaiseTrack(MediaTrack track, RemoteTrackInfo info)
    {
        var handler = TrackAdded;
        if (handler == null) track.Dispose();
        else handler(track, info);
    }

    internal void RaiseDataChannel(DataChannel channel)
    {
        var handler = DataChannelReceived;
        if (handler == null) channel.Dispose();
        else handler(channel);
    }
}
