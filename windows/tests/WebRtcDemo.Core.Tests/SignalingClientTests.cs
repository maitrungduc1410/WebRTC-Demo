using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Core.Tests.Fakes;

namespace WebRtcDemo.Core.Tests;

public sealed class SignalingClientTests : IDisposable
{
    private readonly ManualDispatcher _dispatcher = new();
    private readonly FakeSocketFactory _sockets = new();
    private readonly SignalingClient _client;

    public SignalingClientTests() => _client = new SignalingClient(_sockets.Create, _dispatcher);

    public void Dispose()
    {
        _client.Dispose();
        _dispatcher.Dispose();
    }

    [Fact]
    public void Join_opens_a_socket_on_ws_and_joins_once_open()
    {
        var opened = 0;
        _client.Opened += () => opened++;
        _client.Join("http://192.168.1.10:4000", "123456");
        var socket = _sockets.Last;
        Assert.Equal(new Uri("ws://192.168.1.10:4000/ws"), socket.Url);
        Assert.True(socket.Started);
        Assert.True(_client.IsJoined);
        Assert.Empty(socket.Sent);

        socket.Open();
        _dispatcher.RunPending();
        Assert.Equal(["""{"type":"join","roomId":"123456"}"""], socket.Sent);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void Https_servers_use_wss()
    {
        _client.Join("https://signal.example.com", "1");
        Assert.Equal(new Uri("wss://signal.example.com/ws"), _sockets.Last.Url);
    }

    [Fact]
    public void Messages_have_the_server_shape()
    {
        _client.Join("http://localhost:4000", "42");
        _sockets.Last.Open();
        _client.SendEncryptionKey([1, 2, 3]);
        _client.SendOffer("v=0 offer");
        _client.SendAnswer("v=0 answer");
        _client.SendIceCandidate(new IceCandidateDto("candidate:1", "0", 0));
        _client.SendMediaState(new MediaState(false, true, true));
        _client.SendEncryptionKeyReceived();
        _client.Leave();

        Assert.Equal(
            [
                """{"type":"join","roomId":"42"}""",
                """{"type":"encryption key","key":"AQID"}""",
                """{"type":"offer","sdp":"v=0 offer"}""",
                """{"type":"answer","sdp":"v=0 answer"}""",
                """{"type":"candidate","candidate":{"candidate":"candidate:1","sdpMid":"0","sdpMLineIndex":0}}""",
                """{"type":"media state","state":{"audio":false,"video":true,"screen":true}}""",
                """{"type":"encryption key received"}""",
                """{"type":"leave"}""",
            ],
            _sockets.Created[0].Sent);
        Assert.True(_sockets.Created[0].IsClosed);
        Assert.False(_client.IsJoined);
    }

    [Fact]
    public void Parses_server_messages()
    {
        _client.Join("http://localhost:4000", "1");
        string? offer = null, answer = null;
        IceCandidateDto? candidate = null;
        MediaState? state = null;
        byte[]? key = null;
        ServerError? error = null;
        var joined = 0;
        var acknowledged = 0;
        _client.PeerJoined += () => joined++;
        _client.OfferReceived += s => offer = s;
        _client.AnswerReceived += s => answer = s;
        _client.IceCandidateReceived += c => candidate = c;
        _client.MediaStateReceived += s => state = s;
        _client.EncryptionKeyReceived += k => key = k;
        _client.RemotePeerReceivedKey += () => acknowledged++;
        _client.ErrorReceived += e => error = e;

        var socket = _sockets.Last;
        socket.Receive("""{"type":"peer joined"}""");
        socket.Receive("""{"type":"offer","sdp":"v=0 web"}""");
        socket.Receive("""{"type":"answer","sdp":"v=0 ios"}""");
        // The web client sends RTCIceCandidate.toJSON(), with extra fields.
        socket.Receive("""{"type":"candidate","candidate":{"candidate":"candidate:9","sdpMid":"1","sdpMLineIndex":1,"usernameFragment":"x"}}""");
        socket.Receive("""{"type":"media state","state":{"video":false}}""");
        socket.Receive("""{"type":"encryption key","key":"AAECAw=="}""");
        socket.Receive("""{"type":"encryption key received"}""");
        socket.Receive("""{"type":"error","message":"Room is full","fatal":true}""");
        // Unknown or malformed messages are ignored.
        socket.Receive("""{"type":"answer"}""");
        socket.Receive("""{"type":"encryption key","key":"%%%"}""");
        socket.Receive("""{"type":"something new"}""");
        _dispatcher.RunPending();

        Assert.Equal(1, joined);
        Assert.Equal("v=0 web", offer);
        Assert.Equal("v=0 ios", answer);
        Assert.Equal(new IceCandidateDto("candidate:9", "1", 1), candidate);
        Assert.Equal(new MediaState(true, false, false), state);
        Assert.Equal([0, 1, 2, 3], key);
        Assert.Equal(1, acknowledged);
        Assert.Equal(new ServerError("Room is full", true), error);
    }

    [Fact]
    public void A_closed_socket_is_reported_once_with_whether_it_had_opened()
    {
        var closed = new List<bool>();
        _client.Closed += closed.Add;
        _client.Join("http://localhost:4000", "1");
        _sockets.Last.Drop(wasOpen: false);
        _dispatcher.RunPending();
        Assert.Equal([false], closed);
        Assert.False(_client.IsJoined);

        _client.Join("http://localhost:4000", "1");
        _sockets.Last.Open();
        _sockets.Last.Drop();
        _dispatcher.RunPending();
        Assert.Equal([false, true], closed);
    }

    [Fact]
    public void Nothing_is_raised_for_a_socket_after_leaving()
    {
        var events = 0;
        _client.PeerJoined += () => events++;
        _client.Closed += _ => events++;
        _client.Join("http://localhost:4000", "1");
        var old = _sockets.Last;
        old.Receive("""{"type":"peer joined"}""");
        _client.Leave();
        old.Drop();
        _dispatcher.RunPending();
        Assert.Equal(0, events);
        Assert.True(_client.Completion.IsCompleted);

        // A new call gets a new socket.
        _client.Join("http://localhost:4000", "2");
        Assert.Equal(2, _sockets.Created.Count);
        Assert.NotSame(old, _sockets.Last);
    }

    [Fact]
    public void Sending_without_a_call_does_nothing()
    {
        _client.SendOffer("v=0");
        _client.Leave();
        Assert.Empty(_sockets.Created);
    }
}
