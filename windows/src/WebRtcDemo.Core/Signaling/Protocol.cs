using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebRtcDemo.Core.Signaling;

/// <summary>An ICE candidate as RTCIceCandidateInit spells it on the wire.</summary>
public sealed record IceCandidateDto(string Candidate, string? SdpMid, int? SdpMLineIndex);

/// <summary>The remote peer's media, with the defaults every client assumes until told otherwise.</summary>
/// <param name="Screen">Sharing a screen, a window or a video file.</param>
public readonly record struct MediaState(bool Audio = true, bool Video = true, bool Screen = false)
{
    public static MediaState Default => new(true, true, false);
}

/// <param name="Fatal">The server gave up on this socket (it closes it); the call ends.</param>
public sealed record ServerError(string Message, bool Fatal);

/// <summary>Reading and writing the JSON both servers speak: one object per frame, with a "type".</summary>
public static class Wire
{
    public static string Message(string type, params (string Name, JsonNode? Value)[] fields)
    {
        var message = new JsonObject { ["type"] = type };
        foreach (var (name, value) in fields) message[name] = value;
        return message.ToJsonString();
    }

    public static JsonObject Candidate(IceCandidateDto candidate) => new()
    {
        ["candidate"] = candidate.Candidate,
        ["sdpMid"] = candidate.SdpMid,
        ["sdpMLineIndex"] = candidate.SdpMLineIndex,
    };

    public static JsonObject State(MediaState state) => new()
    {
        ["audio"] = state.Audio,
        ["video"] = state.Video,
        ["screen"] = state.Screen,
    };

    public static string? Type(JsonElement message) => GetString(message, "type");

    public static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool? GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static JsonElement? GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    public static IceCandidateDto? ReadCandidate(JsonElement message)
    {
        if (GetObject(message, "candidate") is not { } candidate || GetString(candidate, "candidate") is not { } text) return null;
        int? index = candidate.TryGetProperty("sdpMLineIndex", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;
        return new IceCandidateDto(text, GetString(candidate, "sdpMid"), index);
    }

    /// <summary>Missing fields keep their defaults (senders may send a partial state).</summary>
    public static MediaState ReadState(JsonElement message)
    {
        var state = GetObject(message, "state");
        return state is not { } s
            ? MediaState.Default
            : new MediaState(GetBool(s, "audio") ?? true, GetBool(s, "video") ?? true, GetBool(s, "screen") ?? false);
    }

    public static ServerError ReadError(JsonElement message) =>
        new(GetString(message, "message") ?? "Server error", GetBool(message, "fatal") ?? false);
}

/// <summary>What signaling-server/server.js sends a 1:1 client.</summary>
public abstract record SignalingMessage
{
    public static SignalingMessage? Parse(JsonElement message) => Wire.Type(message) switch
    {
        "peer joined" => new PeerJoinedMessage(),
        "offer" when Wire.GetString(message, "sdp") is { } sdp => new OfferMessage(sdp),
        "answer" when Wire.GetString(message, "sdp") is { } sdp => new AnswerMessage(sdp),
        "candidate" when Wire.ReadCandidate(message) is { } candidate => new CandidateMessage(candidate),
        "encryption key" when DecodeKey(Wire.GetString(message, "key")) is { } key => new EncryptionKeyMessage(key),
        "encryption key received" => new KeyReceivedMessage(),
        "media state" => new MediaStateMessage(Wire.ReadState(message)),
        "error" => new ErrorMessage(Wire.ReadError(message)),
        _ => null,
    };

    private static byte[]? DecodeKey(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record PeerJoinedMessage : SignalingMessage;
public sealed record OfferMessage(string Sdp) : SignalingMessage;
public sealed record AnswerMessage(string Sdp) : SignalingMessage;
public sealed record CandidateMessage(IceCandidateDto Candidate) : SignalingMessage;
public sealed record EncryptionKeyMessage(byte[] Key) : SignalingMessage;
public sealed record KeyReceivedMessage : SignalingMessage;
public sealed record MediaStateMessage(MediaState State) : SignalingMessage;
public sealed record ErrorMessage(ServerError Error) : SignalingMessage;

/// <summary>What a 1:1 client sends signaling-server/server.js; the server relays all but join and leave.</summary>
public static class SignalingMessages
{
    public static string Join(string roomId) => Wire.Message("join", ("roomId", roomId));
    public static string Leave() => Wire.Message("leave");
    public static string Offer(string sdp) => Wire.Message("offer", ("sdp", sdp));
    public static string Answer(string sdp) => Wire.Message("answer", ("sdp", sdp));
    public static string Candidate(IceCandidateDto candidate) => Wire.Message("candidate", ("candidate", Wire.Candidate(candidate)));
    /// <summary>32 bytes of key material, base64, like the web and mobile clients.</summary>
    public static string EncryptionKey(byte[] key) => Wire.Message("encryption key", ("key", Convert.ToBase64String(key)));
    public static string EncryptionKeyReceived() => Wire.Message("encryption key received");
    public static string MediaState(MediaState state) => Wire.Message("media state", ("state", Wire.State(state)));
}
