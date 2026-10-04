using System.Text.Json;

namespace WebRtcDemo.Core.Signaling;

/// <summary>
/// One WebSocket carrying JSON text frames, as both servers speak (`ws://host:port/ws`). Events
/// are raised on background threads, in order, and none after <see cref="Closed"/>.
/// </summary>
public interface IMessageSocket : IDisposable
{
    event Action? Opened;
    /// <summary>One parsed frame; frames that are not a JSON object are dropped.</summary>
    event Action<JsonElement>? MessageReceived;
    /// <summary>Raised once, whoever closed it. False when it never opened (unreachable).</summary>
    event Action<bool>? Closed;

    /// <summary>Completes once the socket is closed and every event has been raised.</summary>
    Task Completion { get; }

    void Start();
    /// <summary>Queued; sent in order once open. Dropped after <see cref="Close"/>.</summary>
    void Send(string json);
    /// <summary>Sends what is queued, then closes.</summary>
    void Close();
}

public delegate IMessageSocket MessageSocketFactory(Uri url);
