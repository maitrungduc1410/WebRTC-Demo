namespace WebRtcDemo.Effects.Models;

public enum EffectsModelKind { Segmenter, Face }

/// <summary>A loaded model failed while running (not while loading).</summary>
public sealed class EffectsModelException : Exception
{
    public EffectsModelException()
    {
    }

    public EffectsModelException(string message) : base(message)
    {
    }

    public EffectsModelException(string message, Exception inner) : base(message, inner)
    {
    }

    public EffectsModelException(EffectsModelKind model, Exception inner) : base($"The {model} model failed.", inner)
    {
        Model = model;
    }

    public EffectsModelKind Model { get; }
}

/// <summary>
/// Loads the models on first use and keeps them for the life of the app (a session takes a while
/// to create, especially on a GPU). A failed load is retried on the next request; a model that
/// failed while running is dropped with <see cref="MarkBroken"/> and comes back on the CPU.
/// </summary>
public sealed class EffectsModels(string directory, SessionOptionsFactory options) : IDisposable
{
    private readonly Lock _lock = new();
    private Task<SelfieSegmenter>? _segmenter;
    private Task<FaceLandmarker>? _face;
    private bool _segmenterOnCpu;
    private bool _faceOnCpu;
    private bool _disposed;

    public string Directory { get; } = directory;

    /// <summary>Loaded models, or null until their task has finished.</summary>
    public SelfieSegmenter? Segmenter => Completed(_segmenter);

    public FaceLandmarker? Face => Completed(_face);

    public bool IsOnCpu(EffectsModelKind kind)
    {
        lock (_lock) return kind == EffectsModelKind.Segmenter ? _segmenterOnCpu : _faceOnCpu;
    }

    public Task<SelfieSegmenter> LoadSegmenterAsync()
    {
        lock (_lock)
        {
            var cpu = _segmenterOnCpu;
            return Load(ref _segmenter, () => new SelfieSegmenter(Open(SelfieSegmenter.FileName, cpu)));
        }
    }

    public Task<FaceLandmarker> LoadFaceAsync()
    {
        lock (_lock)
        {
            var cpu = _faceOnCpu;
            return Load(ref _face, () =>
            {
                var detector = Open(FaceLandmarker.DetectorFileName, cpu);
                try
                {
                    return new FaceLandmarker(detector, Open(FaceLandmarker.LandmarksFileName, cpu));
                }
                catch
                {
                    detector.Dispose();
                    throw;
                }
            });
        }
    }

    /// <summary>
    /// Forgets a model that failed while running; the next load creates it again with the CPU.
    /// Returns the broken instance, for the caller to dispose once nothing runs it any more.
    /// </summary>
    public IDisposable? MarkBroken(EffectsModelKind kind)
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (kind == EffectsModelKind.Segmenter)
            {
                _segmenterOnCpu = true;
                return Take(ref _segmenter);
            }
            _faceOnCpu = true;
            return Take(ref _face);
        }
    }

    private static IDisposable? Take<T>(ref Task<T>? slot) where T : class, IDisposable
    {
        // Only a model that has run can break, so the task has completed.
        var task = slot;
        slot = null;
        return task is { IsCompletedSuccessfully: true } ? task.Result : null;
    }

    private OnnxModel Open(string file, bool cpuOnly)
    {
        var path = Path.Combine(Directory, file);
        if (!File.Exists(path)) throw new FileNotFoundException("Effects model is missing.", path);
        var model = Path.GetFileNameWithoutExtension(file);
        if (!cpuOnly)
        {
            try
            {
                using var sessionOptions = options(model);
                return new OnnxModel(path, sessionOptions);
            }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                // A GPU or NPU provider that can't take the model; the CPU always can.
            }
        }
        using var cpu = OnnxDefaults.Cpu(model);
        return new OnnxModel(path, cpu);
    }

    // Under _lock.
    private Task<T> Load<T>(ref Task<T>? slot, Func<T> create) where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (slot is { IsFaulted: false, IsCanceled: false } existing) return existing;
        return slot = Task.Run(create);
    }

    private static T? Completed<T>(Task<T>? task) where T : class =>
        task is { IsCompletedSuccessfully: true } ? task.Result : null;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Sessions still loading are disposed when they finish.
        DisposeWhenDone(_segmenter);
        DisposeWhenDone(_face);
    }

    private static void DisposeWhenDone<T>(Task<T>? task) where T : IDisposable =>
        task?.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
