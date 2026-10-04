using WebRtcDemo.Effects.Imaging;

namespace WebRtcDemo.Effects.Models;

/// <summary>
/// MediaPipe selfie segmentation (selfie_segmenter, 256x256, RGB 0..1). The image is stretched to
/// the model's square input, so <see cref="Mask"/> covers the whole frame at 256x256: one
/// person-confidence byte per pixel (0 = background, 255 = person), blended over time like Android.
/// </summary>
public sealed class SelfieSegmenter : IDisposable
{
    public const string FileName = "selfie_segmenter.onnx";
    public const int Size = 256;
    // Weight of the newest mask in the temporal blend; lowers edge flicker between frames.
    private const float Smoothing = 0.7f;

    private readonly OnnxModel _model;
    private readonly BgraBitmap _input = new(Size, Size);
    private readonly float[] _smoothed = new float[Size * Size];
    private bool _hasPrevious;

    public SelfieSegmenter(OnnxModel model)
    {
        var input = model.Input();
        if (input.Length != Size * Size * 3 || model.Output(0).Length != Size * Size)
            throw new InvalidDataException("Unexpected selfie segmenter tensor shapes.");
        _model = model;
    }

    public byte[] Mask { get; } = new byte[Size * Size];

    /// <summary>The next mask starts fresh instead of blending with the previous scene.</summary>
    public void Reset() => _hasPrevious = false;

    /// <summary>Tests: makes the next runs throw, like a provider failing mid-call.</summary>
    internal Exception? Fault { get; set; }

    public void Run(BgraBitmap image)
    {
        if (Fault is { } fault) throw fault;
        Raster.ResizeArea(image, _input);
        Raster.ToTensor(_input, _model.Input(), Size, 0, 0, scale: 1, bias: 0, fill: 0);
        _model.Run();
        var output = _model.Output(0);
        var blend = _hasPrevious ? Smoothing : 1f;
        for (var i = 0; i < output.Length; i++)
        {
            var v = _smoothed[i] + (output[i] - _smoothed[i]) * blend;
            _smoothed[i] = v;
            Mask[i] = (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
        }
        _hasPrevious = true;
    }

    public void Dispose() => _model.Dispose();
}
