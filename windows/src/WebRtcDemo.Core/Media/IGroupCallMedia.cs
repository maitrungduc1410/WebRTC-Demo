using WebRtcDemo.Core.Group;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>
/// The WebRTC side of a group call: a sendonly publish connection carrying the same local tracks
/// as a 1:1 call (so camera, screen, file and effects switch with SetTrack, never renegotiating),
/// and a recvonly subscribe connection that the SFU offers to. Called on the UI thread; events are
/// raised on it, never for connections closed since.
/// </summary>
public interface IGroupCallMedia
{
    event Action<GroupConnection, IceCandidate>? GroupIceCandidateGathered;
    event Action<GroupConnection, PeerConnectionState>? GroupConnectionStateChanged;
    /// <summary>A participant's video track appeared, changed or went away (null).</summary>
    event Action<string, IVideoFeed?>? ParticipantVideoChanged;

    bool HasGroupConnections { get; }

    /// <summary>
    /// Closes any connection and opens both: publish with sendonly audio + video (added even
    /// without a microphone or camera), VP8 first and, with E2EE, sender cryptors.
    /// </summary>
    /// <param name="streamId">Our msid; the SFU replaces it with our participant id anyway.</param>
    void OpenGroupConnections(bool e2ee, string streamId);
    void CloseGroupConnections();

    Task<SessionDescription> CreatePublishOfferAsync();
    Task SetPublishAnswerAsync(string sdp);
    /// <summary>Applies a subscribe offer, attaches receiver cryptors, maps receivers to participants, answers.</summary>
    Task<SessionDescription> AnswerSubscribeOfferAsync(string sdp);
    bool AddGroupIceCandidate(GroupConnection connection, IceCandidate candidate);

    /// <summary>Participant id → inbound audio level of its audio receiver (missing when unknown).</summary>
    Task<IReadOnlyDictionary<string, double>> GetParticipantAudioLevelsAsync();
}
