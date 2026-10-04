using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WebRtcDemo.Effects.Models;

/// <summary>
/// One ONNX session with every input and output bound once to preallocated float buffers, so a
/// run allocates nothing: fill <see cref="Input"/>, call <see cref="Run"/>, read <see cref="Output"/>.
/// Not thread-safe; each model has its own worker thread.
/// </summary>
public sealed class OnnxModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly OrtIoBinding _binding;
    private readonly RunOptions _run = new();
    private readonly float[][] _inputs;
    private readonly float[][] _outputs;
    private readonly List<OrtValue> _values = [];
    private readonly Dictionary<string, int> _outputIndex = [];

    public OnnxModel(string path, SessionOptions options)
    {
        _session = new InferenceSession(path, options);
        try
        {
            _binding = _session.CreateIoBinding();
            _inputs = Bind(_session.InputMetadata, (name, value) => _binding.BindInput(name, value), null);
            _outputs = Bind(_session.OutputMetadata, (name, value) => _binding.BindOutput(name, value), _outputIndex);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public float[] Input(int index = 0) => _inputs[index];

    public float[] Output(int index) => _outputs[index];

    public float[] Output(string name) => _outputs[_outputIndex[name]];

    public void Run() => _session.RunWithBinding(_run, _binding);

    private float[][] Bind(IReadOnlyDictionary<string, NodeMetadata> metadata, Action<string, OrtValue> bind, Dictionary<string, int>? index)
    {
        var buffers = new float[metadata.Count][];
        var i = 0;
        foreach (var (name, node) in metadata)
        {
            if (node.ElementDataType != TensorElementType.Float)
                throw new InvalidDataException($"{name} is {node.ElementDataType}; only float tensors are supported.");
            var shape = node.Dimensions.Select(d => d > 0 ? (long)d : 1L).ToArray();
            var buffer = new float[shape.Aggregate(1L, (a, b) => a * b)];
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

/// <summary>Creates session options for each model; the app plugs in hardware acceleration here.</summary>
public delegate SessionOptions SessionOptionsFactory(string model);

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
