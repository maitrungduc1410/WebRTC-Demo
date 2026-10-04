using System.Globalization;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;

namespace WebRtcDemo.Core.Lobby;

/// <param name="ServerUrl">The signaling server for a 1:1 call, the SFU for a group call.</param>
public sealed record CallRequest(string RoomId, bool E2ee, bool Group, string ServerUrl);

/// <summary>
/// Room id, E2EE, 1:1 or group call, and the address of the server that mode uses with its
/// status. Neither server has a connection before a call, so the status comes from polling
/// GET / every 5 s while the lobby shows, like the web lobby.
/// </summary>
public sealed partial class LobbyViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settings;
    private readonly IServerProbe _probe;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _probeTimer;
    private CancellationTokenSource? _probing;
    private int _probeGeneration;
    private bool _disposed;

    public LobbyViewModel(SettingsStore settings, IServerProbe probe, IDispatcher dispatcher, TimeProvider? time = null)
    {
        _settings = settings;
        _probe = probe;
        _dispatcher = dispatcher;
        _time = time ?? TimeProvider.System;
        var stored = settings.Load();
        E2ee = stored.E2ee;
        SignalingUrl = stored.EffectiveServerUrl;
        SfuUrl = stored.EffectiveSfuUrl;
        ServerAddressText = ServerUrl;
        RoomId = RandomRoomId();
    }

    public event Action<CallRequest>? JoinRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    public partial string RoomId { get; set; }

    [ObservableProperty]
    public partial bool E2ee { get; set; }

    /// <summary>1:1 stays the default and is not remembered, as on the web.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerUrl), nameof(ServerLabel), nameof(ServerPlaceholder), nameof(ServerAddressError), nameof(JoinText), nameof(E2eeHint))]
    public partial bool GroupMode { get; set; }

    partial void OnGroupModeChanged(bool value)
    {
        ServerAddressText = ServerUrl;
        ServerStatus = ServerStatus.Connecting;
        CheckServer();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerUrl))]
    public partial string SignalingUrl { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerUrl))]
    public partial string SfuUrl { get; private set; }

    /// <summary>The address of the server the current mode uses.</summary>
    public string ServerUrl => GroupMode ? SfuUrl : SignalingUrl;

    public string ServerLabel => GroupMode ? "Group call server" : "Signaling server";

    public string ServerPlaceholder => GroupMode ? "192.168.1.10:4001" : "192.168.1.10:4000";

    public string JoinText => GroupMode ? "Join group call" : "Join room";

    public string E2eeHint => GroupMode ? "Everyone must turn it on" : "Both people must turn it on";

    /// <summary>What the user is typing; applied with <see cref="ApplyServerAddress"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerAddressError))]
    public partial string ServerAddressText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerStatusText))]
    public partial ServerStatus ServerStatus { get; private set; }

    public string ServerStatusText => ServerStatus switch
    {
        ServerStatus.Connected => "Connected",
        ServerStatus.Unreachable => "Can't reach this server",
        _ => "Connecting…",
    };

    /// <summary>Null while the typed address is valid (or empty, meaning the default).</summary>
    public string? ServerAddressError =>
        string.IsNullOrWhiteSpace(ServerAddressText) || NormalizeAddress(ServerAddressText) != null
            ? null
            : $"Enter an address like {ServerPlaceholder}";

    public static string RandomRoomId() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);

    private string? NormalizeAddress(string text) => GroupMode ? SfuAddress.Normalize(text) : ServerAddress.Normalize(text);

    /// <summary>Polls the server's status while the lobby shows.</summary>
    public void Start()
    {
        if (_disposed || _probeTimer != null) return;
        _probeTimer = _time.CreateTimer(_ => _dispatcher.Post(CheckServer), null, TimeSpan.Zero, ProbeInterval);
    }

    /// <summary>Stops polling (a call started).</summary>
    public void Stop()
    {
        _probeTimer?.Dispose();
        _probeTimer = null;
        _probeGeneration++;
        _probing?.Cancel();
        _probing = null;
    }

    public bool ApplyServerAddress()
    {
        var blank = string.IsNullOrWhiteSpace(ServerAddressText);
        var url = blank
            ? GroupMode ? SfuAddress.DefaultFor(SignalingUrl) : ServerAddress.DefaultUrl
            : NormalizeAddress(ServerAddressText);
        if (url == null) return false;
        ServerAddressText = url;
        // A blank SFU address goes back to following the signaling host.
        if (GroupMode && blank && _settings.Load().SfuUrl != null) _settings.Update(s => s with { SfuUrl = null });
        if (url == ServerUrl) return true;
        if (GroupMode)
        {
            SfuUrl = url;
            if (!blank) _settings.Update(s => s with { SfuUrl = url });
        }
        else
        {
            SignalingUrl = url;
            var settings = _settings.Update(s => s with { ServerUrl = url });
            // Until one is saved, the SFU is the one on the signaling host.
            SfuUrl = settings.EffectiveSfuUrl;
        }
        ServerStatus = ServerStatus.Connecting;
        CheckServer();
        return true;
    }

    private async void CheckServer()
    {
        if (_disposed) return;
        var generation = ++_probeGeneration;
        _probing?.Cancel();
        var probing = _probing = new CancellationTokenSource();
        var group = GroupMode;
        var url = ServerUrl;
        bool ok;
        try
        {
            ok = await _probe.ProbeAsync(group ? SfuAddress.HealthUrl(url) : ServerAddress.HealthUrl(url),
                group ? HttpServerProbe.SfuServerName : HttpServerProbe.SignalingServerName, probing.Token);
        }
        catch (Exception)
        {
            ok = false;
        }
        // Back on the UI thread: the probe completes on the thread pool.
        _dispatcher.Post(() =>
        {
            if (generation != _probeGeneration || _disposed || group != GroupMode || url != ServerUrl) return;
            ServerStatus = ok ? ServerStatus.Connected : ServerStatus.Unreachable;
        });
    }

    [RelayCommand]
    public void NewRoomId() => RoomId = RandomRoomId();

    private bool CanJoin() => !string.IsNullOrWhiteSpace(RoomId);

    [RelayCommand(CanExecute = nameof(CanJoin))]
    public void Join()
    {
        var room = RoomId.Trim();
        // A typed but not yet applied address counts; an invalid one keeps its error showing.
        if (room.Length == 0 || !ApplyServerAddress()) return;
        _settings.Update(s => s with { E2ee = E2ee });
        JoinRequested?.Invoke(new CallRequest(room, E2ee, GroupMode, ServerUrl));
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
    }
}
