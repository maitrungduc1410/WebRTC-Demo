namespace WebRtcDemo.Interop;

// Values match rtc_shim.h, where they are part of the ABI.

public enum MediaKind { Audio = 0, Video = 1 }

public enum SignalingState
{
    Stable = 0,
    HaveLocalOffer = 1,
    HaveRemoteOffer = 2,
    HaveLocalPranswer = 3,
    HaveRemotePranswer = 4,
    Closed = 5,
}

public enum PeerConnectionState { New = 0, Connecting = 1, Connected = 2, Disconnected = 3, Failed = 4, Closed = 5 }

public enum IceConnectionState { New = 0, Checking = 1, Completed = 2, Connected = 3, Failed = 4, Disconnected = 5, Closed = 6 }

public enum IceGatheringState { New = 0, Gathering = 1, Complete = 2 }

public enum TransceiverDirection { SendRecv = 0, SendOnly = 1, RecvOnly = 2, Inactive = 3, Stopped = 4 }

public enum DataChannelState { Connecting = 0, Open = 1, Closing = 2, Closed = 3 }

public enum FrameCryptionState
{
    New = 0,
    Ok = 1,
    EncryptionFailed = 2,
    DecryptionFailed = 3,
    MissingKey = 4,
    KeyRatcheted = 5,
    InternalError = 6,
}

public enum KeyDerivation { Pbkdf2 = 0, Hkdf = 1 }

public enum DesktopSourceType { Screen = 0, Window = 1 }

public enum DesktopListEvent { Added = 0, Removed = 1, NameChanged = 2, ThumbnailChanged = 3 }

public enum CaptureState
{
    Running = 0,
    Paused = 1,
    /// <summary>Desktop capture stopped, or a non-looping file reached its end.</summary>
    Stopped = 2,
    /// <summary>For example the shared window was closed, or the file cannot be decoded.</summary>
    Failed = 3,
}

public enum LogSeverity { Verbose = 0, Info = 1, Warning = 2, Error = 3, None = 4 }
