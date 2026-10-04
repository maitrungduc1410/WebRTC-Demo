using WebRtcDemo.Interop.Native;

namespace WebRtcDemo.Interop;

public sealed record DeviceInfo(int Index, string Name, string Id);

public sealed record IceServer(string Uri, string? Username = null, string? Password = null);

/// <param name="Type">"offer" or "answer".</param>
public sealed record SessionDescription(string Type, string Sdp);

public sealed record IceCandidate(string? SdpMid, int SdpMLineIndex, string Candidate);

/// <param name="ReceiverId">Stable for the receiver's life, also when its m-line is reused.</param>
/// <param name="StreamId">The remote msid stream id ("" when none) when the track was reported.</param>
public sealed record RemoteTrackInfo(string ReceiverId, string StreamId);

/// <param name="Mid">Empty until negotiated.</param>
public sealed record TransceiverInfo(MediaKind Kind, TransceiverDirection Direction, TransceiverDirection CurrentDirection, string Mid, string ReceiverId);

/// <summary>Mirrors libwebrtc's KeyProviderOptions.</summary>
public sealed record KeyProviderOptions
{
    public bool SharedKey { get; init; } = true;
    public byte[] RatchetSalt { get; init; } = [];
    public byte[]? UncryptedMagicBytes { get; init; }
    public int RatchetWindowSize { get; init; }
    public int FailureTolerance { get; init; } = -1;
    public int KeyRingSize { get; init; } = 16;
    public bool DiscardFrameWhenCryptorNotReady { get; init; }
    public KeyDerivation KeyDerivation { get; init; } = KeyDerivation.Pbkdf2;

    /// <summary>
    /// The options every client of the demo uses (ARCHITECTURE.md 9.3); peers only decrypt each
    /// other's frames when these match.
    /// </summary>
    public static KeyProviderOptions Demo { get; } = new()
    {
        SharedKey = true,
        RatchetSalt = "LKFrameEncryptionKey"u8.ToArray(),
        UncryptedMagicBytes = null,
        RatchetWindowSize = 0,
        FailureTolerance = -1,
        KeyRingSize = 16,
        DiscardFrameWhenCryptorNotReady = false,
        KeyDerivation = KeyDerivation.Pbkdf2,
    };
}

/// <summary>
/// One frame as 8-bit BGRA (DXGI_FORMAT_B8G8R8A8_UNORM), already rotated upright. Only valid
/// during the callback; copy what you need.
/// </summary>
public readonly ref struct VideoFrame(ReadOnlySpan<byte> data, int width, int height, int stride, int rotation)
{
    public ReadOnlySpan<byte> Data { get; } = data;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    /// <summary>The rotation that was applied (0/90/180/270), for information only.</summary>
    public int Rotation { get; } = rotation;
}

/// <summary>Runs on a WebRTC thread (decoder or capturer). Must return quickly.</summary>
public delegate void VideoFrameHandler(VideoFrame frame);

public sealed class WebRtcException(string message) : Exception(message)
{
    internal static WebRtcException FromLastError(string what)
    {
        var detail = NativeMethods.LastError();
        return new WebRtcException(string.IsNullOrEmpty(detail) ? what : $"{what}: {detail}");
    }
}
