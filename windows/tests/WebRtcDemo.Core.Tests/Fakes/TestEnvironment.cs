using System.Runtime.InteropServices;
using WebRtcDemo.Core.Settings;
using WebRtcDemo.Core.Signaling;
using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests.Fakes;

/// <summary>What the end-to-end tests need, and the reason to skip when it is missing.</summary>
public static class TestEnvironment
{
    /// <summary>Set WEBRTC_DEMO_SIGNALING_URL to use a server other than the default.</summary>
    public static string SignalingUrl =>
        Environment.GetEnvironmentVariable("WEBRTC_DEMO_SIGNALING_URL") is { Length: > 0 } url ? url : "http://localhost:4000";

    /// <summary>Set WEBRTC_DEMO_SFU_URL to use an sfu-server other than the default.</summary>
    public static string SfuUrl =>
        Environment.GetEnvironmentVariable("WEBRTC_DEMO_SFU_URL") is { Length: > 0 } url ? url : "ws://localhost:4001";

    private static readonly Lazy<string?> ServerProblem = new(() =>
        Probe(ServerAddress.HealthUrl(SignalingUrl), HttpServerProbe.SignalingServerName,
            $"no signaling server at {SignalingUrl}; start signaling-server/ with `npm run dev`"));

    private static readonly Lazy<string?> SfuProblem = new(() =>
        Probe(SfuAddress.HealthUrl(SfuUrl), HttpServerProbe.SfuServerName,
            $"no sfu-server at {SfuUrl}; start sfu-server/ with `go run .`"));

    private static string? Probe(Uri healthUrl, string name, string problem)
    {
        using var probe = new HttpServerProbe();
        return probe.ProbeAsync(healthUrl, name, CancellationToken.None).GetAwaiter().GetResult() ? null : problem;
    }

    private static readonly Lazy<string?> NativeProblem = new(() =>
    {
        var name = OperatingSystem.IsWindows() ? "rtc_shim.dll" : OperatingSystem.IsMacOS() ? "librtc_shim.dylib" : "librtc_shim.so";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path)) return $"{name} is not next to the tests; build the shim and pass -p:RtcShimNativeDir=<dir>";
        if (!NativeLibrary.TryLoad(path, out _)) return $"{name} could not be loaded";
        try
        {
            WebRtcRuntime.Initialize();
            return null;
        }
        catch (Exception e)
        {
            return "rtc_shim did not initialize: " + e.Message;
        }
    });

    public static void RequireServer()
    {
        if (ServerProblem.Value is { } problem) Assert.Skip(problem);
    }

    public static void RequireSfu()
    {
        if (SfuProblem.Value is { } problem) Assert.Skip(problem);
    }

    public static void RequireNative()
    {
        if (NativeProblem.Value is { } problem) Assert.Skip(problem);
    }

    public static string NewRoomId() => Random.Shared.Next(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
