using WebRtcDemo.Effects;
using WebRtcDemo.Effects.Imaging;
using WebRtcDemo.Effects.Models;

namespace WebRtcDemo.Core.Tests;

/// <summary>
/// Real inference with the committed ONNX models on the CPU ONNX Runtime, against a portrait
/// (TestData/portrait.jpg, 500x624). Expected points were read off the picture and agree with a
/// NumPy reimplementation of MediaPipe's pre/post-processing run on the same models.
/// </summary>
public sealed class EffectsModelTests : IDisposable
{
    private static readonly string s_models = Path.Combine(AppContext.BaseDirectory, "models");
    private static readonly string s_portrait = Path.Combine(AppContext.BaseDirectory, "TestData", "portrait.jpg");

    private readonly EffectsModels _models = new(s_models, OnnxDefaults.Cpu);

    public void Dispose() => _models.Dispose();

    private static BgraBitmap Portrait() => ImageFile.Load(s_portrait, maxSide: 2048, premultiply: false);

    private static float MaskAt(byte[] mask, float x, float y) =>
        mask[(int)(y * SelfieSegmenter.Size) * SelfieSegmenter.Size + (int)(x * SelfieSegmenter.Size)] / 255f;

    [Fact]
    public async Task Segmenter_separates_the_person_from_the_room()
    {
        var image = Portrait();
        Assert.Equal((500, 624), (image.Width, image.Height));
        var segmenter = await _models.LoadSegmenterAsync();
        segmenter.Run(image);
        var mask = segmenter.Mask;

        // Face, tie and suit are the person; flag, curtain, window and desk are not.
        Assert.InRange(MaskAt(mask, 245 / 500f, 140 / 624f), 0.9f, 1f);
        Assert.InRange(MaskAt(mask, 215 / 500f, 330 / 624f), 0.9f, 1f);
        Assert.InRange(MaskAt(mask, 80 / 500f, 480 / 624f), 0.9f, 1f);
        Assert.InRange(MaskAt(mask, 30 / 500f, 200 / 624f), 0f, 0.1f);
        Assert.InRange(MaskAt(mask, 110 / 500f, 60 / 624f), 0f, 0.1f);
        Assert.InRange(MaskAt(mask, 460 / 500f, 300 / 624f), 0f, 0.1f);
        Assert.InRange(MaskAt(mask, 470 / 500f, 590 / 624f), 0f, 0.1f);
        var person = mask.Average(v => v / 255f);
        Assert.InRange(person, 0.4f, 0.62f);
    }

    [Fact]
    public async Task Face_tracker_finds_eyes_nose_and_mouth_and_keeps_tracking()
    {
        var image = Portrait();
        var face = await _models.LoadFaceAsync();
        static void Near(Point p, float x, float y)
        {
            Assert.InRange(p.X * 500, x - 6, x + 6);
            Assert.InRange(p.Y * 624, y - 6, y + 6);
        }
        for (var run = 0; run < 3; run++)
        {
            // Run 0 detects; later runs crop around the previous landmarks.
            var points = face.Run(image);
            Assert.NotNull(points);
            var (left, right) = points.Value.EyeA.X < points.Value.EyeB.X ? (points.Value.EyeA, points.Value.EyeB) : (points.Value.EyeB, points.Value.EyeA);
            Near(left, 222, 109);
            Near(right, 273, 108);
            Near(points.Value.Nose, 249, 142);
            Near(points.Value.Mouth, 249, 166);
        }

        var blank = new BgraBitmap(320, 240);
        face.Reset();
        Assert.Null(face.Run(blank));
    }

    [Fact]
    public async Task Processor_blurs_the_room_and_draws_the_sticker()
    {
        var catalog = EffectsCatalog.Load(EffectsCatalogTests.RepoEffects);
        var sticker = catalog.Stickers.First(s => s.Id == "crown");
        var scene = await EffectsScene.LoadAsync(catalog, new EffectsSelection("blur-strong", sticker.Id), _models, videos: null, previous: null, TestContext.Current.CancellationToken);
        Assert.True(scene.NeedsMask && scene.NeedsFace);

        var image = Portrait();
        var frames = 0;
        var output = new TaskCompletionSource<(byte[] Y, int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var processor = new EffectsProcessor(_models, i420 =>
        {
            // Wait until the sticker is on (the face result arrives a few frames after the mask).
            if (++frames < 15) return;
            output.TrySetResult((i420.Y.AsSpan(0, i420.Width * i420.Height).ToArray(), i420.Width, i420.Height));
        });
        processor.FirstFrame += _ => firstFrame.TrySetResult();
        processor.Failed += (_, e) => output.TrySetException(e);
        processor.SetScene(scene);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(60));
        var pump = Task.Run(async () =>
        {
            while (!output.Task.IsCompleted && !cancel.IsCancellationRequested)
            {
                processor.Submit(image.Pixels, image.Width, image.Height, image.Stride);
                await Task.Delay(20, CancellationToken.None);
            }
        }, CancellationToken.None);
        var (y, width, height) = await output.Task.WaitAsync(cancel.Token);
        await pump;
        Assert.True(firstFrame.Task.IsCompleted);
        Assert.Equal((500, 624), (width, height));

        var original = new I420Buffer();
        Raster.BgraToI420(image, original);
        double Detail(byte[] luma, int x0, int y0, int size)
        {
            // Mean absolute horizontal gradient: high for the flag's stripes, low once blurred.
            double sum = 0;
            for (var yy = y0; yy < y0 + size; yy++)
            for (var xx = x0; xx < x0 + size; xx++)
                sum += Math.Abs(luma[yy * width + xx + 1] - luma[yy * width + xx]);
            return sum / (size * size);
        }
        // The flag on the left is blurred; the suit is untouched.
        Assert.True(Detail(y, 5, 250, 40) < Detail(original.Y, 5, 250, 40) * 0.5);
        Assert.Equal(Detail(original.Y, 220, 300, 20), Detail(y, 220, 300, 20), 1.0);
        // The crown (2.2 eye distances wide, 1.8 above the eyes) covers the hair, which the mask keeps.
        var changed = 0;
        for (var yy = 30; yy < 60; yy++)
        for (var xx = 215; xx < 280; xx++)
            if (Math.Abs(y[yy * width + xx] - original.Y[yy * width + xx]) > 30) changed++;
        Assert.True(changed > 30 * 65 / 4, $"only {changed} pixels changed under the crown");
        scene.Dispose();
    }

    /// <summary>Submits the portrait until <paramref name="until"/> completes (60 s at most).</summary>
    private static async Task Pump(EffectsProcessor processor, Task until)
    {
        var image = Portrait();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(60));
        while (!until.IsCompleted && !cancel.IsCancellationRequested)
        {
            processor.Submit(image.Pixels, image.Width, image.Height, image.Stride);
            await Task.Delay(20, CancellationToken.None);
        }
        await until.WaitAsync(cancel.Token);
    }

    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task A_model_that_breaks_while_drawing_is_retired_and_reloaded_on_the_cpu()
    {
        var accelerated = new List<string>();
        using var models = new EffectsModels(s_models, model =>
        {
            lock (accelerated) accelerated.Add(model);
            return OnnxDefaults.Cpu(model);
        });
        var catalog = EffectsCatalog.Load(EffectsCatalogTests.RepoEffects);
        var selection = new EffectsSelection("blur-strong");
        var frames = 0;
        var drawing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var processor = new EffectsProcessor(models, _ =>
        {
            if (Interlocked.Increment(ref frames) >= 3) drawing.TrySetResult();
        });
        processor.Failed += (_, e) => failed.TrySetResult(e);

        var scene = await EffectsScene.LoadAsync(catalog, selection, models, videos: null, previous: null, TestContext.Current.CancellationToken);
        processor.SetScene(scene);
        await Pump(processor, drawing.Task);
        Assert.Equal(["selfie_segmenter"], accelerated);

        // The provider breaks mid-call: no more frames, and the error names the model.
        var original = models.Segmenter!;
        original.Fault = new InvalidOperationException("device removed");
        await Pump(processor, failed.Task);
        var error = Assert.IsType<EffectsModelException>(await failed.Task);
        Assert.Equal(EffectsModelKind.Segmenter, error.Model);
        var stalled = Volatile.Read(ref frames);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(stalled, Volatile.Read(ref frames));

        // Marked broken: forgotten, disposed by the compositor, and the next load is a CPU session.
        processor.SetScene(null);
        var broken = models.MarkBroken(EffectsModelKind.Segmenter);
        Assert.Same(original, broken);
        Assert.Null(models.Segmenter);
        Assert.True(models.IsOnCpu(EffectsModelKind.Segmenter));
        processor.Retire(broken!);
        var reloaded = await EffectsScene.LoadAsync(catalog, selection, models, videos: null, previous: scene, TestContext.Current.CancellationToken);
        Assert.NotSame(original, models.Segmenter);
        Assert.Equal(["selfie_segmenter"], accelerated);

        // The same selection draws again.
        drawing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref frames, 0);
        processor.SetScene(reloaded);
        await Pump(processor, drawing.Task);
        scene.Dispose();
        reloaded.Dispose();
    }

    [Fact]
    public async Task Retired_models_are_disposed_on_the_compositor_or_with_the_processor()
    {
        var probe = new Probe();
        var processor = new EffectsProcessor(_models, _ => { });
        processor.Retire(probe);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!probe.Disposed && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(probe.Disposed);

        processor.Dispose();
        var late = new Probe();
        processor.Retire(late);
        Assert.True(late.Disposed);
    }
}
