using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WebRtcDemo.Core.Signaling;

/// <summary><see cref="IMessageSocket"/> over <see cref="ClientWebSocket"/>. The server's pings are answered by the runtime.</summary>
public sealed class WebSocketMessageSocket(Uri url, TimeSpan? connectTimeout = null) : IMessageSocket
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly ClientWebSocket _socket = new();
    private readonly Channel<string?> _outgoing = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
    private int _started;
    private int _closing;

    public event Action? Opened;
    public event Action<JsonElement>? MessageReceived;
    public event Action<bool>? Closed;

    public Uri Url { get; } = url;
    public Task Completion => _completion.Task;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(RunAsync);
    }

    public void Send(string json)
    {
        if (Volatile.Read(ref _closing) == 0) _outgoing.Writer.TryWrite(json);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        // Null marks the end: what was queued before still goes out.
        _outgoing.Writer.TryWrite(null);
        _outgoing.Writer.TryComplete();
        if (Volatile.Read(ref _started) == 0) _completion.TrySetResult();
    }

    public void Dispose() => Close();

    private async Task RunAsync()
    {
        var opened = false;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(_connectTimeout);
                await _socket.ConnectAsync(Url, timeout.Token).ConfigureAwait(false);
            }
            opened = true;
            if (Volatile.Read(ref _closing) == 0) Opened?.Invoke();
            var sending = SendLoopAsync();
            await ReceiveLoopAsync().ConfigureAwait(false);
            _stop.Cancel();
            await sending.ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or InvalidOperationException)
        {
        }
        finally
        {
            _stop.Cancel();
            _outgoing.Writer.TryComplete();
            _socket.Dispose();
            try
            {
                Closed?.Invoke(opened);
            }
            finally
            {
                _completion.TrySetResult();
            }
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (message == null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(CloseTimeout);
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "leave", timeout.Token).ConfigureAwait(false);
                    // The receive loop ends with the server's close frame; don't wait for it forever.
                    _ = Task.Delay(CloseTimeout, _stop.Token).ContinueWith(_ => _stop.Cancel(), TaskScheduler.Default);
                    return;
                }
                await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or InvalidOperationException)
        {
            _stop.Cancel();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var memory = buffer.GetMemory(8192);
            var result = await _socket.ReceiveAsync(memory, _stop.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            buffer.Advance(result.Count);
            if (!result.EndOfMessage) continue;
            if (result.MessageType == WebSocketMessageType.Text) Deliver(buffer.WrittenSpan);
            buffer.ResetWrittenCount();
        }
    }

    private void Deliver(ReadOnlySpan<byte> utf8)
    {
        JsonElement message;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            message = JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return;
        }
        if (message.ValueKind == JsonValueKind.Object && Volatile.Read(ref _closing) == 0) MessageReceived?.Invoke(message);
    }
}
