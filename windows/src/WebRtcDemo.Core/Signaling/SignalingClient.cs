using System.Text.Json;

namespace WebRtcDemo.Core.Signaling;

/// <summary>
/// The signaling socket of one 1:1 call (signaling-server/server.js): opened by <see cref="Join"/>,
/// closed by <see cref="Leave"/>. There is no reconnect, like on the other clients: the server
/// frees the seat when the socket drops, so <see cref="Closed"/> ends the call.
/// Lives on the UI thread; every event is raised on it.
/// </summary>
public sealed class SignalingClient(MessageSocketFactory sockets, IDispatcher dispatcher) : IDisposable
{
    private IMessageSocket? _socket;
    private int _generation;

    /// <summary>The socket opened (the join is on its way).</summary>
    public event Action? Opened;
    /// <summary>Someone joined while we were waiting in the room: we make the offer.</summary>
    public event Action? PeerJoined;
    public event Action<string>? OfferReceived;
    public event Action<string>? AnswerReceived;
    public event Action<IceCandidateDto>? IceCandidateReceived;
    public event Action<byte[]>? EncryptionKeyReceived;
    public event Action? RemotePeerReceivedKey;
    public event Action<MediaState>? MediaStateReceived;
    public event Action<ServerError>? ErrorReceived;
    /// <summary>The socket closed by itself; false when it never opened (server unreachable).</summary>
    public event Action<bool>? Closed;

    public bool IsJoined => _socket != null;

    /// <summary>The current or last closed socket, for shutdown to let the leave out.</summary>
    public Task Completion => _socket?.Completion ?? _closing;

    private Task _closing = Task.CompletedTask;

    /// <param name="serverUrl">A normalized signaling address, "http://host:4000".</param>
    public void Join(string serverUrl, string roomId)
    {
        Leave();
        var generation = ++_generation;
        var socket = sockets(Settings.ServerAddress.WebSocketUrl(serverUrl));
        _socket = socket;
        socket.Opened += () =>
        {
            // Queued before anything the call sends; the socket keeps the order.
            socket.Send(SignalingMessages.Join(roomId));
            Post(generation, () => Opened?.Invoke());
        };
        socket.MessageReceived += message => Post(generation, () => OnMessage(message));
        socket.Closed += wasOpen => Post(generation, () =>
        {
            _socket = null;
            Closed?.Invoke(wasOpen);
        });
        socket.Start();
    }

    /// <summary>Tells the server and closes the socket; no event is raised for it afterwards.</summary>
    public void Leave()
    {
        var socket = _socket;
        if (socket == null) return;
        _generation++;
        _socket = null;
        _closing = socket.Completion;
        socket.Send(SignalingMessages.Leave());
        socket.Close();
    }

    public void SendOffer(string sdp) => Send(SignalingMessages.Offer(sdp));
    public void SendAnswer(string sdp) => Send(SignalingMessages.Answer(sdp));
    public void SendIceCandidate(IceCandidateDto candidate) => Send(SignalingMessages.Candidate(candidate));
    public void SendEncryptionKey(byte[] key) => Send(SignalingMessages.EncryptionKey(key));
    public void SendEncryptionKeyReceived() => Send(SignalingMessages.EncryptionKeyReceived());
    public void SendMediaState(MediaState state) => Send(SignalingMessages.MediaState(state));

    private void Send(string json) => _socket?.Send(json);

    private void OnMessage(JsonElement json)
    {
        switch (SignalingMessage.Parse(json))
        {
            case PeerJoinedMessage:
                PeerJoined?.Invoke();
                break;
            case OfferMessage offer:
                OfferReceived?.Invoke(offer.Sdp);
                break;
            case AnswerMessage answer:
                AnswerReceived?.Invoke(answer.Sdp);
                break;
            case CandidateMessage candidate:
                IceCandidateReceived?.Invoke(candidate.Candidate);
                break;
            case EncryptionKeyMessage key:
                EncryptionKeyReceived?.Invoke(key.Key);
                break;
            case KeyReceivedMessage:
                RemotePeerReceivedKey?.Invoke();
                break;
            case MediaStateMessage state:
                MediaStateReceived?.Invoke(state.State);
                break;
            case ErrorMessage error:
                ErrorReceived?.Invoke(error.Error);
                break;
        }
    }

    private void Post(int generation, Action action) => dispatcher.Post(() =>
    {
        if (generation == _generation) action();
    });

    public void Dispose() => Leave();
}
