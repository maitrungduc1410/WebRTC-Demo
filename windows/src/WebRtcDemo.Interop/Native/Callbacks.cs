using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WebRtcDemo.Interop.Native;

/// <summary>
/// The entry points the shim calls on WebRTC threads. Each one looks its target up by id and
/// hands over without blocking; an exception must never unwind into native code.
/// </summary>
internal static unsafe class Callbacks
{
    public static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, byte*, void> Sdp = &OnSdp;
    public static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, void> Result = &OnResult;
    public static readonly delegate* unmanaged[Cdecl]<nint, double, void> AudioLevel = &OnAudioLevel;
    public static readonly delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, int, void> VideoFrame = &OnVideoFrame;
    public static readonly delegate* unmanaged[Cdecl]<nint, int, void> CaptureState = &OnCaptureState;
    public static readonly delegate* unmanaged[Cdecl]<nint, int, byte*, void> DesktopList = &OnDesktopList;
    public static readonly delegate* unmanaged[Cdecl]<nint, byte*, void> Log = &OnLog;

    public static PcObserverNative PeerConnectionObserver => new()
    {
        OnSignalingState = &OnSignalingState,
        OnConnectionState = &OnConnectionState,
        OnIceConnectionState = &OnIceConnectionState,
        OnIceGatheringState = &OnIceGatheringState,
        OnIceCandidate = &OnIceCandidate,
        OnTrack = &OnTrack,
        OnDataChannel = &OnDataChannel,
        OnRenegotiationNeeded = &OnRenegotiationNeeded,
        OnCryptorState = &OnCryptorState,
    };

    public static DataChannelObserverNative DataChannelObserver => new()
    {
        OnState = &OnDataChannelState,
        OnMessage = &OnDataChannelMessage,
    };

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }

    // ---- Operations ---------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSdp(nint ctx, int ok, byte* type, byte* sdp, byte* error)
    {
        try
        {
            var operation = PendingOperation<SessionDescription>.Complete(ctx);
            if (operation == null) return;
            if (ok != 0) operation.Completion.TrySetResult(new SessionDescription(NativeMethods.Utf8(type), NativeMethods.Utf8(sdp)));
            else operation.Completion.TrySetException(new WebRtcException(NativeMethods.Utf8(error)));
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(nint ctx, int ok, byte* error)
    {
        try
        {
            var operation = PendingOperation<bool>.Complete(ctx);
            if (operation == null) return;
            if (ok != 0) operation.Completion.TrySetResult(true);
            else operation.Completion.TrySetException(new WebRtcException(NativeMethods.Utf8(error)));
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAudioLevel(nint ctx, double level)
    {
        Guard(() => PendingOperation<double?>.Complete(ctx)?.Completion.TrySetResult(level < 0 ? null : level));
    }

    // ---- Media --------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnVideoFrame(nint user, byte* bgra, int width, int height, int stride, int rotation)
    {
        try
        {
            var sink = CallbackRegistry.Get<VideoSink>(user);
            sink?.Deliver(new VideoFrame(new ReadOnlySpan<byte>(bgra, stride * height), width, height, stride, rotation));
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCaptureState(nint user, int state)
    {
        Guard(() => CallbackRegistry.Get<VideoSource>(user)?.RaiseCaptureState((CaptureState)state));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDesktopList(nint user, int evt, byte* sourceId)
    {
        var id = NativeMethods.Utf8(sourceId);
        Guard(() => CallbackRegistry.Get<DesktopMediaList>(user)?.Raise((DesktopListEvent)evt, id));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(nint user, byte* message)
    {
        var text = NativeMethods.Utf8(message);
        Guard(() => WebRtcRuntime.RaiseLog(text));
    }

    // ---- Peer connection ----------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSignalingState(nint user, int state) =>
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseSignalingState((SignalingState)state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnConnectionState(nint user, int state) =>
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseConnectionState((PeerConnectionState)state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIceConnectionState(nint user, int state) =>
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseIceConnectionState((IceConnectionState)state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIceGatheringState(nint user, int state) =>
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseIceGatheringState((IceGatheringState)state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIceCandidate(nint user, byte* sdpMid, int sdpMLineIndex, byte* candidate)
    {
        var mid = sdpMid == null ? null : NativeMethods.Utf8(sdpMid);
        var text = NativeMethods.Utf8(candidate);
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseIceCandidate(new IceCandidate(mid, sdpMLineIndex, text)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTrack(nint user, nint track, int kind, byte* receiverId, byte* streamId)
    {
        // Wrapped first so the handle is released even if nobody takes it.
        var handle = ShimHandle.Wrap<TrackHandle>(track);
        var receiver = NativeMethods.Utf8(receiverId);
        var stream = NativeMethods.Utf8(streamId);
        try
        {
            var pc = CallbackRegistry.Get<PeerConnection>(user);
            if (pc == null)
            {
                handle.Dispose();
                return;
            }
            pc.RaiseTrack(new MediaTrack(handle, (MediaKind)kind), new RemoteTrackInfo(receiver, stream));
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDataChannel(nint user, nint channel)
    {
        var handle = ShimHandle.Wrap<DataChannelHandle>(channel);
        try
        {
            var pc = CallbackRegistry.Get<PeerConnection>(user);
            if (pc == null)
            {
                handle.Dispose();
                return;
            }
            pc.RaiseDataChannel(new DataChannel(handle));
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRenegotiationNeeded(nint user) =>
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseRenegotiationNeeded());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCryptorState(nint user, byte* participantId, int state)
    {
        var participant = NativeMethods.Utf8(participantId);
        Guard(() => CallbackRegistry.Get<PeerConnection>(user)?.RaiseCryptorState(participant, (FrameCryptionState)state));
    }

    // ---- Data channel -------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDataChannelState(nint user, int state) =>
        Guard(() => CallbackRegistry.Get<DataChannel>(user)?.RaiseState((DataChannelState)state));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDataChannelMessage(nint user, byte* data, int length, int binary)
    {
        try
        {
            var bytes = length > 0 ? new ReadOnlySpan<byte>(data, length).ToArray() : [];
            CallbackRegistry.Get<DataChannel>(user)?.RaiseMessage(new DataChannelMessage(bytes, binary != 0));
        }
        catch (Exception e)
        {
            WebRtcRuntime.ReportCallbackException(e);
        }
    }
}
