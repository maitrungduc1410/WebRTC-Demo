using WebRtcDemo.Effects.Imaging;

namespace WebRtcDemo.Effects.Models;

/// <summary>
/// One face, tracked the way MediaPipe's face landmarker does it: BlazeFace (short range) finds the
/// face, the face mesh model reads 478 landmarks from a rotated square crop around it, and later
/// frames crop around the previous landmarks instead of detecting again until the face is lost.
/// Returns the four points stickers are placed by, normalized to 0..1 (origin top left).
/// </summary>
public sealed class FaceLandmarker : IDisposable
{
    public const string DetectorFileName = "face_detector.onnx";
    public const string LandmarksFileName = "face_landmarks_detector.onnx";

    private const int DetectorSize = 128;
    private const int AnchorCount = 896;
    private const float MinDetectionScore = 0.5f;
    private const float NmsOverlap = 0.3f;
    private const int MeshSize = 256;
    private const int LandmarkCount = 478;
    private const float MinPresence = 0.5f;
    private const float RoiScale = 1.5f;
    // Face mesh points: the corners of both eyes, the nose tip and the middle of the lips (as on Android).
    private static readonly int[] s_eyeA = [33, 133];
    private static readonly int[] s_eyeB = [362, 263];
    private static readonly int[] s_noseTip = [1];
    private static readonly int[] s_lips = [13, 14];
    // Landmarks the next crop is turned by (outer eye corners), like MediaPipe.
    private const int RotationStart = 33;
    private const int RotationEnd = 263;

    private static readonly float[] s_anchors = CreateAnchors();

    private readonly OnnxModel _detector;
    private readonly OnnxModel _mesh;
    private readonly float[] _regressors;
    private readonly float[] _scores;
    private readonly float[] _landmarks;
    private readonly float[] _presence;
    private readonly BgraBitmap _detectorInput = new(DetectorSize, DetectorSize);
    private Roi? _tracked;

    public FaceLandmarker(OnnxModel detector, OnnxModel mesh)
    {
        _detector = detector;
        _mesh = mesh;
        _regressors = detector.Output("regressors");
        _scores = detector.Output("classificators");
        _landmarks = mesh.Output("Identity");
        _presence = mesh.Output("Identity_1");
        if (detector.Input().Length != DetectorSize * DetectorSize * 3 || _regressors.Length != AnchorCount * 16 || _scores.Length != AnchorCount
            || mesh.Input().Length != MeshSize * MeshSize * 3 || _landmarks.Length != LandmarkCount * 3 || _presence.Length != 1)
            throw new InvalidDataException("Unexpected face model tensor shapes.");
    }

    /// <summary>The result of the last <see cref="Run"/>.</summary>
    public FacePoints? Latest { get; private set; }

    /// <summary>Forget the tracked face (another camera or scene): the next run detects again.</summary>
    public void Reset()
    {
        _tracked = null;
        Latest = null;
    }

    public FacePoints? Run(BgraBitmap image)
    {
        if (_tracked is { } roi && Landmarks(image, roi) is { } tracked) return Latest = tracked;
        _tracked = null;
        return Latest = Detect(image) is { } found ? Landmarks(image, found) : null;
    }

    /// <summary>A square crop, in image pixels, turned clockwise by <see cref="Rotation"/>.</summary>
    private readonly record struct Roi(float X, float Y, float Size, float Rotation);

    private Roi? Detect(BgraBitmap image)
    {
        var scale = (float)DetectorSize / Math.Max(image.Width, image.Height);
        var w = Math.Clamp((int)MathF.Round(image.Width * scale), 1, DetectorSize);
        var h = Math.Clamp((int)MathF.Round(image.Height * scale), 1, DetectorSize);
        var left = (DetectorSize - w) / 2;
        var top = (DetectorSize - h) / 2;
        _detectorInput.Resize(w, h);
        Raster.ResizeArea(image, _detectorInput);
        Raster.ToTensor(_detectorInput, _detector.Input(), DetectorSize, left, top, scale: 2, bias: -1, fill: -1);
        _detector.Run();

        var best = -1;
        var bestScore = MinDetectionScore;
        for (var i = 0; i < AnchorCount; i++)
        {
            var score = Sigmoid(Math.Clamp(_scores[i], -100f, 100f));
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        if (best < 0) return null;

        // Weighted non-maximum suppression of the best cluster: average every box overlapping the best.
        var bestBox = Box(best);
        float total = 0, cx = 0, cy = 0, bw = 0, bh = 0, x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        for (var i = 0; i < AnchorCount; i++)
        {
            var score = Sigmoid(Math.Clamp(_scores[i], -100f, 100f));
            if (score < MinDetectionScore) continue;
            var box = Box(i);
            if (i != best && Overlap(bestBox, box) < NmsOverlap) continue;
            total += score;
            cx += box.X * score;
            cy += box.Y * score;
            bw += box.W * score;
            bh += box.H * score;
            var (ax, ay) = (s_anchors[i * 2], s_anchors[i * 2 + 1]);
            var r = i * 16;
            x0 += (_regressors[r + 4] / DetectorSize + ax) * score;
            y0 += (_regressors[r + 5] / DetectorSize + ay) * score;
            x1 += (_regressors[r + 6] / DetectorSize + ax) * score;
            y1 += (_regressors[r + 7] / DetectorSize + ay) * score;
        }
        // Normalized letterboxed input -> image pixels.
        float ToX(float v) => (v * DetectorSize - left) / scale;
        float ToY(float v) => (v * DetectorSize - top) / scale;
        var size = MathF.Max(bw, bh) / total * DetectorSize / scale * RoiScale;
        var rotation = -MathF.Atan2(-(ToY(y1 / total) - ToY(y0 / total)), ToX(x1 / total) - ToX(x0 / total));
        return new Roi(ToX(cx / total), ToY(cy / total), size, NormalizeAngle(rotation));
    }

    private (float X, float Y, float W, float H) Box(int i)
    {
        var r = i * 16;
        return (
            _regressors[r] / DetectorSize + s_anchors[i * 2],
            _regressors[r + 1] / DetectorSize + s_anchors[i * 2 + 1],
            _regressors[r + 2] / DetectorSize,
            _regressors[r + 3] / DetectorSize);
    }

    private static float Overlap((float X, float Y, float W, float H) a, (float X, float Y, float W, float H) b)
    {
        var w = MathF.Min(a.X + a.W / 2, b.X + b.W / 2) - MathF.Max(a.X - a.W / 2, b.X - b.W / 2);
        var h = MathF.Min(a.Y + a.H / 2, b.Y + b.H / 2) - MathF.Max(a.Y - a.H / 2, b.Y - b.H / 2);
        if (w <= 0 || h <= 0) return 0;
        var intersection = w * h;
        return intersection / (a.W * a.H + b.W * b.H - intersection);
    }

    private FacePoints? Landmarks(BgraBitmap image, Roi roi)
    {
        if (roi.Size < 8) return null;
        var cos = MathF.Cos(roi.Rotation);
        var sin = MathF.Sin(roi.Rotation);
        var step = roi.Size / MeshSize;
        var axisU = new Point(cos * step, sin * step);
        var axisV = new Point(-sin * step, cos * step);
        var origin = new Point(
            roi.X - (axisU.X + axisV.X) * MeshSize / 2,
            roi.Y - (axisU.Y + axisV.Y) * MeshSize / 2);
        Raster.WarpToTensor(image, _mesh.Input(), MeshSize, origin, axisU, axisV, scale: 1, bias: 0);
        _mesh.Run();
        if (Sigmoid(_presence[0]) < MinPresence) return null;

        // Crop pixels -> image pixels, tracking the bounding box for the next crop.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i < LandmarkCount; i++)
        {
            var u = _landmarks[i * 3];
            var v = _landmarks[i * 3 + 1];
            var x = origin.X + u * axisU.X + v * axisV.X;
            var y = origin.Y + u * axisU.Y + v * axisV.Y;
            _landmarks[i * 3] = x;
            _landmarks[i * 3 + 1] = y;
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }
        var start = At(RotationStart);
        var end = At(RotationEnd);
        _tracked = new Roi(
            (minX + maxX) / 2, (minY + maxY) / 2,
            MathF.Max(maxX - minX, maxY - minY) * RoiScale,
            NormalizeAngle(-MathF.Atan2(-(end.Y - start.Y), end.X - start.X)));

        Point Normalized(int[] indices)
        {
            float x = 0, y = 0;
            foreach (var i in indices)
            {
                x += _landmarks[i * 3];
                y += _landmarks[i * 3 + 1];
            }
            return new Point(x / indices.Length / image.Width, y / indices.Length / image.Height);
        }
        return new FacePoints(Normalized(s_eyeA), Normalized(s_eyeB), Normalized(s_noseTip), Normalized(s_lips));
    }

    private Point At(int index) => new(_landmarks[index * 3], _landmarks[index * 3 + 1]);

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    private static float NormalizeAngle(float angle) => angle - 2 * MathF.PI * MathF.Floor((angle + MathF.PI) / (2 * MathF.PI));

    /// <summary>BlazeFace short-range SSD anchors: strides 8, 16, 16, 16 with 2 anchors per cell each.</summary>
    private static float[] CreateAnchors()
    {
        var anchors = new float[AnchorCount * 2];
        var n = 0;
        foreach (var (stride, perCell) in new[] { (8, 2), (16, 6) })
        {
            var grid = DetectorSize / stride;
            for (var y = 0; y < grid; y++)
            for (var x = 0; x < grid; x++)
            for (var k = 0; k < perCell; k++)
            {
                anchors[n++] = (x + 0.5f) / grid;
                anchors[n++] = (y + 0.5f) / grid;
            }
        }
        return anchors;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _mesh.Dispose();
    }
}
