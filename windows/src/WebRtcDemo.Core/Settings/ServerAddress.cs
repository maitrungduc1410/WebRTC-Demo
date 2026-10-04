using System.Globalization;
using System.Text.RegularExpressions;

namespace WebRtcDemo.Core.Settings;

/// <summary>Signaling server addresses, with the web client's rules (web/src/call/serverUrl.ts).</summary>
public static partial class ServerAddress
{
    /// <summary>Where `npm run dev` in signaling-server/ listens on this machine.</summary>
    public const string DefaultUrl = "http://localhost:4000";

    [GeneratedRegex(@"^[a-z][a-z\d+.-]*://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemePattern();

    /// <summary>
    /// Accepts "192.168.1.10:4000" as well as a full URL and returns "scheme://host[:port]", or
    /// null. A path, query or user info is dropped: the server takes its WebSocket on /ws.
    /// </summary>
    public static string? Normalize(string? input) => Parse(input) is { } uri ? Origin(uri.Scheme, uri, uri.IsDefaultPort ? null : uri.Port) : null;

    /// <summary>"http://host:4000" → "ws://host:4000/ws".</summary>
    public static Uri WebSocketUrl(string url) => new(WebSocketScheme(url) + url[url.IndexOf("://", StringComparison.Ordinal)..] + "/ws");

    /// <summary>GET / answers {"name": ..., "ok": true}.</summary>
    public static Uri HealthUrl(string url) => new(url.TrimEnd('/') + "/");

    internal static Uri? Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var withScheme = SchemePattern().IsMatch(text) ? text : "http://" + text;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return string.IsNullOrEmpty(uri.Host) ? null : uri;
    }

    internal static string Origin(string scheme, Uri uri, int? port)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        var suffix = port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"{scheme}://{host.ToLowerInvariant()}{suffix}";
    }

    private static string WebSocketScheme(string url) =>
        url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("wss:", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
}

/// <summary>
/// Group call (sfu-server) addresses, like Android's SfuServer.kt: "ws[s]://host:port", where a
/// bare host gets port 4001, http(s) becomes ws(s) and a path is dropped.
/// </summary>
public static partial class SfuAddress
{
    public const int DefaultPort = 4001;

    [GeneratedRegex(@"^ws(s?)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebSocketScheme();

    [GeneratedRegex(@"^(?:[a-z][a-z\d+.-]*://)?(?:[^@/?#]*@)?(?:\[[^\]]*\]|[^:/?#]*):\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPort();

    /// <summary>Port 4001 on the default signaling host.</summary>
    public static string DefaultUrl => DefaultFor(ServerAddress.DefaultUrl);

    /// <summary>Port 4001 on the host of <paramref name="signalingUrl"/>.</summary>
    public static string DefaultFor(string signalingUrl)
    {
        var uri = ServerAddress.Parse(signalingUrl) ?? ServerAddress.Parse(ServerAddress.DefaultUrl)!;
        return ServerAddress.Origin(uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", uri, DefaultPort);
    }

    public static string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        var http = WebSocketScheme().Replace(text, "http$1://", 1);
        if (ServerAddress.Parse(http) is not { } uri) return null;
        // Uri fills in 80/443, so whether a port was typed comes from the text.
        int? port = ExplicitPort().IsMatch(http) ? uri.Port : DefaultPort;
        return ServerAddress.Origin(uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", uri, port);
    }

    /// <summary>"ws://host:4001" → "ws://host:4001/ws".</summary>
    public static Uri WebSocketUrl(string url) => ServerAddress.WebSocketUrl(url);

    /// <summary>"ws://host:4001" → "http://host:4001/".</summary>
    public static Uri HealthUrl(string url) => new(WebSocketScheme().Replace(url, "http$1://", 1).TrimEnd('/') + "/");
}
