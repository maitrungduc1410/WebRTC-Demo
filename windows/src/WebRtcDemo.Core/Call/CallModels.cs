namespace WebRtcDemo.Core.Call;

public enum CallPhase { Idle, Waiting, Connecting, Connected }

public enum VideoFit { Fill, Fit }

public enum ToastKind { Info, Success, Warning, Error }

/// <param name="Glyph">A Segoe Fluent Icons code point, or null.</param>
public sealed record Toast(string Text, ToastKind Kind = ToastKind.Info, string? Glyph = null);

/// <param name="Sender">Who wrote a received group message ("Name · 1a2b"); null in 1:1 calls and for ours.</param>
public sealed record ChatMessage(int Id, string Text, bool IsLocal, DateTimeOffset Timestamp, string? Sender = null);

public static class VideoLayout
{
    /// <summary>
    /// Fill (crop) by default; fit (letterbox) when the remote shares its screen, where cropping
    /// would hide content, or when the frame and the view have different orientations.
    /// </summary>
    public static VideoFit DefaultFit(bool remoteIsScreen, int frameWidth, int frameHeight, double viewWidth, double viewHeight)
    {
        if (remoteIsScreen) return VideoFit.Fit;
        if (frameWidth <= 0 || frameHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return VideoFit.Fill;
        var frameLandscape = frameWidth >= frameHeight;
        var viewLandscape = viewWidth >= viewHeight;
        return frameLandscape == viewLandscape ? VideoFit.Fill : VideoFit.Fit;
    }

    /// <summary>
    /// View units per frame pixel for the frame to cover the view (the view crops what overflows);
    /// null while either size is unknown. Videos are always laid out at this size.
    /// </summary>
    public static double? CoverScale(int frameWidth, int frameHeight, double viewWidth, double viewHeight) =>
        frameWidth <= 0 || frameHeight <= 0 || viewWidth <= 0 || viewHeight <= 0
            ? null
            : Math.Max(viewWidth / frameWidth, viewHeight / frameHeight);

    /// <summary>
    /// The scale on the covering video that shows <paramref name="fit"/>: 1 to fill, and for fit
    /// the shrink that makes the whole frame visible (letterboxed), as on iOS, Android and macOS.
    /// </summary>
    public static double FitScale(VideoFit fit, int frameWidth, int frameHeight, double viewWidth, double viewHeight)
    {
        if (fit == VideoFit.Fill || frameWidth <= 0 || frameHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return 1;
        var scaleX = viewWidth / frameWidth;
        var scaleY = viewHeight / frameHeight;
        return Math.Min(scaleX, scaleY) / Math.Max(scaleX, scaleY);
    }
}

/// <summary>Whether the picked effect shows yet: loading covers decoding files and loading models.</summary>
public enum EffectsStatus { Off, Loading, On }
