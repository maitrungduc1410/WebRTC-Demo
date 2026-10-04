using System.Text.Json;
using WebRtcDemo.Core.Signaling;

namespace WebRtcDemo.Core.Tests.Fakes;

/// <summary>A socket the test opens, feeds and drops; it records what was sent.</summary>
public sealed class FakeMessageSocket(Uri url) : IMessageSocket
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri Url { get; } = url;
    public bool Started { get; private set; }
    public bool IsClosed { get; private set; }
    public List<string> Sent { get; } = [];

    public event Action? Opened;
    public event Action<JsonElement>? MessageReceived;
    public event Action<bool>? Closed;

    public Task Completion => _completion.Task;

    public void Start() => Started = true;

    public void Send(string json)
    {
        if (!IsClosed) Sent.Add(json);
    }

    public void Close()
    {
        IsClosed = true;
        _completion.TrySetResult();
    }

    public void Dispose() => Close();

    // ---- Driving ------------------------------------------------------------------------------

    public void Open() => Opened?.Invoke();

    public void Receive(string json) => MessageReceived?.Invoke(JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>The server or the network closed it.</summary>
    public void Drop(bool wasOpen = true)
    {
        IsClosed = true;
        Closed?.Invoke(wasOpen);
        _completion.TrySetResult();
    }

    public List<JsonElement> Messages => Sent.Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToList();

    public List<string> Types => Messages.Select(m => m.GetProperty("type").GetString()!).ToList();
}

public sealed class FakeSocketFactory
{
    public List<FakeMessageSocket> Created { get; } = [];
    public FakeMessageSocket Last => Created[^1];

    public IMessageSocket Create(Uri url)
    {
        var socket = new FakeMessageSocket(url);
        Created.Add(socket);
        return socket;
    }
}

/// <summary>Answers health checks from a table; unknown URLs are unreachable.</summary>
public sealed class FakeServerProbe : IServerProbe
{
    public Dictionary<string, string> Servers { get; } = [];
    public List<(Uri Url, string Name)> Requests { get; } = [];

    public Task<bool> ProbeAsync(Uri healthUrl, string name, CancellationToken cancellationToken)
    {
        Requests.Add((healthUrl, name));
        return Task.FromResult(Servers.TryGetValue(healthUrl.ToString(), out var answer) && answer == name);
    }
}
