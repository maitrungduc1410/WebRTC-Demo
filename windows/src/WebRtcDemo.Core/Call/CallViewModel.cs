using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebRtcDemo.Core.Group;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Effects;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Call;

/// <summary>
/// One call, 1:1 or group: the signaling flow of the web and iOS clients, local media and
/// everything the call screen renders. Lives on the UI thread.
/// </summary>
/// <remarks>
/// 1:1: the peer already in the room makes the offer when "peer joined" arrives; the newcomer
/// answers. Group: <see cref="GroupCallClient"/> talks to the SFU. Either way the socket is the
/// call: when it closes the call ends, as on the other clients.
/// </remarks>
public sealed partial class CallViewModel : ObservableObject, IDisposable
{
    /// <summary>Lets the remote swap to its placeholder before our track turns into black frames.</summary>
    public static readonly TimeSpan MediaStateDelay = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan AudioLevelInterval = TimeSpan.FromMilliseconds(250);
    /// <summary>The web's SPEAKER_POLL_MS.</summary>
    public static readonly TimeSpan GroupAudioLevelInterval = TimeSpan.FromMilliseconds(300);
    /// <summary>How often the local microphone indicator reads its level.</summary>
    public static readonly TimeSpan MicLevelInterval = TimeSpan.FromMilliseconds(80);
    /// <summary>How often a call looks for unplugged microphones and speakers.</summary>
    public static readonly TimeSpan DeviceCheckInterval = TimeSpan.FromSeconds(2);
    public const int MaxMessages = 100;
    public const int EncryptionKeyLength = 32;

    private readonly SignalingClient _signaling;
    private readonly GroupCallClient _group;
    private readonly ICallMedia _media;
    private readonly IDispatcher _dispatcher;
    private readonly SettingsStore? _settings;
    private readonly TimeProvider _time;
    private readonly RemoteSnapshotter _snapshotter;

    private Task _mediaReady = Task.CompletedTask;
    private readonly List<IceCandidateDto> _pendingCandidates = [];
    private bool _remoteDescriptionSet;
    /// <summary>Bumped whenever the peer connection is replaced, so late async steps of an old one stop.</summary>
    private int _pcGeneration;
    private ITimer? _mediaStateTimer;
    private ITimer? _audioLevelTimer;
    private ITimer? _deviceTimer;
    private bool _pollingAudioLevel;
    private ITimer? _micLevelTimer;
    private bool _pollingMicLevel;
    private double _micLevel;
    private int _nextMessageId = 1;
    private bool _disposed;
    /// <summary>What the camera shows now; <see cref="Effects"/> is what was picked.</summary>
    private EffectsSelection _appliedEffects = EffectsSelection.Off;
    /// <summary>The effect broke while drawing and was reloaded once (on the CPU); the next break gives up.</summary>
    private bool _effectsRetried;
    /// <summary>It broke again, so the camera was turned off; turning it on tries the effect again.</summary>
    private bool _effectsBroken;
    private int _effectsGeneration;
    /// <summary>Video that arrived before the participant's info (either can come first).</summary>
    private readonly Dictionary<string, IVideoFeed> _orphanVideo = new(StringComparer.Ordinal);
    private readonly ActiveSpeaker _activeSpeaker = new();

    /// <param name="group">The group call client; by default one over real WebSockets.</param>
    public CallViewModel(SignalingClient signaling, ICallMedia media, IDispatcher dispatcher, SettingsStore? settings = null, TimeProvider? time = null, GroupCallClient? group = null)
    {
        _signaling = signaling;
        _media = media;
        _dispatcher = dispatcher;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _group = group ?? new GroupCallClient(url => new WebSocketMessageSocket(url), media, dispatcher);
        _snapshotter = new RemoteSnapshotter(dispatcher, image => RemoteSnapshot = ImageOps.Blur(image, 2), _time);

        _signaling.PeerJoined += OnPeerJoined;
        _signaling.OfferReceived += OnOffer;
        _signaling.AnswerReceived += OnAnswer;
        _signaling.IceCandidateReceived += OnRemoteCandidate;
        _signaling.EncryptionKeyReceived += OnEncryptionKey;
        _signaling.MediaStateReceived += OnRemoteMediaState;
        _signaling.ErrorReceived += OnServerError;
        _signaling.Closed += OnSignalingClosed;

        _group.Joined += OnGroupJoined;
        _group.ParticipantJoined += OnParticipantJoined;
        _group.ParticipantLeft += OnParticipantLeft;
        _group.ParticipantStateChanged += OnParticipantState;
        _group.ChatReceived += OnGroupChat;
        _group.Notice += OnGroupNotice;
        _group.Ended += OnGroupEnded;
        _media.ParticipantVideoChanged += OnParticipantVideo;

        _media.IceCandidateGathered += OnLocalCandidate;
        _media.ConnectionStateChanged += OnConnectionState;
        _media.RemoteVideoChanged += OnRemoteVideo;
        _media.LocalVideoChanged += OnLocalVideo;
        _media.ChatStateChanged += OnChatState;
        _media.ChatMessageReceived += OnChatMessage;
        _media.PresentationStateChanged += OnPresentationState;
        _media.DevicesChanged += OnDevicesChanged;
        _media.AudioDeviceReplaced += OnAudioDeviceReplaced;
        _media.EffectsFailed += OnEffectsFailed;
    }

    public event Action<Toast>? ToastRequested;
    /// <summary>The call ended (left, or the room was full); go back to the lobby.</summary>
    public event Action? Ended;

    // ---- State --------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string RoomId { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool E2ee { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Phase))]
    public partial bool InRoom { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Phase))]
    public partial bool Negotiating { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Phase), nameof(ShowRemotePlaceholder), nameof(HasRemoteVideo))]
    public partial bool PeersConnected { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset? ConnectedSince { get; private set; }

    public CallPhase Phase => !InRoom ? CallPhase.Idle
        : IsGroup ? (!GroupJoined ? CallPhase.Connecting : Participants.Count == 0 ? CallPhase.Waiting : CallPhase.Connected)
        : PeersConnected ? CallPhase.Connected
        : Negotiating ? CallPhase.Connecting
        : CallPhase.Waiting;

    /// <summary>A group call through the SFU (else 1:1 through the signaling server).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Phase), nameof(RemoteAudioText), nameof(RemoteVideoText), nameof(CanToggleFit))]
    public partial bool IsGroup { get; private set; }

    /// <summary>The server of the current call, for the top bar.</summary>
    [ObservableProperty]
    public partial string ServerUrl { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Phase))]
    public partial bool GroupJoined { get; private set; }

    /// <summary>Ours in a group call, once joined.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelfLabel))]
    public partial string? SelfId { get; private set; }

    /// <summary>The label others see on our tile; also on our own tile and people row.</summary>
    public string SelfLabel => SelfId != null ? GroupLabels.Label(SelfId, GroupCallClient.DisplayName) : GroupCallClient.DisplayName;

    /// <summary>The others in a group call, in join order.</summary>
    public ObservableCollection<GroupParticipant> Participants { get; } = [];

    /// <summary>Everyone, us included: the people list's count.</summary>
    public int PeopleCount => Participants.Count + 1;

    [ObservableProperty]
    public partial string? ActiveSpeakerId { get; private set; }

    /// <summary>
    /// The one participant picture-in-picture shows in a group call (as the web's PiP): the active
    /// speaker, else the first with video, else the first.
    /// </summary>
    [ObservableProperty]
    public partial GroupParticipant? FeaturedParticipant { get; private set; }

    public string RemoteAudioText => IsGroup
        ? RemoteAudioMuted ? "Unmute everyone" : "Mute everyone"
        : RemoteAudioMuted ? "Unmute their audio" : "Mute their audio";

    public string RemoteVideoText => IsGroup
        ? RemoteVideoHidden ? "Show everyone's video" : "Hide everyone's video"
        : RemoteVideoHidden ? "Show their video" : "Hide their video";

    /// <summary>F and the menu's fit entry: 1:1 only; group tiles switch with a double-click.</summary>
    public bool CanToggleFit => !IsGroup;

    [ObservableProperty]
    public partial bool MicOn { get; private set; } = true;

    /// <summary>The local microphone indicator's bars in [0, 1]; 0 while muted or without a microphone.</summary>
    [ObservableProperty]
    public partial double MicLevel { get; private set; }

    partial void OnMicLevelChanged(double value)
    {
        if (value == 0) _micLevel = 0;
    }

    [ObservableProperty]
    public partial bool CameraOn { get; private set; } = true;

    [ObservableProperty]
    public partial bool HasMicrophone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPickEffects))]
    public partial bool HasCamera { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPresenting), nameof(CanOpenEffects), nameof(CanPickEffects))]
    public partial Presentation Sharing { get; private set; }

    [ObservableProperty]
    public partial string? SharingTitle { get; private set; }

    public bool IsPresenting => Sharing != Presentation.None;

    // Effects only apply to the camera, so the picker closes while presenting.
    partial void OnSharingChanged(Presentation value)
    {
        if (value != Presentation.None) EffectsOpen = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteAudioText))]
    public partial bool RemoteAudioMuted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemotePlaceholder), nameof(PlaceholderTitle), nameof(RemoteVideoText))]
    public partial bool RemoteVideoHidden { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemotePlaceholder), nameof(PlaceholderSubtitle))]
    public partial MediaState RemoteMedia { get; private set; } = MediaState.Default;

    /// <summary>The remote is connected but its video should not be shown: hidden by us or off on their side.</summary>
    public bool ShowRemotePlaceholder => PeersConnected && (RemoteVideoHidden || !RemoteMedia.Video);

    public string PlaceholderTitle => RemoteVideoHidden ? "You hid their video" : "Camera is off";

    public string? PlaceholderSubtitle => RemoteMedia.Audio ? null : "Microphone muted";

    [ObservableProperty]
    public partial double RemoteAudioLevel { get; private set; }

    /// <summary>The last non-black remote frame, tiny and blurred, behind the placeholder avatar.</summary>
    [ObservableProperty]
    public partial BgraImage? RemoteSnapshot { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteVideo))]
    public partial IVideoFeed? RemoteVideo { get; private set; }

    public bool HasRemoteVideo => PeersConnected && RemoteVideo != null;

    [ObservableProperty]
    public partial IVideoFeed? LocalVideo { get; private set; }

    [ObservableProperty]
    public partial bool LocalMirrored { get; private set; } = true;

    [ObservableProperty]
    public partial VideoFit RemoteFit { get; private set; } = VideoFit.Fill;

    private VideoFit? _fitOverride;
    private (int FrameWidth, int FrameHeight, double ViewWidth, double ViewHeight) _remoteLayout;

    [ObservableProperty]
    public partial bool ChatReady { get; private set; }

    [ObservableProperty]
    public partial bool ChatOpen { get; private set; }

    [ObservableProperty]
    public partial int Unread { get; private set; }

    /// <summary>The picked background and sticker (saved); <see cref="EffectsStatus"/> says whether they show yet.</summary>
    [ObservableProperty]
    public partial EffectsSelection Effects { get; private set; } = EffectsSelection.Off;

    [ObservableProperty]
    public partial EffectsStatus EffectsStatus { get; private set; }

    /// <summary>Why the last pick didn't stick, for the panel's info bar; cleared by the next pick.</summary>
    [ObservableProperty]
    public partial string? EffectsError { get; private set; }

    /// <summary>The effects panel, which takes the chat panel's place.</summary>
    [ObservableProperty]
    public partial bool EffectsOpen { get; private set; }

    public EffectsCatalog EffectsCatalog => _media.EffectsCatalog;
    /// <summary>The toolbar button, menu entry and B key (off while presenting, like on the Mac).</summary>
    public bool CanOpenEffects => _media.SupportsEffects && !IsPresenting;
    /// <summary>The panel's tiles: effects apply to a camera only.</summary>
    public bool CanPickEffects => _media.SupportsEffects && HasCamera && !IsPresenting;

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public IReadOnlyList<MediaDevice> Cameras => _media.Cameras;
    public IReadOnlyList<MediaDevice> Microphones => _media.Microphones;
    public IReadOnlyList<MediaDevice> Speakers => _media.Speakers;
    public string? CameraId => _media.CameraId;
    public string? MicrophoneId => _media.MicrophoneId;
    public string? SpeakerId => _media.SpeakerId;

    public bool CanShareFile => _media.SupportsFileSharing;
    public bool CanSwitchCamera => _media.Cameras.Count > 1 && !IsPresenting;

    public string AvatarSeed => Avatar.RemoteSeed(RoomId);

    /// <summary>The call's sockets closing after <see cref="Leave"/>: shutdown waits so the leave gets out.</summary>
    public Task SocketsClosed => Task.WhenAll(_signaling.Completion, _group.Completion);

    // ---- Room ---------------------------------------------------------------------------------

    /// <summary>Opens the camera and microphone and joins the 1:1 room <paramref name="roomId"/>.</summary>
    /// <param name="serverUrl">A normalized signaling address, "http://host:4000".</param>
    public void Join(string roomId, bool e2ee, string serverUrl)
    {
        if (!Begin(roomId, e2ee, group: false, serverUrl)) return;
        _signaling.Join(serverUrl, RoomId);
        _audioLevelTimer = _time.CreateTimer(_ => _dispatcher.Post(PollAudioLevel), null, AudioLevelInterval, AudioLevelInterval);
    }

    /// <summary>Opens the camera and microphone and joins the group call <paramref name="roomId"/> on an SFU.</summary>
    /// <param name="sfuUrl">A normalized SFU address, "ws://host:4001".</param>
    public void JoinGroup(string roomId, bool e2ee, string sfuUrl)
    {
        if (!Begin(roomId, e2ee, group: true, sfuUrl)) return;
        _group.Start(sfuUrl, RoomId, e2ee);
        _audioLevelTimer = _time.CreateTimer(_ => _dispatcher.Post(PollGroupAudioLevels), null, GroupAudioLevelInterval, GroupAudioLevelInterval);
    }

    private bool Begin(string roomId, bool e2ee, bool group, string serverUrl)
    {
        roomId = roomId.Trim();
        if (InRoom || roomId.Length == 0 || _disposed) return false;
        RoomId = roomId;
        E2ee = e2ee;
        IsGroup = group;
        ServerUrl = serverUrl;
        OnPropertyChanged(nameof(AvatarSeed));
        InRoom = true;
        var settings = _settings?.Load();
        Effects = EffectsCatalog.Sanitize(new EffectsSelection(settings?.EffectsBackground ?? EffectsSelection.None, settings?.EffectsSticker));
        _appliedEffects = EffectsSelection.Off;
        // A saved effect: no camera frame leaves before it is ready.
        var holdEffects = _media.SupportsEffects && EffectsCatalog.HasEffects(Effects);
        if (holdEffects) _media.HoldEffects();
        _mediaReady = StartMediaAsync(settings?.CameraId, settings?.MicrophoneId, settings?.SpeakerId, holdEffects);
        _deviceTimer = _time.CreateTimer(_ => _dispatcher.Post(CheckDevices), null, DeviceCheckInterval, DeviceCheckInterval);
        _micLevelTimer = _time.CreateTimer(_ => _dispatcher.Post(PollMicLevel), null, MicLevelInterval, MicLevelInterval);
        return true;
    }

    private async Task StartMediaAsync(string? cameraId, string? microphoneId, string? speakerId, bool applyEffects)
    {
        LocalMediaResult result;
        try
        {
            result = await _media.StartLocalMediaAsync(cameraId, microphoneId, speakerId);
        }
        catch (Exception)
        {
            result = new LocalMediaResult(false, false);
        }
        if (!InRoom) return;
        HasMicrophone = result.Microphone;
        HasCamera = result.Camera;
        if (!result.Microphone) MicOn = false;
        if (!result.Camera) CameraOn = false;
        _media.SetMicrophoneEnabled(MicOn);
        LocalMirrored = _media.LocalVideoIsCamera;
        OnDevicesChanged();
        if (applyEffects) _ = ApplyEffectsAsync();
        if (!result.Microphone && !result.Camera)
        {
            Show(IsGroup
                ? "No camera or microphone found. You can still see and hear the others."
                : "No camera or microphone found. You can still see and hear the other person.", ToastKind.Warning, Glyphs.Warning);
        }
        else if (!result.Camera)
        {
            Show("No camera found", ToastKind.Warning, Glyphs.VideoOff);
        }
        else if (!result.Microphone)
        {
            Show("No microphone found", ToastKind.Warning, Glyphs.MicOff);
        }
    }

    /// <summary>Ends the call because of <paramref name="reason"/>, which stays on screen in the lobby.</summary>
    private void End(string reason, string? glyph = null)
    {
        if (!InRoom) return;
        Leave();
        Show(reason, ToastKind.Error, glyph ?? Glyphs.Warning);
    }

    [RelayCommand]
    public void Leave()
    {
        if (!InRoom) return;
        _signaling.Leave();
        _group.Leave();
        InRoom = false;
        OnDisconnected();
        ResetGroup();
        _media.StopLocalMedia();
        _mediaStateTimer?.Dispose();
        _mediaStateTimer = null;
        _audioLevelTimer?.Dispose();
        _audioLevelTimer = null;
        _deviceTimer?.Dispose();
        _deviceTimer = null;
        _micLevelTimer?.Dispose();
        _micLevelTimer = null;
        MicLevel = 0;
        Messages.Clear();
        Unread = 0;
        Sharing = Presentation.None;
        SharingTitle = null;
        MicOn = true;
        CameraOn = true;
        RemoteAudioMuted = false;
        RemoteVideoHidden = false;
        _fitOverride = null;
        LocalVideo = null;
        _effectsGeneration++;
        _appliedEffects = EffectsSelection.Off;
        _effectsRetried = false;
        _effectsBroken = false;
        EffectsStatus = EffectsStatus.Off;
        EffectsOpen = false;
        EffectsError = null;
        Ended?.Invoke();
    }

    // ---- Signaling ----------------------------------------------------------------------------

    /// <summary>No resume: the server has already freed our seat, so the call is over.</summary>
    private void OnSignalingClosed(bool wasOpen)
    {
        if (!InRoom || IsGroup) return;
        End(wasOpen ? "Lost the connection to the signaling server." : "Can't reach the signaling server.");
    }

    private void OnServerError(ServerError error)
    {
        if (!InRoom || IsGroup) return;
        if (error.Message == "Room is full") End("That room already has two people in it.", Glyphs.People);
        else if (error.Fatal) End(error.Message);
        else Show(error.Message, ToastKind.Info, Glyphs.Info);
    }

    private async void OnPeerJoined()
    {
        await _mediaReady;
        if (!InRoom || IsGroup) return;
        // "new user joined" while a call exists means the remote restarted it.
        OnDisconnected();
        if (E2ee)
        {
            // The key must go out before the offer; the server relays both in order.
            var key = RandomNumberGenerator.GetBytes(EncryptionKeyLength);
            _media.SetEncryptionKey(key);
            _signaling.SendEncryptionKey(key);
        }
        await StartCallAsync();
    }

    private async Task StartCallAsync()
    {
        var generation = OpenPeerConnection();
        // Negotiated with the first offer: opening the chat later must not renegotiate the call.
        _media.CreateChatChannel();
        try
        {
            var offer = await _media.CreateOfferAsync();
            if (generation != _pcGeneration) return;
            _signaling.SendOffer(offer.Sdp);
        }
        catch (Exception) when (generation != _pcGeneration)
        {
        }
        catch (Exception)
        {
            Show("Couldn't start the call", ToastKind.Error, Glyphs.Warning);
        }
    }

    private int OpenPeerConnection()
    {
        _media.OpenPeerConnection(E2ee);
        _pcGeneration++;
        _remoteDescriptionSet = false;
        Negotiating = true;
        return _pcGeneration;
    }

    private void OnEncryptionKey(byte[] key)
    {
        if (!InRoom || IsGroup || !E2ee) return;
        _media.SetEncryptionKey(key);
        _signaling.SendEncryptionKeyReceived();
    }

    private async void OnOffer(string sdp)
    {
        await _mediaReady;
        if (!InRoom || IsGroup) return;
        var generation = _media.HasPeerConnection ? _pcGeneration : OpenPeerConnection();
        try
        {
            await _media.SetRemoteDescriptionAsync(new SessionDescription("offer", sdp));
            if (generation != _pcGeneration) return;
            _remoteDescriptionSet = true;
            AddPendingCandidates();
            _media.ApplyCodecPreferences();
            var answer = await _media.CreateAnswerAsync();
            if (generation != _pcGeneration) return;
            _signaling.SendAnswer(answer.Sdp);
        }
        catch (Exception) when (generation != _pcGeneration)
        {
        }
        catch (Exception)
        {
            Show("Couldn't answer the call", ToastKind.Error, Glyphs.Warning);
        }
    }

    private async void OnAnswer(string sdp)
    {
        if (!InRoom || IsGroup || !_media.HasPeerConnection) return;
        var generation = _pcGeneration;
        try
        {
            await _media.SetRemoteDescriptionAsync(new SessionDescription("answer", sdp));
            if (generation != _pcGeneration) return;
            _remoteDescriptionSet = true;
            AddPendingCandidates();
        }
        catch (Exception)
        {
            // A stale answer, e.g. for a connection replaced since.
        }
    }

    private void OnRemoteCandidate(IceCandidateDto candidate)
    {
        if (!InRoom || IsGroup) return;
        // Candidates can beat the offer here, for example while our camera is still starting.
        if (!_media.HasPeerConnection || !_remoteDescriptionSet)
        {
            _pendingCandidates.Add(candidate);
            return;
        }
        _media.AddIceCandidate(ToNative(candidate));
    }

    private void AddPendingCandidates()
    {
        var candidates = _pendingCandidates.ToList();
        _pendingCandidates.Clear();
        foreach (var candidate in candidates) _media.AddIceCandidate(ToNative(candidate));
    }

    private static IceCandidate ToNative(IceCandidateDto candidate) =>
        new(candidate.SdpMid, candidate.SdpMLineIndex ?? 0, candidate.Candidate);

    private void OnLocalCandidate(IceCandidate candidate)
    {
        if (InRoom && !IsGroup) _signaling.SendIceCandidate(new IceCandidateDto(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex));
    }

    private void OnRemoteMediaState(MediaState state)
    {
        if (!InRoom || IsGroup) return;
        var screenChanged = state.Screen != RemoteMedia.Screen;
        RemoteMedia = state;
        if (screenChanged) UpdateRemoteFit();
        UpdateSnapshotter();
    }

    private MediaState LocalMediaState
    {
        get
        {
            var presenting = IsPresenting;
            return new MediaState(MicOn, presenting || CameraOn, presenting);
        }
    }

    private void SendMediaState()
    {
        if (!InRoom) return;
        if (IsGroup) _group.SendMediaState(LocalMediaState);
        else _signaling.SendMediaState(LocalMediaState);
    }

    // ---- Group call ---------------------------------------------------------------------------

    private async void OnGroupJoined(GroupJoin join)
    {
        if (!InRoom || !IsGroup) return;
        SelfId = join.ParticipantId;
        foreach (var info in join.Participants) AddParticipant(info);
        GroupJoined = true;
        ChatReady = true;
        if (join.Key != null) _media.SetEncryptionKey(join.Key);
        await _mediaReady;
        if (!InRoom || !_group.IsJoined) return;
        await _group.PublishAsync(LocalMediaState);
    }

    private void OnParticipantJoined(ParticipantInfo info)
    {
        if (!InRoom || !IsGroup || FindParticipant(info.Id) != null) return;
        var participant = AddParticipant(info);
        Show($"{participant.Label} joined", ToastKind.Info, Glyphs.PersonJoined);
    }

    private void OnParticipantLeft(string id)
    {
        if (FindParticipant(id) is not { } participant) return;
        Participants.Remove(participant);
        _orphanVideo.Remove(id);
        if (ActiveSpeakerId == id)
        {
            _activeSpeaker.Reset();
            ActiveSpeakerId = null;
        }
        ParticipantsChanged();
        Show($"{participant.Label} left", ToastKind.Info, Glyphs.PersonLeft);
    }

    private void OnParticipantState(string id, MediaState state)
    {
        if (FindParticipant(id) is not { } participant) return;
        participant.UpdateState(state);
        if (!state.Audio) participant.AudioLevel = 0;
        UpdateFeatured();
    }

    private void OnParticipantVideo(string id, IVideoFeed? feed)
    {
        if (FindParticipant(id) is { } participant)
        {
            participant.Video = feed;
            UpdateFeatured();
        }
        else if (feed != null)
        {
            _orphanVideo[id] = feed;
        }
        else
        {
            _orphanVideo.Remove(id);
        }
    }

    private GroupParticipant AddParticipant(ParticipantInfo info)
    {
        var participant = new GroupParticipant(info.Id, info.Name, info.State) { VideoHidden = RemoteVideoHidden };
        if (_orphanVideo.Remove(info.Id, out var feed)) participant.Video = feed;
        Participants.Add(participant);
        ParticipantsChanged();
        return participant;
    }

    private GroupParticipant? FindParticipant(string id) => Participants.FirstOrDefault(p => p.Id == id);

    private void ParticipantsChanged()
    {
        // The call timer runs from when someone else is first there, as in a 1:1 call.
        if (Participants.Count > 0) ConnectedSince ??= _time.GetUtcNow();
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PeopleCount));
        UpdateFeatured();
    }

    private void UpdateFeatured()
    {
        FeaturedParticipant = Participants.FirstOrDefault(p => p.Id == ActiveSpeakerId)
            ?? Participants.FirstOrDefault(p => p.ShowVideo)
            ?? Participants.FirstOrDefault();
    }

    private void OnGroupChat(ChatReceivedMessage chat)
    {
        if (!InRoom || !IsGroup) return;
        AppendMessage(chat.Text, isLocal: false, GroupLabels.Label(chat.ParticipantId, chat.Name));
    }

    private void OnGroupNotice(string text)
    {
        if (InRoom && IsGroup) Show(text, ToastKind.Info, Glyphs.Info);
    }

    private void OnGroupEnded(string reason)
    {
        if (InRoom && IsGroup) End(reason);
    }

    /// <summary>The web's pollAudioLevels: per-participant levels, then the held loudest one.</summary>
    private async void PollGroupAudioLevels()
    {
        if (_pollingAudioLevel || !InRoom || !IsGroup || !_media.HasGroupConnections) return;
        _pollingAudioLevel = true;
        try
        {
            IReadOnlyDictionary<string, double> levels;
            try
            {
                levels = RemoteAudioMuted ? new Dictionary<string, double>() : await _media.GetParticipantAudioLevelsAsync();
            }
            catch (Exception)
            {
                levels = new Dictionary<string, double>();
            }
            if (!InRoom || !IsGroup) return;
            var speaking = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var participant in Participants)
            {
                var level = participant.State.Audio ? levels.GetValueOrDefault(participant.Id) : 0;
                participant.AudioLevel = level;
                if (participant.State.Audio && levels.ContainsKey(participant.Id)) speaking[participant.Id] = level;
            }
            var active = _activeSpeaker.Update(speaking, _time.GetUtcNow());
            if (active != ActiveSpeakerId)
            {
                ActiveSpeakerId = active;
                foreach (var participant in Participants) participant.Speaking = participant.Id == active;
                UpdateFeatured();
            }
        }
        finally
        {
            _pollingAudioLevel = false;
        }
    }

    private void ResetGroup()
    {
        GroupJoined = false;
        SelfId = null;
        Participants.Clear();
        _orphanVideo.Clear();
        _activeSpeaker.Reset();
        ActiveSpeakerId = null;
        FeaturedParticipant = null;
        OnPropertyChanged(nameof(PeopleCount));
    }

    // ---- Peer connection ----------------------------------------------------------------------

    private void OnConnectionState(PeerConnectionState state)
    {
        if (state == PeerConnectionState.Connected)
        {
            var first = !PeersConnected;
            PeersConnected = true;
            Negotiating = false;
            ConnectedSince ??= _time.GetUtcNow();
            SendMediaState();
            UpdateSnapshotter();
            if (first) Show("Connected", ToastKind.Success, Glyphs.Connected);
        }
        else if (state is PeerConnectionState.Disconnected or PeerConnectionState.Failed)
        {
            var wasConnected = PeersConnected;
            OnDisconnected();
            if (wasConnected) Show("The other participant left", ToastKind.Info, Glyphs.PersonLeft);
        }
    }

    private void OnDisconnected()
    {
        _pendingCandidates.Clear();
        _remoteDescriptionSet = false;
        _pcGeneration++;
        _media.ClosePeerConnection();
        Negotiating = false;
        PeersConnected = false;
        ConnectedSince = null;
        ChatReady = false;
        RemoteVideo = null;
        _snapshotter.Attach(null);
        RemoteMedia = MediaState.Default;
        RemoteSnapshot = null;
        RemoteAudioLevel = 0;
    }

    private void OnRemoteVideo(IVideoFeed? feed)
    {
        RemoteVideo = feed;
        _snapshotter.Attach(feed);
        UpdateSnapshotter();
        if (feed == null) RemoteSnapshot = null;
    }

    private void OnLocalVideo(IVideoFeed? feed)
    {
        LocalVideo = feed;
        LocalMirrored = _media.LocalVideoIsCamera;
    }

    private void UpdateSnapshotter() => _snapshotter.Active = !ShowRemotePlaceholder;

    private async void PollAudioLevel()
    {
        if (_pollingAudioLevel) return;
        // Remote audioLevel (0..1) drives the avatar ring; only polled while the placeholder shows.
        if (!InRoom || !ShowRemotePlaceholder || RemoteAudioMuted)
        {
            if (RemoteAudioLevel != 0) RemoteAudioLevel = 0;
            return;
        }
        _pollingAudioLevel = true;
        try
        {
            var generation = _pcGeneration;
            var level = await _media.GetRemoteAudioLevelAsync();
            if (generation == _pcGeneration && ShowRemotePlaceholder) RemoteAudioLevel = level ?? 0;
        }
        catch (Exception)
        {
            RemoteAudioLevel = 0;
        }
        finally
        {
            _pollingAudioLevel = false;
        }
    }

    private async void PollMicLevel()
    {
        if (_pollingMicLevel || !InRoom) return;
        if (!MicOn || !HasMicrophone)
        {
            MicLevel = 0;
            return;
        }
        // As on iOS, only a 1:1 room meters the idle microphone; a group call waits for its publish stats.
        if (IsGroup && !_media.HasGroupConnections)
        {
            _micLevel = Media.MicLevel.Next(_micLevel, null);
            MicLevel = _micLevel;
            return;
        }
        _pollingMicLevel = true;
        double? peak;
        try
        {
            peak = await _media.GetMicPeakAsync();
        }
        catch (Exception)
        {
            peak = null;
        }
        finally
        {
            _pollingMicLevel = false;
        }
        // Muted or left while the read was in flight: the stale peak doesn't count.
        if (!InRoom || !MicOn || !HasMicrophone)
        {
            MicLevel = 0;
            return;
        }
        // The level decays unrounded; the bars only redraw for a visible change.
        _micLevel = Media.MicLevel.Next(_micLevel, peak);
        if (_micLevel == 0 || Math.Abs(_micLevel - MicLevel) >= Media.MicLevel.Step) MicLevel = _micLevel;
    }

    // ---- Chat ---------------------------------------------------------------------------------

    private void OnChatState(DataChannelState state)
    {
        if (state == DataChannelState.Open)
        {
            ChatReady = true;
            Messages.Clear();
            Unread = 0;
        }
        else if (state == DataChannelState.Closed)
        {
            ChatReady = false;
            // The other side hung up; ICE takes several seconds to notice on its own.
            if (PeersConnected)
            {
                OnDisconnected();
                Show("The other participant left", ToastKind.Info, Glyphs.PersonLeft);
            }
        }
    }

    private void OnChatMessage(string text)
    {
        if (!IsGroup) AppendMessage(text, isLocal: false);
    }

    /// <summary>1:1 over the data channel; group over the SFU's socket (the server relays it to the others).</summary>
    public bool SendMessage(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !ChatReady) return false;
        if (!(IsGroup ? _group.SendChat(trimmed) : _media.SendChat(trimmed))) return false;
        AppendMessage(trimmed, isLocal: true);
        return true;
    }

    private void AppendMessage(string text, bool isLocal, string? sender = null)
    {
        Messages.Add(new ChatMessage(_nextMessageId++, text, isLocal, _time.GetLocalNow(), sender));
        while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
        if (!isLocal && !ChatOpen) Unread++;
    }

    public void SetChatOpen(bool open)
    {
        ChatOpen = open;
        if (open)
        {
            Unread = 0;
            EffectsOpen = false;
        }
    }

    [RelayCommand]
    public void ToggleChat() => SetChatOpen(!ChatOpen);

    // ---- Local media --------------------------------------------------------------------------

    [RelayCommand]
    public void ToggleMic()
    {
        if (!HasMicrophone)
        {
            Show("No microphone available", ToastKind.Warning, Glyphs.MicOff);
            return;
        }
        MicOn = !MicOn;
        if (!MicOn) MicLevel = 0;
        _media.SetMicrophoneEnabled(MicOn);
        SendMediaState();
    }

    /// <summary>
    /// The peer is told first and the track disabled shortly after (the reverse when turning on),
    /// so it shows its placeholder instead of a frozen or black frame.
    /// </summary>
    [RelayCommand]
    public void ToggleCamera()
    {
        // The camera is closed while presenting.
        if (IsPresenting) return;
        if (!HasCamera)
        {
            Show("No camera available", ToastKind.Warning, Glyphs.VideoOff);
            return;
        }
        SetCameraOn(!CameraOn);
    }

    private void SetCameraOn(bool enable)
    {
        CameraOn = enable;
        _mediaStateTimer?.Dispose();
        if (enable)
        {
            // Still held from the failure, so the room is not shown while it loads.
            if (_effectsBroken)
            {
                _effectsBroken = false;
                _ = ApplyEffectsAsync();
            }
            _mediaStateTimer = After(MediaStateDelay, SendMediaState);
            _ = TurnCameraOnAsync();
        }
        else
        {
            SendMediaState();
            _mediaStateTimer = After(MediaStateDelay, () =>
            {
                if (!CameraOn && !IsPresenting) _ = _media.SetCameraEnabledAsync(false);
            });
        }
    }

    private async Task TurnCameraOnAsync()
    {
        if (await _media.SetCameraEnabledAsync(true) || !CameraOn || !InRoom) return;
        SetCameraOn(false);
        Show("Couldn't turn the camera on", ToastKind.Error, Glyphs.VideoOff);
    }

    [RelayCommand]
    public async Task SwitchCameraAsync()
    {
        var cameras = _media.Cameras;
        if (cameras.Count < 2 || IsPresenting) return;
        var index = cameras.ToList().FindIndex(c => c.Id == _media.CameraId);
        var next = cameras[(index + 1) % cameras.Count];
        await SelectCameraAsync(next.Id);
    }

    public async Task SelectCameraAsync(string id)
    {
        if (!InRoom) return;
        if (await _media.SelectCameraAsync(id))
        {
            _settings?.Update(s => s with { CameraId = id });
            HasCamera = true;
            Show(_media.Cameras.FirstOrDefault(c => c.Id == id)?.Name ?? "Camera switched", ToastKind.Info, Glyphs.Camera);
        }
        else
        {
            Show("Couldn't switch to that camera", ToastKind.Error, Glyphs.VideoOff);
        }
        OnDevicesChanged();
    }

    public void SelectMicrophone(string id)
    {
        if (_media.SelectMicrophone(id)) _settings?.Update(s => s with { MicrophoneId = id });
        else Show("Couldn't switch to that microphone", ToastKind.Error, Glyphs.MicOff);
        OnDevicesChanged();
    }

    public void SelectSpeaker(string id)
    {
        if (_media.SelectSpeaker(id)) _settings?.Update(s => s with { SpeakerId = id });
        else Show("Couldn't switch to that speaker", ToastKind.Error, Glyphs.Speaker);
        OnDevicesChanged();
    }

    public void RefreshDevices() => _media.RefreshDevices();

    private void CheckDevices()
    {
        if (InRoom) _ = _media.CheckDevicesAsync();
    }

    /// <summary>
    /// The audio device module moved to the default device, so the audio sender (and its
    /// cryptor) keep their track and nothing is renegotiated. The saved choice stays for next time.
    /// </summary>
    private void OnAudioDeviceReplaced(AudioDeviceKind kind, MediaDevice? device)
    {
        if (!InRoom) return;
        if (kind == AudioDeviceKind.Speaker)
        {
            Show(device != null ? $"Switched to {device.Name}" : "The speaker was disconnected", ToastKind.Info, Glyphs.Speaker);
            return;
        }
        if (device != null)
        {
            HasMicrophone = true;
            Show($"Switched to {device.Name}", ToastKind.Info, Glyphs.Mic);
            return;
        }
        HasMicrophone = false;
        if (MicOn)
        {
            MicOn = false;
            _media.SetMicrophoneEnabled(false);
            SendMediaState();
        }
        Show("The microphone was disconnected", ToastKind.Warning, Glyphs.MicOff);
    }

    private void OnDevicesChanged()
    {
        OnPropertyChanged(nameof(Cameras));
        OnPropertyChanged(nameof(Microphones));
        OnPropertyChanged(nameof(Speakers));
        OnPropertyChanged(nameof(CameraId));
        OnPropertyChanged(nameof(MicrophoneId));
        OnPropertyChanged(nameof(SpeakerId));
        OnPropertyChanged(nameof(CanSwitchCamera));
    }

    // ---- Effects ------------------------------------------------------------------------------

    public void SetEffectsOpen(bool open)
    {
        if (open && !CanOpenEffects) return;
        EffectsOpen = open;
        if (open) ChatOpen = false;
    }

    [RelayCommand]
    public void ToggleEffects()
    {
        if (EffectsOpen) SetEffectsOpen(false);
        else OpenEffects();
    }

    /// <summary>The `B` shortcut and the menu entry, as on the web: they only open the panel.</summary>
    public void OpenEffects()
    {
        if (!_media.SupportsEffects)
        {
            Show("Backgrounds and effects aren't available", ToastKind.Info, Glyphs.Background);
            return;
        }
        SetEffectsOpen(true);
    }

    /// <summary>Remembers the choice and applies it to the camera.</summary>
    public void SetEffects(EffectsSelection selection)
    {
        if (!CanPickEffects) return;
        selection = EffectsCatalog.Sanitize(selection);
        if (selection == Effects) return;
        EffectsError = null;
        _effectsRetried = false;
        _effectsBroken = false;
        SaveEffects(selection);
        _ = ApplyEffectsAsync();
    }

    public void SetBackground(string id) => SetEffects(Effects with { Background = id });

    public void SetSticker(string? id) => SetEffects(Effects with { Sticker = id });

    private void SaveEffects(EffectsSelection selection)
    {
        Effects = selection;
        var sticker = selection.Sticker;
        var background = selection.Background == EffectsSelection.None ? null : selection.Background;
        _settings?.Update(s => s with { EffectsBackground = background, EffectsSticker = sticker });
    }

    /// <summary>
    /// If the pick can't be loaded, the previous one comes back (as on iOS and Android). When
    /// <paramref name="recovering"/> from a failure while drawing, it gives up instead.
    /// </summary>
    private async Task ApplyEffectsAsync(bool recovering = false)
    {
        var generation = ++_effectsGeneration;
        if (!InRoom || !_media.SupportsEffects) return;
        var selection = Effects;
        if (!EffectsCatalog.HasEffects(selection))
        {
            await _media.SetEffectsAsync(selection);
            if (generation != _effectsGeneration) return;
            _appliedEffects = selection;
            EffectsStatus = EffectsStatus.Off;
            return;
        }
        EffectsStatus = EffectsStatus.Loading;
        bool applied;
        try
        {
            applied = await _media.SetEffectsAsync(selection);
        }
        catch (Exception)
        {
            applied = false;
        }
        if (generation != _effectsGeneration || !InRoom) return;
        if (applied)
        {
            _appliedEffects = selection;
            EffectsStatus = EffectsStatus.On;
            return;
        }
        if (recovering)
        {
            GiveUpOnEffects();
            return;
        }
        ReportEffectsFailure();
        SaveEffects(_appliedEffects != selection ? _appliedEffects : EffectsSelection.Off);
        await ApplyEffectsAsync();
    }

    public void DismissEffectsError() => EffectsError = null;

    private void ReportEffectsFailure()
    {
        const string text = "Couldn't load that effect";
        EffectsError = text;
        // The panel shows it in place; a toast covers a closed panel.
        if (!EffectsOpen) Show(text, ToastKind.Error, Glyphs.Warning);
    }

    /// <summary>
    /// A model broke while drawing. The media layer holds frames (the peer never sees the room)
    /// and reloads a broken model on the CPU, so the same pick is loaded once more.
    /// </summary>
    private void OnEffectsFailed()
    {
        // Off: nothing to recover. Loading: a newer pick replaces the broken one.
        if (!InRoom || EffectsStatus != EffectsStatus.On) return;
        if (!_effectsRetried)
        {
            _effectsRetried = true;
            _ = ApplyEffectsAsync(recovering: true);
            return;
        }
        GiveUpOnEffects();
    }

    /// <summary>
    /// The effect can't be drawn even on the CPU. Rather than send the room, the camera goes off
    /// the normal way (the peer is told first); the saved choice stays for next time.
    /// </summary>
    private void GiveUpOnEffects()
    {
        ReportEffectsFailure();
        EffectsStatus = EffectsStatus.Off;
        _effectsBroken = true;
        if (CameraOn) SetCameraOn(false);
    }

    // ---- Presenting ---------------------------------------------------------------------------

    public IShareSourceList CreateShareSourceList(DesktopSourceType type) => _media.CreateShareSourceList(type);

    /// <summary>Picking another screen or window while presenting switches to it.</summary>
    public async Task ShareScreenAsync(ShareSource source)
    {
        if (!InRoom) return;
        if (!await _media.StartScreenShareAsync(source))
        {
            Show($"Couldn't share {source.Name}", ToastKind.Error, Glyphs.Warning);
            return;
        }
        if (!InRoom) return;
        _mediaStateTimer?.Dispose();
        Sharing = Presentation.Screen;
        SharingTitle = source.Name;
        SendMediaState();
        Show($"Sharing {source.Name}", ToastKind.Info, source.Type == DesktopSourceType.Window ? Glyphs.Window : Glyphs.Screen);
        OnPropertyChanged(nameof(CanSwitchCamera));
    }

    public async Task ShareFileAsync(string path)
    {
        if (!InRoom) return;
        if (!await _media.StartFileShareAsync(path))
        {
            Show("Couldn't play that video", ToastKind.Error, Glyphs.Warning);
            return;
        }
        if (!InRoom) return;
        _mediaStateTimer?.Dispose();
        Sharing = Presentation.File;
        SharingTitle = Path.GetFileNameWithoutExtension(path);
        SendMediaState();
        Show($"Sharing {Path.GetFileName(path)}", ToastKind.Info, Glyphs.Film);
        OnPropertyChanged(nameof(CanSwitchCamera));
    }

    [RelayCommand]
    public async Task StopSharingAsync()
    {
        var previous = Sharing;
        if (previous == Presentation.None) return;
        Sharing = Presentation.None;
        SharingTitle = null;
        var cameraBack = await _media.StopPresentingAsync();
        if (!cameraBack && CameraOn && InRoom)
        {
            SetCameraOn(false);
            Show("Couldn't turn the camera back on", ToastKind.Error, Glyphs.VideoOff);
        }
        else
        {
            SendMediaState();
            Show(previous == Presentation.File ? "Stopped video sharing" : "Screen sharing stopped", ToastKind.Info, Glyphs.StopShare);
        }
        OnPropertyChanged(nameof(CanSwitchCamera));
    }

    private void OnPresentationState(Presentation kind, CaptureState state)
    {
        if (Sharing != kind) return;
        if (state == CaptureState.Failed && kind == Presentation.File) Show("Couldn't play that video", ToastKind.Error, Glyphs.Warning);
        _ = StopSharingAsync();
    }

    // ---- Remote media -------------------------------------------------------------------------

    /// <summary>Only mutes playout here; nobody is notified. In a group call: everyone, also later joiners.</summary>
    [RelayCommand]
    public void ToggleRemoteAudio()
    {
        RemoteAudioMuted = !RemoteAudioMuted;
        _media.SetRemoteAudioEnabled(!RemoteAudioMuted);
        if (RemoteAudioMuted)
        {
            foreach (var participant in Participants) participant.AudioLevel = 0;
        }
    }

    /// <summary>Stops rendering remote video here only; nobody is notified. In a group call: everyone, also later joiners.</summary>
    [RelayCommand]
    public void ToggleRemoteVideo()
    {
        RemoteVideoHidden = !RemoteVideoHidden;
        _media.SetRemoteVideoEnabled(!RemoteVideoHidden);
        foreach (var participant in Participants) participant.VideoHidden = RemoteVideoHidden;
        UpdateFeatured();
        UpdateSnapshotter();
    }

    [RelayCommand]
    public void ToggleFit()
    {
        if (IsGroup) return;
        _fitOverride = RemoteFit == VideoFit.Fill ? VideoFit.Fit : VideoFit.Fill;
        UpdateRemoteFit();
    }

    /// <summary>The renderer reports the remote frame and view sizes; they decide the default fit.</summary>
    public void ReportRemoteLayout(int frameWidth, int frameHeight, double viewWidth, double viewHeight)
    {
        _remoteLayout = (frameWidth, frameHeight, viewWidth, viewHeight);
        UpdateRemoteFit();
    }

    private void UpdateRemoteFit()
    {
        var (fw, fh, vw, vh) = _remoteLayout;
        RemoteFit = _fitOverride ?? VideoLayout.DefaultFit(RemoteMedia.Screen, fw, fh, vw, vh);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private void Show(string text, ToastKind kind, string? glyph) => ToastRequested?.Invoke(new Toast(text, kind, glyph));

    private ITimer After(TimeSpan delay, Action action) =>
        _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            if (!_disposed) action();
        }), null, delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        if (_disposed) return;
        Leave();
        _disposed = true;
        _snapshotter.Dispose();
        _signaling.PeerJoined -= OnPeerJoined;
        _signaling.OfferReceived -= OnOffer;
        _signaling.AnswerReceived -= OnAnswer;
        _signaling.IceCandidateReceived -= OnRemoteCandidate;
        _signaling.EncryptionKeyReceived -= OnEncryptionKey;
        _signaling.MediaStateReceived -= OnRemoteMediaState;
        _signaling.ErrorReceived -= OnServerError;
        _signaling.Closed -= OnSignalingClosed;
        _group.Joined -= OnGroupJoined;
        _group.ParticipantJoined -= OnParticipantJoined;
        _group.ParticipantLeft -= OnParticipantLeft;
        _group.ParticipantStateChanged -= OnParticipantState;
        _group.ChatReceived -= OnGroupChat;
        _group.Notice -= OnGroupNotice;
        _group.Ended -= OnGroupEnded;
        _group.Dispose();
        _media.ParticipantVideoChanged -= OnParticipantVideo;
        _media.IceCandidateGathered -= OnLocalCandidate;
        _media.ConnectionStateChanged -= OnConnectionState;
        _media.RemoteVideoChanged -= OnRemoteVideo;
        _media.LocalVideoChanged -= OnLocalVideo;
        _media.ChatStateChanged -= OnChatState;
        _media.ChatMessageReceived -= OnChatMessage;
        _media.PresentationStateChanged -= OnPresentationState;
        _media.DevicesChanged -= OnDevicesChanged;
        _media.AudioDeviceReplaced -= OnAudioDeviceReplaced;
        _media.EffectsFailed -= OnEffectsFailed;
    }
}