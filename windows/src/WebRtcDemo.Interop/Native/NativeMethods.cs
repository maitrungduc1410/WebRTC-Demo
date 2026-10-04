using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WebRtcDemo.Interop.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PcObserverNative
{
    public delegate* unmanaged[Cdecl]<nint, int, void> OnSignalingState;
    public delegate* unmanaged[Cdecl]<nint, int, void> OnConnectionState;
    public delegate* unmanaged[Cdecl]<nint, int, void> OnIceConnectionState;
    public delegate* unmanaged[Cdecl]<nint, int, void> OnIceGatheringState;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, byte*, void> OnIceCandidate;
    public delegate* unmanaged[Cdecl]<nint, nint, int, byte*, byte*, void> OnTrack;
    public delegate* unmanaged[Cdecl]<nint, nint, void> OnDataChannel;
    public delegate* unmanaged[Cdecl]<nint, void> OnRenegotiationNeeded;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, void> OnCryptorState;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DataChannelObserverNative
{
    public delegate* unmanaged[Cdecl]<nint, int, void> OnState;
    public delegate* unmanaged[Cdecl]<nint, byte*, int, int, void> OnMessage;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IceServerNative
{
    public byte* Uri;
    public byte* Username;
    public byte* Password;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TransceiverInfoNative
{
    public int Kind;
    public int Direction;
    public int CurrentDirection;
    public fixed byte Mid[64];
    public fixed byte ReceiverId[192];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KeyProviderOptionsNative
{
    public int SharedKey;
    public byte* RatchetSalt;
    public int RatchetSaltLength;
    public byte* UncryptedMagicBytes;
    public int UncryptedMagicBytesLength;
    public int RatchetWindowSize;
    public int FailureTolerance;
    public int KeyRingSize;
    public int DiscardFrameWhenCryptorNotReady;
    public int KeyDerivation;
}

/// <summary>Raw rtc_shim.h imports. Use the wrapper classes instead.</summary>
[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Mirrors the C API names")]
internal static unsafe partial class NativeMethods
{
    public const string Library = "rtc_shim";
    public const int ExpectedAbiVersion = 7;

    // ---- Library ------------------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_shim_abi_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* rtc_shim_libwebrtc_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* rtc_last_error();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_initialize();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_terminate();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_set_log_callback(int minSeverity, delegate* unmanaged[Cdecl]<nint, byte*, void> cb, nint user);

    // ---- Factory and devices ------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial FactoryHandle rtc_factory_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_factory_release(nint factory);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_video_device_count(FactoryHandle factory);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_video_device_info(FactoryHandle factory, uint index, byte* name, int nameCap, byte* uniqueId, int uniqueIdCap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_audio_recording_device_count(FactoryHandle factory);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_audio_recording_device_info(FactoryHandle factory, uint index, byte* name, int nameCap, byte* guid, int guidCap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_audio_set_recording_device(FactoryHandle factory, uint index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_audio_playout_device_count(FactoryHandle factory);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_audio_playout_device_info(FactoryHandle factory, uint index, byte* name, int nameCap, byte* guid, int guidCap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_audio_set_playout_device(FactoryHandle factory, uint index);

    // ---- Tracks -------------------------------------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial TrackHandle rtc_audio_track_create(FactoryHandle factory, string trackId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial TrackHandle rtc_video_track_create(FactoryHandle factory, VideoSourceHandle source, string trackId);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_track_kind(TrackHandle track);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_track_id(TrackHandle track, byte* buf, int cap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_track_set_enabled(TrackHandle track, int enabled);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_track_is_enabled(TrackHandle track);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_track_release(nint track);

    // ---- Video sources ------------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial VideoSourceHandle rtc_camera_source_create(FactoryHandle factory, uint deviceIndex, uint width, uint height, uint fps);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_video_source_set_capturing(VideoSourceHandle source, int capturing);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial VideoSourceHandle rtc_custom_source_create(FactoryHandle factory);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_custom_source_push_i420(VideoSourceHandle source, int width, int height, byte* y, int strideY, byte* u, int strideU, byte* v, int strideV);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial VideoSourceHandle rtc_desktop_source_create(FactoryHandle factory, MediaSourceHandle mediaSource, uint fps, int showCursor, delegate* unmanaged[Cdecl]<nint, int, void> cb, nint user);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial VideoSourceHandle rtc_file_source_create(FactoryHandle factory, string utf8Path, int loop, delegate* unmanaged[Cdecl]<nint, int, void> cb, nint user);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_video_source_release(nint source);

    // ---- Video sinks --------------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial VideoSinkHandle rtc_video_sink_create(TrackHandle videoTrack, int maxWidth, int maxHeight, delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, int, void> cb, nint user);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_video_sink_set_max_size(VideoSinkHandle sink, int maxWidth, int maxHeight);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_video_sink_release(nint sink);

    // ---- Screen / window sources --------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial DesktopListHandle rtc_desktop_list_create(FactoryHandle factory, int type, delegate* unmanaged[Cdecl]<nint, int, byte*, void> cb, nint user);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_desktop_list_update(DesktopListHandle list, int forceReload, int thumbnails);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial MediaSourceHandle rtc_desktop_list_source(DesktopListHandle list, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_desktop_list_release(nint list);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_media_source_id(MediaSourceHandle source, byte* buf, int cap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_media_source_name(MediaSourceHandle source, byte* buf, int cap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_media_source_type(MediaSourceHandle source);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_media_source_thumbnail(MediaSourceHandle source, byte* buf, int cap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_media_source_release(nint source);

    // ---- End-to-end encryption ----------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial KeyProviderHandle rtc_key_provider_create(KeyProviderOptionsNative* options);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_key_provider_set_shared_key(KeyProviderHandle provider, int index, byte* key, int length);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_key_provider_release(nint provider);

    // ---- Peer connection ----------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial PeerConnectionHandle rtc_pc_create(FactoryHandle factory, IceServerNative* iceServers, int iceServerCount, nint keyProvider, PcObserverNative* observer, nint user);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SenderHandle rtc_pc_add_track(PeerConnectionHandle pc, TrackHandle track, string streamId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SenderHandle rtc_pc_add_transceiver(PeerConnectionHandle pc, int kind, int direction, nint track, string? streamId);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_get_transceivers(PeerConnectionHandle pc, TransceiverInfoNative* output, int capacity);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_sender_set_track(SenderHandle sender, nint track);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_sender_release(nint sender);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial DataChannelHandle rtc_pc_create_data_channel(PeerConnectionHandle pc, string label);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_create_offer(PeerConnectionHandle pc, delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, byte*, void> cb, nint ctx);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_create_answer(PeerConnectionHandle pc, delegate* unmanaged[Cdecl]<nint, int, byte*, byte*, byte*, void> cb, nint ctx);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_set_remote_description(PeerConnectionHandle pc, string type, string sdp, delegate* unmanaged[Cdecl]<nint, int, byte*, void> cb, nint ctx);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_add_ice_candidate(PeerConnectionHandle pc, string? sdpMid, int sdpMLineIndex, string candidate);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_prefer_codec(PeerConnectionHandle pc, int kind, string mimeType);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_attach_sender_cryptors(PeerConnectionHandle pc);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_attach_receiver_cryptors(PeerConnectionHandle pc);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_get_remote_audio_level(PeerConnectionHandle pc, delegate* unmanaged[Cdecl]<nint, double, void> cb, nint ctx);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_get_inbound_audio_packets(PeerConnectionHandle pc, delegate* unmanaged[Cdecl]<nint, double, void> cb, nint ctx);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_get_local_audio_level(PeerConnectionHandle pc, delegate* unmanaged[Cdecl]<nint, double, void> cb, nint ctx);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_pc_get_receiver_audio_level(PeerConnectionHandle pc, string receiverId, delegate* unmanaged[Cdecl]<nint, double, void> cb, nint ctx);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_close(PeerConnectionHandle pc);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_pc_release(nint pc);

    // ---- Data channel -------------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_data_channel_set_observer(DataChannelHandle channel, DataChannelObserverNative* observer, nint user);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_data_channel_send(DataChannelHandle channel, byte* data, int length, int binary);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_data_channel_state(DataChannelHandle channel);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int rtc_data_channel_label(DataChannelHandle channel, byte* buf, int cap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_data_channel_close(DataChannelHandle channel);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void rtc_data_channel_release(nint channel);

    // ---- Helpers ------------------------------------------------------------------------------

    public static string LastError() => Utf8(rtc_last_error());

    public static string Utf8(byte* text) => text == null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    public delegate int StringGetter(byte* buffer, int capacity);

    /// <summary>The shim's buffer pattern: returns the full length; retry when it did not fit.</summary>
    public static string ReadString(StringGetter getter)
    {
        Span<byte> stack = stackalloc byte[256];
        fixed (byte* buffer = stack)
        {
            var length = getter(buffer, stack.Length);
            if (length <= 0) return string.Empty;
            if (length < stack.Length) return Encoding.UTF8.GetString(buffer, length);
        }
        var capacity = 256;
        while (true)
        {
            capacity *= 2;
            var heap = new byte[capacity];
            fixed (byte* buffer = heap)
            {
                var length = getter(buffer, heap.Length);
                if (length < heap.Length) return Encoding.UTF8.GetString(buffer, Math.Max(0, length));
            }
        }
    }
}