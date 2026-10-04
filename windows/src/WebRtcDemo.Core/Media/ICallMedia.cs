using WebRtcDemo.Effects;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

public sealed record MediaDevice(string Id, string Name);

public enum AudioDeviceKind { Microphone, Speaker }

public enum Presentation { None, Screen, File }

/// <summary>A screen or window offered by the share picker.</summary>
public sealed class ShareSource(string id, string name, DesktopSourceType type, object? native)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public DesktopSourceType Type { get; } = type;
    /// <summary>JPEG; empty until captured. Updated on the UI thread.</summary>
    public byte[] Thumbnail { get; internal set; } = [];
    internal object? Native { get; } = native;
}

/// <summary>The screens or windows to share, refreshed while the picker is open.</summary>
public interface IShareSourceList : IDisposable
{
    DesktopSourceType Type { get; }

    /// <summary>Raised on the UI thread when a source's thumbnail was updated.</summary>
    event Action<ShareSource>? ThumbnailChanged;

    Task<IReadOnlyList<ShareSource>> RefreshAsync();
}

public sealed record LocalMediaResult(bool Microphone, bool Camera);

/// <summary>
/// The WebRTC side of a call: local devices, one peer connection at a time and its data channel
/// (or the two group connections). Every member is called on the UI thread and every event is
/// raised on it; events of a peer connection that has been closed since are never raised.
/// Muting or hiding the remote side applies to every remote track, also ones added later.
/// </summary>
public interface ICallMedia : IGroupCallMedia, IDisposable
{
    event Action<IceCandidate>? IceCandidateGathered;
    event Action<PeerConnectionState>? ConnectionStateChanged;
    event Action<IVideoFeed?>? RemoteVideoChanged;
    event Action<IVideoFeed?>? LocalVideoChanged;
    event Action<DataChannelState>? ChatStateChanged;
    event Action<string>? ChatMessageReceived;
    /// <summary>The screen or file capture stopped or failed on its own (window closed, bad file).</summary>
    event Action<Presentation, CaptureState>? PresentationStateChanged;
    event Action? DevicesChanged;
    /// <summary>
    /// The selected microphone or speaker went away mid-call (unplugged, a headset disconnected)
    /// and the default one took over through the audio device module, so the audio track and its
    /// sender stay as they are; null when none is left. A microphone event needs a microphone track.
    /// </summary>
    event Action<AudioDeviceKind, MediaDevice?>? AudioDeviceReplaced;
    /// <summary>
    /// The applied effect stopped working (a model failed while drawing). Frames stay held, never
    /// the raw camera; the broken model comes back on the CPU with the next <see cref="SetEffectsAsync"/>.
    /// </summary>
    event Action? EffectsFailed;

    bool HasMicrophone { get; }
    bool HasCamera { get; }
    bool HasPeerConnection { get; }
    /// <summary>What the local tile shows: the video being sent.</summary>
    IVideoFeed? LocalVideo { get; }
    /// <summary>The local tile mirrors the camera, but never a presentation.</summary>
    bool LocalVideoIsCamera { get; }
    bool SupportsFileSharing { get; }
    /// <summary>Backgrounds and stickers for the camera; false when the models aren't bundled.</summary>
    bool SupportsEffects { get; }
    EffectsCatalog EffectsCatalog { get; }

    IReadOnlyList<MediaDevice> Cameras { get; }
    IReadOnlyList<MediaDevice> Microphones { get; }
    IReadOnlyList<MediaDevice> Speakers { get; }
    string? CameraId { get; }
    string? MicrophoneId { get; }
    string? SpeakerId { get; }

    /// <summary>Opens the microphone and camera (preferred ids when still connected).</summary>
    Task<LocalMediaResult> StartLocalMediaAsync(string? cameraId, string? microphoneId, string? speakerId);
    void StopLocalMedia();
    void RefreshDevices();
    /// <summary>
    /// Re-reads the microphones, speakers and their defaults off the UI thread; only a change is
    /// applied (and raises <see cref="DevicesChanged"/>). Cameras are read by <see cref="RefreshDevices"/>.
    /// </summary>
    Task CheckDevicesAsync();

    /// <summary>The key for the next peer connection, and for the current one when it has E2EE.</summary>
    void SetEncryptionKey(byte[] key);

    /// <summary>Closes any previous connection and opens one with the local tracks attached.</summary>
    void OpenPeerConnection(bool e2ee);
    void ClosePeerConnection();
    /// <summary>VP8 first under E2EE, so every platform negotiates the codec they all can encrypt.</summary>
    void ApplyCodecPreferences();
    void CreateChatChannel();
    Task<SessionDescription> CreateOfferAsync();
    Task<SessionDescription> CreateAnswerAsync();
    Task SetRemoteDescriptionAsync(SessionDescription description);
    bool AddIceCandidate(IceCandidate candidate);
    bool SendChat(string text);
    /// <summary>Null when unavailable.</summary>
    Task<double?> GetRemoteAudioLevelAsync();
    /// <summary>
    /// The microphone's peak in [0, 1] since the last call: from the call's stats when a connection
    /// sends it, else from a meter that only runs while waiting alone. 0 while muted, null when unknown.
    /// </summary>
    Task<double?> GetMicPeakAsync();

    void SetMicrophoneEnabled(bool enabled);
    /// <summary>Off disables the track and closes the camera (its light goes off); on reopens it.</summary>
    Task SetCameraEnabledAsync(bool enabled);
    Task<bool> SelectCameraAsync(string id);
    bool SelectMicrophone(string id);
    bool SelectSpeaker(string id);

    void SetRemoteAudioEnabled(bool enabled);
    void SetRemoteVideoEnabled(bool enabled);

    IShareSourceList CreateShareSourceList(DesktopSourceType type);
    Task<bool> StartScreenShareAsync(ShareSource source);
    Task<bool> StartFileShareAsync(string path);
    /// <summary>Back to the camera, which is reopened when it is on.</summary>
    Task StopPresentingAsync();

    /// <summary>
    /// Call before <see cref="StartLocalMediaAsync"/> when a saved effect will be applied: no
    /// camera frame is sent or shown until it is ready (or <see cref="SetEffectsAsync"/> clears it).
    /// </summary>
    void HoldEffects();

    /// <summary>
    /// Loads and applies a background and sticker to the camera (never to a presentation). The
    /// camera keeps going out unchanged until the first processed frame. False when loading failed;
    /// the previous effect is still applied then.
    /// </summary>
    Task<bool> SetEffectsAsync(EffectsSelection selection);
}
