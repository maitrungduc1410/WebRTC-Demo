using System.Runtime.InteropServices;

namespace WebRtcDemo.Interop.Native;

// One SafeHandle type per shim handle, so a handle can only be passed where the C API expects it
// and is released exactly once. LibraryImport AddRefs them around each call, so Dispose racing a
// call on another thread releases the native object only after that call returns.

internal abstract class ShimHandle : SafeHandle
{
    protected ShimHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal static T Wrap<T>(nint raw) where T : ShimHandle, new()
    {
        var wrapped = new T();
        wrapped.SetHandle(raw);
        return wrapped;
    }
}

internal sealed class FactoryHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_factory_release(handle); return true; }
}

internal sealed class PeerConnectionHandle : ShimHandle
{
    protected override bool ReleaseHandle() => NativeMethods.rtc_pc_release(handle) != 0;
}

internal sealed class TrackHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_track_release(handle); return true; }
}

internal sealed class SenderHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_sender_release(handle); return true; }
}

internal sealed class VideoSourceHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_video_source_release(handle); return true; }
}

internal sealed class VideoSinkHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_video_sink_release(handle); return true; }
}

internal sealed class DataChannelHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_data_channel_release(handle); return true; }
}

internal sealed class KeyProviderHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_key_provider_release(handle); return true; }
}

internal sealed class DesktopListHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_desktop_list_release(handle); return true; }
}

internal sealed class MediaSourceHandle : ShimHandle
{
    protected override bool ReleaseHandle() { NativeMethods.rtc_media_source_release(handle); return true; }
}
