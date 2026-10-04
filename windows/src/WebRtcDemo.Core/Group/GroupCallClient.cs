using System.Security.Cryptography;
using System.Text.Json;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Group;

/// <summary>What <see cref="GroupCallClient.Joined"/> brings: who we are and who is already there.</summary>
/// <param name="Key">The room key (the creator's material) with E2EE, else null.</param>
public sealed record GroupJoin(string ParticipantId, IReadOnlyList<ParticipantInfo> Participants, byte[]? Key);

/// <summary>
/// One group call through sfu-server, as useGroupCall.ts does it: a WebSocket for signaling and
/// chat, a publish connection we offer once, and a subscribe connection the server offers to
/// (answered one offer at a time). There is no session resume: a closed socket or a fatal error
/// ends the call. Lives on the UI thread; every event is raised on it.
/// </summary>
public sealed class GroupCallClient : IDisposable
{
    /// <summary>The name others see (web: CLIENT_NAME).</summary>
    public const string DisplayName = "Windows";
    public const int KeyLength = 32;

    private readonly MessageSocketFactory _sockets;
    private readonly IGroupCallMedia _media;
    private readonly IDispatcher _dispatcher;
    private IMessageSocket? _socket;
    private int _generation;
    private bool _e2ee;
    /// <summary>Subscribe offers that came before our connections opened.</summary>
    private readonly List<string> _heldOffers = [];
    private Task _subscribeQueue = Task.CompletedTask;
    private readonly Dictionary<GroupConnection, List<IceCandidateDto>> _pendingCandidates = new()
    {
        [GroupConnection.Publish] = [],
        [GroupConnection.Subscribe] = [],
    };
    private readonly HashSet<GroupConnection> _described = [];

    public GroupCallClient(MessageSocketFactory sockets, IGroupCallMedia media, IDispatcher dispatcher)
    {
        _sockets = sockets;
        _media = media;
        _dispatcher = dispatcher;
        _media.GroupIceCandidateGathered += OnLocalCandidate;
        _media.GroupConnectionStateChanged += OnConnectionState;
    }

    public event Action<GroupJoin>? Joined;
    public event Action<ParticipantInfo>? ParticipantJoined;
    public event Action<string>? ParticipantLeft;
    public event Action<string, MediaState>? ParticipantStateChanged;
    public event Action<ChatReceivedMessage>? ChatReceived;
    /// <summary>A non-fatal server error, for an info toast.</summary>
    public event Action<string>? Notice;
    /// <summary>The call is over (the client has already left); the text says why.</summary>
    public event Action<string>? Ended;

    public bool IsActive => _socket != null;
    /// <summary>Ours, once the server accepted the join.</summary>
    public string? ParticipantId { get; private set; }
    public bool IsJoined => ParticipantId != null;

    /// <summary>The current or last closed socket, for shutdown to let the leave out.</summary>
    public Task Completion => _socket?.Completion ?? _closing;

    private Task _closing = Task.CompletedTask;

    /// <param name="sfuUrl">A normalized SFU address, "ws://host:4001".</param>
    public void Start(string sfuUrl, string roomId, bool e2ee)
    {
        Leave();
        var generation = ++_generation;
        _e2ee = e2ee;
        // Becomes the room key if we are the first one in; otherwise `joined` brings the existing one.
        var keyMaterial = e2ee ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyLength)) : null;
        var socket = _sockets(SfuAddress.WebSocketUrl(sfuUrl));
        _socket = socket;
        socket.Opened += () => socket.Send(GroupMessages.Join(roomId, DisplayName, e2ee, keyMaterial));
        socket.MessageReceived += message => Post(generation, () => OnMessage(message));
        socket.Closed += _ => Post(generation, () =>
            End(IsJoined ? "Lost the connection to the group call server." : "Can't reach the group call server."));
        socket.Start();
    }

    /// <summary>
    /// Opens both connections, tells the room our media state and offers the publish connection.
    /// Called once after <see cref="Joined"/>, when local media is ready and the key is set.
    /// </summary>
    public async Task PublishAsync(MediaState state)
    {
        if (!IsJoined || _media.HasGroupConnections) return;
        var generation = _generation;
        _media.OpenGroupConnections(_e2ee, ParticipantId!);
        SendMediaState(state);
        foreach (var offer in _heldOffers) EnqueueSubscribeOffer(offer, generation);
        _heldOffers.Clear();
        try
        {
            var offer = await _media.CreatePublishOfferAsync();
            if (generation != _generation) return;
            Send(GroupMessages.PublishOffer(offer.Sdp));
        }
        catch (Exception) when (generation != _generation)
        {
        }
        catch (Exception)
        {
            Notice?.Invoke("Couldn't send your camera and microphone to the group call.");
        }
    }

    public void SendMediaState(MediaState state)
    {
        if (IsJoined) Send(GroupMessages.MediaState(state));
    }

    public bool SendChat(string text)
    {
        if (!IsJoined || _socket == null) return false;
        Send(GroupMessages.Chat(text));
        return true;
    }

    /// <summary>Tells the server and closes everything; no event is raised for this call afterwards.</summary>
    public void Leave()
    {
        var socket = _socket;
        if (socket == null) return;
        _generation++;
        _socket = null;
        _closing = socket.Completion;
        ParticipantId = null;
        socket.Send(GroupMessages.Leave());
        socket.Close();
        _media.CloseGroupConnections();
        _heldOffers.Clear();
        _subscribeQueue = Task.CompletedTask;
        foreach (var list in _pendingCandidates.Values) list.Clear();
        _described.Clear();
    }

    private void End(string reason)
    {
        if (_socket == null) return;
        Leave();
        Ended?.Invoke(reason);
    }

    private void Send(string json) => _socket?.Send(json);

    private void OnMessage(JsonElement json)
    {
        switch (GroupMessage.Parse(json))
        {
            case JoinedMessage joined:
                OnJoined(joined);
                break;
            case GroupAnswerMessage { Connection: GroupConnection.Publish } answer:
                _ = ApplyPublishAnswerAsync(answer.Sdp);
                break;
            case GroupOfferMessage { Connection: GroupConnection.Subscribe } offer:
                if (_media.HasGroupConnections) EnqueueSubscribeOffer(offer.Sdp, _generation);
                else _heldOffers.Add(offer.Sdp);
                break;
            case GroupCandidateMessage candidate:
                AddRemoteCandidate(candidate.Connection, candidate.Candidate);
                break;
            case ParticipantJoinedMessage joined when joined.Participant.Id != ParticipantId:
                ParticipantJoined?.Invoke(joined.Participant);
                break;
            case ParticipantLeftMessage left:
                ParticipantLeft?.Invoke(left.ParticipantId);
                break;
            case ParticipantStateMessage state when state.ParticipantId != ParticipantId:
                ParticipantStateChanged?.Invoke(state.ParticipantId, state.State);
                break;
            case ChatReceivedMessage chat:
                ChatReceived?.Invoke(chat);
                break;
            case GroupErrorMessage { Error.Fatal: true } error:
                // The server closes the socket next; this explains it better than the close would.
                End(FatalErrorText(error.Error.Message));
                break;
            case GroupErrorMessage error:
                Notice?.Invoke(error.Error.Message);
                break;
        }
    }

    private void OnJoined(JoinedMessage joined)
    {
        if (IsJoined) return;
        byte[]? key = null;
        if (_e2ee)
        {
            key = TryBase64(joined.E2eeKey);
            if (key is not { Length: > 0 })
            {
                End("The server did not send an encryption key for this room.");
                return;
            }
        }
        ParticipantId = joined.ParticipantId;
        Joined?.Invoke(new GroupJoin(joined.ParticipantId, joined.Participants.Where(p => p.Id != joined.ParticipantId).ToList(), key));
    }

    private string FatalErrorText(string message) => message switch
    {
        "Room is full" => "That group call is full.",
        "E2EE setting does not match the room" => _e2ee
            ? "That group call doesn't use end-to-end encryption. Turn it off to join."
            : "That group call uses end-to-end encryption. Turn it on to join.",
        _ => message,
    };

    private async Task ApplyPublishAnswerAsync(string sdp)
    {
        if (!_media.HasGroupConnections) return;
        var generation = _generation;
        try
        {
            await _media.SetPublishAnswerAsync(sdp);
            if (generation != _generation) return;
            _described.Add(GroupConnection.Publish);
            AddPendingCandidates(GroupConnection.Publish);
        }
        catch (Exception)
        {
            // A stale answer, or one for connections replaced since.
        }
    }

    private void EnqueueSubscribeOffer(string sdp, int generation) =>
        _subscribeQueue = AnswerSubscribeOfferAsync(_subscribeQueue, sdp, generation);

    private async Task AnswerSubscribeOfferAsync(Task previous, string sdp, int generation)
    {
        await previous;
        if (generation != _generation) return;
        try
        {
            var answer = await _media.AnswerSubscribeOfferAsync(sdp);
            if (generation != _generation) return;
            Send(GroupMessages.SubscribeAnswer(answer.Sdp));
            _described.Add(GroupConnection.Subscribe);
            AddPendingCandidates(GroupConnection.Subscribe);
        }
        catch (Exception) when (generation == _generation)
        {
            // The server holds every later renegotiation until this offer is answered, so
            // without an answer nobody new would ever be seen or heard.
            End("Couldn't receive the others' audio and video from the group call server.");
        }
        catch (Exception)
        {
            // An offer for connections closed since.
        }
    }

    /// <summary>The server's candidates are in its SDP; this only covers a server that trickles.</summary>
    private void AddRemoteCandidate(GroupConnection connection, IceCandidateDto candidate)
    {
        if (!_described.Contains(connection) || !_media.HasGroupConnections)
        {
            _pendingCandidates[connection].Add(candidate);
            return;
        }
        _media.AddGroupIceCandidate(connection, ToNative(candidate));
    }

    private void AddPendingCandidates(GroupConnection connection)
    {
        var candidates = _pendingCandidates[connection].ToList();
        _pendingCandidates[connection].Clear();
        foreach (var candidate in candidates) _media.AddGroupIceCandidate(connection, ToNative(candidate));
    }

    private static IceCandidate ToNative(IceCandidateDto candidate) =>
        new(candidate.SdpMid, candidate.SdpMLineIndex ?? 0, candidate.Candidate);

    private void OnLocalCandidate(GroupConnection connection, IceCandidate candidate)
    {
        // The end of gathering has no candidate.
        if (_socket == null || string.IsNullOrEmpty(candidate.Candidate)) return;
        Send(GroupMessages.Candidate(connection, new IceCandidateDto(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex)));
    }

    private void OnConnectionState(GroupConnection connection, PeerConnectionState state)
    {
        // Without TURN or ICE restarts a failed connection stays failed.
        if (state == PeerConnectionState.Failed) End("Couldn't connect the media to the group call server.");
    }

    private static byte[]? TryBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private void Post(int generation, Action action) => _dispatcher.Post(() =>
    {
        if (generation == _generation) action();
    });

    public void Dispose()
    {
        Leave();
        _media.GroupIceCandidateGathered -= OnLocalCandidate;
        _media.GroupConnectionStateChanged -= OnConnectionState;
    }
}
