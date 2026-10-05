using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Group;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Core.Tests.Fakes;
using WebRtcDemo.Effects;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests;

public sealed class CallViewModelTests : IDisposable
{
    private const string Room = "123456";

    private readonly ManualDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSocketFactory _sockets = new();
    private readonly SignalingClient _signaling;
    private readonly FakeCallMedia _media = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), "webrtcdemo-vm-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly CallViewModel _vm;
    private readonly List<Toast> _toasts = [];
    private int _ended;

    public CallViewModelTests()
    {
        _signaling = new SignalingClient(_sockets.Create, _dispatcher);
        var group = new GroupCallClient(new FakeSocketFactory().Create, _media, _dispatcher);
        _vm = new CallViewModel(_signaling, _media, _dispatcher, new SettingsStore(_settingsPath), _time, group);
        _vm.ToastRequested += _toasts.Add;
        _vm.Ended += () => _ended++;
    }

    public void Dispose()
    {
        _vm.Dispose();
        _signaling.Dispose();
        _dispatcher.Dispose();
        File.Delete(_settingsPath);
    }

    private FakeMessageSocket Socket => _sockets.Last;

    private List<string> Types() => Socket.Types;

    private List<JsonElement> MediaStates() =>
        Socket.Messages.Where(m => m.GetProperty("type").GetString() == "media state").Select(m => m.GetProperty("state")).ToList();

    private static string State(JsonElement state) =>
        $"audio={state.GetProperty("audio").GetBoolean()} video={state.GetProperty("video").GetBoolean()} screen={state.GetProperty("screen").GetBoolean()}";

    private void Receive(string json)
    {
        Socket.Receive(json);
        _dispatcher.RunPending();
    }

    private void Join(bool e2ee = false)
    {
        _vm.Join(Room, e2ee, ServerAddress.DefaultUrl);
        Socket.Open();
        _dispatcher.RunPending();
    }

    private void Run(Action action)
    {
        action();
        _dispatcher.RunPending();
    }

    private void Advance(int milliseconds)
    {
        _time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        _dispatcher.RunPending();
    }

    private void ConnectCall()
    {
        Join();
        Receive(PeerJoined);
        Run(() => _media.RaiseConnection(PeerConnectionState.Connected));
    }

    private const string PeerJoined = """{"type":"peer joined"}""";

    private static string MediaState(bool audio, bool video, bool screen) =>
        $$$"""{"type":"media state","state":{"audio":{{{(audio ? "true" : "false")}}},"video":{{{(video ? "true" : "false")}}},"screen":{{{(screen ? "true" : "false")}}}}}""";

    // ---- Joining and negotiation ----------------------------------------------------------------

    [Fact]
    public void Join_opens_media_and_joins_the_room()
    {
        Join();
        Assert.Equal(CallPhase.Waiting, _vm.Phase);
        Assert.Equal(["StartLocalMedia"], _media.Log);
        Assert.Equal(new Uri("ws://localhost:4000/ws"), Socket.Url);
        Assert.Equal([$$"""{"type":"join","roomId":"{{Room}}"}"""], Socket.Sent);
        Assert.Equal("peer-123456", _vm.AvatarSeed);
        Assert.False(_vm.IsGroup);
        Assert.Equal(ServerAddress.DefaultUrl, _vm.ServerUrl);
    }

    [Fact]
    public void Each_call_has_its_own_socket()
    {
        Join();
        _vm.Leave();
        Assert.True(Socket.IsClosed);
        Join();
        Assert.Equal(2, _sockets.Created.Count);
        Assert.Equal(["join"], Types());
    }

    [Fact]
    public void The_peer_in_the_room_offers_when_someone_joins()
    {
        Join();
        Receive(PeerJoined);

        Assert.Equal(["StartLocalMedia", "Open(e2ee=False)", "CreateChatChannel", "CreateOffer"], _media.Log);
        Assert.Equal(["join", "offer"], Types());
        Assert.Equal("""{"type":"offer","sdp":"v=0 offer"}""", Socket.Sent[1]);
        Assert.Equal(CallPhase.Connecting, _vm.Phase);
    }

    [Fact]
    public void E2ee_offerer_sends_the_key_before_the_offer()
    {
        Join(e2ee: true);
        Receive(PeerJoined);

        Assert.Equal(["StartLocalMedia", "SetEncryptionKey", "Open(e2ee=True)", "CreateChatChannel", "CreateOffer"], _media.Log);
        Assert.Equal(["join", "encryption key", "offer"], Types());
        var key = Convert.FromBase64String(Socket.Messages[1].GetProperty("key").GetString()!);
        Assert.Equal(CallViewModel.EncryptionKeyLength, key.Length);
        Assert.Equal(key, _media.Key);

        // A new joiner gets a fresh key.
        Receive(PeerJoined);
        Assert.NotEqual(key, _media.Key);
    }

    [Fact]
    public void E2ee_answerer_acknowledges_the_key_and_answers_with_vp8_first()
    {
        Join(e2ee: true);
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Receive($$"""{"type":"encryption key","key":"{{Convert.ToBase64String(key)}}"}""");
        Assert.Equal(key, _media.Key);

        // A candidate that beats the offer waits for the remote description.
        Receive("""{"type":"candidate","candidate":{"candidate":"candidate:early","sdpMid":"0","sdpMLineIndex":0}}""");
        Assert.Empty(_media.AddedCandidates);

        Receive("""{"type":"offer","sdp":"v=0 remote"}""");
        Assert.Equal(
            ["StartLocalMedia", "SetEncryptionKey", "Open(e2ee=True)", "SetRemote(offer)", "ApplyCodecPreferences", "CreateAnswer"],
            _media.Log);
        Assert.Equal(["candidate:early"], _media.AddedCandidates.Select(c => c.Candidate));

        Receive("""{"type":"candidate","candidate":{"candidate":"candidate:late","sdpMid":"1","sdpMLineIndex":1}}""");
        Assert.Equal(new IceCandidate("1", 1, "candidate:late"), _media.AddedCandidates[^1]);

        Assert.Equal(["join", "encryption key received", "answer"], Types());
        Assert.Equal("""{"type":"answer","sdp":"v=0 answer"}""", Socket.Sent[2]);
    }

    [Fact]
    public void A_key_is_ignored_without_e2ee()
    {
        Join();
        Receive("""{"type":"encryption key","key":"AQID"}""");
        Assert.Null(_media.Key);
        Assert.Equal(["join"], Types());
    }

    [Fact]
    public void Offerer_applies_the_answer_and_queued_candidates()
    {
        Join();
        Receive(PeerJoined);
        Receive("""{"type":"candidate","candidate":{"candidate":"candidate:a","sdpMid":"0","sdpMLineIndex":0}}""");
        Assert.Empty(_media.AddedCandidates);
        Receive("""{"type":"answer","sdp":"v=0"}""");
        Assert.Contains("SetRemote(answer)", _media.Log);
        Assert.Single(_media.AddedCandidates);
    }

    [Fact]
    public void Local_candidates_are_relayed()
    {
        Join();
        Receive(PeerJoined);
        Run(() => _media.RaiseCandidate(new IceCandidate("0", 0, "candidate:local")));
        Assert.Equal(
            """{"type":"candidate","candidate":{"candidate":"candidate:local","sdpMid":"0","sdpMLineIndex":0}}""",
            Socket.Sent.Single(m => m.Contains("\"candidate\",", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_late_offer_from_a_replaced_connection_is_not_sent()
    {
        Join();
        var pending = new TaskCompletionSource<SessionDescription>();
        _media.PendingOffer = pending;
        Receive(PeerJoined);
        Run(() => _media.RaiseConnection(PeerConnectionState.Failed));
        pending.SetResult(new SessionDescription("offer", "stale"));
        _dispatcher.RunPending();
        Assert.DoesNotContain("offer", Types());
    }

    // ---- Connection lifecycle -------------------------------------------------------------------

    [Fact]
    public void Connected_sends_media_state_and_toasts()
    {
        ConnectCall();
        Assert.Equal(CallPhase.Connected, _vm.Phase);
        Assert.NotNull(_vm.ConnectedSince);
        Assert.Equal(["audio=True video=True screen=False"], MediaStates().Select(State));
        Assert.Equal("Connected", _toasts[^1].Text);
    }

    [Fact]
    public void Remote_hang_up_via_the_chat_channel_ends_the_call()
    {
        ConnectCall();
        Run(() => _media.RaiseChatState(DataChannelState.Open));
        Assert.True(_vm.ChatReady);
        Run(() => _media.RaiseChatState(DataChannelState.Closed));
        Assert.Equal(CallPhase.Waiting, _vm.Phase);
        Assert.Contains("Close", _media.Log);
        Assert.Equal("The other participant left", _toasts[^1].Text);
        Assert.True(_vm.InRoom);
    }

    [Fact]
    public void Failed_connection_goes_back_to_waiting()
    {
        ConnectCall();
        Run(() => _media.RaiseConnection(PeerConnectionState.Disconnected));
        Assert.Equal(CallPhase.Waiting, _vm.Phase);
        Assert.False(_media.HasPeerConnection);
        Assert.Equal("The other participant left", _toasts[^1].Text);
    }

    [Fact]
    public void A_closed_socket_ends_the_call()
    {
        // No resume, as on the other clients: the server has already freed the seat.
        ConnectCall();
        Socket.Drop();
        _dispatcher.RunPending();

        Assert.Equal(1, _ended);
        Assert.False(_vm.InRoom);
        Assert.Contains("Close", _media.Log);
        Assert.Contains("StopLocalMedia", _media.Log);
        Assert.Equal(new Toast("Lost the connection to the signaling server.", ToastKind.Error, Glyphs.Warning), _toasts[^1]);
        Assert.Single(_sockets.Created);
    }

    [Fact]
    public void An_unreachable_server_ends_the_call()
    {
        _vm.Join(Room, false, "http://10.0.0.9:4000");
        Socket.Drop(wasOpen: false);
        _dispatcher.RunPending();
        Assert.Equal(1, _ended);
        Assert.Equal("Can't reach the signaling server.", _toasts[^1].Text);
    }

    [Fact]
    public void A_full_room_ends_the_call()
    {
        Join();
        Receive("""{"type":"error","message":"Room is full","fatal":true}""");
        Assert.Equal(1, _ended);
        Assert.False(_vm.InRoom);
        Assert.Equal(new Toast("That room already has two people in it.", ToastKind.Error, Glyphs.People), _toasts[^1]);
        Assert.Equal(["join", "leave"], Types());
        Assert.Contains("StopLocalMedia", _media.Log);

        // The server closes the socket next; that is not a second ending.
        Socket.Drop();
        _dispatcher.RunPending();
        Assert.Equal(1, _ended);
    }

    [Fact]
    public void Other_fatal_errors_end_the_call_with_their_text()
    {
        Join();
        Receive("""{"type":"error","message":"Missing room id","fatal":true}""");
        Assert.Equal(1, _ended);
        Assert.Equal(new Toast("Missing room id", ToastKind.Error, Glyphs.Warning), _toasts[^1]);
    }

    [Fact]
    public void Non_fatal_errors_are_shown()
    {
        Join();
        Receive("""{"type":"error","message":"You are already in this room","fatal":false}""");
        Assert.Equal("You are already in this room", _toasts[^1].Text);
        Assert.True(_vm.InRoom);
    }

    [Fact]
    public void Leave_resets_the_call()
    {
        ConnectCall();
        Run(_vm.ToggleMic);
        _vm.Leave();
        Assert.Equal(CallPhase.Idle, _vm.Phase);
        Assert.True(_vm.MicOn);
        Assert.Equal(1, _ended);
        Assert.Equal("leave", _sockets.Last.Types[^1]);
        Assert.True(_sockets.Last.IsClosed);
    }

    // ---- Media state ----------------------------------------------------------------------------

    [Fact]
    public void Camera_off_tells_the_peer_first_and_closes_the_camera_300ms_later()
    {
        ConnectCall();
        Run(_vm.ToggleCamera);
        Assert.False(_vm.CameraOn);
        Assert.Equal("audio=True video=False screen=False", State(MediaStates()[^1]));
        Assert.DoesNotContain("Camera(False)", _media.Log);

        Advance(299);
        Assert.DoesNotContain("Camera(False)", _media.Log);
        Advance(1);
        Assert.Contains("Camera(False)", _media.Log);
    }

    [Fact]
    public void Camera_on_opens_the_camera_first_and_tells_the_peer_300ms_later()
    {
        ConnectCall();
        Run(_vm.ToggleCamera);
        Advance(300);
        var states = MediaStates().Count;

        Run(_vm.ToggleCamera);
        Assert.Equal("Camera(True)", _media.Log[^1]);
        Assert.Equal(states, MediaStates().Count);
        Advance(300);
        Assert.Equal("audio=True video=True screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public void Turning_the_camera_back_on_quickly_cancels_closing_it()
    {
        ConnectCall();
        Run(_vm.ToggleCamera);
        Advance(100);
        Run(_vm.ToggleCamera);
        Advance(1000);
        Assert.DoesNotContain("Camera(False)", _media.Log);
        Assert.Equal("audio=True video=True screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public void A_camera_that_cannot_reopen_turns_off_again()
    {
        ConnectCall();
        Run(_vm.ToggleCamera);
        Advance(300);
        _media.CameraReopens = false;

        Run(_vm.ToggleCamera);
        Assert.False(_vm.CameraOn);
        Assert.Equal(new Toast("Couldn't turn the camera on", ToastKind.Error, Glyphs.VideoOff), _toasts[^1]);
        Advance(300);
        Assert.Equal("Camera(False)", _media.Log[^1]);
        Assert.Equal("audio=True video=False screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public async Task A_camera_that_cannot_reopen_after_presenting_turns_off()
    {
        ConnectCall();
        await _vm.ShareFileAsync("/videos/demo clip.mp4");
        _dispatcher.RunPending();
        _media.CameraReopens = false;

        await _vm.StopSharingAsync();
        Assert.False(_vm.CameraOn);
        Assert.Equal(new Toast("Couldn't turn the camera back on", ToastKind.Error, Glyphs.VideoOff), _toasts[^1]);
        Assert.Equal("audio=True video=False screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public void Mic_toggle_is_sent_at_once()
    {
        ConnectCall();
        Run(_vm.ToggleMic);
        Assert.False(_media.MicEnabled);
        Assert.Equal("audio=False video=True screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public void An_unplugged_microphone_falls_back_to_the_default_one_without_renegotiating()
    {
        ConnectCall();
        var log = _media.Log.Count;
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Advance(2000);
        Assert.Equal(1, _media.DeviceChecks);
        Assert.DoesNotContain(_toasts, t => t.Text.StartsWith("Switched", StringComparison.Ordinal));

        _media.NextDevices = ([new("mic-2", "Built-in Mic")], [new("spk-1", "Speakers")]);
        Advance(2000);
        Assert.Equal("mic-2", _vm.MicrophoneId);
        Assert.Contains(nameof(_vm.MicrophoneId), changed);
        Assert.Equal(new Toast("Switched to Built-in Mic", ToastKind.Info, Glyphs.Mic), _toasts[^1]);
        Assert.True(_vm.MicOn);
        Assert.True(_vm.HasMicrophone);
        // The track and its sender stay; the module just records from another device.
        Assert.Equal(log, _media.Log.Count);

        _media.NextDevices = ([], [new("spk-2", "Headphones")]);
        Advance(2000);
        Assert.Null(_vm.MicrophoneId);
        Assert.False(_vm.MicOn);
        Assert.False(_vm.HasMicrophone);
        Assert.False(_media.MicEnabled);
        Assert.Equal("audio=False video=True screen=False", State(MediaStates()[^1]));
        Assert.Contains(new Toast("The microphone was disconnected", ToastKind.Warning, Glyphs.MicOff), _toasts);
        Assert.Equal(new Toast("Switched to Headphones", ToastKind.Info, Glyphs.Speaker), _toasts[^1]);
        Assert.Equal("spk-2", _vm.SpeakerId);
        Assert.Equal(log, _media.Log.Count);
    }

    [Fact]
    public void Devices_are_not_checked_after_the_call()
    {
        ConnectCall();
        Run(_vm.Leave);
        Advance(10_000);
        Assert.Equal(0, _media.DeviceChecks);
    }

    [Fact]
    public void A_fallback_device_is_not_saved()
    {
        ConnectCall();
        _media.NextDevices = ([new("mic-2", "Built-in Mic")], [new("spk-1", "Speakers")]);
        Advance(2000);
        Assert.Equal("mic-2", _vm.MicrophoneId);
        Assert.Null(new SettingsStore(_settingsPath).Load().MicrophoneId);
    }

    [Fact]
    public async Task Presenting_counts_as_video_even_with_the_camera_off()
    {
        ConnectCall();
        Run(_vm.ToggleCamera);
        await _vm.ShareFileAsync("/videos/demo clip.mp4");
        _dispatcher.RunPending();
        Assert.Equal(Presentation.File, _vm.Sharing);
        Assert.Equal("demo clip", _vm.SharingTitle);
        Assert.Equal("audio=True video=True screen=True", State(MediaStates()[^1]));

        // The camera stays off while presenting, even after the 300 ms timer.
        Advance(300);
        var log = _media.Log.Count;
        Run(_vm.ToggleCamera);
        Assert.Equal(log, _media.Log.Count);

        // The file ended on its own.
        Run(() => _media.RaisePresentation(Presentation.File, CaptureState.Stopped));
        Assert.Equal(Presentation.None, _vm.Sharing);
        Assert.Equal("audio=True video=False screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public async Task A_file_that_cannot_play_is_reported()
    {
        ConnectCall();
        await _vm.ShareFileAsync("/videos/broken.mp4");
        Assert.Equal(Presentation.None, _vm.Sharing);
        Assert.Equal("Couldn't play that video", _toasts[^1].Text);
    }

    [Fact]
    public void Without_a_camera_the_call_still_works()
    {
        _media.LocalResult = new LocalMediaResult(true, false);
        _media.Cameras = [];
        Join();
        Assert.False(_vm.CameraOn);
        Assert.Equal("No camera found", _toasts[^1].Text);
        Run(_vm.ToggleCamera);
        Assert.Equal("No camera available", _toasts[^1].Text);

        Receive(PeerJoined);
        Run(() => _media.RaiseConnection(PeerConnectionState.Connected));
        Assert.Equal("audio=True video=False screen=False", State(MediaStates()[^1]));
    }

    [Fact]
    public void A_camera_another_app_had_at_the_start_opens_when_turned_on()
    {
        _media.LocalResult = new LocalMediaResult(true, false);
        _media.CameraOpens = false;
        ConnectCall();
        Assert.False(_vm.HasCamera);

        Run(_vm.ToggleCamera);
        Assert.Equal("SelectCamera(cam-1)", _media.Log[^1]);
        Assert.Equal("Couldn't open the camera. Another app may be using it.", _toasts[^1].Text);
        Assert.False(_vm.CameraOn);

        _media.CameraOpens = true;
        Run(_vm.ToggleCamera);
        Assert.True(_vm.HasCamera);
        Assert.True(_vm.CameraOn);
        Assert.Contains("Camera(True)", _media.Log);
        Advance(300);
        Assert.Equal("audio=True video=True screen=False", State(MediaStates()[^1]));
    }

    // ---- Remote video ---------------------------------------------------------------------------

    [Fact]
    public void Placeholder_follows_remote_state_and_local_hide()
    {
        ConnectCall();
        Assert.False(_vm.ShowRemotePlaceholder);

        Receive(MediaState(false, false, false));
        Assert.True(_vm.ShowRemotePlaceholder);
        Assert.Equal("Camera is off", _vm.PlaceholderTitle);
        Assert.Equal("Microphone muted", _vm.PlaceholderSubtitle);

        Receive(MediaState(true, true, false));
        Assert.False(_vm.ShowRemotePlaceholder);
        Run(_vm.ToggleRemoteVideo);
        Assert.True(_vm.ShowRemotePlaceholder);
        Assert.Equal("You hid their video", _vm.PlaceholderTitle);
        Assert.Null(_vm.PlaceholderSubtitle);
        Assert.Equal("RemoteVideo(False)", _media.Log[^1]);
    }

    [Fact]
    public void Mic_level_follows_the_peak_and_decays_while_waiting_and_in_the_call()
    {
        _media.MicPeak = 1;
        Join();
        Advance(80);
        Assert.Equal(1, _vm.MicLevel);

        _media.MicPeak = 0;
        Advance(80);
        Assert.Equal(0.75, _vm.MicLevel);
        _media.MicPeak = null;
        Advance(80);
        Assert.Equal(0.5625, _vm.MicLevel);

        Receive(PeerJoined);
        Run(() => _media.RaiseConnection(PeerConnectionState.Connected));
        _media.MicPeak = 0.1;
        Advance(80);
        Assert.Equal(0.75, _vm.MicLevel, 6);
    }

    [Fact]
    public void Mic_level_ignores_tiny_changes_and_snaps_to_silence()
    {
        _media.MicPeak = Math.Pow(10, -1.5); // -30 dBFS
        Join();
        Advance(80);
        Assert.Equal(0.5, _vm.MicLevel, 6);
        var changes = 0;
        _vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(CallViewModel.MicLevel) ? 1 : 0;

        _media.MicPeak = Math.Pow(10, -1.49);
        Advance(80);
        Assert.Equal(0.5, _vm.MicLevel, 6);
        Assert.Equal(0, changes);

        _media.MicPeak = 0;
        for (var i = 0; i < 20; i++) Advance(80);
        Assert.Equal(0, _vm.MicLevel);
    }

    [Fact]
    public void Mic_level_is_zero_and_unpolled_while_muted()
    {
        _media.MicPeak = 1;
        Join();
        Advance(80);
        Assert.Equal(1, _vm.MicLevel);

        Run(_vm.ToggleMic);
        Assert.Equal(0, _vm.MicLevel);
        var requests = _media.MicPeakRequests;
        Advance(400);
        Assert.Equal(0, _vm.MicLevel);
        Assert.Equal(requests, _media.MicPeakRequests);

        Run(_vm.ToggleMic);
        Advance(80);
        Assert.Equal(1, _vm.MicLevel);
    }

    [Fact]
    public void A_mic_level_read_finishing_after_mute_is_dropped()
    {
        Join();
        _media.PendingMicPeak = new TaskCompletionSource<double?>();
        Advance(80);
        Advance(80);
        Assert.Equal(1, _media.MicPeakRequests);

        Run(_vm.ToggleMic);
        _media.PendingMicPeak.SetResult(1);
        _dispatcher.RunPending();
        Assert.Equal(0, _vm.MicLevel);
    }

    [Fact]
    public void Mic_level_stays_zero_without_a_microphone_and_stops_after_leaving()
    {
        _media.LocalResult = new(false, true);
        _media.MicPeak = 1;
        Join();
        Advance(400);
        Assert.Equal(0, _vm.MicLevel);
        Assert.Equal(0, _media.MicPeakRequests);

        Run(_vm.Leave);
        _media.LocalResult = new(true, true);
        Advance(400);
        Assert.Equal(0, _media.MicPeakRequests);
    }

    [Fact]
    public void Leaving_resets_the_mic_level()
    {
        _media.MicPeak = 1;
        Join();
        Advance(80);
        Run(_vm.Leave);
        Assert.Equal(0, _vm.MicLevel);
    }

    [Fact]
    public void Audio_level_is_polled_only_while_the_placeholder_shows()
    {
        ConnectCall();
        Advance(1000);
        Assert.Equal(0, _media.AudioLevelRequests);

        Receive(MediaState(true, false, false));
        Advance(250);
        Assert.Equal(1, _media.AudioLevelRequests);
        Assert.Equal(0.2, _vm.RemoteAudioLevel);

        Run(_vm.ToggleRemoteAudio);
        Advance(250);
        Assert.Equal(1, _media.AudioLevelRequests);
        Assert.Equal(0, _vm.RemoteAudioLevel);
    }

    [Fact]
    public void Remote_screen_share_switches_to_fit_unless_overridden()
    {
        ConnectCall();
        _vm.ReportRemoteLayout(1280, 720, 1600, 900);
        Assert.Equal(VideoFit.Fill, _vm.RemoteFit);
        Receive(MediaState(true, true, true));
        Assert.Equal(VideoFit.Fit, _vm.RemoteFit);
        _vm.ToggleFit();
        Assert.Equal(VideoFit.Fill, _vm.RemoteFit);
    }

    // ---- Chat -----------------------------------------------------------------------------------

    [Fact]
    public void Chat_counts_unread_and_sends_only_when_ready()
    {
        ConnectCall();
        Assert.False(_vm.SendMessage("hi"));
        Run(() => _media.RaiseChatState(DataChannelState.Open));
        _media.ChatOpen = true;

        Assert.False(_vm.SendMessage("   "));
        Assert.True(_vm.SendMessage("  hi  "));
        Assert.Equal(["hi"], _media.SentChat);

        Run(() => _media.RaiseChatMessage("hello"));
        Assert.Equal(1, _vm.Unread);
        _vm.ToggleChat();
        Assert.Equal(0, _vm.Unread);
        Run(() => _media.RaiseChatMessage("again"));
        Assert.Equal(0, _vm.Unread);

        Assert.Equal([(true, "hi"), (false, "hello"), (false, "again")], _vm.Messages.Select(m => (m.IsLocal, m.Text)));
    }

    [Fact]
    public void Chat_keeps_the_last_100_messages()
    {
        ConnectCall();
        Run(() => _media.RaiseChatState(DataChannelState.Open));
        for (var i = 0; i < 120; i++) _media.RaiseChatMessage($"m{i}");
        Assert.Equal(CallViewModel.MaxMessages, _vm.Messages.Count);
        Assert.Equal("m20", _vm.Messages[0].Text);
    }

    // ---- Devices --------------------------------------------------------------------------------

    [Fact]
    public async Task Switching_cameras_cycles_and_is_remembered()
    {
        Join();
        await _vm.SwitchCameraAsync();
        Assert.Equal("cam-2", _vm.CameraId);
        Assert.Equal("USB", _toasts[^1].Text);
        Assert.Equal("cam-2", new SettingsStore(_settingsPath).Load().CameraId);
        await _vm.SwitchCameraAsync();
        Assert.Equal("cam-1", _vm.CameraId);
    }

    [Fact]
    public void Effects_are_saved_applied_and_combined()
    {
        Join();
        Assert.True(_vm.CanPickEffects);
        _vm.SetBackground("blur-strong");
        _dispatcher.RunPending();
        _vm.SetSticker("crown");
        _dispatcher.RunPending();
        Assert.Equal(new EffectsSelection("blur-strong", "crown"), _media.AppliedEffects);
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
        var saved = new SettingsStore(_settingsPath).Load();
        Assert.Equal(("blur-strong", "crown"), (saved.EffectsBackground, saved.EffectsSticker));

        _vm.SetEffects(EffectsSelection.Off);
        _dispatcher.RunPending();
        Assert.Equal(EffectsStatus.Off, _vm.EffectsStatus);
        Assert.Null(new SettingsStore(_settingsPath).Load().EffectsBackground);
    }

    [Fact]
    public void A_saved_effect_holds_the_camera_until_it_is_applied()
    {
        new SettingsStore(_settingsPath).Save(new AppSettings { EffectsBackground = "blur-light", EffectsSticker = "gone" });
        Join();
        _dispatcher.RunPending();
        // The vanished sticker is dropped; the hold comes before the camera opens.
        Assert.Equal(new EffectsSelection("blur-light"), _vm.Effects);
        Assert.True(_media.Log.IndexOf("HoldEffects") < _media.Log.IndexOf("StartLocalMedia"));
        Assert.Contains("SetEffects(blur-light,)", _media.Log);
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
    }

    [Fact]
    public void No_saved_effect_means_no_hold()
    {
        Join();
        _dispatcher.RunPending();
        Assert.DoesNotContain("HoldEffects", _media.Log);
        Assert.DoesNotContain(_media.Log, l => l.StartsWith("SetEffects", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_effect_reverts_to_the_previous_one_with_a_toast()
    {
        Join();
        _vm.SetBackground("blur-light");
        _dispatcher.RunPending();
        _media.BrokenEffects.Add("blur-strong");
        _vm.SetBackground("blur-strong");
        _dispatcher.RunPending();
        Assert.Equal("Couldn't load that effect", _toasts[^1].Text);
        Assert.Equal("Couldn't load that effect", _vm.EffectsError);
        Assert.Equal(new EffectsSelection("blur-light"), _vm.Effects);
        Assert.Equal(new EffectsSelection("blur-light"), _media.AppliedEffects);
        Assert.Equal("blur-light", new SettingsStore(_settingsPath).Load().EffectsBackground);
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
    }

    [Fact]
    public void A_failed_first_effect_falls_back_to_none()
    {
        Join();
        _media.BrokenEffects.Add("blur-strong");
        _vm.SetBackground("blur-strong");
        _dispatcher.RunPending();
        Assert.Equal(EffectsSelection.Off, _vm.Effects);
        Assert.Equal(EffectsStatus.Off, _vm.EffectsStatus);

        // With the panel open the info bar says it instead of a toast; the next pick clears it.
        var toasts = _toasts.Count;
        _vm.SetEffectsOpen(true);
        _vm.SetBackground("blur-strong");
        _dispatcher.RunPending();
        Assert.Equal(toasts, _toasts.Count);
        Assert.NotNull(_vm.EffectsError);
        _media.BrokenEffects.Clear();
        _vm.SetBackground("blur-light");
        _dispatcher.RunPending();
        Assert.Null(_vm.EffectsError);
    }

    private int EffectsCalls(string selection) => _media.Log.Count(l => l == $"SetEffects({selection})");

    [Fact]
    public void A_model_failure_while_drawing_reloads_the_same_effect_once()
    {
        ConnectCall();
        Run(() => _vm.SetBackground("blur-strong"));
        var toasts = _toasts.Count;
        Run(_media.RaiseEffectsFailed);

        // Reloaded quietly (the media layer recreates the model on the CPU); never cleared to the raw camera.
        Assert.Equal(2, EffectsCalls("blur-strong,"));
        Assert.Equal(0, EffectsCalls("none,"));
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
        Assert.Equal(toasts, _toasts.Count);
        Assert.True(_vm.CameraOn);
        Assert.Equal("blur-strong", new SettingsStore(_settingsPath).Load().EffectsBackground);
    }

    [Fact]
    public void A_second_failure_turns_the_camera_off_and_keeps_the_choice()
    {
        ConnectCall();
        Run(() => _vm.SetBackground("blur-strong"));
        Run(_media.RaiseEffectsFailed);
        Run(_media.RaiseEffectsFailed);

        Assert.Equal("Couldn't load that effect", _toasts[^1].Text);
        Assert.False(_vm.CameraOn);
        Assert.Equal(EffectsStatus.Off, _vm.EffectsStatus);
        Assert.Equal(new EffectsSelection("blur-strong"), _vm.Effects);
        Assert.Equal("blur-strong", new SettingsStore(_settingsPath).Load().EffectsBackground);
        Assert.Equal(0, EffectsCalls("none,"));
        // The normal camera-off path: the peer is told first, the camera closes 300 ms later.
        Assert.Equal("audio=True video=False screen=False", State(MediaStates()[^1]));
        Assert.DoesNotContain("Camera(False)", _media.Log);
        Advance(300);
        Assert.Contains("Camera(False)", _media.Log);

        // Turning the camera back on loads the effect before the camera starts.
        Run(_vm.ToggleCamera);
        Assert.True(_media.Log.LastIndexOf("SetEffects(blur-strong,)") < _media.Log.LastIndexOf("Camera(True)"));
        Assert.Equal(3, EffectsCalls("blur-strong,"));
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
    }

    [Fact]
    public void A_reload_that_cannot_load_turns_the_camera_off_instead_of_reverting()
    {
        ConnectCall();
        Run(() => _vm.SetBackground("blur-light"));
        Run(() => _vm.SetBackground("blur-strong"));
        _media.BrokenEffects.Add("blur-strong");
        Run(_media.RaiseEffectsFailed);

        Assert.False(_vm.CameraOn);
        Assert.Equal(new EffectsSelection("blur-strong"), _vm.Effects);
        // No revert to the previous pick (that is for picks that never loaded).
        Assert.Equal(1, EffectsCalls("blur-light,"));
        Assert.Equal("Couldn't load that effect", _vm.EffectsError);
    }

    [Fact]
    public void A_new_pick_after_a_failure_gets_its_own_retry()
    {
        ConnectCall();
        Run(() => _vm.SetBackground("blur-strong"));
        Run(_media.RaiseEffectsFailed);
        Run(() => _vm.SetBackground("blur-light"));
        Run(_media.RaiseEffectsFailed);
        Assert.True(_vm.CameraOn);
        Assert.Equal(2, EffectsCalls("blur-light,"));
    }

    [Fact]
    public void A_failure_while_a_newer_pick_loads_is_left_to_that_pick()
    {
        ConnectCall();
        Run(() => _vm.SetBackground("blur-strong"));
        _media.PendingEffects = new TaskCompletionSource();
        Run(() => _vm.SetBackground("blur-light"));
        Run(_media.RaiseEffectsFailed);
        Assert.Equal(1, EffectsCalls("blur-strong,"));
        var pending = _media.PendingEffects;
        _media.PendingEffects = null;
        pending.SetResult();
        _dispatcher.RunPending();
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
        Assert.True(_vm.CameraOn);
    }

    [Fact]
    public void Only_the_latest_pick_is_kept()
    {
        Join();
        _media.PendingEffects = new TaskCompletionSource();
        _vm.SetBackground("blur-light");
        Assert.Equal(EffectsStatus.Loading, _vm.EffectsStatus);
        _vm.SetBackground("blur-strong");
        var pending = _media.PendingEffects;
        _media.PendingEffects = null;
        pending.SetResult();
        _dispatcher.RunPending();
        Assert.Equal(new EffectsSelection("blur-strong"), _vm.Effects);
        Assert.Equal(EffectsStatus.On, _vm.EffectsStatus);
    }

    [Fact]
    public async Task The_effects_panel_shares_the_chat_slot_and_is_off_while_presenting()
    {
        Join();
        _vm.ToggleEffects();
        Assert.True(_vm.EffectsOpen);
        _vm.ToggleChat();
        Assert.True(_vm.ChatOpen);
        Assert.False(_vm.EffectsOpen);
        _vm.ToggleEffects();
        Assert.False(_vm.ChatOpen);
        _vm.ToggleEffects();
        Assert.False(_vm.EffectsOpen);

        // Starting to present closes an open panel.
        _vm.ToggleEffects();
        Assert.True(_vm.EffectsOpen);
        await _vm.ShareFileAsync("clip.mp4");
        Assert.False(_vm.EffectsOpen);
        Assert.False(_vm.CanOpenEffects);
        Assert.False(_vm.CanPickEffects);
        _vm.ToggleEffects();
        Assert.False(_vm.EffectsOpen);
        _vm.SetBackground("blur-strong");
        Assert.Equal(EffectsSelection.Off, _vm.Effects);
    }

    [Fact]
    public async Task Opening_effects_like_the_B_key_never_closes_the_panel()
    {
        Join();
        _vm.OpenEffects();
        Assert.True(_vm.EffectsOpen);
        _vm.OpenEffects();
        Assert.True(_vm.EffectsOpen);
        _vm.SetEffectsOpen(false);
        Assert.False(_vm.EffectsOpen);

        _vm.ToggleChat();
        _vm.OpenEffects();
        Assert.True(_vm.EffectsOpen);
        Assert.False(_vm.ChatOpen);

        await _vm.ShareFileAsync("clip.mp4");
        _vm.OpenEffects();
        Assert.False(_vm.EffectsOpen);
    }

    [Fact]
    public void Effects_need_a_camera_and_the_models()
    {
        _media.LocalResult = new LocalMediaResult(true, false);
        Join();
        _dispatcher.RunPending();
        Assert.True(_vm.CanOpenEffects);
        Assert.False(_vm.CanPickEffects);

        _media.SupportsEffects = false;
        _vm.ToggleEffects();
        Assert.Equal("Backgrounds and effects aren't available", _toasts[^1].Text);
    }
}