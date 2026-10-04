using WebRtcDemo.Effects.Imaging;
using WebRtcDemo.Effects.Models;

namespace WebRtcDemo.Effects;

/// <summary>
/// Applies the background and sticker to camera frames, off the capture thread.
///
///  1. <see cref="Submit"/> (capture thread) copies the frame into a single pending slot; a frame
///     that arrives before the previous one was taken replaces it, so a slow machine drops frames
///     instead of adding latency.
///  2. The compositor thread takes it, hands a small copy to each idle model worker (segmenter and
///     face tracker run on their own threads; a busy one simply skips frames), and composites with
///     the latest results: person mask, picture / video frame / blurred camera, sticker.
///  3. The result is converted to I420 and passed to the output callback (still on the compositor thread).
///
/// Frames are dropped until the scene's first mask, so the real room is never sent. All buffers are
/// reused; a steady stream at a fixed size allocates nothing.
/// </summary>
public sealed class EffectsProcessor : IDisposable
{
    private const int AnalysisLongSide = 512;
    private const float MaskEdgeLow = 0.3f;
    private const float MaskEdgeHigh = 0.7f;
    // Blur runs on a copy scaled down until the radius is this many pixels (as on Android).
    private const float BlurWorkingRadius = 3f;
    private const float BlurWorkingSigma = 2.5f;
    // Until the first video frame, and if the video can't be played, the room is blurred.
    private const float VideoFallbackBlur = 0.02f;

    private static readonly byte[] s_edge = Raster.EdgeTable(MaskEdgeLow, MaskEdgeHigh);

    private readonly EffectsModels _models;
    private readonly Action<I420Buffer> _output;
    private readonly Thread _thread;
    private readonly Lock _pendingLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<IDisposable> _retired = [];
    private volatile bool _disposed;

    private BgraBitmap _pending = new(2, 2);
    private bool _hasPending;
    private BgraBitmap _frame = new(2, 2);

    private volatile EffectsScene? _scene;
    private EffectsScene? _drawnScene;
    private int _sceneGeneration;
    private int _reportedGeneration = -1;
    private int _firstFrameGeneration = -1;

    private readonly Worker<SelfieSegmenter> _segmenterWorker;
    private readonly Worker<FaceLandmarker> _faceWorker;
    private readonly BgraBitmap _analysis = new(2, 2);
    private readonly Results _latest = new();
    private bool _hasMask;
    private long _maskVersion;
    private long _faceVersion;
    private readonly PlacementSmoother _smoother = new();

    private readonly ResampleTable _maskTable = new();
    private readonly ResampleTable _backgroundTable = new();
    private readonly BgraBitmap _blurred = new(2, 2);
    private readonly BgraBitmap _blurTemp = new(2, 2);
    private BlurKernel _blurKernel = new(BlurWorkingSigma);
    private readonly BgraBitmap _videoFrame = new(2, 2);
    private long _videoVersion;
    private IBackgroundVideo? _videoSource;
    private readonly I420Buffer _i420 = new();

    public EffectsProcessor(EffectsModels models, Action<I420Buffer> output)
    {
        _models = models;
        _output = output;
        _segmenterWorker = new Worker<SelfieSegmenter>("Effects segmenter",
            (model, image) => model.Run(image),
            (model, results) => Array.Copy(model.Mask, results.Mask, results.Mask.Length),
            model => model.Reset(),
            (from, to) => Array.Copy(from.Mask, to.Mask, to.Mask.Length));
        _faceWorker = new Worker<FaceLandmarker>("Effects face tracker",
            (model, image) => model.Run(image),
            (model, results) => results.Face = model.Latest,
            model => model.Reset(),
            (from, to) => to.Face = from.Face);
        _thread = new Thread(Run) { IsBackground = true, Name = "Effects compositor" };
        _thread.Start();
    }

    /// <summary>Raised on the compositor thread with the first frame drawn for each scene.</summary>
    public event Action<EffectsScene>? FirstFrame;

    /// <summary>
    /// Raised on the compositor thread, once per scene, when drawing it fails; an
    /// <see cref="EffectsModelException"/> says which model broke.
    /// </summary>
    public event Action<EffectsScene, Exception>? Failed;

    public EffectsScene? Scene => _scene;

    /// <summary>Swaps what is drawn. Null stops processing (frames are dropped).</summary>
    public void SetScene(EffectsScene? scene)
    {
        lock (_pendingLock)
        {
            _scene = scene;
            _sceneGeneration++;
        }
    }

    /// <summary>The next frames show another scene (camera switch): old masks and faces no longer apply.</summary>
    public void InvalidateAnalysis()
    {
        _segmenterWorker.Invalidate();
        _faceWorker.Invalidate();
        lock (_pendingLock)
        {
            _invalidate = true;
            // A frame still waiting is of the old picture.
            _hasPending = false;
        }
    }

    private bool _invalidate;

    /// <summary>
    /// Disposes a model (from <see cref="EffectsModels.MarkBroken"/>) on the compositor thread once
    /// no worker runs it; later frames only see the models loaded after it was marked.
    /// </summary>
    public void Retire(IDisposable model)
    {
        lock (_pendingLock)
        {
            if (!_disposed)
            {
                _retired.Add(model);
                _wake.Set();
                return;
            }
        }
        model.Dispose();
    }

    private void DisposeRetired()
    {
        IDisposable[] retired;
        lock (_pendingLock)
        {
            if (_retired.Count == 0) return;
            retired = [.. _retired];
            _retired.Clear();
        }
        // A worker may still be finishing the run that failed.
        while (!(_segmenterWorker.IsIdle && _faceWorker.IsIdle) && !_disposed) Thread.Sleep(1);
        foreach (var model in retired) model.Dispose();
    }

    /// <summary>Capture thread: queues a copy of a BGRA frame, replacing one not yet processed.</summary>
    public void Submit(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (_disposed || _scene == null || width < 2 || height < 2) return;
        lock (_pendingLock)
        {
            _pending.CopyFrom(bgra, width, height, stride);
            _hasPending = true;
        }
        _wake.Set();
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_disposed) return;
            DisposeRetired();
            EffectsScene? scene;
            int generation;
            bool invalidate;
            lock (_pendingLock)
            {
                if (!_hasPending) continue;
                (_pending, _frame) = (_frame, _pending);
                _hasPending = false;
                scene = _scene;
                generation = _sceneGeneration;
                invalidate = _invalidate;
                _invalidate = false;
            }
            if (scene == null) continue;
            try
            {
                if (Process(scene, generation, invalidate) && _firstFrameGeneration != generation)
                {
                    _firstFrameGeneration = generation;
                    FirstFrame?.Invoke(scene);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Dropped rather than sent unprocessed; reported once per scene.
                if (_reportedGeneration != generation)
                {
                    _reportedGeneration = generation;
                    Failed?.Invoke(scene, e);
                }
            }
        }
    }

    /// <summary>Draws and emits one frame; false when it was dropped (no mask yet).</summary>
    private bool Process(EffectsScene scene, int generation, bool invalidate)
    {
        if (_reportedGeneration == generation) return false;
        var previous = _drawnScene;
        _drawnScene = scene;
        if (invalidate || (scene.NeedsMask && previous?.NeedsMask != true))
        {
            _hasMask = false;
            _segmenterWorker.Invalidate();
        }
        if (invalidate || (scene.NeedsFace && previous?.Sticker?.Id != scene.Sticker?.Id))
        {
            _smoother.Reset();
            _faceWorker.Invalidate();
        }
        if (_videoSource != scene.Video)
        {
            _videoSource = scene.Video;
            _videoVersion = 0;
        }

        var segmenter = scene.NeedsMask ? _models.Segmenter ?? throw new InvalidOperationException("Segmentation model isn't loaded.") : null;
        var face = scene.NeedsFace ? _models.Face ?? throw new InvalidOperationException("Face model isn't loaded.") : null;
        if (previous != scene)
        {
            _segmenterWorker.ClearError();
            _faceWorker.ClearError();
        }
        if (segmenter != null && _segmenterWorker.Error is { } segmenterError)
            throw new EffectsModelException(EffectsModelKind.Segmenter, segmenterError);
        if (face != null && _faceWorker.Error is { } faceError)
            throw new EffectsModelException(EffectsModelKind.Face, faceError);

        var frame = _frame;
        var segmenterIdle = segmenter != null && _segmenterWorker.IsIdle;
        var faceIdle = face != null && _faceWorker.IsIdle;
        if (segmenterIdle || faceIdle)
        {
            var scale = Math.Min(1f, (float)AnalysisLongSide / Math.Max(frame.Width, frame.Height));
            _analysis.Resize(Math.Max(2, (int)MathF.Round(frame.Width * scale)), Math.Max(2, (int)MathF.Round(frame.Height * scale)));
            Raster.ResizeArea(frame, _analysis);
            if (segmenterIdle) _segmenterWorker.Submit(segmenter!, _analysis);
            if (faceIdle) _faceWorker.Submit(face!, _analysis);
        }

        if (segmenter != null)
        {
            if (_segmenterWorker.TakeResult(ref _maskVersion, _latest)) _hasMask = true;
            if (!_hasMask) return false;
            var (background, cover) = PrepareBackground(scene, frame);
            _maskTable.Configure(SelfieSegmenter.Size, SelfieSegmenter.Size, frame.Width, frame.Height, cover: false);
            _backgroundTable.Configure(background.Width, background.Height, frame.Width, frame.Height, cover);
            Raster.Composite(frame, background, _backgroundTable, _latest.Mask, SelfieSegmenter.Size, SelfieSegmenter.Size, _maskTable, s_edge);
        }

        if (face != null && scene is { Sticker: { } sticker, StickerImage: { } image })
        {
            if (_faceWorker.TakeResult(ref _faceVersion, _latest))
            {
                var placed = _latest.Face is { } p
                    ? StickerPlacement.Place(Scaled(p, frame.Width, frame.Height), sticker, (float)image.Height / image.Width)
                    : null;
                _smoother.Update(placed);
            }
            if (_smoother.Current is { } placement) Raster.DrawSticker(frame, image, placement);
        }

        Raster.BgraToI420(frame, _i420);
        _output(_i420);
        return true;
    }

    private (BgraBitmap Image, bool Cover) PrepareBackground(EffectsScene scene, BgraBitmap frame)
    {
        switch (scene.Background.Kind)
        {
            case BackgroundKind.Image when scene.Picture is { } picture:
                return (picture, true);
            case BackgroundKind.Video:
                if (scene.Video is { Failed: false } video)
                {
                    video.TryCopyLatest(ref _videoVersion, _videoFrame);
                    if (_videoVersion != 0) return (_videoFrame, true);
                }
                return (Blur(frame, VideoFallbackBlur), false);
            default:
                return (Blur(frame, scene.Background.Blur), false);
        }
    }

    /// <summary>A blurred copy of the frame at reduced size, stretched back over it by the composite.</summary>
    private BgraBitmap Blur(BgraBitmap frame, float fraction)
    {
        var radius = Math.Max(2f, fraction * frame.Width);
        var scale = Math.Min(1f, BlurWorkingRadius / radius);
        _blurred.Resize(Math.Max(2, (int)MathF.Round(frame.Width * scale)), Math.Max(2, (int)MathF.Round(frame.Height * scale)));
        Raster.ResizeArea(frame, _blurred);
        // Small radii stay at full size; scale the kernel down with them.
        var sigma = BlurWorkingSigma * Math.Min(1f, radius * scale / BlurWorkingRadius);
        if (MathF.Abs(_blurKernel.Sigma - sigma) > 0.01f) _blurKernel = new BlurKernel(sigma);
        Raster.GaussianBlur(_blurred, _blurKernel, _blurTemp);
        return _blurred;
    }

    private static FacePoints Scaled(FacePoints p, int width, int height)
    {
        Point S(Point q) => new(q.X * width, q.Y * height);
        return new FacePoints(S(p.EyeA), S(p.EyeB), S(p.Nose), S(p.Mouth));
    }

    public void Dispose()
    {
        lock (_pendingLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _wake.Set();
        _thread.Join();
        _segmenterWorker.Dispose();
        _faceWorker.Dispose();
        _wake.Dispose();
        foreach (var model in _retired) model.Dispose();
        _retired.Clear();
    }

    /// <summary>What the workers produce: a published copy per worker, and the compositor's copy.</summary>
    private sealed class Results
    {
        public byte[] Mask { get; } = new byte[SelfieSegmenter.Size * SelfieSegmenter.Size];
        public FacePoints? Face { get; set; }
    }

    /// <summary>
    /// A model on its own thread with one input slot. Results of work submitted before
    /// <see cref="Invalidate"/> are thrown away, and the model starts fresh.
    /// </summary>
    private sealed class Worker<T> : IDisposable where T : class
    {
        private readonly Action<T, BgraBitmap> _run;
        private readonly Action<T, Results> _publish;
        private readonly Action<T> _reset;
        private readonly Action<Results, Results> _copy;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new(false);
        private readonly BgraBitmap _input = new(2, 2);
        private readonly Results _results = new();
        private T? _model;
        private volatile bool _busy;
        private volatile bool _disposed;
        private volatile Exception? _error;
        private int _epoch;
        private int _submittedEpoch;
        private int _lastEpoch = -1;
        private long _version;
        private bool _hasResult;

        public Worker(string name, Action<T, BgraBitmap> run, Action<T, Results> publish, Action<T> reset, Action<Results, Results> copy)
        {
            _copy = copy;
            _run = run;
            _publish = publish;
            _reset = reset;
            _thread = new Thread(Run) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public bool IsIdle => !_busy;

        public Exception? Error => _error;

        public void ClearError() => _error = null;

        /// <summary>Compositor thread, only while idle: copies the image and starts a run.</summary>
        public void Submit(T model, BgraBitmap image)
        {
            if (_busy || _error != null) return;
            _input.CopyFrom(image.Pixels, image.Width, image.Height, image.Stride);
            _model = model;
            _submittedEpoch = Volatile.Read(ref _epoch);
            _busy = true;
            _wake.Set();
        }

        public void Invalidate()
        {
            lock (_results)
            {
                _epoch++;
                _hasResult = false;
            }
        }

        /// <summary>Reads the newest result if it is newer than <paramref name="version"/>.</summary>
        public bool TakeResult(ref long version, Results into)
        {
            lock (_results)
            {
                if (!_hasResult || _version == version) return false;
                version = _version;
                _copy(_results, into);
                return true;
            }
        }

        private void Run()
        {
            while (true)
            {
                _wake.WaitOne();
                if (_disposed) return;
                var model = _model!;
                var epoch = _submittedEpoch;
                try
                {
                    if (epoch != _lastEpoch)
                    {
                        _lastEpoch = epoch;
                        _reset(model);
                    }
                    _run(model, _input);
                    lock (_results)
                    {
                        if (epoch == _epoch)
                        {
                            _publish(model, _results);
                            _version++;
                            _hasResult = true;
                        }
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _error = e;
                }
                finally
                {
                    _busy = false;
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _wake.Set();
            _thread.Join();
            _wake.Dispose();
        }
    }
}
