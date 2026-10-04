using WebRtcDemo.Core.Group;
using WebRtcDemo.Effects;
using WebRtcDemo.Effects.Imaging;
using WebRtcDemo.Effects.Models;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>Where the effects assets are, and how model sessions are created (hardware acceleration).</summary>
/// <param name="EffectsDirectory">The shared effects folder (backgrounds.json, stickers.json).</param>
/// <param name="ModelsDirectory">The ONNX models.</param>
public sealed record EffectsSetup(string EffectsDirectory, string ModelsDirectory, SessionOptionsFactory Sessions);

/// <summary>
/// <see cref="ICallMedia"/> over the native shim. The video sender is created once per peer
/// connection and its track switched between camera, screen and file (no renegotiation, and the
/// sender's frame cryptor stays), like the Android client.
///
/// Effects: a sink on the camera track feeds <see cref="EffectsProcessor"/>, whose frames go into a
/// custom source with its own track. That track replaces the camera track (sender and local tile)
/// from the first processed frame on, or right away while <see cref="HoldEffects"/> holds frames.
/// </summary>
public sealed class NativeCallMedia : ICallMedia
{
    public const string DataChannelLabel = "MyApp Channel";
    private const string StreamId = "stream";
    private const int ScreenFps = 30;
    private static readonly IceServer[] IceServers = [new("stun:stun.l.google.com:19302")];

    private readonly PeerConnectionFactory _factory;
    private readonly IDispatcher _dispatcher;
    private bool _disposed;

    // Local media
    private int _localGeneration;
    private MediaTrack? _micTrack;
    private bool _micEnabled = true;
    private VideoSource? _cameraSource;
    private MediaTrack? _cameraTrack;
    private int _cameraIndex = -1;
    private bool _cameraEnabled = true;
    private Task<bool> _cameraReopen = Task.FromResult(true);
    private VideoSource? _placeholderSource;
    private MediaTrack? _placeholderTrack;
    private VideoSource? _presentSource;
    private MediaTrack? _presentTrack;
    private Presentation _presentation;
    private int _presentGeneration;
    private TrackVideoFeed? _localFeed;

    // Effects
    private readonly EffectsModels? _effectsModels;
    private readonly Lock _effectsOutputGate = new();
    private EffectsProcessor? _processor;
    // Disposing a processor joins its model threads (a first GPU run can take seconds).
    private Task _processorDisposal = Task.CompletedTask;
    private VideoSource? _effectsSource;
    private MediaTrack? _effectsTrack;
    private VideoSink? _effectsInput;
    private MediaTrack? _effectsInputTrack;
    private EffectsScene? _scene;
    private int _effectsGeneration;
    private bool _holdEffects;
    private bool _effectsLive;

    // Peer connection
    private PeerConnection? _pc;
    private int _pcGeneration;
    private bool _e2ee;
    private byte[]? _key;
    private KeyProvider? _keyProvider;
    private RtpSender? _audioSender;
    private RtpSender? _videoSender;
    private DataChannel? _channel;
    private MediaTrack? _remoteAudio;
    private MediaTrack? _remoteVideo;
    private TrackVideoFeed? _remoteFeed;
    private bool _remoteAudioEnabled = true;
    private bool _remoteVideoEnabled = true;

    // Group call
    private PeerConnection? _publishPc;
    private PeerConnection? _subscribePc;
    private int _groupGeneration;
    private IReadOnlyList<SdpMediaSection> _subscribeOffer = [];
    /// <summary>Subscribe receiver id → its current track.</summary>
    private readonly Dictionary<string, MediaTrack> _groupTracks = new(StringComparer.Ordinal);
    /// <summary>Receiver id → the stream id it was reported with.</summary>
    private readonly Dictionary<string, string> _trackStreams = new(StringComparer.Ordinal);
    /// <summary>Receiver id → participant id, for live receivers.</summary>
    private Dictionary<string, string> _receiverOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ReceiverId, TrackVideoFeed Feed)> _participantVideo = new(StringComparer.Ordinal);

    private IReadOnlyList<DeviceInfo> _cameraInfos = [];
    private readonly AudioDeviceSelection _microphones;
    private readonly AudioDeviceSelection _speakers;
    private bool _checkingDevices;
    private int _deviceGeneration;
    private readonly Lock _deviceReadGate = new();
    private bool _localStarted;
    private readonly IdleMicMeter _idleMeter;

    /// <summary>Opens the waiting-alone meter on a device id (null: the default); a seam for tests.</summary>
    internal Func<string?, IAudioPeakMeter?> StartIdleMeter { get; set; } =
        id => OperatingSystem.IsWindows() ? WasapiMicMeter.Start(id) : null;

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
    public event Action<GroupConnection, IceCandidate>? GroupIceCandidateGathered;
    public event Action<GroupConnection, PeerConnectionState>? GroupConnectionStateChanged;
    public event Action<string, IVideoFeed?>? ParticipantVideoChanged;

    public NativeCallMedia(PeerConnectionFactory factory, IDispatcher dispatcher, EffectsSetup? effects = null)
    {
        _factory = factory;
        _dispatcher = dispatcher;
        _microphones = new AudioDeviceSelection(factory.SetMicrophone);
        _speakers = new AudioDeviceSelection(factory.SetSpeaker);
        _idleMeter = new IdleMicMeter(id => StartIdleMeter(id));
        if (effects != null && Directory.Exists(effects.ModelsDirectory))
        {
            EffectsCatalog = EffectsCatalog.Load(effects.EffectsDirectory);
            _effectsModels = new EffectsModels(effects.ModelsDirectory, effects.Sessions);
        }
    }

    public bool HasMicrophone => _micTrack != null && _microphones.Devices.Count > 0;
    public bool HasCamera => _cameraTrack != null;
    public bool HasPeerConnection => _pc != null;
    public IVideoFeed? LocalVideo => _localFeed;
    public bool LocalVideoIsCamera => _presentation == Presentation.None && _cameraTrack != null;
    public bool SupportsFileSharing => OperatingSystem.IsWindows();
    public bool SupportsEffects => _effectsModels != null;
    public EffectsCatalog EffectsCatalog { get; } = EffectsCatalog.Empty;

    public IReadOnlyList<MediaDevice> Cameras { get; private set; } = [];
    public IReadOnlyList<MediaDevice> Microphones => _microphones.Devices;
    public IReadOnlyList<MediaDevice> Speakers => _speakers.Devices;
    public string? CameraId { get; private set; }
    public string? MicrophoneId => _microphones.Id;
    public string? SpeakerId => _speakers.Id;

    // ---- Devices ------------------------------------------------------------------------------

    public void RefreshDevices()
    {
        _deviceGeneration++;
        _cameraInfos = _factory.GetCameras();
        Cameras = [.. _cameraInfos.Select(d => new MediaDevice(d.Id, d.Name))];
        if (CameraId != null && !Cameras.Any(c => c.Id == CameraId)) CameraId = null;
        ApplyAudioDevices(ReadAudioDevices());
    }

    /// <summary>
    /// Only audio devices: libwebrtc enumerates them under the module's lock and on its worker
    /// thread, while camera enumeration runs unguarded on the calling thread, so cameras stay on
    /// the UI thread (<see cref="RefreshDevices"/>).
    /// </summary>
    public async Task CheckDevicesAsync()
    {
        if (_checkingDevices || _disposed) return;
        _checkingDevices = true;
        try
        {
            var generation = _deviceGeneration;
            var devices = await Task.Run(ReadAudioDevices).ConfigureAwait(true);
            // A RefreshDevices meanwhile read (and applied) newer lists.
            if (_disposed || generation != _deviceGeneration) return;
            if (_microphones.IsUnchanged(devices.Microphones, devices.DefaultMicrophone)
                && _speakers.IsUnchanged(devices.Speakers, devices.DefaultSpeaker)) return;
            ApplyAudioDevices(devices);
        }
        catch (WebRtcException)
        {
        }
        finally
        {
            _checkingDevices = false;
        }
    }

    private sealed record AudioDeviceLists(IReadOnlyList<DeviceInfo> Microphones, string? DefaultMicrophone,
        IReadOnlyList<DeviceInfo> Speakers, string? DefaultSpeaker);

    private AudioDeviceLists ReadAudioDevices()
    {
        lock (_deviceReadGate)
        {
            return new(_factory.GetMicrophones(), _factory.GetDefaultMicrophoneId(), _factory.GetSpeakers(), _factory.GetDefaultSpeakerId());
        }
    }

    private void ApplyAudioDevices(AudioDeviceLists devices)
    {
        var micReplaced = _microphones.Update(devices.Microphones, devices.DefaultMicrophone);
        var speakerReplaced = _speakers.Update(devices.Speakers, devices.DefaultSpeaker);
        DevicesChanged?.Invoke();
        // Not while a call starts: the saved choice is applied right after.
        if (micReplaced && _micTrack != null)
        {
            RestartMicrophoneSend();
            AudioDeviceReplaced?.Invoke(AudioDeviceKind.Microphone, _microphones.Current);
        }
        if (speakerReplaced && _localStarted) AudioDeviceReplaced?.Invoke(AudioDeviceKind.Speaker, _speakers.Current);
    }

    /// <summary>
    /// The module only restarts capture on a new device while it is recording; after all
    /// microphones went (or none was there when the call started) it may not be. Taking the track
    /// off the sender and putting it back stops and starts the send stream, and starting the first
    /// one starts recording. The sender, its cryptor and the negotiated m-line stay.
    /// </summary>
    internal void RestartMicrophoneSend()
    {
        if (_audioSender == null || _micTrack == null) return;
        _audioSender.SetTrack(null);
        _audioSender.SetTrack(_micTrack);
    }

    private static int IndexOf(IReadOnlyList<DeviceInfo> devices, string? id) => AudioDeviceSelection.IndexOf(devices, id);

    public bool SelectMicrophone(string id) => _microphones.Select(id);

    public bool SelectSpeaker(string id) => _speakers.Select(id);

    public async Task<bool> SelectCameraAsync(string id)
    {
        var index = IndexOf(_cameraInfos, id);
        if (index < 0) return false;
        // After a camera still being opened, so a failure goes back to the last one that worked.
        await Task.WhenAny(_cameraReopen).ConfigureAwait(true);
        if (!_localStarted) return false;
        if (index == _cameraIndex && _cameraTrack != null) return true;
        var generation = _localGeneration;
        var previousIndex = _cameraIndex;
        var previousId = CameraId;
        _cameraIndex = index;
        CameraId = id;
        if (await ReopenCameraAsync().ConfigureAwait(true)) return true;
        // The previous camera was released for the attempt; open it again.
        if (generation == _localGeneration && previousIndex >= 0 && previousIndex != index && _cameraIndex == index)
        {
            _cameraIndex = previousIndex;
            CameraId = previousId;
            await ReopenCameraAsync().ConfigureAwait(true);
        }
        return false;
    }

    // ---- Local media --------------------------------------------------------------------------

    public async Task<LocalMediaResult> StartLocalMediaAsync(string? cameraId, string? microphoneId, string? speakerId)
    {
        var generation = ++_localGeneration;
        // A reopen from the last call may still hold the camera.
        await Task.WhenAny(_cameraReopen).ConfigureAwait(true);
        if (generation != _localGeneration || _disposed) return new LocalMediaResult(false, false);
        RefreshDevices();
        if (_microphones.IndexOf(microphoneId) >= 0 && !(_microphones.Pinned && microphoneId == MicrophoneId)) SelectMicrophone(microphoneId!);
        if (_speakers.IndexOf(speakerId) >= 0 && !(_speakers.Pinned && speakerId == SpeakerId)) SelectSpeaker(speakerId!);
        var cameraIndex = IndexOf(_cameraInfos, cameraId);
        if (cameraIndex < 0 && _cameraInfos.Count > 0) cameraIndex = _cameraInfos[0].Index;

        // Opening a camera takes a moment; keep it off the UI thread.
        var (mic, source, camera) = await Task.Run(() =>
        {
            MediaTrack? micTrack = null;
            VideoSource? cameraSource = null;
            MediaTrack? cameraTrack = null;
            try
            {
                micTrack = _factory.CreateAudioTrack("audio");
            }
            catch (WebRtcException)
            {
            }
            if (cameraIndex >= 0) (cameraSource, cameraTrack) = TryOpenCamera(cameraIndex);
            return (micTrack, cameraSource, cameraTrack);
        }).ConfigureAwait(true);

        if (generation != _localGeneration || _disposed)
        {
            camera?.Dispose();
            source?.Dispose();
            mic?.Dispose();
            return new LocalMediaResult(false, false);
        }

        _localStarted = true;
        _micTrack = mic;
        if (_micTrack != null) _micTrack.Enabled = _micEnabled;
        _cameraSource = source;
        _cameraTrack = camera;
        if (camera != null)
        {
            _cameraIndex = cameraIndex;
            CameraId = _cameraInfos.FirstOrDefault(d => d.Index == cameraIndex)?.Id;
            camera.Enabled = _cameraEnabled;
            if (!_cameraEnabled) source!.SetCapturing(false);
        }
        SyncEffectsTrack();
        AttachEffectsInput();
        UpdateLocalFeed();
        return new LocalMediaResult(HasMicrophone, HasCamera);
    }

    private (VideoSource?, MediaTrack?) TryOpenCamera(int index)
    {
        VideoSource? source = null;
        try
        {
            source = _factory.CreateCameraSource(index);
            return (source, _factory.CreateVideoTrack(source, "video"));
        }
        catch (WebRtcException)
        {
            source?.Dispose();
            return (null, null);
        }
    }

    public void StopLocalMedia()
    {
        _localGeneration++;
        _localStarted = false;
        _idleMeter.Stop();
        _presentGeneration++;
        ClosePeerConnection();
        CloseGroupConnections();
        // Detaches the processor's camera sink; no rerouting onto tracks that are about to go.
        ClearEffects(route: false);
        SetLocalFeed(null);
        lock (_effectsOutputGate)
        {
            _effectsTrack?.Dispose();
            _effectsSource?.Dispose();
            _effectsTrack = null;
            _effectsSource = null;
        }
        // Nothing feeds it or receives from it any more (its events check _scene, now null).
        if (_processor is { } processor)
        {
            _processor = null;
            _processorDisposal = _processorDisposal.ContinueWith(_ => processor.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
        _presentTrack?.Dispose();
        _presentSource?.Dispose();
        _presentTrack = null;
        _presentSource = null;
        _presentation = Presentation.None;
        _cameraTrack?.Dispose();
        _cameraSource?.Dispose();
        _cameraTrack = null;
        _cameraSource = null;
        _placeholderTrack?.Dispose();
        _placeholderSource?.Dispose();
        _placeholderTrack = null;
        _placeholderSource = null;
        _micTrack?.Dispose();
        _micTrack = null;
        _micEnabled = true;
        _cameraEnabled = true;
        _remoteAudioEnabled = true;
        _remoteVideoEnabled = true;
        _key = null;
    }

    public void SetMicrophoneEnabled(bool enabled)
    {
        _micEnabled = enabled;
        if (_micTrack != null) _micTrack.Enabled = enabled;
        if (!enabled) _idleMeter.Stop();
    }

    public async Task<bool> SetCameraEnabledAsync(bool enabled)
    {
        _cameraEnabled = enabled;
        // The camera stays closed while presenting; StopPresentingAsync applies the setting.
        if (_presentation != Presentation.None || _cameraTrack == null) return true;
        if (!enabled)
        {
            _cameraTrack.Enabled = false;
            SyncEffectsTrack();
            _cameraSource?.SetCapturing(false);
            return true;
        }
        // A camera being opened (an earlier "on", or a switch) follows the setting once it is in.
        if (!_cameraReopen.IsCompleted)
        {
            await Task.WhenAny(_cameraReopen).ConfigureAwait(true);
            if (!_cameraEnabled || _presentation != Presentation.None || _cameraTrack == null) return true;
        }
        // The last mask and face are from before the camera went off.
        _processor?.InvalidateAnalysis();
        if (_cameraSource != null && _cameraSource.SetCapturing(true))
        {
            _cameraTrack.Enabled = true;
            SyncEffectsTrack();
            return true;
        }
        // libwebrtc releases the camera when it stops, so turning it back on opens it again.
        var generation = _localGeneration;
        return await ReopenCameraAsync().ConfigureAwait(true) || generation != _localGeneration;
    }

    /// <summary>
    /// Replaces the camera source (another device, or turning it back on). One at a time: most
    /// cameras cannot be opened twice, and a later open must not leak an earlier one's source.
    /// </summary>
    private Task<bool> ReopenCameraAsync()
    {
        var reopen = ReopenAfterAsync(_cameraReopen, _localGeneration);
        _cameraReopen = reopen;
        return reopen;
    }

    private async Task<bool> ReopenAfterAsync(Task previous, int generation)
    {
        // Its outcome (or failure) is its caller's.
        await Task.WhenAny(previous).ConfigureAwait(true);
        if (generation != _localGeneration || _disposed) return false;
        return await ReopenCameraNowAsync(generation).ConfigureAwait(true);
    }

    private async Task<bool> ReopenCameraNowAsync(int generation)
    {
        if (_cameraIndex < 0) return false;
        var index = _cameraIndex;
        var oldSource = _cameraSource;
        var oldTrack = _cameraTrack;
        // Most cameras cannot be opened twice, so the old capture stops first.
        oldSource?.SetCapturing(false);

        var (source, track) = await Task.Run(() => TryOpenCamera(index)).ConfigureAwait(true);
        if (generation != _localGeneration || _disposed)
        {
            track?.Dispose();
            source?.Dispose();
            return false;
        }
        if (track == null) return false;

        _cameraSource = source;
        _cameraTrack = track;
        track.Enabled = _cameraEnabled;
        SyncEffectsTrack();
        if (!_cameraEnabled || _presentation != Presentation.None) source!.SetCapturing(false);
        // Before the new camera's first frame reaches the processor: the old mask is of another picture.
        _processor?.InvalidateAnalysis();
        AttachEffectsInput();
        if (_presentation == Presentation.None) _videoSender?.SetTrack(CameraOutput);
        UpdateLocalFeed();
        oldTrack?.Dispose();
        oldSource?.Dispose();
        return true;
    }

    /// <summary>What represents the camera: the processed track while effects are on (or held).</summary>
    private MediaTrack? CameraOutput =>
        _cameraTrack != null && _effectsTrack != null && (_holdEffects || _effectsLive) ? _effectsTrack : _cameraTrack;

    private MediaTrack OutgoingVideoTrack()
    {
        if (_presentTrack != null) return _presentTrack;
        if (CameraOutput is { } camera) return camera;
        // Without a camera a video sender is still needed, to present later without renegotiating.
        _placeholderSource ??= _factory.CreateCustomSource();
        _placeholderTrack ??= _factory.CreateVideoTrack(_placeholderSource, "video");
        return _placeholderTrack;
    }

    /// <summary>What a camera-less client sends; tests push frames into it.</summary>
    internal VideoSource? PlaceholderSource => _placeholderSource;

    /// <summary>The subscribe connection's m-lines, for tests of their reuse.</summary>
    internal int SubscribeTransceiverCount => _subscribePc?.GetTransceivers().Count ?? 0;

    private void UpdateLocalFeed()
    {
        var track = _presentTrack ?? CameraOutput;
        if (track == _localFeed?.Track) return;
        SetLocalFeed(track == null ? null : new TrackVideoFeed(track));
    }

    private void SetLocalFeed(TrackVideoFeed? feed)
    {
        var old = _localFeed;
        _localFeed = feed;
        if (old == null && feed == null) return;
        LocalVideoChanged?.Invoke(feed);
        old?.Dispose();
    }

    // ---- Presenting ---------------------------------------------------------------------------

    public IShareSourceList CreateShareSourceList(DesktopSourceType type) => new NativeShareSourceList(_factory, type, _dispatcher);

    public Task<bool> StartScreenShareAsync(ShareSource source)
    {
        if (source.Native is not DesktopMediaSource native) return Task.FromResult(false);
        return StartPresentingAsync(Presentation.Screen, onState =>
            _factory.CreateDesktopSource(native, ScreenFps, showCursor: true, onState));
    }

    public Task<bool> StartFileShareAsync(string path) =>
        StartPresentingAsync(Presentation.File, onState => _factory.CreateFileSource(path, loop: true, onState));

    private async Task<bool> StartPresentingAsync(Presentation kind, Func<Action<CaptureState>, VideoSource> create)
    {
        var generation = ++_presentGeneration;
        void OnState(CaptureState state) => _dispatcher.Post(() =>
        {
            if (generation == _presentGeneration && state is CaptureState.Stopped or CaptureState.Failed)
            {
                PresentationStateChanged?.Invoke(kind, state);
            }
        });

        VideoSource? source = null;
        MediaTrack? track = null;
        try
        {
            (source, track) = await Task.Run(() =>
            {
                var created = create(OnState);
                return (created, _factory.CreateVideoTrack(created, kind == Presentation.File ? "file" : "screen"));
            }).ConfigureAwait(true);
        }
        catch (WebRtcException)
        {
            source?.Dispose();
            return false;
        }
        if (generation != _presentGeneration || _disposed)
        {
            track.Dispose();
            source.Dispose();
            return false;
        }

        var oldSource = _presentSource;
        var oldTrack = _presentTrack;
        _presentSource = source;
        _presentTrack = track;
        _presentation = kind;
        // The camera (and its light) is off while presenting, as on the web.
        _cameraSource?.SetCapturing(false);
        _videoSender?.SetTrack(track);
        UpdateLocalFeed();
        oldTrack?.Dispose();
        oldSource?.Dispose();
        return true;
    }

    public async Task<bool> StopPresentingAsync()
    {
        if (_presentation == Presentation.None) return true;
        _presentGeneration++;
        var oldSource = _presentSource;
        var oldTrack = _presentTrack;
        _presentSource = null;
        _presentTrack = null;
        _presentation = Presentation.None;

        _videoSender?.SetTrack(OutgoingVideoTrack());
        UpdateLocalFeed();
        oldTrack?.Dispose();
        oldSource?.Dispose();

        // A camera still being opened sees the presentation gone and starts by itself.
        if (!_cameraReopen.IsCompleted)
        {
            var generation = _localGeneration;
            await Task.WhenAny(_cameraReopen).ConfigureAwait(true);
            if (generation != _localGeneration || _presentation != Presentation.None) return true;
        }
        if (_cameraTrack != null)
        {
            _cameraTrack.Enabled = _cameraEnabled;
            SyncEffectsTrack();
            // The last mask and face are from before the presentation.
            _processor?.InvalidateAnalysis();
            if (_cameraEnabled && !(_cameraSource?.SetCapturing(true) ?? false))
            {
                var generation = _localGeneration;
                return await ReopenCameraAsync().ConfigureAwait(true) || generation != _localGeneration;
            }
        }
        return true;
    }

    // ---- Effects ------------------------------------------------------------------------------

    public void HoldEffects()
    {
        if (_effectsModels == null) return;
        EnsureEffectsPipeline();
        _holdEffects = true;
        RouteCamera();
    }

    public async Task<bool> SetEffectsAsync(EffectsSelection selection)
    {
        var generation = ++_effectsGeneration;
        if (_effectsModels == null || !EffectsCatalog.HasEffects(selection))
        {
            ClearEffects();
            return _effectsModels != null || !EffectsCatalog.HasEffects(selection);
        }
        EnsureEffectsPipeline();
        EffectsScene scene;
        try
        {
            // The previous call's processor may still be running these models.
            await _processorDisposal.ConfigureAwait(true);
            scene = await EffectsScene.LoadAsync(EffectsCatalog, selection, _effectsModels, Video, _scene).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Effects failed to load: {e}");
            return false;
        }
        if (generation != _effectsGeneration || _disposed || _processor == null)
        {
            scene.Dispose();
            return false;
        }
        var old = _scene;
        _scene = scene;
        _processor.SetScene(scene);
        AttachEffectsInput();
        // The compositor may still be drawing the old scene; its video is safe to stop meanwhile.
        old?.Dispose();
        return true;
    }

    private IBackgroundVideo Video(string path) => new NativeBackgroundVideo(_factory, path);

    private void EnsureEffectsPipeline()
    {
        if (_effectsModels == null) return;
        if (_effectsTrack == null)
        {
            var source = _factory.CreateCustomSource();
            lock (_effectsOutputGate)
            {
                _effectsSource = source;
                _effectsTrack = _factory.CreateVideoTrack(source, "video");
            }
            SyncEffectsTrack();
        }
        if (_processor != null) return;
        var processor = new EffectsProcessor(_effectsModels, PushEffectsFrame);
        processor.FirstFrame += scene => _dispatcher.Post(() =>
        {
            if (scene != _scene || _effectsLive) return;
            _effectsLive = true;
            _holdEffects = false;
            RouteCamera();
        });
        processor.Failed += (scene, error) => _dispatcher.Post(() => OnProcessorFailed(processor, scene, error));
        _processor = processor;
    }

    /// <summary>
    /// Drawing failed. The sender keeps the effects track (now sending nothing) rather than the
    /// camera, and a model that broke is reloaded on the CPU by the next <see cref="SetEffectsAsync"/>.
    /// </summary>
    private void OnProcessorFailed(EffectsProcessor processor, EffectsScene scene, Exception error)
    {
        System.Diagnostics.Debug.WriteLine($"Effects failed: {error}");
        if (processor != _processor || scene != _scene) return;
        processor.SetScene(null);
        _holdEffects = true;
        _effectsLive = false;
        RouteCamera();
        if (error is EffectsModelException failed && _effectsModels?.MarkBroken(failed.Model) is { } broken) processor.Retire(broken);
        EffectsFailed?.Invoke();
    }

    /// <summary>Compositor thread.</summary>
    private void PushEffectsFrame(I420Buffer frame)
    {
        lock (_effectsOutputGate)
        {
            _effectsSource?.PushI420(frame.Width, frame.Height, frame.Y, frame.StrideY, frame.U, frame.StrideUV, frame.V, frame.StrideUV);
        }
    }

    /// <summary>Feeds the camera into the processor while a scene is set.</summary>
    private void AttachEffectsInput()
    {
        var track = _scene != null && _processor != null ? _cameraTrack : null;
        if (track == _effectsInputTrack) return;
        _effectsInput?.Dispose();
        _effectsInput = null;
        _effectsInputTrack = track;
        if (track == null) return;
        var processor = _processor!;
        // Frames come downscaled to 720p at most, the size the effects are tuned for.
        _effectsInput = track.AddSink(frame => processor.Submit(frame.Data, frame.Width, frame.Height, frame.Stride), 1280, 720);
    }

    private void ClearEffects(bool route = true)
    {
        _effectsGeneration++;
        _holdEffects = false;
        _effectsLive = false;
        var scene = _scene;
        _scene = null;
        AttachEffectsInput();
        _processor?.SetScene(null);
        if (route) RouteCamera();
        scene?.Dispose();
    }

    private void RouteCamera()
    {
        if (_presentation == Presentation.None) _videoSender?.SetTrack(OutgoingVideoTrack());
        UpdateLocalFeed();
    }

    /// <summary>The processed track is on and off with the camera (a disabled one sends black).</summary>
    private void SyncEffectsTrack()
    {
        if (_effectsTrack != null) _effectsTrack.Enabled = _cameraTrack?.Enabled ?? _cameraEnabled;
    }

    // ---- Peer connection ----------------------------------------------------------------------

    public void SetEncryptionKey(byte[] key)
    {
        _key = key;
        _keyProvider?.SetSharedKey(0, key);
    }

    public void OpenPeerConnection(bool e2ee)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The call's audio device module opens the microphone next; the waiting meter lets go first.
        _idleMeter.Stop();
        ClosePeerConnection();
        CloseGroupConnections();
        var generation = ++_pcGeneration;
        _e2ee = e2ee;
        if (e2ee)
        {
            _keyProvider = new KeyProvider(KeyProviderOptions.Demo);
            if (_key != null) _keyProvider.SetSharedKey(0, _key);
        }

        var pc = _factory.CreatePeerConnection(IceServers, _keyProvider);
        // Handlers run on WebRTC threads; everything is handed to the UI thread.
        pc.IceCandidateGathered += candidate => Post(generation, () => IceCandidateGathered?.Invoke(candidate));
        pc.ConnectionStateChanged += state => Post(generation, () => ConnectionStateChanged?.Invoke(state));
        pc.TrackAdded += (track, _) => _dispatcher.Post(() =>
        {
            if (generation == _pcGeneration && !_disposed) OnRemoteTrack(track);
            else track.Dispose();
        });
        pc.DataChannelReceived += channel =>
        {
            // Subscribed here, on the signaling thread, so no state change is missed.
            Observe(channel, generation);
            _dispatcher.Post(() =>
            {
                if (generation == _pcGeneration && !_disposed) AdoptChannel(channel);
                else channel.Dispose();
            });
        };
        _pc = pc;

        if (_micTrack != null) _audioSender = pc.AddTrack(_micTrack, StreamId);
        _videoSender = pc.AddTrack(OutgoingVideoTrack(), StreamId);
        if (e2ee) pc.AttachSenderCryptors();
        ApplyCodecPreferences();
    }

    public void ApplyCodecPreferences()
    {
        if (_e2ee && _pc != null) _pc.PreferCodec(MediaKind.Video, "video/VP8");
    }

    public void CreateChatChannel()
    {
        if (_pc == null) return;
        var channel = _pc.CreateDataChannel(DataChannelLabel);
        Observe(channel, _pcGeneration);
        AdoptChannel(channel);
    }

    private void Observe(DataChannel channel, int generation)
    {
        channel.StateChanged += state => Post(generation, () =>
        {
            if (channel == _channel) ChatStateChanged?.Invoke(state);
        });
        channel.MessageReceived += message => Post(generation, () =>
        {
            if (channel == _channel && !message.IsBinary) ChatMessageReceived?.Invoke(message.Text);
        });
    }

    private void AdoptChannel(DataChannel channel)
    {
        _channel?.Dispose();
        _channel = channel;
        // It may have opened before it was adopted.
        if (channel.State == DataChannelState.Open) ChatStateChanged?.Invoke(DataChannelState.Open);
    }

    private void OnRemoteTrack(MediaTrack track)
    {
        if (track.Kind == MediaKind.Audio)
        {
            _remoteAudio?.Dispose();
            _remoteAudio = track;
            track.Enabled = _remoteAudioEnabled;
            return;
        }
        var oldFeed = _remoteFeed;
        var oldTrack = _remoteVideo;
        _remoteVideo = track;
        track.Enabled = _remoteVideoEnabled;
        _remoteFeed = new TrackVideoFeed(track);
        RemoteVideoChanged?.Invoke(_remoteFeed);
        oldFeed?.Dispose();
        oldTrack?.Dispose();
    }

    public void ClosePeerConnection()
    {
        var pc = _pc;
        if (pc == null) return;
        _pcGeneration++;
        _pc = null;

        _channel?.Dispose();
        _channel = null;
        if (_remoteFeed != null)
        {
            _remoteFeed.Dispose();
            _remoteFeed = null;
            RemoteVideoChanged?.Invoke(null);
        }
        pc.Close();
        _audioSender?.Dispose();
        _videoSender?.Dispose();
        _audioSender = null;
        _videoSender = null;
        _remoteAudio?.Dispose();
        _remoteVideo?.Dispose();
        _remoteAudio = null;
        _remoteVideo = null;
        pc.Dispose();
        _keyProvider?.Dispose();
        _keyProvider = null;
    }

    private PeerConnection RequirePeerConnection() =>
        _pc ?? throw new InvalidOperationException("No peer connection.");

    public Task<SessionDescription> CreateOfferAsync() => RequirePeerConnection().CreateOfferAsync();

    public Task<SessionDescription> CreateAnswerAsync() => RequirePeerConnection().CreateAnswerAsync();

    public Task SetRemoteDescriptionAsync(SessionDescription description) =>
        RequirePeerConnection().SetRemoteDescriptionAsync(description);

    public bool AddIceCandidate(IceCandidate candidate) => _pc?.AddIceCandidate(candidate) ?? false;

    public bool SendChat(string text) => _channel is { State: DataChannelState.Open } channel && channel.Send(text);

    public async Task<double?> GetRemoteAudioLevelAsync()
    {
        if (_pc == null) return null;
        try
        {
            return await _pc.GetRemoteAudioLevelAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<double?> GetMicPeakAsync()
    {
        if (!_micEnabled || _micTrack == null)
        {
            _idleMeter.Stop();
            return 0;
        }
        if ((_pc ?? _publishPc) is not { } pc) return _idleMeter.Read(MicrophoneId);
        _idleMeter.Stop();
        try
        {
            return await pc.GetLocalAudioLevelAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether the waiting-alone meter holds the microphone.</summary>
    internal bool IdleMeterRunning => _idleMeter.Running;

    internal async Task<long?> GetInboundAudioPacketsAsync()
    {
        if (_pc == null) return null;
        try
        {
            return await _pc.GetInboundAudioPacketsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void SetRemoteAudioEnabled(bool enabled)
    {
        _remoteAudioEnabled = enabled;
        if (_remoteAudio != null) _remoteAudio.Enabled = enabled;
        foreach (var track in _groupTracks.Values)
        {
            if (track.Kind == MediaKind.Audio) track.Enabled = enabled;
        }
    }

    public void SetRemoteVideoEnabled(bool enabled)
    {
        _remoteVideoEnabled = enabled;
        if (_remoteVideo != null) _remoteVideo.Enabled = enabled;
        foreach (var track in _groupTracks.Values)
        {
            if (track.Kind == MediaKind.Video) track.Enabled = enabled;
        }
    }

    private void Post(int generation, Action action) => _dispatcher.Post(() =>
    {
        if (generation == _pcGeneration && !_disposed) action();
    });

    // ---- Group call ---------------------------------------------------------------------------

    public bool HasGroupConnections => _publishPc != null;

    public void OpenGroupConnections(bool e2ee, string streamId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _idleMeter.Stop();
        ClosePeerConnection();
        CloseGroupConnections();
        var generation = ++_groupGeneration;
        _e2ee = e2ee;
        if (e2ee)
        {
            // One key provider for both connections: the room has one key.
            _keyProvider = new KeyProvider(KeyProviderOptions.Demo);
            if (_key != null) _keyProvider.SetSharedKey(0, _key);
        }

        var publish = _factory.CreatePeerConnection(IceServers, _keyProvider);
        publish.IceCandidateGathered += candidate => PostGroup(generation, () => GroupIceCandidateGathered?.Invoke(GroupConnection.Publish, candidate));
        publish.ConnectionStateChanged += state => PostGroup(generation, () => GroupConnectionStateChanged?.Invoke(GroupConnection.Publish, state));
        _publishPc = publish;
        // Both are added even without a microphone or camera, so presenting never renegotiates.
        _audioSender = publish.AddTransceiver(MediaKind.Audio, TransceiverDirection.SendOnly, _micTrack, streamId);
        _videoSender = publish.AddTransceiver(MediaKind.Video, TransceiverDirection.SendOnly, OutgoingVideoTrack(), streamId);
        if (e2ee) publish.AttachSenderCryptors();
        // The SFU only forwards VP8 (and Opus), and the E2EE frame format keeps VP8's header readable.
        publish.PreferCodec(MediaKind.Video, "video/VP8");

        var subscribe = _factory.CreatePeerConnection(IceServers, _keyProvider);
        subscribe.IceCandidateGathered += candidate => PostGroup(generation, () => GroupIceCandidateGathered?.Invoke(GroupConnection.Subscribe, candidate));
        subscribe.ConnectionStateChanged += state => PostGroup(generation, () => GroupConnectionStateChanged?.Invoke(GroupConnection.Subscribe, state));
        subscribe.TrackAdded += (track, info) => _dispatcher.Post(() =>
        {
            if (generation == _groupGeneration && !_disposed) OnGroupTrack(track, info);
            else track.Dispose();
        });
        _subscribePc = subscribe;
    }

    public Task<SessionDescription> CreatePublishOfferAsync() => RequireGroup(_publishPc).CreateOfferAsync();

    public Task SetPublishAnswerAsync(string sdp) => RequireGroup(_publishPc).SetRemoteDescriptionAsync(new SessionDescription("answer", sdp));

    public async Task<SessionDescription> AnswerSubscribeOfferAsync(string sdp)
    {
        var pc = RequireGroup(_subscribePc);
        var generation = _groupGeneration;
        await pc.SetRemoteDescriptionAsync(new SessionDescription("offer", sdp)).ConfigureAwait(true);
        ThrowIfReplaced(generation);
        // Before answering: a receiver reused for a new participant decrypts from its first frame.
        if (_e2ee) pc.AttachReceiverCryptors();
        _subscribeOffer = SdpMedia.Parse(sdp);
        RemapReceivers();
        var answer = await pc.CreateAnswerAsync().ConfigureAwait(true);
        ThrowIfReplaced(generation);
        RemapReceivers();
        return answer;
    }

    public bool AddGroupIceCandidate(GroupConnection connection, IceCandidate candidate) =>
        (connection == GroupConnection.Publish ? _publishPc : _subscribePc)?.AddIceCandidate(candidate) ?? false;

    public async Task<IReadOnlyDictionary<string, double>> GetParticipantAudioLevelsAsync()
    {
        var levels = new Dictionary<string, double>(StringComparer.Ordinal);
        if (_subscribePc is not { } pc) return levels;
        var requests = new List<(string Participant, Task<double?> Level)>();
        foreach (var (receiverId, participant) in _receiverOwners)
        {
            if (_groupTracks.TryGetValue(receiverId, out var track) && track.Kind == MediaKind.Audio)
            {
                requests.Add((participant, pc.GetReceiverAudioLevelAsync(receiverId)));
            }
        }
        foreach (var (participant, request) in requests)
        {
            double? level;
            try
            {
                level = await request.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            if (level is { } value) levels[participant] = Math.Max(value, levels.GetValueOrDefault(participant));
        }
        return levels;
    }

    public void CloseGroupConnections()
    {
        var publish = _publishPc;
        var subscribe = _subscribePc;
        if (publish == null && subscribe == null) return;
        _groupGeneration++;
        _publishPc = null;
        _subscribePc = null;

        var feeds = _participantVideo.ToList();
        _participantVideo.Clear();
        foreach (var (participant, _) in feeds) ParticipantVideoChanged?.Invoke(participant, null);
        foreach (var (_, video) in feeds) video.Feed.Dispose();
        publish?.Close();
        subscribe?.Close();
        _audioSender?.Dispose();
        _videoSender?.Dispose();
        _audioSender = null;
        _videoSender = null;
        foreach (var track in _groupTracks.Values) track.Dispose();
        _groupTracks.Clear();
        _trackStreams.Clear();
        _receiverOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        _subscribeOffer = [];
        publish?.Dispose();
        subscribe?.Dispose();
        _keyProvider?.Dispose();
        _keyProvider = null;
    }

    private void OnGroupTrack(MediaTrack track, RemoteTrackInfo info)
    {
        // A receiver reported again (its m-line became active again) comes with a new handle.
        _groupTracks.Remove(info.ReceiverId, out var old);
        _groupTracks[info.ReceiverId] = track;
        _trackStreams[info.ReceiverId] = info.StreamId;
        track.Enabled = track.Kind == MediaKind.Audio ? _remoteAudioEnabled : _remoteVideoEnabled;
        RemapReceivers();
        old?.Dispose();
    }

    /// <summary>Receivers → participants from the latest offer, then each participant's video feed.</summary>
    private void RemapReceivers()
    {
        if (_subscribePc is not { } pc) return;
        var owners = ReceiverMap.Map(_subscribeOffer, pc.GetTransceivers());
        if (_subscribeOffer.Count == 0)
        {
            foreach (var (receiverId, stream) in _trackStreams)
            {
                if (stream.Length > 0) owners.TryAdd(receiverId, stream);
            }
        }
        _receiverOwners = owners;

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (receiverId, participant) in owners)
        {
            if (_groupTracks.TryGetValue(receiverId, out var track) && track.Kind == MediaKind.Video) wanted.TryAdd(participant, receiverId);
        }
        var changed = new List<string>();
        var retired = new List<TrackVideoFeed>();
        foreach (var (participant, video) in _participantVideo.ToList())
        {
            if (wanted.TryGetValue(participant, out var receiverId) && receiverId == video.ReceiverId && video.Feed.Track == _groupTracks[receiverId]) continue;
            _participantVideo.Remove(participant);
            retired.Add(video.Feed);
            changed.Add(participant);
        }
        foreach (var (participant, receiverId) in wanted)
        {
            if (_participantVideo.ContainsKey(participant)) continue;
            _participantVideo[participant] = (receiverId, new TrackVideoFeed(_groupTracks[receiverId]));
            if (!changed.Contains(participant)) changed.Add(participant);
        }
        foreach (var participant in changed)
        {
            ParticipantVideoChanged?.Invoke(participant, _participantVideo.TryGetValue(participant, out var video) ? video.Feed : null);
        }
        foreach (var feed in retired) feed.Dispose();
    }

    private void ThrowIfReplaced(int generation)
    {
        if (generation != _groupGeneration || _disposed) throw new OperationCanceledException("The group connections were replaced.");
    }

    private static PeerConnection RequireGroup(PeerConnection? pc) =>
        pc ?? throw new InvalidOperationException("No group connection.");

    private void PostGroup(int generation, Action action) => _dispatcher.Post(() =>
    {
        if (generation == _groupGeneration && !_disposed) action();
    });

    public void Dispose()
    {
        if (_disposed) return;
        StopLocalMedia();
        _disposed = true;
        if (_effectsModels is { } models)
        {
            _processorDisposal.ContinueWith(_ => models.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}