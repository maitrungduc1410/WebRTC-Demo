using Microsoft.Extensions.Time.Testing;
using WebRtcDemo.Core.Lobby;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Core.Tests.Fakes;

namespace WebRtcDemo.Core.Tests;

public sealed class LobbyViewModelTests : IDisposable
{
    private readonly ManualDispatcher _dispatcher = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeServerProbe _probe = new();
    private readonly string _path = Path.Combine(Path.GetTempPath(), "webrtcdemo-lobby-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly SettingsStore _settings;
    private LobbyViewModel? _lobby;

    public LobbyViewModelTests() => _settings = new SettingsStore(_path);

    public void Dispose()
    {
        _lobby?.Dispose();
        _dispatcher.Dispose();
        File.Delete(_path);
    }

    private LobbyViewModel Lobby()
    {
        _lobby ??= new LobbyViewModel(_settings, _probe, _dispatcher, _time);
        return _lobby;
    }

    [Fact]
    public void Defaults_to_a_1_to_1_call_on_localhost()
    {
        var lobby = Lobby();
        Assert.False(lobby.GroupMode);
        Assert.Equal("http://localhost:4000", lobby.ServerUrl);
        Assert.Equal("ws://localhost:4001", lobby.SfuUrl);
        Assert.Equal("Join room", lobby.JoinText);
        Assert.Matches("^[1-9][0-9]{5}$", lobby.RoomId);
    }

    [Fact]
    public void Group_mode_has_its_own_saved_address()
    {
        var lobby = Lobby();
        lobby.ServerAddressText = "192.168.1.10:4000";
        Assert.True(lobby.ApplyServerAddress());
        // With no SFU saved, it is on the signaling host.
        Assert.Equal("ws://192.168.1.10:4001", lobby.SfuUrl);

        lobby.GroupMode = true;
        Assert.Equal("ws://192.168.1.10:4001", lobby.ServerAddressText);
        Assert.Equal("Group call server", lobby.ServerLabel);
        Assert.Equal("Join group call", lobby.JoinText);
        lobby.ServerAddressText = "http://10.0.0.5/anything";
        Assert.True(lobby.ApplyServerAddress());
        Assert.Equal("ws://10.0.0.5:4001", lobby.ServerUrl);

        lobby.GroupMode = false;
        Assert.Equal("http://192.168.1.10:4000", lobby.ServerAddressText);
        var saved = _settings.Load();
        Assert.Equal("http://192.168.1.10:4000", saved.ServerUrl);
        Assert.Equal("ws://10.0.0.5:4001", saved.SfuUrl);

        // A saved SFU no longer follows the signaling host.
        lobby.ServerAddressText = "192.168.1.20:4000";
        lobby.ApplyServerAddress();
        Assert.Equal("ws://10.0.0.5:4001", lobby.SfuUrl);

        // An explicit localhost SFU with a LAN signaling server survives a change and a relaunch.
        lobby.GroupMode = true;
        lobby.ServerAddressText = "localhost:4001";
        Assert.True(lobby.ApplyServerAddress());
        lobby.GroupMode = false;
        lobby.ServerAddressText = "192.168.1.30:4000";
        lobby.ApplyServerAddress();
        Assert.Equal("ws://localhost:4001", lobby.SfuUrl);
        Assert.Equal("ws://localhost:4001", _settings.Load().EffectiveSfuUrl);
        using (var relaunched = new LobbyViewModel(_settings, _probe, _dispatcher, _time))
        {
            Assert.Equal("ws://localhost:4001", relaunched.SfuUrl);
            Assert.Equal("http://192.168.1.30:4000", relaunched.SignalingUrl);
        }

        // Clearing it follows the signaling host again.
        lobby.GroupMode = true;
        lobby.ServerAddressText = "";
        lobby.ApplyServerAddress();
        Assert.Equal("ws://192.168.1.30:4001", lobby.SfuUrl);
        Assert.Null(_settings.Load().SfuUrl);
        lobby.GroupMode = false;
        lobby.ServerAddressText = "192.168.1.40:4000";
        lobby.ApplyServerAddress();
        Assert.Equal("ws://192.168.1.40:4001", lobby.SfuUrl);
    }

    [Fact]
    public void An_invalid_address_shows_an_error_and_blocks_joining()
    {
        var lobby = Lobby();
        var requests = new List<CallRequest>();
        lobby.JoinRequested += requests.Add;
        lobby.GroupMode = true;
        lobby.ServerAddressText = "ftp://nope";
        Assert.Equal("Enter an address like 192.168.1.10:4001", lobby.ServerAddressError);
        lobby.Join();
        Assert.Empty(requests);

        // Empty means the default.
        lobby.ServerAddressText = " ";
        Assert.Null(lobby.ServerAddressError);
        lobby.Join();
        Assert.Equal(new CallRequest(lobby.RoomId, false, true, "ws://localhost:4001"), Assert.Single(requests));
    }

    [Fact]
    public void Join_applies_a_typed_address_and_remembers_e2ee()
    {
        var lobby = Lobby();
        CallRequest? request = null;
        lobby.JoinRequested += r => request = r;
        lobby.RoomId = " 424242 ";
        lobby.E2ee = true;
        lobby.ServerAddressText = "https://Signal.Example.com/";
        lobby.Join();
        Assert.Equal(new CallRequest("424242", true, false, "https://signal.example.com"), request);
        Assert.True(_settings.Load().E2ee);
    }

    [Fact]
    public void Polls_the_health_of_the_server_the_mode_uses()
    {
        _probe.Servers["http://localhost:4000/"] = HttpServerProbe.SignalingServerName;
        // Something else answers on 4001: not an SFU.
        _probe.Servers["http://localhost:4001/"] = "signaling-server";
        var lobby = Lobby();
        lobby.Start();
        _dispatcher.RunUntil(() => lobby.ServerStatus != ServerStatus.Connecting);
        Assert.Equal(ServerStatus.Connected, lobby.ServerStatus);
        Assert.Equal((new Uri("http://localhost:4000/"), "signaling-server"), _probe.Requests[^1]);

        lobby.GroupMode = true;
        _dispatcher.RunUntil(() => lobby.ServerStatus != ServerStatus.Connecting);
        Assert.Equal(ServerStatus.Unreachable, lobby.ServerStatus);
        Assert.Equal("Can't reach this server", lobby.ServerStatusText);
        Assert.Equal((new Uri("http://localhost:4001/"), "sfu-server"), _probe.Requests[^1]);

        var count = _probe.Requests.Count;
        _time.Advance(LobbyViewModel.ProbeInterval);
        _dispatcher.RunUntil(() => _probe.Requests.Count > count);

        lobby.Stop();
        count = _probe.Requests.Count;
        _time.Advance(LobbyViewModel.ProbeInterval * 3);
        _dispatcher.RunPending();
        Assert.Equal(count, _probe.Requests.Count);
    }
}
