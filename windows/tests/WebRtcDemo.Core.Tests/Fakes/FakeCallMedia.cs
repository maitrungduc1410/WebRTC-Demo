using WebRtcDemo.Core.Group;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Effects;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests.Fakes;

/// <summary>Records what the view model asks of the media layer; the test raises its events.</summary>
public sealed class FakeCallMedia : ICallMedia
{
    public List<string> Log { get; } = [];
    public byte[]? Key { get; private set; }
    public bool? OpenedWithE2ee { get; private set; }
    public List<IceCandidate> AddedCandidates { get; } = [];
    public List<string> SentChat { get; } = [];
    public bool CameraEnabled { get; private set; } = true;
    public bool MicEnabled { get; private set; } = true;
    public double? AudioLevel { get; set; } = 0.2;
    public int AudioLevelRequests { get; private set; }
    public bool ChatOpen { get; set; }
    public LocalMediaResult LocalResult { get; set; } = new(true, true);
    public TaskCompletionSource<SessionDescription>? PendingOffer { get; set; }

    public event Action<IceCandidate>? IceCandidateGathered;
    public event Action<PeerConnectionState>? ConnectionStateChanged;
    public event Action<IVideoFeed?>? RemoteVideoChanged;
    public event Action<IVideoFeed?>? LocalVideoChanged;
    public event Action<DataChannelState>? ChatStateChanged;
    public event Action<string>? ChatMessageReceived;
    public event Action<Presentation, CaptureState>? PresentationStateChanged;
    public event Action? DevicesChanged;
    public event Action<AudioDeviceKind, MediaDevice?>? AudioDeviceReplaced;
    public event Action? EffectsFailed;

    public bool HasMicrophone => LocalResult.Microphone;
    public bool HasCamera => LocalResult.Camera;
    public bool HasPeerConnection { get; private set; }
    public IVideoFeed? LocalVideo => null;
    public bool LocalVideoIsCamera => true;
    public bool SupportsFileSharing => true;
    public bool SupportsEffects { get; set; } = true;
    public EffectsCatalog EffectsCatalog { get; set; } = EffectsCatalog.Load(Path.Combine(AppContext.BaseDirectory, "effects"));
    /// <summary>Background ids whose load fails.</summary>
    public HashSet<string> BrokenEffects { get; } = [];
    /// <summary>When set, SetEffectsAsync waits for it (to test superseded loads).</summary>
    public TaskCompletionSource? PendingEffects { get; set; }
    public EffectsSelection AppliedEffects { get; private set; } = EffectsSelection.Off;
    public IReadOnlyList<MediaDevice> Cameras { get; set; } = [new("cam-1", "Front"), new("cam-2", "USB")];
    public IReadOnlyList<MediaDevice> Microphones { get; private set; } = [new("mic-1", "Mic")];
    public IReadOnlyList<MediaDevice> Speakers { get; private set; } = [new("spk-1", "Speakers")];
    public string? CameraId { get; private set; } = "cam-1";
    public string? MicrophoneId { get; private set; } = "mic-1";
    public string? SpeakerId { get; private set; } = "spk-1";
    public int DeviceChecks { get; private set; }
    /// <summary>Applied by the next <see cref="CheckDevicesAsync"/>, as the native media does with a changed list.</summary>
    public (IReadOnlyList<MediaDevice> Microphones, IReadOnlyList<MediaDevice> Speakers)? NextDevices { get; set; }

    public Task<LocalMediaResult> StartLocalMediaAsync(string? cameraId, string? microphoneId, string? speakerId)
    {
        Log.Add("StartLocalMedia");
        return Task.FromResult(LocalResult);
    }

    public void StopLocalMedia()
    {
        HasGroupConnections = false;
        Log.Add("StopLocalMedia");
    }
    public void RefreshDevices() => DevicesChanged?.Invoke();

    public Task CheckDevicesAsync()
    {
        DeviceChecks++;
        if (NextDevices is not { } next) return Task.CompletedTask;
        NextDevices = null;
        Microphones = next.Microphones;
        Speakers = next.Speakers;
        var microphoneId = Follow(MicrophoneId, Microphones);
        var speakerId = Follow(SpeakerId, Speakers);
        var micReplaced = microphoneId != MicrophoneId;
        var speakerReplaced = speakerId != SpeakerId;
        MicrophoneId = microphoneId;
        SpeakerId = speakerId;
        DevicesChanged?.Invoke();
        if (micReplaced) AudioDeviceReplaced?.Invoke(AudioDeviceKind.Microphone, Microphones.FirstOrDefault(d => d.Id == microphoneId));
        if (speakerReplaced) AudioDeviceReplaced?.Invoke(AudioDeviceKind.Speaker, Speakers.FirstOrDefault(d => d.Id == speakerId));
        return Task.CompletedTask;
    }

    private static string? Follow(string? current, IReadOnlyList<MediaDevice> devices) =>
        current != null && devices.Any(d => d.Id == current) ? current : devices.Count > 0 ? devices[0].Id : null;

    public void SetEncryptionKey(byte[] key)
    {
        Key = key;
        Log.Add("SetEncryptionKey");
    }

    public void OpenPeerConnection(bool e2ee)
    {
        HasPeerConnection = true;
        OpenedWithE2ee = e2ee;
        Log.Add($"Open(e2ee={e2ee})");
    }

    public void ClosePeerConnection()
    {
        if (!HasPeerConnection) return;
        HasPeerConnection = false;
        Log.Add("Close");
    }

    public void ApplyCodecPreferences() => Log.Add("ApplyCodecPreferences");
    public void CreateChatChannel() => Log.Add("CreateChatChannel");

    public Task<SessionDescription> CreateOfferAsync()
    {
        Log.Add("CreateOffer");
        return PendingOffer?.Task ?? Task.FromResult(new SessionDescription("offer", "v=0 offer"));
    }

    public Task<SessionDescription> CreateAnswerAsync()
    {
        Log.Add("CreateAnswer");
        return Task.FromResult(new SessionDescription("answer", "v=0 answer"));
    }

    public Task SetRemoteDescriptionAsync(SessionDescription description)
    {
        Log.Add($"SetRemote({description.Type})");
        return Task.CompletedTask;
    }

    public bool AddIceCandidate(IceCandidate candidate)
    {
        AddedCandidates.Add(candidate);
        return true;
    }

    public bool SendChat(string text)
    {
        if (!ChatOpen) return false;
        SentChat.Add(text);
        return true;
    }

    public Task<double?> GetRemoteAudioLevelAsync()
    {
        AudioLevelRequests++;
        return Task.FromResult(AudioLevel);
    }

    /// <summary>What the next mic level poll reads; a pending source holds the poll open.</summary>
    public double? MicPeak { get; set; }
    public TaskCompletionSource<double?>? PendingMicPeak { get; set; }
    public int MicPeakRequests { get; private set; }

    public Task<double?> GetMicPeakAsync()
    {
        MicPeakRequests++;
        return PendingMicPeak?.Task ?? Task.FromResult(MicPeak);
    }

    public void SetMicrophoneEnabled(bool enabled) => MicEnabled = enabled;

    public bool CameraReopens { get; set; } = true;

    public Task<bool> SetCameraEnabledAsync(bool enabled)
    {
        CameraEnabled = enabled;
        Log.Add($"Camera({enabled})");
        return Task.FromResult(!enabled || CameraReopens);
    }

    public Task<bool> SelectCameraAsync(string id)
    {
        CameraId = id;
        return Task.FromResult(true);
    }

    public bool SelectMicrophone(string id) => true;
    public bool SelectSpeaker(string id) => true;
    public void SetRemoteAudioEnabled(bool enabled)
    {
        RemoteAudioEnabled = enabled;
        Log.Add($"RemoteAudio({enabled})");
    }

    public void SetRemoteVideoEnabled(bool enabled)
    {
        RemoteVideoEnabled = enabled;
        Log.Add($"RemoteVideo({enabled})");
    }
    public IShareSourceList CreateShareSourceList(DesktopSourceType type) => throw new NotSupportedException();

    public Task<bool> StartScreenShareAsync(ShareSource source)
    {
        Log.Add($"ShareScreen({source.Name})");
        return Task.FromResult(true);
    }

    public Task<bool> StartFileShareAsync(string path)
    {
        Log.Add("ShareFile");
        return Task.FromResult(!path.Contains("broken", StringComparison.Ordinal));
    }

    public Task<bool> StopPresentingAsync()
    {
        Log.Add("StopPresenting");
        return Task.FromResult(!CameraEnabled || CameraReopens);
    }

    public void HoldEffects() => Log.Add("HoldEffects");

    public async Task<bool> SetEffectsAsync(EffectsSelection selection)
    {
        Log.Add($"SetEffects({selection.Background},{selection.Sticker})");
        if (PendingEffects is { } pending) await pending.Task;
        if (BrokenEffects.Contains(selection.Background)) return false;
        AppliedEffects = selection;
        return true;
    }

    public void Dispose() => Log.Add("Dispose");

    // ---- Group call ---------------------------------------------------------------------------

    public event Action<GroupConnection, IceCandidate>? GroupIceCandidateGathered;
    public event Action<GroupConnection, PeerConnectionState>? GroupConnectionStateChanged;
    public event Action<string, IVideoFeed?>? ParticipantVideoChanged;

    public bool HasGroupConnections { get; private set; }
    public string? GroupStreamId { get; private set; }
    public List<(GroupConnection Connection, IceCandidate Candidate)> AddedGroupCandidates { get; } = [];
    public Dictionary<string, double> ParticipantLevels { get; } = [];
    public bool RemoteAudioEnabled { get; private set; } = true;
    public bool RemoteVideoEnabled { get; private set; } = true;
    /// <summary>When set, the next subscribe answer waits for it (to test the one-at-a-time queue).</summary>
    public TaskCompletionSource? PendingSubscribeAnswer { get; set; }
    public bool FailSubscribeAnswer { get; set; }

    public void OpenGroupConnections(bool e2ee, string streamId)
    {
        HasGroupConnections = true;
        OpenedWithE2ee = e2ee;
        GroupStreamId = streamId;
        Log.Add($"OpenGroup(e2ee={e2ee})");
    }

    public void CloseGroupConnections()
    {
        if (!HasGroupConnections) return;
        HasGroupConnections = false;
        Log.Add("CloseGroup");
    }

    public Task<SessionDescription> CreatePublishOfferAsync()
    {
        Log.Add("CreatePublishOffer");
        return Task.FromResult(new SessionDescription("offer", "v=0 publish"));
    }

    public Task SetPublishAnswerAsync(string sdp)
    {
        Log.Add($"SetPublishAnswer({sdp})");
        return Task.CompletedTask;
    }

    public async Task<SessionDescription> AnswerSubscribeOfferAsync(string sdp)
    {
        Log.Add($"AnswerSubscribe({sdp})");
        if (FailSubscribeAnswer) throw new WebRtcException("bad offer");
        if (PendingSubscribeAnswer is { } pending)
        {
            PendingSubscribeAnswer = null;
            await pending.Task;
        }
        return new SessionDescription("answer", "answer to " + sdp);
    }

    public bool AddGroupIceCandidate(GroupConnection connection, IceCandidate candidate)
    {
        AddedGroupCandidates.Add((connection, candidate));
        return true;
    }

    public Task<IReadOnlyDictionary<string, double>> GetParticipantAudioLevelsAsync()
    {
        AudioLevelRequests++;
        return Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>(ParticipantLevels));
    }

    public void RaiseGroupCandidate(GroupConnection connection, IceCandidate candidate) => GroupIceCandidateGathered?.Invoke(connection, candidate);
    public void RaiseGroupConnection(GroupConnection connection, PeerConnectionState state) => GroupConnectionStateChanged?.Invoke(connection, state);
    public void RaiseParticipantVideo(string id, IVideoFeed? feed) => ParticipantVideoChanged?.Invoke(id, feed);

    // ---- Driving events -----------------------------------------------------------------------

    public void RaiseCandidate(IceCandidate candidate) => IceCandidateGathered?.Invoke(candidate);
    public void RaiseConnection(PeerConnectionState state) => ConnectionStateChanged?.Invoke(state);
    public void RaiseRemoteVideo(IVideoFeed? feed) => RemoteVideoChanged?.Invoke(feed);
    public void RaiseLocalVideo(IVideoFeed? feed) => LocalVideoChanged?.Invoke(feed);
    public void RaiseChatState(DataChannelState state) => ChatStateChanged?.Invoke(state);
    public void RaiseChatMessage(string text) => ChatMessageReceived?.Invoke(text);
    public void RaisePresentation(Presentation kind, CaptureState state) => PresentationStateChanged?.Invoke(kind, state);
    public void RaiseEffectsFailed() => EffectsFailed?.Invoke();
}