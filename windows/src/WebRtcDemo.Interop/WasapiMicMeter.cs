using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WebRtcDemo.Interop;

/// <summary>A running microphone capture that only measures; disposing it releases the device.</summary>
public interface IAudioPeakMeter : IDisposable
{
    /// <summary>The highest sample magnitude in [0, 1] since the last call; null until capture runs or after it failed.</summary>
    double? TakePeak();
}

/// <summary>
/// A shared-mode WASAPI capture of one endpoint (the id the audio device module reports as the
/// device guid) or the default communications microphone, on its own thread. It never blocks the
/// caller: opening, reading and releasing all happen on that thread, and <see cref="Dispose"/>
/// only signals it, so the device is let go within one read period (~20 ms).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WasapiMicMeter : IAudioPeakMeter
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IMMDeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IAudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IAudioCaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");

    private const uint ClsctxAll = 0x17;
    private const uint CoinitMultithreaded = 0;
    private const int ECapture = 1;
    private const int ECommunications = 2;
    private const long BufferDuration = 1_000_000; // 100 ms in 100 ns units
    private const uint SilentFlag = 0x2;
    private const int ReadPeriodMs = 20;

    private enum SampleFormat { Unsupported, Float32, Int16, Int32 }

    private readonly string? _endpointId;
    private volatile bool _stopping;
    private volatile bool _capturing;
    private int _peakBits;

    private WasapiMicMeter(string? endpointId) => _endpointId = endpointId;

    /// <param name="endpointId">The endpoint to measure; null or unknown measures the default communications microphone.</param>
    public static WasapiMicMeter Start(string? endpointId)
    {
        var meter = new WasapiMicMeter(endpointId);
        new Thread(meter.Run) { IsBackground = true, Name = "WASAPI mic meter" }.Start();
        return meter;
    }

    public double? TakePeak()
    {
        if (!_capturing) return null;
        return BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));
    }

    public void Dispose() => _stopping = true;

    private void Raise(float peak)
    {
        // Non-negative floats order like their bit patterns.
        var bits = BitConverter.SingleToInt32Bits(Math.Min(peak, 1f));
        var current = Volatile.Read(ref _peakBits);
        while (bits > current)
        {
            var seen = Interlocked.CompareExchange(ref _peakBits, bits, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private void Run()
    {
        var initialized = CoInitializeEx(0, CoinitMultithreaded) >= 0;
        nint enumerator = 0, device = 0, client = 0, capture = 0;
        byte* format = null;
        try
        {
            var clsid = MMDeviceEnumeratorClsid;
            var iid = IMMDeviceEnumeratorIid;
            if (CoCreateInstance(&clsid, 0, ClsctxAll, &iid, &enumerator) < 0 || _stopping) return;
            if (_endpointId != null)
            {
                fixed (char* id = _endpointId)
                {
                    if (((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Vtbl(enumerator)[5])(enumerator, id, &device) < 0) device = 0;
                }
            }
            if (device == 0
                && ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Vtbl(enumerator)[4])(enumerator, ECapture, ECommunications, &device) < 0) return;

            var clientIid = IAudioClientIid;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Vtbl(device)[3])(device, &clientIid, ClsctxAll, 0, &client) < 0) return;
            if (((delegate* unmanaged[Stdcall]<nint, byte**, int>)Vtbl(client)[8])(client, &format) < 0) return;
            var sampleFormat = FormatOf(format);
            if (sampleFormat == SampleFormat.Unsupported) return;
            int channels = *(ushort*)(format + 2);
            if (((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, byte*, Guid*, int>)Vtbl(client)[3])(
                    client, 0, 0, BufferDuration, 0, format, null) < 0) return;
            var captureIid = IAudioCaptureClientIid;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(client)[14])(client, &captureIid, &capture) < 0) return;
            if (((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(client)[10])(client) < 0) return;
            _capturing = true;

            var getNextPacketSize = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(capture)[5];
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Vtbl(capture)[3];
            var releaseBuffer = (delegate* unmanaged[Stdcall]<nint, uint, int>)Vtbl(capture)[4];
            while (!_stopping)
            {
                Thread.Sleep(ReadPeriodMs);
                uint packet;
                // A failure here is usually the endpoint going away; the owner restarts on the fallback.
                while (getNextPacketSize(capture, &packet) >= 0 && packet > 0)
                {
                    byte* data;
                    uint frames, flags;
                    if (getBuffer(capture, &data, &frames, &flags, null, null) < 0) return;
                    if ((flags & SilentFlag) == 0) Raise(Peak(data, (int)(frames * channels), sampleFormat));
                    releaseBuffer(capture, frames);
                }
                if (getNextPacketSize(capture, &packet) < 0) return;
            }
            ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(client)[11])(client);
        }
        finally
        {
            _capturing = false;
            Release(capture);
            Release(client);
            Release(device);
            Release(enumerator);
            if (format != null) CoTaskMemFree(format);
            if (initialized) CoUninitialize();
        }
    }

    private static SampleFormat FormatOf(byte* format)
    {
        var tag = *(ushort*)format;
        var bits = *(ushort*)(format + 14);
        bool isFloat;
        if (tag == 0xFFFE && *(ushort*)(format + 16) >= 22)
        {
            var sub = *(Guid*)(format + 24);
            if (sub == FloatSubFormat) isFloat = true;
            else if (sub == PcmSubFormat) isFloat = false;
            else return SampleFormat.Unsupported;
        }
        else if (tag is 1 or 3)
        {
            isFloat = tag == 3;
        }
        else
        {
            return SampleFormat.Unsupported;
        }
        return (isFloat, bits) switch
        {
            (true, 32) => SampleFormat.Float32,
            (false, 16) => SampleFormat.Int16,
            (false, 32) => SampleFormat.Int32,
            _ => SampleFormat.Unsupported,
        };
    }

    private static float Peak(byte* data, int samples, SampleFormat format)
    {
        var peak = 0f;
        switch (format)
        {
            case SampleFormat.Float32:
                for (var i = 0; i < samples; i++) peak = Math.Max(peak, Math.Abs(((float*)data)[i]));
                break;
            case SampleFormat.Int16:
                for (var i = 0; i < samples; i++) peak = Math.Max(peak, Math.Abs(((short*)data)[i] / 32768f));
                break;
            case SampleFormat.Int32:
                for (var i = 0; i < samples; i++) peak = Math.Max(peak, Math.Abs(((int*)data)[i] / 2147483648f));
                break;
        }
        return peak;
    }

    private static void** Vtbl(nint unknown) => *(void***)unknown;

    private static void Release(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(unknown)[2])(unknown);
    }

    [LibraryImport("ole32")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);

    [LibraryImport("ole32")]
    private static partial void CoTaskMemFree(void* memory);
}
