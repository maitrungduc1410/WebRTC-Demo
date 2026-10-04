using System.Text.Json;
using WebRtcDemo.Core.Signaling;

namespace WebRtcDemo.Core.Group;

/// <summary>The client's two connections to the SFU; the wire calls them "publish" and "subscribe".</summary>
public enum GroupConnection { Publish, Subscribe }

public sealed record ParticipantInfo(string Id, string Name, MediaState State);

/// <summary>What sfu-server sends (sfu-server/signaling.go).</summary>
public abstract record GroupMessage
{
    public static GroupMessage? Parse(JsonElement message) => Wire.Type(message) switch
    {
        "joined" when Wire.GetString(message, "participantId") is { } id => new JoinedMessage(
            id, ReadParticipants(message), Wire.GetBool(message, "e2ee") ?? false, NullIfEmpty(Wire.GetString(message, "e2eeKey"))),
        "answer" when ReadConnection(message) is { } pc && Wire.GetString(message, "sdp") is { } sdp => new GroupAnswerMessage(pc, sdp),
        "offer" when ReadConnection(message) is { } pc && Wire.GetString(message, "sdp") is { } sdp => new GroupOfferMessage(pc, sdp),
        "candidate" when ReadConnection(message) is { } pc && Wire.ReadCandidate(message) is { } candidate => new GroupCandidateMessage(pc, candidate),
        "participant joined" when Wire.GetObject(message, "participant") is { } p && ReadParticipant(p) is { } info => new ParticipantJoinedMessage(info),
        "participant left" when Wire.GetString(message, "participantId") is { } id => new ParticipantLeftMessage(id),
        "media state" when Wire.GetString(message, "participantId") is { } id => new ParticipantStateMessage(id, Wire.ReadState(message)),
        "chat" when Wire.GetString(message, "text") is { } text => new ChatReceivedMessage(
            Wire.GetString(message, "participantId") ?? string.Empty, Wire.GetString(message, "name") ?? string.Empty, text),
        "error" => new GroupErrorMessage(Wire.ReadError(message)),
        _ => null,
    };

    public static string WireName(GroupConnection connection) => connection == GroupConnection.Publish ? "publish" : "subscribe";

    private static GroupConnection? ReadConnection(JsonElement message) => Wire.GetString(message, "pc") switch
    {
        "publish" => GroupConnection.Publish,
        "subscribe" => GroupConnection.Subscribe,
        _ => null,
    };

    private static List<ParticipantInfo> ReadParticipants(JsonElement message)
    {
        var participants = new List<ParticipantInfo>();
        if (!message.TryGetProperty("participants", out var list) || list.ValueKind != JsonValueKind.Array) return participants;
        foreach (var item in list.EnumerateArray())
        {
            if (ReadParticipant(item) is { } info) participants.Add(info);
        }
        return participants;
    }

    private static ParticipantInfo? ReadParticipant(JsonElement participant) =>
        Wire.GetString(participant, "id") is { Length: > 0 } id
            ? new ParticipantInfo(id, Wire.GetString(participant, "name") ?? string.Empty, Wire.ReadState(participant))
            : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <param name="Participants">The others already in the room, in join order.</param>
/// <param name="E2eeKey">The room's key material (base64), when the room has E2EE.</param>
public sealed record JoinedMessage(string ParticipantId, IReadOnlyList<ParticipantInfo> Participants, bool E2ee, string? E2eeKey) : GroupMessage;
public sealed record GroupAnswerMessage(GroupConnection Connection, string Sdp) : GroupMessage;
public sealed record GroupOfferMessage(GroupConnection Connection, string Sdp) : GroupMessage;
public sealed record GroupCandidateMessage(GroupConnection Connection, IceCandidateDto Candidate) : GroupMessage;
public sealed record ParticipantJoinedMessage(ParticipantInfo Participant) : GroupMessage;
public sealed record ParticipantLeftMessage(string ParticipantId) : GroupMessage;
public sealed record ParticipantStateMessage(string ParticipantId, MediaState State) : GroupMessage;
public sealed record ChatReceivedMessage(string ParticipantId, string Name, string Text) : GroupMessage;
public sealed record GroupErrorMessage(ServerError Error) : GroupMessage;

/// <summary>What a group client sends sfu-server.</summary>
public static class GroupMessages
{
    /// <param name="e2eeKey">Our key material (base64), when joining with E2EE; the room keeps its creator's.</param>
    public static string Join(string roomId, string name, bool e2ee, string? e2eeKey) => e2ee && e2eeKey != null
        ? Wire.Message("join", ("roomId", roomId), ("name", name), ("e2ee", true), ("e2eeKey", e2eeKey))
        : Wire.Message("join", ("roomId", roomId), ("name", name), ("e2ee", e2ee));

    public static string PublishOffer(string sdp) => Wire.Message("offer", ("pc", "publish"), ("sdp", sdp));
    public static string SubscribeAnswer(string sdp) => Wire.Message("answer", ("pc", "subscribe"), ("sdp", sdp));

    public static string Candidate(GroupConnection connection, IceCandidateDto candidate) =>
        Wire.Message("candidate", ("pc", GroupMessage.WireName(connection)), ("candidate", Wire.Candidate(candidate)));

    public static string MediaState(MediaState state) => Wire.Message("media state", ("state", Wire.State(state)));
    public static string Chat(string text) => Wire.Message("chat", ("text", text));
    public static string Leave() => Wire.Message("leave");
}
