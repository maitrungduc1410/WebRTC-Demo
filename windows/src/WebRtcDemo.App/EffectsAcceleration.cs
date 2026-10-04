using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using WebRtcDemo.Core.Media;
using WebRtcDemo.Effects.Models;

namespace WebRtcDemo.App;

/// <summary>
/// Runs the effects models through Windows ML: the certified execution providers for this PC (GPU
/// or NPU, fetched by Windows on first use) are registered at launch, and sessions prefer the GPU.
/// Without them, or if a provider rejects a model, the models run on the CPU.
/// </summary>
internal static class EffectsAcceleration
{
    // Measured from launch and shared by every session, so a call that starts later waits less or not at all.
    private static readonly TimeSpan RegistrationBudget = TimeSpan.FromSeconds(10);
    private static readonly System.Diagnostics.Stopwatch s_sinceStart = new();
    private static Task<bool>? s_registered;

    public static EffectsSetup Setup() => new(
        Path.Combine(AppContext.BaseDirectory, "effects"),
        Path.Combine(AppContext.BaseDirectory, "models"),
        CreateOptions);

    /// <summary>
    /// Starts registering the providers, once per process (at launch). Sessions created before it
    /// finishes wait until 10 s after launch at most, then use the CPU.
    /// </summary>
    public static void Start()
    {
        if (s_registered != null) return;
        s_sinceStart.Start();
        s_registered = Task.Run(RegisterAsync);
    }

    private static async Task<bool> RegisterAsync()
    {
        try
        {
            _ = OrtEnv.Instance();
            await ExecutionProviderCatalog.GetDefault().EnsureAndRegisterCertifiedAsync();
            return true;
        }
        catch (Exception e)
        {
            App.Log("Windows ML execution providers unavailable, effects use the CPU: " + e.Message);
            return false;
        }
    }

    // Called on a model-loading thread, never the UI thread.
    private static SessionOptions CreateOptions(string model)
    {
        var options = OnnxDefaults.Cpu(model);
        if (s_registered is not { } registered) return options;
        try
        {
            var wait = RegistrationBudget - s_sinceStart.Elapsed;
            if (registered.Wait(wait > TimeSpan.Zero ? wait : TimeSpan.Zero) && registered.Result)
                options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.PREFER_GPU);
        }
        catch (OnnxRuntimeException e)
        {
            App.Log("Effects stay on the CPU: " + e.Message);
        }
        return options;
    }
}
