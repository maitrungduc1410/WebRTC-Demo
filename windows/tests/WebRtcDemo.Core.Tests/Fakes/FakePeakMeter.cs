using WebRtcDemo.Interop;

namespace WebRtcDemo.Core.Tests.Fakes;

public sealed class FakePeakMeter(double? peak) : IAudioPeakMeter
{
    public double? Peak { get; set; } = peak;
    public bool Disposed { get; private set; }

    public double? TakePeak() => Disposed ? null : Peak;

    public void Dispose() => Disposed = true;
}
