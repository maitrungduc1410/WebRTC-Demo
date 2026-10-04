using System.Text.Json;

namespace WebRtcDemo.Core.Signaling;

public enum ServerStatus { Connecting, Connected, Unreachable }

/// <summary>The lobby's status dot: neither server has a connection before a call.</summary>
public interface IServerProbe
{
    /// <summary>True when GET <paramref name="healthUrl"/> answers {"name": <paramref name="name"/>}.</summary>
    Task<bool> ProbeAsync(Uri healthUrl, string name, CancellationToken cancellationToken);
}

/// <summary>Like probeServer in web/src/call/serverUrl.ts: 3 s timeout, checks the server's name.</summary>
public sealed class HttpServerProbe : IServerProbe, IDisposable
{
    public const string SignalingServerName = "signaling-server";
    public const string SfuServerName = "sfu-server";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<bool> ProbeAsync(Uri healthUrl, string name, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return Wire.GetString(json.RootElement, "name") == name;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
