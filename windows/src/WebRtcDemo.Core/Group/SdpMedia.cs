using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Group;

/// <summary>One m-section of an SDP, as far as mapping receivers to participants needs it.</summary>
/// <param name="StreamId">The msid stream id: the publisher's participant id on the SFU.</param>
public sealed record SdpMediaSection(MediaKind? Kind, string? Mid, string Direction, bool Rejected, string? StreamId, string? TrackId)
{
    /// <summary>The remote side sends on it: a live track.</summary>
    public bool Sends => !Rejected && Direction is "sendonly" or "sendrecv";
}

public static class SdpMedia
{
    private static readonly string[] Directions = ["sendrecv", "sendonly", "recvonly", "inactive"];

    public static IReadOnlyList<SdpMediaSection> Parse(string sdp)
    {
        var sections = new List<SdpMediaSection>();
        Builder? current = null;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (current != null) sections.Add(current.Build());
                var fields = line[2..].Split(' ');
                current = new Builder
                {
                    Kind = fields[0] switch { "audio" => MediaKind.Audio, "video" => MediaKind.Video, _ => null },
                    PortZero = fields.Length > 1 && fields[1] == "0",
                };
                continue;
            }
            if (current == null || !line.StartsWith("a=", StringComparison.Ordinal)) continue;
            var attribute = line[2..];
            if (attribute.StartsWith("mid:", StringComparison.Ordinal))
            {
                current.Mid = attribute[4..].Trim();
            }
            else if (Array.IndexOf(Directions, attribute) >= 0)
            {
                current.Direction = attribute;
            }
            else if (attribute == "bundle-only")
            {
                current.BundleOnly = true;
            }
            else if (attribute.StartsWith("msid:", StringComparison.Ordinal) && current.StreamId == null)
            {
                current.SetMsid(attribute[5..]);
            }
            else if (attribute.StartsWith("ssrc:", StringComparison.Ordinal) && current.StreamId == null)
            {
                // "a=ssrc:<n> msid:<stream> <track>", for senders without a=msid.
                var msid = attribute.IndexOf(" msid:", StringComparison.Ordinal);
                if (msid >= 0) current.SetMsid(attribute[(msid + 6)..]);
            }
        }
        if (current != null) sections.Add(current.Build());
        return sections;
    }

    private sealed class Builder
    {
        public MediaKind? Kind;
        public bool PortZero;
        public bool BundleOnly;
        public string? Mid;
        public string Direction = "sendrecv";
        public string? StreamId;
        public string? TrackId;

        public void SetMsid(string value)
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] == "-") return;
            StreamId = parts[0];
            TrackId = parts.Length > 1 ? parts[1] : null;
        }

        // Port 0 rejects the m-line, unless it is bundle-only (it then shares the first line's port).
        public SdpMediaSection Build() => new(Kind, Mid, Direction, PortZero && !BundleOnly, StreamId, TrackId);
    }
}

/// <summary>
/// Which participant each subscribe receiver plays. The SFU recycles m-lines: libwebrtc keeps the
/// receiver (and its id) when an m-line is reused for another publisher, and does not always report
/// it again, so the latest offer is the truth: transceiver mid → m-section msid → participant.
/// </summary>
public static class ReceiverMap
{
    /// <returns>receiver id → participant id, for receivers whose m-line is live.</returns>
    public static Dictionary<string, string> Map(IReadOnlyList<SdpMediaSection> offer, IReadOnlyList<TransceiverInfo> transceivers)
    {
        var byMid = new Dictionary<string, SdpMediaSection>(StringComparer.Ordinal);
        foreach (var section in offer)
        {
            if (section.Mid != null) byMid[section.Mid] = section;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var transceiver in transceivers)
        {
            if (transceiver.Mid.Length == 0 || transceiver.ReceiverId.Length == 0) continue;
            if (!byMid.TryGetValue(transceiver.Mid, out var section) || !section.Sends || section.StreamId == null) continue;
            map[transceiver.ReceiverId] = section.StreamId;
        }
        return map;
    }
}
