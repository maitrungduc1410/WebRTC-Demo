using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WebRtcDemo.Effects.Models;

/// <summary>
/// One ONNX session with every input and output bound once to preallocated float buffers, so a
/// run allocates nothing: fill <see cref="Input"/>, call <see cref="Run"/>, read <see cref="Output"/>.
/// Not thread-safe; each model has its own worker thread.
/// <para>
/// A session on a GPU or NPU can be given a CPU session to check it against: some drivers run a
/// model without any error and return nonsense (an empty mask, no face). The first runs go through
/// both; while they agree the CPU one is dropped, and when they don't the model stays on the CPU.
/// Either way <see cref="Input"/> and <see cref="Output"/> keep their buffers.
/// </para>
/// </summary>
public sealed class OnnxModel : IDisposable
{
    /// <summary>
    /// Agreeing runs, on pictures that tell something, before the accelerated session is trusted.
    /// One is enough: a broken driver is off on every such picture, and the face detector only runs
    /// until a face is found.
    /// </summary>
    private const int AgreementsNeeded = 1;
    /// <summary>Runs checked one by one; after them (nobody in front of the camera all along) only every <see cref="LateCheckInterval"/>th.</summary>
    private const int EarlyChecks = 100;
    private const int LateCheckInterval = 30;
    /// <summary>Of an output: sum of |accelerated - CPU| over the sum of |CPU| (precision differences stay well below).</summary>
    private const double MaxDifference = 0.1;
    /// <summary>Mean |value| below which a result says little, such as the mask of an empty room.</summary>
    private const double MeaningfulMean = 0.01;

    private readonly string _name;
    private readonly Action<string>? _log;
    private Session? _accelerated;
    private Session? _cpu;
    private readonly float[][] _inputs;
    private readonly float[][] _outputs;
    private readonly IReadOnlyDictionary<string, int> _outputIndex;
    private long _runs;
    private int _agreements;

    public OnnxModel(string path, SessionOptions options) : this(path, options, check: null)
    {
    }

    /// <param name="check">CPU options to check the first runs of <paramref name="options"/> against.</param>
    /// <param name="log">Says how the check ended.</param>
    public OnnxModel(string path, SessionOptions options, SessionOptions? check, Action<string>? log = null)
    {
        _name = Path.GetFileNameWithoutExtension(path);
        _log = log;
        var main = new Session(path, options, inputs: null);
        _inputs = main.Inputs;
        _outputs = main.Outputs;
        _outputIndex = main.OutputIndex;
        if (check == null)
        {
            _cpu = main;
            return;
        }
        _accelerated = main;
        try
        {
            _cpu = new Session(path, check, _inputs);
        }
        catch
        {
            main.Dispose();
            throw;
        }
    }

    /// <summary>Tests: changes the accelerated session's outputs after each run, like a broken driver.</summary>
    internal Action<float[][]>? Corrupt { get; set; }

    /// <summary>Tests: the accelerated session is still being checked against the CPU.</summary>
    internal bool Checking => _accelerated != null && _cpu != null;

    /// <summary>Tests: the outputs come from the CPU session.</summary>
    internal bool OnCpu => _accelerated == null;

    public float[] Input(int index = 0) => _inputs[index];

    public float[] Output(int index) => _outputs[index];

    public float[] Output(string name) => _outputs[_outputIndex[name]];

    public void Run()
    {
        if (_accelerated == null)
        {
            _cpu!.Run();
            _cpu.CopyOutputsTo(_outputs, _outputIndex);
            return;
        }
        _accelerated.Run();
        Corrupt?.Invoke(_outputs);
        if (_cpu == null) return;
        _runs++;
        if (_runs > EarlyChecks && _runs % LateCheckInterval != 0) return;

        _cpu.Run();
        var (difference, meaningful) = Compare();
        // NaN from the accelerated session fails this too.
        if (!(difference <= MaxDifference))
        {
            _cpu.CopyOutputsTo(_outputs, _outputIndex);
            _accelerated.Dispose();
            _accelerated = null;
            _log?.Invoke($"Effects: {_name} gives wrong results on the GPU/NPU ({difference:P0} off the CPU); it runs on the CPU.");
            return;
        }
        if (meaningful) _agreements++;
        if (_agreements < AgreementsNeeded) return;
        _cpu.Dispose();
        _cpu = null;
        _log?.Invoke($"Effects: {_name} on the GPU/NPU matches the CPU.");
    }

    /// <summary>The largest relative difference over the outputs, and whether the CPU result says anything.</summary>
    private (double Difference, bool Meaningful) Compare()
    {
        double worst = 0, total = 0;
        long count = 0;
        foreach (var (name, index) in _outputIndex)
        {
            var accelerated = _outputs[index];
            var cpu = _cpu!.Output(name);
            double diff = 0, magnitude = 0;
            for (var i = 0; i < cpu.Length; i++)
            {
                diff += Math.Abs(accelerated[i] - cpu[i]);
                magnitude += Math.Abs(cpu[i]);
            }
            var relative = diff / Math.Max(magnitude, cpu.Length * MeaningfulMean);
            if (!(relative <= worst)) worst = relative;
            total += magnitude;
            count += cpu.Length;
        }
        return (worst, total >= count * MeaningfulMean);
    }

    public void Dispose()
    {
        _accelerated?.Dispose();
        _cpu?.Dispose();
        _accelerated = _cpu = null;
    }

    /// <summary>A session and its bindings; inputs can be shared with another session of the same model.</summary>
    private sealed class Session : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly OrtIoBinding _binding;
        private readonly RunOptions _run = new();
        private readonly List<OrtValue> _values = [];
        private readonly List<(string Name, OrtValue Value)> _boundInputs = [];
        private readonly Dictionary<string, int> _outputIndex = [];

        public Session(string path, SessionOptions options, float[][]? inputs)
        {
            _session = new InferenceSession(path, options);
            try
            {
                _binding = _session.CreateIoBinding();
                Inputs = Bind(_session.InputMetadata, (name, value) =>
                {
                    _binding.BindInput(name, value);
                    _boundInputs.Add((name, value));
                }, null, inputs);
                Outputs = Bind(_session.OutputMetadata, (name, value) => _binding.BindOutput(name, value), _outputIndex, null);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public float[][] Inputs { get; } = [];
        public float[][] Outputs { get; } = [];
        public IReadOnlyDictionary<string, int> OutputIndex => _outputIndex;

        public float[] Output(string name) => Outputs[_outputIndex[name]];

        public void Run()
        {
            // Binding copies an input to the device that needs it (a GPU), so the copy made at
            // construction would be every run's input. On the CPU it copies nothing.
            foreach (var (name, value) in _boundInputs) _binding.BindInput(name, value);
            _session.RunWithBinding(_run, _binding);
        }

        public void CopyOutputsTo(float[][] outputs, IReadOnlyDictionary<string, int> index)
        {
            if (ReferenceEquals(outputs, Outputs)) return;
            foreach (var (name, i) in index) Array.Copy(Output(name), outputs[i], outputs[i].Length);
        }

        private float[][] Bind(IReadOnlyDictionary<string, NodeMetadata> metadata, Action<string, OrtValue> bind,
            Dictionary<string, int>? index, float[][]? shared)
        {
            var buffers = new float[metadata.Count][];
            var i = 0;
            foreach (var (name, node) in metadata)
            {
                if (node.ElementDataType != TensorElementType.Float)
                    throw new InvalidDataException($"{name} is {node.ElementDataType}; only float tensors are supported.");
                var shape = node.Dimensions.Select(d => d > 0 ? (long)d : 1L).ToArray();
                var length = shape.Aggregate(1L, (a, b) => a * b);
                var buffer = shared?[i] ?? new float[length];
                if (buffer.Length != length) throw new InvalidDataException($"{name} doesn't match the other session.");
                var value = OrtValue.CreateTensorValueFromMemory(buffer, shape);
                _values.Add(value);
                bind(name, value);
                buffers[i] = buffer;
                index?.Add(name, i);
                i++;
            }
            return buffers;
        }

        public void Dispose()
        {
            _binding?.Dispose();
            foreach (var value in _values) value.Dispose();
            _run.Dispose();
            _session.Dispose();
        }
    }
}

/// <summary>
/// Creates session options for each model; the app plugs in hardware acceleration here. Null when
/// nothing is accelerated: the model then runs on the CPU without being checked against it.
/// </summary>
public delegate SessionOptions? SessionOptionsFactory(string model);

public static class OnnxDefaults
{
    /// <summary>Plain CPU execution: two threads per model, no busy-waiting between runs.</summary>
    public static SessionOptions Cpu(string model)
    {
        _ = model;
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        return options;
    }
}