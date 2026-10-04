namespace WebRtcDemo.Core.Call;

/// <summary>Gradient avatar colors, picked exactly like iOS (PeerPlaceholderView.swift) and Android.</summary>
public static class Avatar
{
    public static IReadOnlyList<(uint Start, uint End)> Palettes { get; } =
    [
        (0x7C4DFF, 0x448AFF),
        (0xFF6E40, 0xFF4081),
        (0x00BFA5, 0x00B0FF),
        (0xFFAB00, 0xFF5252),
        (0x651FFF, 0xD500F9),
        (0x00C853, 0x64DD17),
    ];

    /// <summary>The seed the other platforms use for the remote peer.</summary>
    public static string RemoteSeed(string roomId) => "peer-" + roomId;

    public const string LocalSeed = "you";

    /// <summary>Java's String.hashCode over UTF-16 code units, with Int32 wrap-around.</summary>
    public static int JavaHashCode(string value)
    {
        var hash = 0;
        foreach (var unit in value)
        {
            hash = unchecked(hash * 31 + unit);
        }
        return hash;
    }

    /// <summary>RGB colors (0xRRGGBB) for <paramref name="seed"/>.</summary>
    public static (uint Start, uint End) Colors(string seed)
    {
        var hash = JavaHashCode(seed);
        // Swift's Int32.magnitude: |hash| as UInt32, which is also defined for Int32.MinValue.
        var magnitude = hash < 0 ? unchecked((uint)-(long)hash) : (uint)hash;
        return Palettes[(int)(magnitude % (uint)Palettes.Count)];
    }
}
