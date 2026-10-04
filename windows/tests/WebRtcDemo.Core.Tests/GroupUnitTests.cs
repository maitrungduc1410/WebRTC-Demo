using System.Text.Json;
using WebRtcDemo.Core.Group;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests;

public class SfuAddressTests
{
    [Theory]
    [InlineData("192.168.1.10", "ws://192.168.1.10:4001")]
    [InlineData("192.168.1.10:5000", "ws://192.168.1.10:5000")]
    [InlineData("  ws://Host.local:4001/ws  ", "ws://host.local:4001")]
    [InlineData("wss://sfu.example.com", "wss://sfu.example.com:4001")]
    [InlineData("wss://sfu.example.com:443", "wss://sfu.example.com:443")]
    [InlineData("http://10.0.0.2", "ws://10.0.0.2:4001")]
    [InlineData("http://10.0.0.2:80/path?q=1", "ws://10.0.0.2:80")]
    [InlineData("https://sfu.example.com/x", "wss://sfu.example.com:4001")]
    [InlineData("[::1]", "ws://[::1]:4001")]
    [InlineData("[::1]:4002", "ws://[::1]:4002")]
    [InlineData("localhost", "ws://localhost:4001")]
    [InlineData("ftp://example.com", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("ws://", null)]
    [InlineData("host:99999", null)]
    public void Normalizes_like_the_other_clients(string input, string? expected) =>
        Assert.Equal(expected, SfuAddress.Normalize(input));

    [Theory]
    [InlineData("http://192.168.1.10:4000", "ws://192.168.1.10:4001")]
    [InlineData("https://signal.example.com", "wss://signal.example.com:4001")]
    [InlineData("not a url", "ws://localhost:4001")]
    public void Defaults_to_4001_on_the_signaling_host(string signaling, string expected) =>
        Assert.Equal(expected, SfuAddress.DefaultFor(signaling));

    [Fact]
    public void Builds_socket_and_health_urls()
    {
        Assert.Equal(new Uri("ws://host:4001/ws"), SfuAddress.WebSocketUrl("ws://host:4001"));
        Assert.Equal(new Uri("wss://host:4001/ws"), SfuAddress.WebSocketUrl("wss://host:4001"));
        Assert.Equal(new Uri("http://host:4001/"), SfuAddress.HealthUrl("ws://host:4001"));
        Assert.Equal(new Uri("https://host:4001/"), SfuAddress.HealthUrl("wss://host:4001"));
        Assert.Equal(new Uri("ws://host:4000/ws"), ServerAddress.WebSocketUrl("http://host:4000"));
        Assert.Equal(new Uri("wss://host/ws"), ServerAddress.WebSocketUrl("https://host"));
        Assert.Equal(new Uri("http://host:4000/"), ServerAddress.HealthUrl("http://host:4000"));
    }
}

public class GroupProtocolTests
{
    private static GroupMessage? Parse(string json) => GroupMessage.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parses_joined_with_participants()
    {
        var message = Parse("""
            {"type":"joined","participantId":"me1","e2ee":true,"e2eeKey":"AAEC",
             "participants":[{"id":"p1","name":"Web","state":{"audio":false,"video":true,"screen":true}},
                             {"id":"","name":"broken"},{"id":"p2"}]}
            """);
        var joined = Assert.IsType<JoinedMessage>(message);
        Assert.Equal("me1", joined.ParticipantId);
        Assert.True(joined.E2ee);
        Assert.Equal("AAEC", joined.E2eeKey);
        Assert.Equal(
            [new ParticipantInfo("p1", "Web", new MediaState(false, true, true)), new ParticipantInfo("p2", "", MediaState.Default)],
            joined.Participants);
    }

    [Fact]
    public void Parses_the_rest()
    {
        Assert.Equal(new GroupOfferMessage(GroupConnection.Subscribe, "v=0"), Parse("""{"type":"offer","pc":"subscribe","sdp":"v=0"}"""));
        Assert.Equal(new GroupAnswerMessage(GroupConnection.Publish, "v=0"), Parse("""{"type":"answer","pc":"publish","sdp":"v=0"}"""));
        Assert.Equal(
            new GroupCandidateMessage(GroupConnection.Publish, new IceCandidateDto("candidate:1", "0", 0)),
            Parse("""{"type":"candidate","pc":"publish","candidate":{"candidate":"candidate:1","sdpMid":"0","sdpMLineIndex":0}}"""));
        Assert.Equal(
            new ParticipantJoinedMessage(new ParticipantInfo("p3", "Android", MediaState.Default)),
            Parse("""{"type":"participant joined","participant":{"id":"p3","name":"Android"}}"""));
        Assert.Equal(new ParticipantLeftMessage("p3"), Parse("""{"type":"participant left","participantId":"p3"}"""));
        Assert.Equal(
            new ParticipantStateMessage("p3", new MediaState(true, false, false)),
            Parse("""{"type":"media state","participantId":"p3","state":{"video":false}}"""));
        Assert.Equal(new ChatReceivedMessage("p3", "Android", "hi"), Parse("""{"type":"chat","participantId":"p3","name":"Android","text":"hi"}"""));
        Assert.Equal(new GroupErrorMessage(new ServerError("Room is full", true)), Parse("""{"type":"error","message":"Room is full","fatal":true}"""));
    }

    [Theory]
    [InlineData("""{"type":"offer","pc":"sideways","sdp":"v=0"}""")]
    [InlineData("""{"type":"offer","pc":"subscribe"}""")]
    [InlineData("""{"type":"joined"}""")]
    [InlineData("""{"type":"participant joined","participant":{"name":"x"}}""")]
    [InlineData("""{"type":"something new"}""")]
    [InlineData("""{"sdp":"v=0"}""")]
    public void Ignores_what_it_does_not_know(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Builds_what_the_server_expects()
    {
        Assert.Equal("""{"type":"join","roomId":"42","name":"Windows","e2ee":false}""", GroupMessages.Join("42", "Windows", false, null));
        Assert.Equal("""{"type":"join","roomId":"42","name":"Windows","e2ee":true,"e2eeKey":"AAEC"}""", GroupMessages.Join("42", "Windows", true, "AAEC"));
        Assert.Equal("""{"type":"offer","pc":"publish","sdp":"v=0"}""", GroupMessages.PublishOffer("v=0"));
        Assert.Equal("""{"type":"answer","pc":"subscribe","sdp":"v=0"}""", GroupMessages.SubscribeAnswer("v=0"));
        Assert.Equal(
            """{"type":"candidate","pc":"subscribe","candidate":{"candidate":"candidate:1","sdpMid":"1","sdpMLineIndex":1}}""",
            GroupMessages.Candidate(GroupConnection.Subscribe, new IceCandidateDto("candidate:1", "1", 1)));
        Assert.Equal("""{"type":"media state","state":{"audio":true,"video":false,"screen":false}}""", GroupMessages.MediaState(new MediaState(true, false, false)));
        Assert.Equal("hi \"you\"", JsonDocument.Parse(GroupMessages.Chat("hi \"you\"")).RootElement.GetProperty("text").GetString());
        Assert.Equal("""{"type":"leave"}""", GroupMessages.Leave());
    }
}

public class SdpMediaTests
{
    // What sfu-server (Pion) offers on the subscribe connection: two publishers, one of them gone
    // (its m-lines inactive, reusable), and a section without a=msid that names its stream in ssrc.
    private const string Offer = """
        v=0
        o=- 1 2 IN IP4 0.0.0.0
        s=-
        a=group:BUNDLE 0 1 2 3 4
        m=audio 9 UDP/TLS/RTP/SAVPF 111
        a=mid:0
        a=msid:alice alice-audio
        a=sendonly
        m=video 9 UDP/TLS/RTP/SAVPF 96
        a=mid:1
        a=msid:alice alice-video
        a=sendonly
        m=audio 9 UDP/TLS/RTP/SAVPF 111
        a=mid:2
        a=msid:bob bob-audio
        a=inactive
        m=video 0 UDP/TLS/RTP/SAVPF 96
        a=mid:3
        a=bundle-only
        a=ssrc:1234 cname:x
        a=ssrc:1234 msid:carol carol-video
        a=sendonly
        m=video 0 UDP/TLS/RTP/SAVPF 96
        a=mid:4
        a=msid:- dave-video
        a=sendonly
        """;

    [Fact]
    public void Parses_sections()
    {
        var sections = SdpMedia.Parse(Offer.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(
            [
                new SdpMediaSection(MediaKind.Audio, "0", "sendonly", false, "alice", "alice-audio"),
                new SdpMediaSection(MediaKind.Video, "1", "sendonly", false, "alice", "alice-video"),
                new SdpMediaSection(MediaKind.Audio, "2", "inactive", false, "bob", "bob-audio"),
                new SdpMediaSection(MediaKind.Video, "3", "sendonly", false, "carol", "carol-video"),
                new SdpMediaSection(MediaKind.Video, "4", "sendonly", true, null, null),
            ],
            sections);
        Assert.Equal([true, true, false, true, false], sections.Select(s => s.Sends));
    }

    [Fact]
    public void Maps_receivers_by_mid_to_the_publisher()
    {
        TransceiverInfo T(string mid, string receiver, MediaKind kind = MediaKind.Video) =>
            new(kind, TransceiverDirection.RecvOnly, TransceiverDirection.RecvOnly, mid, receiver);
        var map = ReceiverMap.Map(
            SdpMedia.Parse(Offer),
            [T("0", "r-a", MediaKind.Audio), T("1", "r-b"), T("2", "r-c", MediaKind.Audio), T("3", "r-d"), T("4", "r-e"), T("", "r-f"), T("9", "r-g")]);
        Assert.Equal(new Dictionary<string, string> { ["r-a"] = "alice", ["r-b"] = "alice", ["r-d"] = "carol" }, map);
    }

    [Fact]
    public void A_reused_mid_moves_its_receiver_to_the_new_publisher()
    {
        var transceivers = new[] { new TransceiverInfo(MediaKind.Audio, TransceiverDirection.RecvOnly, TransceiverDirection.RecvOnly, "2", "r-c") };
        Assert.Empty(ReceiverMap.Map(SdpMedia.Parse(Offer), transceivers));
        var reused = Offer.Replace("a=msid:bob bob-audio\na=inactive", "a=msid:erin erin-audio\na=sendonly", StringComparison.Ordinal);
        Assert.Equal("erin", ReceiverMap.Map(SdpMedia.Parse(reused), transceivers)["r-c"]);
    }
}

public class GroupLayoutTests
{
    [Theory]
    [InlineData(1, 1600, 900, 1, 1)]
    [InlineData(2, 1600, 900, 2, 1)]
    [InlineData(2, 600, 1000, 1, 2)]
    [InlineData(3, 1600, 900, 2, 2)]
    [InlineData(4, 1600, 900, 2, 2)]
    [InlineData(5, 1600, 900, 3, 2)]
    [InlineData(7, 1600, 900, 4, 2)]
    [InlineData(7, 1000, 1000, 3, 3)]
    [InlineData(9, 1600, 900, 3, 3)]
    [InlineData(10, 1600, 900, 4, 3)]
    [InlineData(0, 0, 0, 1, 1)]
    public void Picks_the_shape_with_the_largest_tiles(int count, double width, double height, int columns, int rows) =>
        Assert.Equal((columns, rows), GroupGrid.Shape(count, width, height, 12));

    [Fact]
    public void Centres_the_last_row()
    {
        var tiles = GroupGrid.Layout(3, 1000, 600, 10);
        Assert.Equal(new TileRect(0, 0, 495, 295), tiles[0]);
        Assert.Equal(new TileRect(505, 0, 495, 295), tiles[1]);
        Assert.Equal(new TileRect(252.5, 305, 495, 295), tiles[2]);
        Assert.Empty(GroupGrid.Layout(0, 1000, 600, 10));
    }

    [Fact]
    public void Tiles_never_overlap_or_leave_the_area()
    {
        for (var count = 1; count <= 12; count++)
        {
            var tiles = GroupGrid.Layout(count, 1280, 720, 12);
            Assert.Equal(count, tiles.Count);
            Assert.All(tiles, t => Assert.True(t.X >= 0 && t.Y >= 0 && t.X + t.Width <= 1280.001 && t.Y + t.Height <= 720.001));
            for (var i = 0; i < count; i++)
            {
                for (var j = i + 1; j < count; j++)
                {
                    var (a, b) = (tiles[i], tiles[j]);
                    Assert.False(a.X < b.X + b.Width - 0.001 && b.X < a.X + a.Width - 0.001 && a.Y < b.Y + b.Height - 0.001 && b.Y < a.Y + a.Height - 0.001);
                }
            }
        }
    }
}

public class TileFitTests
{
    [Fact]
    public void Fills_by_default_and_fits_while_presenting()
    {
        var fit = default(TileFit);
        Assert.False(fit.Fit);
        Assert.True(fit.SetPresenting(true));
        Assert.True(fit.Fit);
        Assert.False(fit.SetPresenting(true));
        Assert.True(fit.SetPresenting(false));
        Assert.False(fit.Fit);
    }

    [Fact]
    public void A_double_click_lasts_until_presenting_changes()
    {
        var fit = default(TileFit);
        fit.Toggle();
        Assert.True(fit.Fit);
        fit.Toggle();
        Assert.False(fit.Fit);
        fit.Toggle();

        // They start presenting: the override goes, and fit is what presenting wants anyway.
        Assert.False(fit.SetPresenting(true));
        Assert.True(fit.Fit);
        fit.Toggle();
        Assert.False(fit.Fit);
        Assert.False(fit.SetPresenting(true));
        Assert.False(fit.Fit);

        // They stop: back to fill, the override gone.
        Assert.False(fit.SetPresenting(false));
        Assert.False(fit.Fit);
        fit.Toggle();
        Assert.True(fit.SetPresenting(true) is false && fit.Fit);
    }
}

public class ActiveSpeakerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Picks_the_loudest_above_the_threshold_and_holds_it()
    {
        var speaker = new ActiveSpeaker();
        Assert.Null(speaker.Update(new Dictionary<string, double> { ["a"] = 0.02 }, T0));
        Assert.Equal("b", speaker.Update(new Dictionary<string, double> { ["a"] = 0.1, ["b"] = 0.3 }, T0));
        // A pause between words keeps the ring.
        Assert.Equal("b", speaker.Update(new Dictionary<string, double> { ["a"] = 0, ["b"] = 0 }, T0.AddMilliseconds(1100)));
        Assert.Null(speaker.Update(new Dictionary<string, double> { ["a"] = 0, ["b"] = 0 }, T0.AddMilliseconds(1200)));
        Assert.Equal("a", speaker.Update(new Dictionary<string, double> { ["a"] = 0.5 }, T0.AddSeconds(2)));
    }

    [Fact]
    public void Clears_at_once_when_the_speaker_mutes_or_leaves()
    {
        var speaker = new ActiveSpeaker();
        speaker.Update(new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 0 }, T0);
        Assert.Null(speaker.Update(new Dictionary<string, double> { ["b"] = 0 }, T0.AddMilliseconds(300)));
        speaker.Update(new Dictionary<string, double> { ["a"] = 0.5 }, T0);
        speaker.Reset();
        Assert.Null(speaker.Id);
    }
}

public class GroupLabelTests
{
    [Theory]
    [InlineData("a1b2c3d4", "Web", "Web · a1b2")]
    [InlineData("a1b2c3d4", "", "Guest · a1b2")]
    [InlineData("a1b2c3d4", "  ", "Guest · a1b2")]
    [InlineData("ab", "iPhone", "iPhone · ab")]
    public void Labels_name_and_short_id(string id, string name, string expected) =>
        Assert.Equal(expected, GroupLabels.Label(id, name));

    [Fact]
    public void A_participant_follows_its_state()
    {
        var participant = new GroupParticipant("p1234567", "Android", new MediaState(false, true, false));
        Assert.Equal("Android · p123", participant.Label);
        Assert.True(participant.MicOff);
        Assert.False(participant.Fit);
        Assert.False(participant.ShowVideo);

        participant.UpdateState(new MediaState(true, true, true));
        Assert.False(participant.MicOff);
        Assert.True(participant.IsPresenting);
        Assert.True(participant.Fit);
        participant.ToggleFit();
        Assert.False(participant.Fit);
        participant.UpdateState(new MediaState(true, true, false));
        Assert.False(participant.Fit);
        Assert.False(participant.IsPresenting);
    }
}
