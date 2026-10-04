using WebRtcDemo.Interop.Native;

namespace WebRtcDemo.Interop;

/// <summary>Process-wide libwebrtc state: initialize once before creating a factory.</summary>
public static unsafe class WebRtcRuntime
{
    private static readonly Lock Gate = new();
    private static Action<string>? _logSink;
    private static bool _initialized;

    /// <summary>Raised when a managed handler throws inside a native callback (which must not unwind).</summary>
    public static event Action<Exception>? CallbackException;

    public static int AbiVersion => NativeMethods.rtc_shim_abi_version();

    public static string LibWebRtcVersion => NativeMethods.Utf8(NativeMethods.rtc_shim_libwebrtc_version());

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            var abi = NativeMethods.rtc_shim_abi_version();
            if (abi != NativeMethods.ExpectedAbiVersion)
            {
                throw new WebRtcException($"rtc_shim ABI {abi} does not match the bindings (expected {NativeMethods.ExpectedAbiVersion}); rebuild the shim.");
            }
            if (NativeMethods.rtc_initialize() == 0) throw WebRtcException.FromLastError("rtc_initialize failed");
            _initialized = true;
        }
    }

    public static void Terminate()
    {
        lock (Gate)
        {
            if (!_initialized) return;
            SetLogSink(null);
            NativeMethods.rtc_terminate();
            _initialized = false;
        }
    }

    /// <summary>Forwards libwebrtc logging (on WebRTC threads) to <paramref name="sink"/>; null stops it.</summary>
    public static void SetLogSink(Action<string>? sink, LogSeverity minimum = LogSeverity.Warning)
    {
        lock (Gate)
        {
            _logSink = sink;
            NativeMethods.rtc_set_log_callback((int)minimum, sink == null ? null : Callbacks.Log, 0);
        }
    }

    internal static void RaiseLog(string message) => _logSink?.Invoke(message);

    internal static void ReportCallbackException(Exception e)
    {
        try
        {
            CallbackException?.Invoke(e);
        }
        catch
        {
            // Nothing may escape into native code.
        }
    }
}
