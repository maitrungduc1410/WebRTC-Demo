using System.Collections.Concurrent;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Core.Tests.Fakes;

namespace WebRtcDemo.Core.Tests;

/// <summary>Real WebSocket clients through the repo's Node signaling server (signaling-server/server.js).</summary>
public sealed class LiveSignalingTests : IAsyncDisposable
{
    private readonly ThreadDispatcher _ui = new("signaling-ui");
    private readonly List<SignalingClient> _clients = [];

    private async Task<SignalingClient> JoinAsync(string room, string? server = null)
    {
        var opened = false;
        var client = await _ui.InvokeAsync(() =>
        {
            var c = new SignalingClient(url => new WebSocketMessageSocket(url), _ui);
            c.Opened += () => opened = true;
            c.Join(server ?? TestEnvironment.SignalingUrl, room);
            return c;
        });
        _clients.Add(client);
        if (server == null) await _ui.WaitUntilAsync(() => opened, "socket open");
        return client;
    }

    [Fact]
    public async Task Relays_the_call_flow_between_two_clients()
    {
        TestEnvironment.RequireServer();
        var room = TestEnvironment.NewRoomId();

        var aEvents = new ConcurrentQueue<string>();
        var bEvents = new ConcurrentQueue<string>();
        byte[]? keyAtB = null;
        IceCandidateDto? candidateAtA = null;
        MediaState? stateAtB = null;
        var a = await JoinAsync(room);
        await _ui.InvokeAsync(() =>
        {
            a.PeerJoined += () => aEvents.Enqueue("joined");
            a.AnswerReceived += answer => aEvents.Enqueue("answer:" + answer);
            a.RemotePeerReceivedKey += () => aEvents.Enqueue("key-ack");
            a.IceCandidateReceived += candidate =>
            {
                candidateAtA = candidate;
                aEvents.Enqueue("ice");
            };
        });
        // The join must reach the server before the second one does.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var b = await JoinAsync(room);
        await _ui.InvokeAsync(() =>
        {
            b.EncryptionKeyReceived += key =>
            {
                keyAtB = key;
                bEvents.Enqueue("key");
            };
            b.OfferReceived += offer => bEvents.Enqueue("offer:" + offer);
            b.MediaStateReceived += state =>
            {
                stateAtB = state;
                bEvents.Enqueue("state");
            };
        });
        // The peer already in the room is told and makes the offer.
        await _ui.WaitUntilAsync(() => aEvents.Contains("joined"), "peer joined at a");

        // As an E2EE offerer does: the key, then the offer, in that order.
        var key = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        await _ui.InvokeAsync(() =>
        {
            a.SendEncryptionKey(key);
            a.SendOffer("v=0 from a");
        });
        await _ui.WaitUntilAsync(() => bEvents.Count >= 2, "key and offer at b");
        Assert.Equal(["key", "offer:v=0 from a"], bEvents);
        Assert.Equal(key, keyAtB);

        await _ui.InvokeAsync(() =>
        {
            b.SendEncryptionKeyReceived();
            b.SendAnswer("v=0 from b");
            b.SendIceCandidate(new IceCandidateDto("candidate:1 1 udp 2122260223 10.0.0.1 50000 typ host", "0", 0));
            a.SendMediaState(new MediaState(false, true, true));
        });
        await _ui.WaitUntilAsync(() => aEvents.Count >= 4 && stateAtB != null, "answer, ack, candidate and media state");
        Assert.Equal(["joined", "key-ack", "answer:v=0 from b", "ice"], aEvents);
        Assert.Equal(new IceCandidateDto("candidate:1 1 udp 2122260223 10.0.0.1 50000 typ host", "0", 0), candidateAtA);
        Assert.Equal(new MediaState(false, true, true), stateAtB);

        // A third one is turned away, fatally.
        // The server keeps the socket open; the client ends the call on a fatal error.
        ServerError? error = null;
        var c = await _ui.InvokeAsync(() =>
        {
            var client = new SignalingClient(url => new WebSocketMessageSocket(url), _ui);
            client.ErrorReceived += e => error = e;
            client.Join(TestEnvironment.SignalingUrl, room);
            return client;
        });
        _clients.Add(c);
        await _ui.WaitUntilAsync(() => error != null, "room full error");
        Assert.Equal(new ServerError("Room is full", true), error);
        await _ui.InvokeAsync(c.Leave);

        // When b leaves, a's socket stays open for the next peer.
        await _ui.InvokeAsync(b.Leave);
        var bAgain = await JoinAsync(room);
        await _ui.WaitUntilAsync(() => aEvents.Count(e => e == "joined") == 2, "second peer joined at a", 15_000);
        Assert.True(_ui.Errors.IsEmpty, string.Join("\n", _ui.Errors));
        Assert.True(bAgain.IsJoined);
    }

    [Fact]
    public async Task An_unreachable_server_closes_without_opening()
    {
        bool? closed = null;
        var client = await _ui.InvokeAsync(() =>
        {
            var c = new SignalingClient(url => new WebSocketMessageSocket(url), _ui);
            c.Closed += wasOpen => closed = wasOpen;
            c.Join("http://127.0.0.1:9", "1");
            return c;
        });
        _clients.Add(client);
        await _ui.WaitUntilAsync(() => closed != null, "closed", 15_000);
        Assert.False(closed);
    }

    public async ValueTask DisposeAsync()
    {
        await _ui.InvokeAsync(() =>
        {
            foreach (var client in _clients) client.Dispose();
        });
        foreach (var client in _clients) await client.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default);
        _ui.Dispose();
    }
}
