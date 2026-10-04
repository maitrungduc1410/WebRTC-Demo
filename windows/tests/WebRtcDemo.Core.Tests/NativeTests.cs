using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WebRtcDemo.Core.Call;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Core.Tests.Fakes;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeTestGroup
{
    public const string Name = "native";
}

/// <summary>
/// The C# bindings over the real shim and libwebrtc: two peer connections in this process,
/// E2EE with the demo key-provider options, frames and chat both ways.
/// </summary>
[Collection(NativeTestGroup.Name)]
public sealed partial class NativeInteropTests
{
    private const int Width = 320;
    private const int Height = 240;

    [GeneratedRegex(@"m=video \d+ \S+ (\d+)")]
    private static partial Regex FirstVideoPayload();

    private static string FirstVideoCodec(string sdp)
    {
        var payload = FirstVideoPayload().Match(sdp).Groups[1].Value;
        return Regex.Match(sdp, $@"a=rtpmap:{payload} ([^/]+)/").Groups[1].Value;
    }

    /// <summary>Feeds a solid color (as YUV) until cancelled.</summary>
    private static Task Pump(VideoSource source, byte y, byte u, byte v, CancellationToken cancel) => Task.Run(async () =>
    {
        var luma = new byte[Width * Height];
        var cb = new byte[Width * Height / 4];
        var cr = new byte[Width * Height / 4];
        Array.Fill(luma, y);
        Array.Fill(cb, u);
        Array.Fill(cr, v);
        while (!cancel.IsCancellationRequested)
        {
            source.PushI420(Width, Height, luma, Width, cb, Width / 2, cr, Width / 2);
            try
            {
                await Task.Delay(33, cancel);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }, CancellationToken.None);

    private sealed class Peer : IDisposable
    {
        public required PeerConnection Pc { get; init; }
        public required KeyProvider Keys { get; init; }
        public required VideoSource Source { get; init; }
        public required MediaTrack Video { get; init; }
        public required MediaTrack Audio { get; init; }
        public ConcurrentQueue<IceCandidate> Candidates { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<FrameCryptionState> Cryptor { get; } = new();
        public ConcurrentBag<MediaTrack> RemoteTracks { get; } = [];
        public ConcurrentBag<VideoSink> Sinks { get; } = [];
        public DataChannel? Channel { get; set; }
        public int MatchingFrames;
        public int OtherFrames;
        public volatile bool Connected;

        public void Dispose()
        {
            foreach (var sink in Sinks) sink.Dispose();
            Pc.Close();
            Channel?.Dispose();
            foreach (var track in RemoteTracks) track.Dispose();
            Pc.Dispose();
            Video.Dispose();
            Audio.Dispose();
            Source.Dispose();
            Keys.Dispose();
        }
    }

    private static Peer CreatePeer(PeerConnectionFactory factory, byte[] key, Func<byte, byte, byte, bool> expectedColor)
    {
        var keys = new KeyProvider(KeyProviderOptions.Demo);
        Assert.True(keys.SetSharedKey(0, key));
        var source = factory.CreateCustomSource();
        var peer = new Peer
        {
            Pc = factory.CreatePeerConnection([], keys),
            Keys = keys,
            Source = source,
            Video = factory.CreateVideoTrack(source, "video"),
            Audio = factory.CreateAudioTrack("audio"),
        };
        peer.Pc.IceCandidateGathered += peer.Candidates.Enqueue;
        peer.Pc.ConnectionStateChanged += state => peer.Connected |= state == PeerConnectionState.Connected;
        peer.Pc.CryptorStateChanged += (_, state) => peer.Cryptor.Enqueue(state);
        peer.Pc.TrackAdded += (track, _) =>
        {
            peer.RemoteTracks.Add(track);
            if (track.Kind != MediaKind.Video) return;
            peer.Sinks.Add(track.AddSink(frame =>
            {
                var row = frame.Height / 2 * frame.Stride;
                var pixel = frame.Data.Slice(row + frame.Width / 2 * 4, 4);
                if (frame.Width == Width && frame.Height == Height && frame.Stride >= Width * 4 && expectedColor(pixel[2], pixel[1], pixel[0]))
                {
                    Interlocked.Increment(ref peer.MatchingFrames);
                }
                else
                {
                    Interlocked.Increment(ref peer.OtherFrames);
                }
            }));
        };
        peer.Pc.DataChannelReceived += channel =>
        {
            channel.MessageReceived += message => peer.Messages.Enqueue(message.Text);
            peer.Channel = channel;
        };
        peer.Pc.AddTrack(peer.Audio, "stream").Dispose();
        peer.Pc.AddTrack(peer.Video, "stream").Dispose();
        Assert.True(peer.Pc.AttachSenderCryptors() > 0);
        return peer;
    }

    private static void Flush(Peer from, Peer to)
    {
        while (from.Candidates.TryDequeue(out var candidate)) to.Pc.AddIceCandidate(candidate);
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(25);
        }
    }

    private static bool Red(byte r, byte g, byte b) => r > 180 && g < 90 && b < 90;
    private static bool Green(byte r, byte g, byte b) => g > 180 && r < 90 && b < 90;

    [Fact]
    public async Task Two_peers_exchange_encrypted_video_and_chat()
    {
        TestEnvironment.RequireNative();
        using var factory = new PeerConnectionFactory();
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        // a sends red, so b expects red.
        using var a = CreatePeer(factory, key, Green);
        using var b = CreatePeer(factory, key, Red);
        using var stop = new CancellationTokenSource();
        var pumps = new[] { Pump(a.Source, 81, 90, 240, stop.Token), Pump(b.Source, 145, 54, 34, stop.Token) };

        try
        {
            a.Channel = a.Pc.CreateDataChannel(NativeCallMedia.DataChannelLabel);
            var aOpen = new TaskCompletionSource();
            a.Channel.StateChanged += state =>
            {
                if (state == DataChannelState.Open) aOpen.TrySetResult();
            };
            a.Channel.MessageReceived += message => a.Messages.Enqueue(message.Text);

            Assert.True(a.Pc.PreferCodec(MediaKind.Video, "video/VP8") > 0);
            var offer = await a.Pc.CreateOfferAsync();
            Assert.Equal("VP8", FirstVideoCodec(offer.Sdp));
            await b.Pc.SetRemoteDescriptionAsync(offer);
            Assert.True(b.Pc.PreferCodec(MediaKind.Video, "video/VP8") > 0);
            var answer = await b.Pc.CreateAnswerAsync();
            Assert.Equal("VP8", FirstVideoCodec(answer.Sdp));
            await a.Pc.SetRemoteDescriptionAsync(answer);

            await Until(() =>
            {
                Flush(a, b);
                Flush(b, a);
                return a.Connected && b.Connected;
            }, "connected");

            await Until(() => a.MatchingFrames >= 10 && b.MatchingFrames >= 10, "decrypted frames both ways");
            Assert.Contains(FrameCryptionState.Ok, a.Cryptor);
            Assert.Contains(FrameCryptionState.Ok, b.Cryptor);

            await aOpen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Until(() => b.Channel?.State == DataChannelState.Open, "b's channel open");
            Assert.Equal(NativeCallMedia.DataChannelLabel, b.Channel!.Label);
            Assert.True(a.Channel.Send("hello from a"));
            Assert.True(b.Channel.Send("héllo from b ✓"));
            await Until(() => !a.Messages.IsEmpty && !b.Messages.IsEmpty, "chat both ways");
            Assert.Equal(["hello from a"], b.Messages);
            Assert.Equal(["héllo from b ✓"], a.Messages);

            double? level = null;
            await Until(() =>
            {
                level = b.Pc.GetRemoteAudioLevelAsync().GetAwaiter().GetResult();
                return level != null;
            }, "remote audio level");
            Assert.InRange(level!.Value, 0, 1);

            // Disabling the track stops the frames (libwebrtc then sends black ones).
            a.Video.Enabled = false;
            Assert.False(a.Video.Enabled);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(pumps);
        }
    }

    [Fact]
    public async Task A_wrong_key_shows_no_video()
    {
        TestEnvironment.RequireNative();
        using var factory = new PeerConnectionFactory();
        using var a = CreatePeer(factory, Enumerable.Repeat((byte)1, 32).ToArray(), Green);
        using var b = CreatePeer(factory, Enumerable.Repeat((byte)2, 32).ToArray(), Red);
        using var stop = new CancellationTokenSource();
        var pump = Pump(a.Source, 81, 90, 240, stop.Token);
        try
        {
            a.Pc.PreferCodec(MediaKind.Video, "video/VP8");
            var offer = await a.Pc.CreateOfferAsync();
            await b.Pc.SetRemoteDescriptionAsync(offer);
            b.Pc.PreferCodec(MediaKind.Video, "video/VP8");
            await a.Pc.SetRemoteDescriptionAsync(await b.Pc.CreateAnswerAsync());
            await Until(() =>
            {
                Flush(a, b);
                Flush(b, a);
                return a.Connected && b.Connected;
            }, "connected");
            await Task.Delay(3000, TestContext.Current.CancellationToken);
            Assert.Equal(0, b.MatchingFrames + b.OtherFrames);
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }
    }

    [Fact]
    public async Task Calls_after_close_fail_cleanly()
    {
        TestEnvironment.RequireNative();
        using var factory = new PeerConnectionFactory();
        using var pc = factory.CreatePeerConnection([], null);
        pc.Close();
        Assert.True(pc.IsClosed);
        await Assert.ThrowsAnyAsync<Exception>(pc.CreateOfferAsync);
        Assert.False(pc.AddIceCandidate(new IceCandidate("0", 0, "candidate:1 1 udp 1 127.0.0.1 9 typ host")));
        Assert.Null(await pc.GetRemoteAudioLevelAsync());
    }
}

/// <summary>Two complete clients (view model, native media, WebSocket) calling each other through the server.</summary>
[Collection(NativeTestGroup.Name)]
public sealed class NativeCallTests : IAsyncDisposable
{
    private readonly ThreadDispatcher _ui = new("call-ui");
    private readonly List<(SignalingClient Signaling, NativeCallMedia Media, CallViewModel Call)> _clients = [];
    private PeerConnectionFactory? _factory;

    private Task<CallViewModel> CreateClientAsync() => _ui.InvokeAsync(() =>
    {
        _factory ??= new PeerConnectionFactory();
        var signaling = new SignalingClient(url => new WebSocketMessageSocket(url), _ui);
        var media = new NativeCallMedia(_factory, _ui);
        var call = new CallViewModel(signaling, media, _ui);
        _clients.Add((signaling, media, call));
        return call;
    });

    [Fact]
    public async Task Two_clients_call_each_other_with_e2ee()
    {
        TestEnvironment.RequireNative();
        TestEnvironment.RequireServer();
        var room = TestEnvironment.NewRoomId();
        var a = await CreateClientAsync();
        var b = await CreateClientAsync();
        var bToasts = new ConcurrentQueue<string>();
        await _ui.InvokeAsync(() => b.ToastRequested += toast => bToasts.Enqueue(toast.Text));
        // Waiting alone there is no connection to read stats from: the idle meter measures, on a's microphone.
        var idleMeter = new FakePeakMeter(1);
        var idleDevices = new ConcurrentQueue<string?>();
        await _ui.InvokeAsync(() => _clients[0].Media.StartIdleMeter = id =>
        {
            idleDevices.Enqueue(id);
            return idleMeter;
        });

        await _ui.InvokeAsync(() => a.Join(room, e2ee: true, TestEnvironment.SignalingUrl));
        await _ui.WaitUntilAsync(() => a.MicLevel == 1, "idle meter drives a's mic level");
        await _ui.InvokeAsync(() => Assert.Equal([_clients[0].Media.MicrophoneId], idleDevices));
        await _ui.InvokeAsync(() => b.Join(room, e2ee: true, TestEnvironment.SignalingUrl));

        await _ui.WaitUntilAsync(() => a.Phase == CallPhase.Connected && b.Phase == CallPhase.Connected, "call connected", 30_000);
        await _ui.InvokeAsync(() =>
        {
            Assert.True(idleMeter.Disposed, "the idle meter lets go of the microphone once the call opens");
            Assert.False(_clients[0].Media.IdleMeterRunning);
        });
        // In the call the level comes from the sending connection's audio media-source stats.
        double? statsPeak = null;
        for (var i = 0; i < 50 && statsPeak == null; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            statsPeak = await _ui.InvokeAsync<Task<double?>>(() => _clients[0].Media.GetMicPeakAsync()).Unwrap();
        }
        Assert.InRange(statsPeak ?? -1, 0, 1);
        await _ui.InvokeAsync(() => Assert.False(_clients[0].Media.IdleMeterRunning));
        await _ui.WaitUntilAsync(() => a.ChatReady && b.ChatReady, "chat ready");
        await _ui.WaitUntilAsync(() => a.RemoteVideo != null && b.RemoteVideo != null, "remote video tracks");

        await _ui.InvokeAsync(() =>
        {
            Assert.True(a.SendMessage("hi from a"));
            Assert.True(b.SendMessage("hi from b"));
        });
        await _ui.WaitUntilAsync(() => a.Messages.Count == 2 && b.Messages.Count == 2, "chat both ways");
        await _ui.InvokeAsync(() =>
        {
            Assert.Equal([(true, "hi from a"), (false, "hi from b")], a.Messages.OrderBy(m => !m.IsLocal).Select(m => (m.IsLocal, m.Text)));
            Assert.Equal(1, b.Unread);
        });

        // The container has no camera: each side sees the other's placeholder.
        await _ui.WaitUntilAsync(() => !b.RemoteMedia.Video && b.ShowRemotePlaceholder, "placeholder at b");

        // What a replaced microphone does to the sender: the encrypted call carries on, unrenegotiated.
        Task<long?> Packets() => _ui.InvokeAsync<Task<long?>>(() => _clients[1].Media.GetInboundAudioPacketsAsync()).Unwrap();
        long? before = null;
        for (var i = 0; i < 50 && before is not > 0; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            before = await Packets();
        }
        Assert.True(before > 0, "b receives a's audio");
        await _ui.InvokeAsync(_clients[0].Media.RestartMicrophoneSend);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var restarted = await Packets();
        long? after = restarted;
        for (var i = 0; i < 50 && after <= restarted; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            after = await Packets();
        }
        Assert.True(after > restarted, $"audio packets keep arriving after the restart ({before} → {restarted} → {after})");
        await _ui.InvokeAsync(() => Assert.True(a.Phase == CallPhase.Connected && b.Phase == CallPhase.Connected));

        await _ui.InvokeAsync(a.ToggleMic);
        await _ui.WaitUntilAsync(() => !b.RemoteMedia.Audio && b.PlaceholderSubtitle == "Microphone muted", "mute reaches b");
        await _ui.InvokeAsync(() => Assert.Equal(0, a.MicLevel));

        await _ui.InvokeAsync(a.Leave);
        await _ui.WaitUntilAsync(() => b.Phase == CallPhase.Waiting, "b back to waiting", 15_000);
        Assert.Contains("The other participant left", bToasts);
        Assert.True(_ui.Errors.IsEmpty, string.Join("\n", _ui.Errors));
    }

    public async ValueTask DisposeAsync()
    {
        await _ui.InvokeAsync(() =>
        {
            foreach (var (_, media, call) in _clients)
            {
                call.Dispose();
                media.Dispose();
            }
        });
        await _ui.InvokeAsync(() =>
        {
            foreach (var (signaling, _, _) in _clients) signaling.Dispose();
            _factory?.Dispose();
        });
        _ui.Dispose();
    }
}

/// <summary>
/// Complete group clients (view model, native media, GroupCallClient) through a real sfu-server:
/// each publishes a solid color and must see the others' on the right tiles, with and without E2EE.
/// </summary>
[Collection(NativeTestGroup.Name)]
public sealed class NativeGroupCallTests : IAsyncDisposable
{
    private const int Width = 320;
    private const int Height = 240;

    private readonly ThreadDispatcher _ui = new("group-ui");
    private readonly List<(SignalingClient Signaling, NativeCallMedia Media, CallViewModel Call)> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _pumps = [];
    private PeerConnectionFactory? _factory;

    private Task<(CallViewModel Call, NativeCallMedia Media)> CreateClientAsync() => _ui.InvokeAsync(() =>
    {
        _factory ??= new PeerConnectionFactory();
        var signaling = new SignalingClient(url => new WebSocketMessageSocket(url), _ui);
        var media = new NativeCallMedia(_factory, _ui);
        var call = new CallViewModel(signaling, media, _ui);
        _clients.Add((signaling, media, call));
        return (call, media);
    });

    /// <summary>The container has no camera; push a color into what the client sends instead.</summary>
    /// <returns>Cancel it to stop this client's frames (before it leaves).</returns>
    private Task<CancellationTokenSource> PumpAsync(NativeCallMedia media, Color color)
    {
        var (y, u, v) = Yuv(color);
        return PumpAsync(media, y, u, v);
    }

    private async Task<CancellationTokenSource> PumpAsync(NativeCallMedia media, byte y, byte u, byte v)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        await _ui.WaitUntilAsync(() => media.PlaceholderSource != null, "video sender");
        var source = await _ui.InvokeAsync(() => media.PlaceholderSource!);
        var luma = new byte[Width * Height];
        var cb = new byte[Width * Height / 4];
        var cr = new byte[Width * Height / 4];
        Array.Fill(luma, y);
        Array.Fill(cb, u);
        Array.Fill(cr, v);
        _pumps.Add(Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    source.PushI420(Width, Height, luma, Width, cb, Width / 2, cr, Width / 2);
                }
                catch (ObjectDisposedException)
                {
                    // The client left (StopLocalMedia disposed its placeholder): nothing more to send.
                    return;
                }
                try
                {
                    await Task.Delay(33, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }, CancellationToken.None));
        return stop;
    }

    /// <summary>Solid colors as I420, told apart again in the received BGRA.</summary>
    private enum Color { Red, Green, Blue, Yellow, Other }

    private static (byte Y, byte U, byte V) Yuv(Color color) => color switch
    {
        Color.Red => (76, 84, 255),
        Color.Green => (150, 44, 21),
        Color.Blue => (29, 255, 107),
        Color.Yellow => (226, 0, 148),
        _ => (128, 128, 128),
    };

    private static Color Classify(byte b, byte g, byte r) => (r > 150, g > 150, b > 150) switch
    {
        (true, false, false) when g < 100 && b < 100 => Color.Red,
        (false, true, false) when r < 100 && b < 100 => Color.Green,
        (false, false, true) when r < 100 && g < 100 => Color.Blue,
        (true, true, false) when b < 100 => Color.Yellow,
        _ => Color.Other,
    };

    /// <summary>The colors of the frames one tile receives.</summary>
    private sealed class TileColors
    {
        private readonly int[] _counts = new int[5];

        public int this[Color color] => Volatile.Read(ref _counts[(int)color]);

        public void OnFrame(VideoFrame frame)
        {
            var c = frame.Data.Slice((frame.Height / 2 * frame.Stride) + (frame.Width / 2 * 4), 4);
            Interlocked.Increment(ref _counts[(int)Classify(c[0], c[1], c[2])]);
        }

        public override string ToString() => string.Join(", ", Enum.GetValues<Color>().Select(c => $"{c}={this[c]}"));
    }

    /// <summary>Waits for <paramref name="id"/>'s video at <paramref name="call"/> and counts its colors.</summary>
    private async Task<(TileColors Colors, IDisposable Subscription)> WatchAsync(CallViewModel call, string id, string what)
    {
        await _ui.WaitUntilAsync(() => call.Participants.FirstOrDefault(p => p.Id == id)?.Video != null, what + " video", 30_000);
        var colors = new TileColors();
        var subscription = await _ui.InvokeAsync(() => call.Participants.First(p => p.Id == id).Video!.Subscribe(colors.OnFrame));
        return (colors, subscription);
    }

    /// <summary>
    /// A, B and C join; B leaves; D joins. The SFU reuses B's m-lines on A's and C's subscribe
    /// connections for D, so D's video must come out of a re-reported receiver (with a fresh
    /// cryptor under E2EE) mapped to D, and nothing of B may remain.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_later_joiner_takes_over_the_m_lines_of_one_who_left(bool e2ee)
    {
        TestEnvironment.RequireNative();
        TestEnvironment.RequireSfu();
        var room = TestEnvironment.NewRoomId();
        var (a, aMedia) = await CreateClientAsync();
        var (b, bMedia) = await CreateClientAsync();
        var (c, cMedia) = await CreateClientAsync();
        var (d, dMedia) = await CreateClientAsync();

        await _ui.InvokeAsync(() => a.JoinGroup(room, e2ee, TestEnvironment.SfuUrl));
        await _ui.WaitUntilAsync(() => a.Phase == CallPhase.Waiting, "a alone in the room", 15_000);
        await PumpAsync(aMedia, Color.Red);
        await _ui.InvokeAsync(() => b.JoinGroup(room, e2ee, TestEnvironment.SfuUrl));
        var bFrames = await PumpAsync(bMedia, Color.Blue);
        await _ui.InvokeAsync(() => c.JoinGroup(room, e2ee, TestEnvironment.SfuUrl));
        await PumpAsync(cMedia, Color.Green);
        await _ui.WaitUntilAsync(() => a.Participants.Count == 2 && b.Participants.Count == 2 && c.Participants.Count == 2, "three in the room", 15_000);
        var (aId, bId, cId) = await _ui.InvokeAsync(() => (a.SelfId!, b.SelfId!, c.SelfId!));

        // Everyone's color on the right tile.
        var (bAtA, s1) = await WatchAsync(a, bId, "b at a");
        var (cAtA, s2) = await WatchAsync(a, cId, "c at a");
        var (aAtC, s3) = await WatchAsync(c, aId, "a at c");
        var (bAtC, s4) = await WatchAsync(c, bId, "b at c");
        await _ui.WaitUntilAsync(() => bAtA[Color.Blue] > 10 && cAtA[Color.Green] > 10 && aAtC[Color.Red] > 10 && bAtC[Color.Blue] > 10, "three colors", 30_000);
        Assert.True(bAtA[Color.Red] + bAtA[Color.Green] == 0 && cAtA[Color.Red] + cAtA[Color.Blue] == 0, $"at a: b {bAtA}; c {cAtA}");
        Assert.True(aAtC[Color.Blue] + aAtC[Color.Green] == 0 && bAtC[Color.Red] + bAtC[Color.Green] == 0, $"at c: a {aAtC}; b {bAtC}");
        foreach (var s in new[] { s1, s2, s3, s4 }) s.Dispose();
        var linesAtA = await _ui.InvokeAsync(() => aMedia.SubscribeTransceiverCount);
        var linesAtC = await _ui.InvokeAsync(() => cMedia.SubscribeTransceiverCount);
        Assert.Equal(4, linesAtA);

        // B leaves: its tile goes everywhere.
        await bFrames.CancelAsync();
        await _ui.InvokeAsync(b.Leave);
        await _ui.WaitUntilAsync(() => a.Participants.All(p => p.Id != bId) && c.Participants.All(p => p.Id != bId), "b gone", 15_000);

        // D joins, and A and C receive it on B's old m-lines.
        await _ui.InvokeAsync(() => d.JoinGroup(room, e2ee, TestEnvironment.SfuUrl));
        await PumpAsync(dMedia, Color.Yellow);
        await _ui.WaitUntilAsync(() => a.Participants.Count == 2 && c.Participants.Count == 2 && d.Participants.Count == 2, "a, c and d", 15_000);
        var dId = await _ui.InvokeAsync(() => d.SelfId!);
        var (dAtA, t1) = await WatchAsync(a, dId, "d at a");
        var (cAtA2, t2) = await WatchAsync(a, cId, "c at a after d");
        var (dAtC, t3) = await WatchAsync(c, dId, "d at c");
        var (aAtD, t4) = await WatchAsync(d, aId, "a at d");
        var (cAtD, t5) = await WatchAsync(d, cId, "c at d");
        await _ui.WaitUntilAsync(
            () => dAtA[Color.Yellow] > 10 && cAtA2[Color.Green] > 10 && dAtC[Color.Yellow] > 10 && aAtD[Color.Red] > 10 && cAtD[Color.Green] > 10,
            "d's color at a and c, theirs at d", 30_000);
        foreach (var t in new[] { t1, t2, t3, t4, t5 }) t.Dispose();
        Assert.True(dAtA[Color.Blue] + dAtA[Color.Green] + dAtA[Color.Red] == 0, $"d at a: {dAtA}");
        Assert.True(dAtC[Color.Blue] + dAtC[Color.Green] + dAtC[Color.Red] == 0, $"d at c: {dAtC}");
        Assert.True(cAtA2[Color.Yellow] + cAtA2[Color.Blue] == 0, $"c at a: {cAtA2}");

        await _ui.InvokeAsync(() =>
        {
            Assert.Equal([cId, dId], a.Participants.Select(p => p.Id));
            Assert.Equal([aId, dId], c.Participants.Select(p => p.Id));
            // Reused, not added: the SFU put D on the m-lines B had.
            Assert.Equal(linesAtA, aMedia.SubscribeTransceiverCount);
            Assert.Equal(linesAtC, cMedia.SubscribeTransceiverCount);
        });
        Assert.True(_ui.Errors.IsEmpty, string.Join("\n", _ui.Errors));
    }

    /// <summary>Frames from the one other participant, by dominant BGRA channel.</summary>
    private sealed class ColorCounter
    {
        public int Red;
        public int Blue;

        public void OnFrame(VideoFrame frame)
        {
            var center = frame.Data.Slice((frame.Height / 2 * frame.Stride) + (frame.Width / 2 * 4), 4);
            if (center[2] > center[0] + 60) Interlocked.Increment(ref Red);
            else if (center[0] > center[2] + 60) Interlocked.Increment(ref Blue);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_clients_see_each_other_through_the_sfu(bool e2ee)
    {
        TestEnvironment.RequireNative();
        TestEnvironment.RequireSfu();
        var room = TestEnvironment.NewRoomId();
        var (a, aMedia) = await CreateClientAsync();
        var (b, bMedia) = await CreateClientAsync();
        var aToasts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await _ui.InvokeAsync(() => a.ToastRequested += toast => aToasts.Enqueue(toast.Text));

        await _ui.InvokeAsync(() => a.JoinGroup(room, e2ee, TestEnvironment.SfuUrl));
        await _ui.WaitUntilAsync(() => a.Phase == CallPhase.Waiting, "a alone in the room", 15_000);
        await PumpAsync(aMedia, Color.Red);
        await _ui.InvokeAsync(() => b.JoinGroup(room, e2ee, TestEnvironment.SfuUrl));
        await PumpAsync(bMedia, Color.Blue);

        await _ui.WaitUntilAsync(() => a.Phase == CallPhase.Connected && b.Phase == CallPhase.Connected, "both see a participant", 15_000);
        await _ui.InvokeAsync(() =>
        {
            Assert.Equal(b.SelfId, Assert.Single(a.Participants).Id);
            Assert.Equal(a.SelfId, Assert.Single(b.Participants).Id);
            Assert.Equal("Windows", a.Participants[0].Name);
            Assert.Equal(e2ee, a.E2ee);
        });

        // Each side's video arrives on the right participant: red at b, blue at a.
        await _ui.WaitUntilAsync(() => a.Participants[0].Video != null && b.Participants[0].Video != null, "participant video", 30_000);
        var atA = new ColorCounter();
        var atB = new ColorCounter();
        using var aSubscription = await _ui.InvokeAsync(() => a.Participants[0].Video!.Subscribe(atA.OnFrame));
        using var bSubscription = await _ui.InvokeAsync(() => b.Participants[0].Video!.Subscribe(atB.OnFrame));
        await _ui.WaitUntilAsync(() => atA.Blue > 10 && atB.Red > 10, "colors both ways", 30_000);
        Assert.True(atA.Red == 0 && atB.Blue == 0, $"a saw {atA.Red} red, b saw {atB.Blue} blue");

        // Chat and media state go through the SFU's socket.
        await _ui.InvokeAsync(() => Assert.True(a.SendMessage("hi group")));
        await _ui.WaitUntilAsync(() => b.Messages.Any(m => m.Text == "hi group"), "chat at b");
        await _ui.InvokeAsync(() => Assert.Equal(a.SelfLabel, b.Messages.Single(m => m.Text == "hi group").Sender));
        await _ui.InvokeAsync(b.ToggleMic);
        await _ui.WaitUntilAsync(() => a.Participants[0].MicOff, "mute reaches a");

        await _ui.InvokeAsync(b.Leave);
        await _ui.WaitUntilAsync(() => a.Phase == CallPhase.Waiting && a.Participants.Count == 0, "a alone again", 15_000);
        Assert.Contains(aToasts, t => t.EndsWith(" left", StringComparison.Ordinal));
        Assert.True(_ui.Errors.IsEmpty, string.Join("\n", _ui.Errors));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_pumps);
        await _ui.InvokeAsync(() =>
        {
            foreach (var (_, media, call) in _clients)
            {
                call.Dispose();
                media.Dispose();
            }
            foreach (var (signaling, _, _) in _clients) signaling.Dispose();
            _factory?.Dispose();
        });
        _stop.Dispose();
        _ui.Dispose();
    }
}
