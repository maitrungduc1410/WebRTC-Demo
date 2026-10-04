using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Group;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Core.Tests.Fakes;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests;

/// <summary>Group calls through the view model, with a fake SFU socket and fake media.</summary>
public sealed class GroupCallTests : IDisposable
{
    private const string Room = "777777";
    private const string Sfu = "ws://localhost:4001";
    private const string Me = "me123456";

    private readonly ManualDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSocketFactory _signalingSockets = new();
    private readonly FakeSocketFactory _sockets = new();
    private readonly SignalingClient _signaling;
    private readonly GroupCallClient _group;
    private readonly FakeCallMedia _media = new();
    private readonly CallViewModel _vm;
    private readonly List<Toast> _toasts = [];
    private int _ended;

    public GroupCallTests()
    {
        _signaling = new SignalingClient(_signalingSockets.Create, _dispatcher);
        _group = new GroupCallClient(_sockets.Create, _media, _dispatcher);
        _vm = new CallViewModel(_signaling, _media, _dispatcher, null, _time, _group);
        _vm.ToastRequested += _toasts.Add;
        _vm.Ended += () => _ended++;
    }

    public void Dispose()
    {
        _vm.Dispose();
        _group.Dispose();
        _signaling.Dispose();
        _dispatcher.Dispose();
    }

    private FakeMessageSocket Socket => _sockets.Last;

    private void Receive(string json)
    {
        Socket.Receive(json);
        _dispatcher.RunPending();
    }

    private void Run(Action action)
    {
        action();
        _dispatcher.RunPending();
    }

    private static string Participant(string id, string name, bool audio = true, bool video = true, bool screen = false) =>
        $$$"""{"id":"{{{id}}}","name":"{{{name}}}","state":{"audio":{{{Bool(audio)}}},"video":{{{Bool(video)}}},"screen":{{{Bool(screen)}}}}}""";

    private static string Bool(bool value) => value ? "true" : "false";

    private void Open(bool e2ee = false)
    {
        _vm.JoinGroup(Room, e2ee, Sfu);
        Socket.Open();
        _dispatcher.RunPending();
    }

    private void Joined(string participants = "", string? key = null) =>
        Receive($$"""{"type":"joined","participantId":"{{Me}}","e2ee":{{Bool(key != null)}},"e2eeKey":"{{key ?? ""}}","participants":[{{participants}}]}""");

    private JsonElement Sent(string type) => Socket.Messages.Last(m => m.GetProperty("type").GetString() == type);

    [Fact]
    public void Joins_the_sfu_as_Windows_and_publishes_after_media()
    {
        Open();
        Assert.True(_vm.IsGroup);
        Assert.Equal(Sfu, _vm.ServerUrl);
        Assert.Equal(new Uri("ws://localhost:4001/ws"), Socket.Url);
        Assert.Empty(_signalingSockets.Created);
        Assert.Equal([$$"""{"type":"join","roomId":"{{Room}}","name":"Windows","e2ee":false}"""], Socket.Sent);
        Assert.Equal(CallPhase.Connecting, _vm.Phase);
        Assert.False(_vm.CanToggleFit);
        Assert.Equal("Mute everyone", _vm.RemoteAudioText);
        Assert.Equal("Hide everyone's video", _vm.RemoteVideoText);

        Joined();
        Assert.Equal(Me, _vm.SelfId);
        Assert.Equal("Windows · me12", _vm.SelfLabel);
        Assert.Equal(CallPhase.Waiting, _vm.Phase);
        Assert.Equal(1, _vm.PeopleCount);
        Assert.True(_vm.ChatReady);
        Assert.Equal(["StartLocalMedia", "OpenGroup(e2ee=False)", "CreatePublishOffer"], _media.Log);
        Assert.Equal(Me, _media.GroupStreamId);
        // Our state first, so others never see a wrong icon; then the offer.
        Assert.Equal(["join", "media state", "offer"], Socket.Types);
        Assert.Equal("""{"type":"offer","pc":"publish","sdp":"v=0 publish"}""", Socket.Sent[^1]);

        Receive("""{"type":"answer","pc":"publish","sdp":"v=0 sfu"}""");
        Assert.Equal("SetPublishAnswer(v=0 sfu)", _media.Log[^1]);
    }

    [Fact]
    public void Mic_level_waits_for_the_publish_connection()
    {
        _media.MicPeak = 1;
        Open();
        _time.Advance(TimeSpan.FromMilliseconds(400));
        _dispatcher.RunPending();
        Assert.Equal(0, _media.MicPeakRequests);
        Assert.Equal(0, _vm.MicLevel);

        Joined();
        _time.Advance(CallViewModel.MicLevelInterval);
        _dispatcher.RunPending();
        Assert.Equal(1, _media.MicPeakRequests);
        Assert.Equal(1, _vm.MicLevel);
    }

    [Fact]
    public void With_e2ee_the_rooms_key_is_set_before_the_connections_open()
    {
        Open(e2ee: true);
        var join = Socket.Messages[0];
        Assert.True(join.GetProperty("e2ee").GetBoolean());
        Assert.Equal(GroupCallClient.KeyLength, Convert.FromBase64String(join.GetProperty("e2eeKey").GetString()!).Length);

        // The room's creator chose the key; ours is ignored if we are not first.
        var roomKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Joined(key: Convert.ToBase64String(roomKey));
        Assert.Equal(roomKey, _media.Key);
        Assert.Equal(["StartLocalMedia", "SetEncryptionKey", "OpenGroup(e2ee=True)", "CreatePublishOffer"], _media.Log);
    }

    [Fact]
    public void An_e2ee_room_without_a_key_ends_the_call()
    {
        Open(e2ee: true);
        Receive($$"""{"type":"joined","participantId":"{{Me}}","e2ee":true,"participants":[]}""");
        Assert.Equal(1, _ended);
        Assert.Equal("The server did not send an encryption key for this room.", _toasts[^1].Text);
        Assert.Equal("leave", Socket.Types[^1]);
        Assert.True(Socket.IsClosed);
    }

    [Fact]
    public void Subscribe_offers_wait_for_the_connections_and_are_answered_one_at_a_time()
    {
        Open();
        // The SFU can offer before `joined` is handled and before our media is ready.
        Receive("""{"type":"offer","pc":"subscribe","sdp":"first"}""");
        Assert.DoesNotContain(_media.Log, l => l.StartsWith("AnswerSubscribe", StringComparison.Ordinal));

        var slow = new TaskCompletionSource();
        _media.PendingSubscribeAnswer = slow;
        Joined();
        Assert.Contains("AnswerSubscribe(first)", _media.Log);
        Receive("""{"type":"offer","pc":"subscribe","sdp":"second"}""");
        Assert.DoesNotContain("AnswerSubscribe(second)", _media.Log);

        Run(slow.SetResult);
        _dispatcher.RunUntil(() => _media.Log.Contains("AnswerSubscribe(second)"));
        _dispatcher.RunUntil(() => Socket.Sent.Count(m => m.Contains("\"pc\":\"subscribe\"", StringComparison.Ordinal)) == 2);
        Assert.Equal(
            ["""{"type":"answer","pc":"subscribe","sdp":"answer to first"}""", """{"type":"answer","pc":"subscribe","sdp":"answer to second"}"""],
            Socket.Sent.Where(m => m.Contains("\"type\":\"answer\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Candidates_carry_their_connection()
    {
        Open();
        Joined();
        Run(() => _media.RaiseGroupCandidate(GroupConnection.Publish, new IceCandidate("0", 0, "candidate:pub")));
        Run(() => _media.RaiseGroupCandidate(GroupConnection.Subscribe, new IceCandidate("1", 1, "candidate:sub")));
        Run(() => _media.RaiseGroupCandidate(GroupConnection.Subscribe, new IceCandidate("1", 1, "")));
        Assert.Equal(
            [
                """{"type":"candidate","pc":"publish","candidate":{"candidate":"candidate:pub","sdpMid":"0","sdpMLineIndex":0}}""",
                """{"type":"candidate","pc":"subscribe","candidate":{"candidate":"candidate:sub","sdpMid":"1","sdpMLineIndex":1}}""",
            ],
            Socket.Sent.Where(m => m.Contains("\"type\":\"candidate\"", StringComparison.Ordinal)));

        // A remote candidate for the subscribe connection waits for its first offer.
        Receive("""{"type":"candidate","pc":"subscribe","candidate":{"candidate":"candidate:early","sdpMid":"0","sdpMLineIndex":0}}""");
        Assert.Empty(_media.AddedGroupCandidates);
        Receive("""{"type":"offer","pc":"subscribe","sdp":"v=0"}""");
        _dispatcher.RunUntil(() => _media.AddedGroupCandidates.Count == 1);
        Assert.Equal((GroupConnection.Subscribe, new IceCandidate("0", 0, "candidate:early")), _media.AddedGroupCandidates[0]);
    }

    [Fact]
    public void Publish_candidates_wait_for_the_publish_answer()
    {
        Open();
        Joined();
        // The offer is out but not answered yet.
        Receive("""{"type":"candidate","pc":"publish","candidate":{"candidate":"candidate:pub","sdpMid":"0","sdpMLineIndex":0}}""");
        Assert.Empty(_media.AddedGroupCandidates);
        Receive("""{"type":"answer","pc":"publish","sdp":"v=0 sfu"}""");
        _dispatcher.RunUntil(() => _media.AddedGroupCandidates.Count == 1);
        Assert.Equal((GroupConnection.Publish, new IceCandidate("0", 0, "candidate:pub")), _media.AddedGroupCandidates[0]);
        Receive("""{"type":"candidate","pc":"publish","candidate":{"candidate":"candidate:late","sdpMid":"0","sdpMLineIndex":0}}""");
        Assert.Equal(2, _media.AddedGroupCandidates.Count);
    }

    [Fact]
    public void A_subscribe_offer_that_cannot_be_answered_ends_the_call()
    {
        // The SFU holds every later offer until this one is answered.
        Open();
        Joined();
        _media.FailSubscribeAnswer = true;
        Receive("""{"type":"offer","pc":"subscribe","sdp":"bad"}""");
        _dispatcher.RunUntil(() => _ended == 1);
        Assert.Equal(new Toast("Couldn't receive the others' audio and video from the group call server.", ToastKind.Error, Glyphs.Warning), _toasts[^1]);
        Assert.Equal("leave", Socket.Types[^1]);
        Assert.False(_vm.InRoom);
    }

    [Fact]
    public void Participants_come_and_go_with_toasts()
    {
        Open();
        Joined(Participant("web11111", "Web", audio: false) + "," + Participant(Me, "Windows"));
        var web = Assert.Single(_vm.Participants);
        Assert.Equal("Web · web1", web.Label);
        Assert.True(web.MicOff);
        Assert.Equal(CallPhase.Connected, _vm.Phase);
        Assert.NotNull(_vm.ConnectedSince);
        Assert.Empty(_toasts);

        Receive($$"""{"type":"participant joined","participant":{{Participant("and22222", "Android", screen: true)}}}""");
        Assert.Equal(2, _vm.Participants.Count);
        Assert.Equal(3, _vm.PeopleCount);
        Assert.Equal(new Toast("Android · and2 joined", ToastKind.Info, Glyphs.PersonJoined), _toasts[^1]);
        Assert.True(_vm.Participants[1].Fit);
        // Ours, echoed back, is not someone else.
        Receive($$"""{"type":"participant joined","participant":{{Participant(Me, "Windows")}}}""");
        Assert.Equal(2, _vm.Participants.Count);

        Receive("""{"type":"media state","participantId":"web11111","state":{"audio":true,"video":false,"screen":false}}""");
        Assert.False(web.MicOff);
        Assert.False(web.State.Video);

        Receive("""{"type":"participant left","participantId":"web11111"}""");
        Receive("""{"type":"participant left","participantId":"and22222"}""");
        Assert.Equal("Android · and2 left", _toasts[^1].Text);
        Assert.Equal(CallPhase.Waiting, _vm.Phase);
        Assert.Equal(0, _ended);
    }

    [Fact]
    public void Video_that_arrives_before_its_participant_is_kept()
    {
        Open();
        Joined();
        var feed = new FakeVideoFeed();
        Run(() => _media.RaiseParticipantVideo("web11111", feed));
        Receive($$"""{"type":"participant joined","participant":{{Participant("web11111", "Web")}}}""");
        var web = Assert.Single(_vm.Participants);
        Assert.Same(feed, web.Video);
        Assert.True(web.ShowVideo);
        Assert.Same(web, _vm.FeaturedParticipant);

        Run(() => _media.RaiseParticipantVideo("web11111", null));
        Assert.Null(web.Video);
        Assert.False(web.ShowVideo);
    }

    [Fact]
    public void Chat_goes_over_the_socket_with_sender_names()
    {
        Open();
        Joined();
        Assert.True(_vm.SendMessage("  hello all "));
        Assert.Equal("""{"type":"chat","text":"hello all"}""", Socket.Sent[^1]);
        Assert.Empty(_media.SentChat);
        var mine = Assert.Single(_vm.Messages);
        Assert.True(mine.IsLocal);

        Receive("""{"type":"chat","participantId":"web11111","name":"Web","text":"hi Windows"}""");
        var theirs = _vm.Messages[^1];
        Assert.False(theirs.IsLocal);
        Assert.Equal("hi Windows", theirs.Text);
        Assert.Equal("Web · web1", theirs.Sender);
    }

    [Fact]
    public void Mute_everyone_and_hide_everyone_cover_later_joiners()
    {
        Open();
        Joined(Participant("web11111", "Web"));
        _vm.ToggleRemoteAudio();
        _vm.ToggleRemoteVideo();
        Assert.Equal("Unmute everyone", _vm.RemoteAudioText);
        Assert.Equal("Show everyone's video", _vm.RemoteVideoText);
        Assert.False(_media.RemoteAudioEnabled);
        Assert.False(_media.RemoteVideoEnabled);
        Assert.True(_vm.Participants[0].VideoHidden);

        Receive($$"""{"type":"participant joined","participant":{{Participant("and22222", "Android")}}}""");
        Assert.True(_vm.Participants[1].VideoHidden);
        Assert.Equal("You hid their video", _vm.Participants[1].PlaceholderTitle);
        // Local only: nothing is sent.
        Assert.DoesNotContain(Socket.Types, t => t is "mute" or "hide");

        _vm.ToggleRemoteVideo();
        Assert.All(_vm.Participants, p => Assert.False(p.VideoHidden));
    }

    [Fact]
    public void An_unplugged_microphone_keeps_the_publish_connection()
    {
        Open();
        Joined();
        Receive("""{"type":"answer","pc":"publish","sdp":"v=0 sfu"}""");
        var log = _media.Log.Count;
        _media.NextDevices = ([new("mic-2", "Built-in Mic")], [new("spk-1", "Speakers")]);
        _time.Advance(CallViewModel.DeviceCheckInterval);
        _dispatcher.RunPending();
        Assert.Equal("mic-2", _vm.MicrophoneId);
        Assert.Equal(new Toast("Switched to Built-in Mic", ToastKind.Info, Glyphs.Mic), _toasts[^1]);
        Assert.Equal(log, _media.Log.Count);
        Assert.Equal(["join", "media state", "offer"], Socket.Types);

        _media.NextDevices = ([], [new("spk-1", "Speakers")]);
        _time.Advance(CallViewModel.DeviceCheckInterval);
        _dispatcher.RunPending();
        Assert.False(_vm.MicOn);
        Assert.Equal("""{"audio":false,"video":true,"screen":false}""", Sent("media state").GetProperty("state").GetRawText());
    }

    [Fact]
    public void Local_toggles_send_media_state_and_the_camera_waits_300_ms()
    {
        Open();
        Joined();
        _vm.ToggleMic();
        Assert.Equal("""{"audio":false,"video":true,"screen":false}""", Sent("media state").GetProperty("state").GetRawText());

        _vm.ToggleCamera();
        Assert.Equal("""{"audio":false,"video":false,"screen":false}""", Sent("media state").GetProperty("state").GetRawText());
        // Others see the avatar before the last frame freezes on their side.
        Assert.True(_media.CameraEnabled);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _dispatcher.RunUntil(() => !_media.CameraEnabled);
    }

    [Fact]
    public void F_does_nothing_in_a_group_call()
    {
        Open();
        Joined(Participant("web11111", "Web"));
        var fit = _vm.Participants[0].Fit;
        _vm.ToggleFit();
        Assert.Equal(fit, _vm.Participants[0].Fit);
        _vm.Participants[0].ToggleFit();
        Assert.NotEqual(fit, _vm.Participants[0].Fit);
    }

    [Fact]
    public void The_loudest_participant_is_the_active_speaker()
    {
        Open();
        Joined(Participant("web11111", "Web") + "," + Participant("and22222", "Android", audio: false));
        _media.ParticipantLevels["web11111"] = 0.4;
        _media.ParticipantLevels["and22222"] = 0.9;
        _time.Advance(CallViewModel.GroupAudioLevelInterval);
        _dispatcher.RunUntil(() => _vm.ActiveSpeakerId != null);
        // Android's mic is off; its receiver's level does not count.
        Assert.Equal("web11111", _vm.ActiveSpeakerId);
        Assert.True(_vm.Participants[0].Speaking);
        Assert.Equal(0.4, _vm.Participants[0].AudioLevel);
        Assert.Equal(0, _vm.Participants[1].AudioLevel);
        Assert.Same(_vm.Participants[0], _vm.FeaturedParticipant);

        // Muting everyone stops the ring.
        _vm.ToggleRemoteAudio();
        _time.Advance(CallViewModel.GroupAudioLevelInterval);
        _dispatcher.RunUntil(() => _vm.ActiveSpeakerId == null);
        Assert.False(_vm.Participants[0].Speaking);

        _vm.ToggleRemoteAudio();
        _time.Advance(CallViewModel.GroupAudioLevelInterval);
        _dispatcher.RunUntil(() => _vm.ActiveSpeakerId == "web11111");
        Receive("""{"type":"participant left","participantId":"web11111"}""");
        Assert.Null(_vm.ActiveSpeakerId);
    }

    [Theory]
    [InlineData("Room is full", false, "That group call is full.")]
    [InlineData("E2EE setting does not match the room", false, "That group call uses end-to-end encryption. Turn it on to join.")]
    [InlineData("E2EE setting does not match the room", true, "That group call doesn't use end-to-end encryption. Turn it off to join.")]
    [InlineData("Server error", false, "Server error")]
    public void Fatal_errors_end_the_call(string message, bool e2ee, string shown)
    {
        Open(e2ee);
        Receive($$"""{"type":"error","message":"{{message}}","fatal":true}""");
        Assert.Equal(1, _ended);
        Assert.False(_vm.InRoom);
        Assert.Equal(new Toast(shown, ToastKind.Error, Glyphs.Warning), _toasts[^1]);
        Assert.Contains("StopLocalMedia", _media.Log);
        Socket.Drop();
        _dispatcher.RunPending();
        Assert.Equal(1, _ended);
    }

    [Fact]
    public void Non_fatal_errors_are_shown()
    {
        Open();
        Joined();
        Receive("""{"type":"error","message":"Unknown message","fatal":false}""");
        Assert.Equal(new Toast("Unknown message", ToastKind.Info, Glyphs.Info), _toasts[^1]);
        Assert.True(_vm.InRoom);
    }

    [Fact]
    public void A_closed_socket_ends_the_call()
    {
        Open();
        Joined(Participant("web11111", "Web"));
        Socket.Drop();
        _dispatcher.RunPending();
        Assert.Equal(1, _ended);
        Assert.Equal("Lost the connection to the group call server.", _toasts[^1].Text);
        Assert.Contains("CloseGroup", _media.Log);
        Assert.Empty(_vm.Participants);
        Assert.Null(_vm.SelfId);
    }

    [Fact]
    public void An_unreachable_sfu_ends_the_call()
    {
        _vm.JoinGroup(Room, false, Sfu);
        Socket.Drop(wasOpen: false);
        _dispatcher.RunPending();
        Assert.Equal(1, _ended);
        Assert.Equal("Can't reach the group call server.", _toasts[^1].Text);
    }

    [Fact]
    public void A_failed_media_connection_ends_the_call()
    {
        Open();
        Joined();
        Run(() => _media.RaiseGroupConnection(GroupConnection.Subscribe, PeerConnectionState.Disconnected));
        Assert.True(_vm.InRoom);
        Run(() => _media.RaiseGroupConnection(GroupConnection.Subscribe, PeerConnectionState.Failed));
        Assert.Equal(1, _ended);
        Assert.Equal("Couldn't connect the media to the group call server.", _toasts[^1].Text);
    }

    [Fact]
    public void Leaving_tells_the_sfu_and_a_new_call_starts_clean()
    {
        Open();
        Joined(Participant("web11111", "Web"));
        _vm.Leave();
        Assert.Equal("leave", Socket.Types[^1]);
        Assert.True(Socket.IsClosed);
        Assert.Equal(1, _ended);
        Assert.Empty(_vm.Participants);

        // Nothing from the old socket reaches the next call.
        var old = Socket;
        _vm.JoinGroup(Room, false, Sfu);
        old.Receive($$"""{"type":"participant joined","participant":{{Participant("ghost999", "Ghost")}}}""");
        _dispatcher.RunPending();
        Assert.Empty(_vm.Participants);
        Assert.NotSame(old, Socket);
    }

    [Fact]
    public void The_1_to_1_call_does_not_touch_the_sfu()
    {
        _vm.Join(Room, false, ServerAddress.DefaultUrl);
        Assert.False(_vm.IsGroup);
        Assert.Empty(_sockets.Created);
        Assert.Single(_signalingSockets.Created);
        Assert.Equal("Mute their audio", _vm.RemoteAudioText);
        Assert.True(_vm.CanToggleFit);
    }

    private sealed class FakeVideoFeed : IVideoFeed
    {
        public IDisposable Subscribe(VideoFrameHandler handler) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
