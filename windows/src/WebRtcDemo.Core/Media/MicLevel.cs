using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Media;

/// <summary>The local microphone indicator's level, as on the web, Android and iOS.</summary>
public static class MicLevel
{
    /// <summary>Each poll keeps at least this share of the previous level, so the bars fall smoothly.</summary>
    public const double Decay = 0.75;

    /// <summary>Changes smaller than this don't redraw; a level below it is silence.</summary>
    public const double Step = 0.01;

    /// <summary>A linear peak in [0, 1] on the -50..-10 dBFS scale: quiet room noise is 0, a raised voice 1.</summary>
    public static double FromPeak(double peak) =>
        peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 50) / 40, 0, 1);

    /// <summary>The level after <paramref name="previous"/> for a poll that measured <paramref name="peak"/> (null: nothing measured).</summary>
    public static double Next(double previous, double? peak)
    {
        var next = Math.Max(FromPeak(peak ?? 0), previous * Decay);
        return next < Step ? 0 : next;
    }
}

/// <summary>
/// Measures the microphone while no peer connection does (waiting alone): starts a meter on the
/// selected device, restarts it when that device changes (a pick or a fallback), and releases it
/// before WebRTC opens the device for a call.
/// </summary>
internal sealed class IdleMicMeter(Func<string?, IAudioPeakMeter?> start)
{
    private IAudioPeakMeter? _meter;
    private bool _started;
    private string? _deviceId;

    public bool Running => _meter != null;

    /// <summary>The peak since the last read, or null while the meter is (re)opening or unavailable.</summary>
    public double? Read(string? deviceId)
    {
        if (!_started || !string.Equals(deviceId, _deviceId, StringComparison.Ordinal))
        {
            Stop();
            _meter = start(deviceId);
            _started = true;
            _deviceId = deviceId;
            return null;
        }
        return _meter?.TakePeak();
    }

    public void Stop()
    {
        _meter?.Dispose();
        _meter = null;
        _started = false;
        _deviceId = null;
    }
}
