namespace WebRtcDemo.Effects;

public readonly record struct Point(float X, float Y);

/// <summary>Face points in pixels, y down. The two eyes can come in either order.</summary>
public readonly record struct FacePoints(Point EyeA, Point EyeB, Point Nose, Point Mouth);

/// <summary>A sticker's center, size and rotation (radians, clockwise on screen).</summary>
public readonly record struct Placement(float X, float Y, float Width, float Height, float Angle);

/// <summary>
/// Places a sticker on a face, matching placement.ts, StickerPlacement.kt and EffectsCatalog.swift.
/// The face's own axes are used, so the sticker tilts with the head: "right" runs from one eye to
/// the other, "up" from the mouth to the eyes.
/// </summary>
public static class StickerPlacement
{
    // Eyes to mouth over the distance between the eyes on a face looking at the camera. The larger
    // of the two scales is used so a sticker doesn't shrink when the head turns sideways.
    private const float EyesToMouthRatio = 1.2f;

    public static Placement? Place(FacePoints face, StickerOption sticker, float aspect)
    {
        var eyes = new Point((face.EyeA.X + face.EyeB.X) / 2, (face.EyeA.Y + face.EyeB.Y) / 2);
        var roughX = eyes.X - face.Mouth.X;
        var roughY = eyes.Y - face.Mouth.Y;
        // Up (0, -1) turns into right (1, 0).
        var forward = (face.EyeB.X - face.EyeA.X) * -roughY + (face.EyeB.Y - face.EyeA.Y) * roughX >= 0;
        var left = forward ? face.EyeA : face.EyeB;
        var right = forward ? face.EyeB : face.EyeA;

        var axisX = right.X - left.X;
        var axisY = right.Y - left.Y;
        var iod = MathF.Sqrt(axisX * axisX + axisY * axisY);
        if (iod < 1f) return null;
        var uxX = axisX / iod;
        var uxY = axisY / iod;
        var upX = uxY;
        var upY = -uxX;
        var unit = MathF.Max(iod, MathF.Sqrt(roughX * roughX + roughY * roughY) / EyesToMouthRatio);

        var anchor = sticker.Anchor switch
        {
            StickerAnchor.Nose => face.Nose,
            StickerAnchor.Mouth => face.Mouth,
            _ => eyes,
        };
        var width = sticker.Width * unit;
        return new Placement(
            anchor.X + unit * (sticker.OffsetX * uxX + sticker.OffsetY * upX),
            anchor.Y + unit * (sticker.OffsetX * uxY + sticker.OffsetY * upY),
            width,
            sticker.Height is { } height ? height * unit : width * aspect,
            MathF.Atan2(uxY, uxX));
    }
}

/// <summary>Evens out landmark jitter, and keeps the last placement through a few missed detections.</summary>
/// <param name="amount">Weight of the previous placement (0.4 on every platform).</param>
public sealed class PlacementSmoother(float amount = 0.4f, int keepFrames = 6)
{
    private int _missed;

    public Placement? Current { get; private set; }

    public Placement? Update(Placement? next)
    {
        if (next is not { } n)
        {
            if (++_missed > keepFrames) Current = null;
            return Current;
        }
        _missed = 0;
        if (Current is not { } prev) return Current = n;
        var k = 1 - amount;
        var delta = n.Angle - prev.Angle;
        delta = MathF.Atan2(MathF.Sin(delta), MathF.Cos(delta));
        return Current = new Placement(
            prev.X + (n.X - prev.X) * k,
            prev.Y + (n.Y - prev.Y) * k,
            prev.Width + (n.Width - prev.Width) * k,
            prev.Height + (n.Height - prev.Height) * k,
            prev.Angle + delta * k);
    }

    public void Reset()
    {
        Current = null;
        _missed = 0;
    }
}
