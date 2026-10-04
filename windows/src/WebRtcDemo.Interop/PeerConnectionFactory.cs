using WebRtcDemo.Interop.Native;

namespace WebRtcDemo.Interop;

/// <summary>Owns libwebrtc's threads and the audio device module; create one per app.</summary>
public sealed unsafe class PeerConnectionFactory : IDisposable
{
    internal FactoryHandle Handle { get; }

    public PeerConnectionFactory()
    {
        Handle = NativeMethods.rtc_factory_create();
        if (Handle.IsInvalid) throw WebRtcException.FromLastError("Creating the peer connection factory failed");
    }

    // ---- Devices ------------------------------------------------------------------------------

    public IReadOnlyList<DeviceInfo> GetCameras()
    {
        var count = NativeMethods.rtc_video_device_count(Handle);
        var devices = new List<DeviceInfo>();
        for (var i = 0; i < count; i++)
        {
            var info = ReadDevice((uint)i, NativeMethods.rtc_video_device_info);
            if (info != null) devices.Add(info);
        }
        return devices;
    }

    public IReadOnlyList<DeviceInfo> GetMicrophones() =>
        ReadAudioDevices(NativeMethods.rtc_audio_recording_device_count(Handle), NativeMethods.rtc_audio_recording_device_info);

    public IReadOnlyList<DeviceInfo> GetSpeakers() =>
        ReadAudioDevices(NativeMethods.rtc_audio_playout_device_count(Handle), NativeMethods.rtc_audio_playout_device_info);

    /// <summary>RTC_AUDIO_DEFAULT_DEVICE: libwebrtc's (uint16_t)-1, the default communications device.</summary>
    private const uint DefaultAudioDevice = 0xFFFF;

    /// <summary>The Windows default communications microphone (an id of <see cref="GetMicrophones"/>); null elsewhere.</summary>
    public string? GetDefaultMicrophoneId() =>
        ReadDevice(DefaultAudioDevice, NativeMethods.rtc_audio_recording_device_info)?.Id is { Length: > 0 } id ? id : null;

    /// <summary>The Windows default communications speaker (an id of <see cref="GetSpeakers"/>); null elsewhere.</summary>
    public string? GetDefaultSpeakerId() =>
        ReadDevice(DefaultAudioDevice, NativeMethods.rtc_audio_playout_device_info)?.Id is { Length: > 0 } id ? id : null;

    public bool SetMicrophone(int index) => NativeMethods.rtc_audio_set_recording_device(Handle, (uint)index) == 0;

    public bool SetSpeaker(int index) => NativeMethods.rtc_audio_set_playout_device(Handle, (uint)index) == 0;

    private delegate int DeviceGetter(FactoryHandle factory, uint index, byte* name, int nameCap, byte* id, int idCap);

    private List<DeviceInfo> ReadAudioDevices(int count, DeviceGetter getter)
    {
        var devices = new List<DeviceInfo>();
        for (var i = 0; i < count; i++)
        {
            var info = ReadDevice((uint)i, getter);
            if (info != null) devices.Add(info);
        }
        return devices;
    }

    private DeviceInfo? ReadDevice(uint index, DeviceGetter getter)
    {
        // libwebrtc caps names at 128 bytes and ids at 128 (audio) / 256 (video).
        const int capacity = 512;
        var name = stackalloc byte[capacity];
        var id = stackalloc byte[capacity];
        if (getter(Handle, index, name, capacity, id, capacity) != 0) return null;
        return new DeviceInfo((int)index, NativeMethods.Utf8(name), NativeMethods.Utf8(id));
    }

    // ---- Tracks and sources -------------------------------------------------------------------

    public MediaTrack CreateAudioTrack(string trackId)
    {
        var handle = NativeMethods.rtc_audio_track_create(Handle, trackId);
        if (handle.IsInvalid) throw WebRtcException.FromLastError("Creating the microphone track failed");
        return new MediaTrack(handle, MediaKind.Audio);
    }

    public MediaTrack CreateVideoTrack(VideoSource source, string trackId)
    {
        var handle = NativeMethods.rtc_video_track_create(Handle, source.Handle, trackId);
        if (handle.IsInvalid) throw WebRtcException.FromLastError("Creating the video track failed");
        return new MediaTrack(handle, MediaKind.Video);
    }

    /// <summary>Opens a camera and starts capturing.</summary>
    public VideoSource CreateCameraSource(int deviceIndex, int width = 1280, int height = 720, int fps = 30) =>
        VideoSource.Create(VideoSourceKind.Camera, null, _ =>
            NativeMethods.rtc_camera_source_create(Handle, (uint)deviceIndex, (uint)width, (uint)height, (uint)fps),
            "Opening the camera failed");

    /// <summary>A source fed with <see cref="VideoSource.PushI420"/>.</summary>
    public VideoSource CreateCustomSource() =>
        VideoSource.Create(VideoSourceKind.Custom, null, _ => NativeMethods.rtc_custom_source_create(Handle),
            "Creating a custom video source failed");

    /// <param name="onState">Called on a WebRTC thread when capture starts, pauses, stops or fails.</param>
    public VideoSource CreateDesktopSource(DesktopMediaSource source, int fps, bool showCursor, Action<CaptureState>? onState) =>
        VideoSource.Create(VideoSourceKind.Desktop, onState, user =>
            NativeMethods.rtc_desktop_source_create(Handle, source.Handle, (uint)fps, showCursor ? 1 : 0, Callbacks.CaptureState, user),
            "Starting screen capture failed");

    /// <summary>Decodes a local video file (Windows only) at its own frame rate.</summary>
    public VideoSource CreateFileSource(string path, bool loop, Action<CaptureState>? onState) =>
        VideoSource.Create(VideoSourceKind.File, onState, user =>
            NativeMethods.rtc_file_source_create(Handle, path, loop ? 1 : 0, Callbacks.CaptureState, user),
            "Opening the video file failed");

    /// <param name="onChanged">Called on a WebRTC thread, e.g. when a thumbnail is ready.</param>
    public DesktopMediaList CreateDesktopMediaList(DesktopSourceType type, Action<DesktopListEvent, string>? onChanged) =>
        new(this, type, onChanged);

    public PeerConnection CreatePeerConnection(IReadOnlyList<IceServer> iceServers, KeyProvider? keyProvider) =>
        new(this, iceServers, keyProvider);

    public void Dispose() => Handle.Dispose();
}
