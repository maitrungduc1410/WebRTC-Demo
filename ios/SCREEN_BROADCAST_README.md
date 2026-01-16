# iOS Screen Broadcasting Implementation Guide

This implementation enables system-wide screen sharing for the iOS WebRTC Demo app using a Broadcast Extension with Unix domain socket IPC (Inter-Process Communication).

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                Main App (WebRTCDemo)                     │
│  ┌────────────────────────────────────────────────┐    │
│  │ BroadcastScreenCapturer                        │    │
│  │  - RTCVideoCapturer subclass                   │    │
│  │  - Receives frames via socket                  │    │
│  └────────────┬───────────────────────────────────┘    │
│               │                                          │
│  ┌────────────▼───────────────────────────────────┐    │
│  │ SocketConnectionFrameReader                    │    │
│  │  - Reads frames from socket                    │    │
│  │  - Decodes JPEG -> CVPixelBuffer               │    │
│  │  - Converts to RTCVideoFrame                   │    │
│  └────────────┬───────────────────────────────────┘    │
│               │                                          │
│  ┌────────────▼───────────────────────────────────┐    │
│  │ SocketConnection (Server)                      │    │
│  │  - Unix domain socket server                   │    │
│  │  - Listens for broadcast extension             │    │
│  └───────────────────────────────────────────────  ┘    │
└─────────────────────────────────────────────────────────┘
                        ▲
                        │ IPC via Unix Socket
                        │ (App Groups)
                        ▼
┌─────────────────────────────────────────────────────────┐
│      Broadcast Extension (WebRTCDemoScreenBroadcast)     │
│  ┌─────────────────────────────────────────────────┐   │
│  │ SampleHandlerSocket                             │   │
│  │  - Receives screen frames from ReplayKit        │   │
│  │  - Manages lifecycle                            │   │
│  └────────────┬────────────────────────────────────┘   │
│               │                                          │
│  ┌────────────▼────────────────────────────────────┐   │
│  │ SampleUploader                                  │   │
│  │  - Encodes frames as JPEG                       │   │
│  │  - Wraps in HTTP-style message                  │   │
│  │  - Sends via socket in chunks                   │   │
│  └────────────┬────────────────────────────────────┘   │
│               │                                          │
│  ┌────────────▼────────────────────────────────────┐   │
│  │ SocketConnection (Client)                       │   │
│  │  - Unix domain socket client                    │   │
│  │  - Connects to main app                         │   │
│  └─────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
```

## Setup Instructions

### 1. App Groups Configuration

Both the main app and broadcast extension must share the same App Group:

**Entitlements** (already configured):
- Main App: `WebRTCDemo.entitlements`
- Broadcast Extension: `WebRTCDemoScreenBroadcast.entitlements`

Both should have:
```xml
<key>com.apple.security.application-groups</key>
<array>
    <string>group.com.ducmai.webrtc.broadcast</string>
</array>
```

### 2. Project Structure

**Main App Files:**
- `SocketConnection.swift` - Unix domain socket implementation
- `SocketConnectionFrameReader.swift` - Frame decoder
- `BroadcastScreenCapturer.swift` - RTCVideoCapturer for broadcast
- `PeerConnectionClient.swift` - Added screen sharing methods
- `CallViewController.swift` - UI integration

**Broadcast Extension Files:**
- `SampleHandlerSocket.swift` - Broadcast handler
- `SampleUploader.swift` - Frame encoder and uploader
- `SocketConnection.swift` (shared) - Socket client

### 3. Xcode Configuration

1. **Add Files to Targets:**
   - Add `SocketConnection.swift` to BOTH targets:
     - WebRTCDemo
     - WebRTCDemoScreenBroadcast
   
   - Main app only files:
     - `BroadcastScreenCapturer.swift`
     - `SocketConnectionFrameReader.swift`
   
   - Broadcast extension only files:
     - `SampleHandlerSocket.swift`
     - `SampleUploader.swift`

2. **Update Broadcast Extension Bundle ID:**
   - Open Xcode project settings
   - Select `WebRTCDemoScreenBroadcast` target
   - Verify Bundle Identifier matches: `com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast`
   - Update in `PeerConnectionClient.swift` line ~550:
     ```swift
     picker.preferredExtension = "YOUR_MAIN_BUNDLE_ID.WebRTCDemoScreenBroadcast"
     ```

3. **Info.plist Configuration:**
   - No additional keys needed beyond default Broadcast Extension setup

## Usage

### Starting Screen Share

1. User taps the **Share** button in CallViewController
2. `webRTCClient.startScreenCapture()` is called
3. System broadcast picker appears
4. User selects "WebRTCDemoScreenBroadcast"
5. Broadcast extension starts:
   - Creates socket connection to main app
   - Receives screen frames from ReplayKit
   - Encodes frames as JPEG
   - Sends via socket
6. Main app receives frames:
   - Decodes JPEG to pixel buffers
   - Converts to RTCVideoFrame
   - Injects into WebRTC peer connection

### Stopping Screen Share

1. User taps **Share** button again (or broadcast UI)
2. `webRTCClient.stopScreenCapture()` is called
3. Socket connection closes
4. Camera track is restored

## Technical Details

### Socket Communication

- **Protocol:** Unix domain socket (AF_UNIX)
- **File Path:** `<App Group Container>/rtc_SSFD`
- **Format:** HTTP-style framed messages
- **Headers:**
  - `Content-Length`: Size of JPEG data
  - `Buffer-Width`: Frame width
  - `Buffer-Height`: Frame height
  - `Buffer-Orientation`: Rotation (0, 90, 180, 270)

### Frame Encoding

Broadcast Extension:
1. Receive `CMSampleBuffer` from ReplayKit
2. Extract `CVPixelBuffer`
3. Convert to JPEG using CIImage
4. Wrap in HTTP message
5. Send in 10KB chunks

### Frame Decoding

Main App:
1. Read HTTP message from socket
2. Extract headers and JPEG data
3. Create `CVPixelBuffer` (BGRA format)
4. Render JPEG to pixel buffer using CIContext
5. Convert to RTCVideoFrame with proper rotation
6. Pass to RTCVideoCapturer delegate

### Performance Considerations

- **Compression:** JPEG quality = 1.0 (max quality)
- **Scale Factor:** 1.0 (no downscaling)
- **Chunk Size:** 10KB for socket writes
- **Frame Rate:** Depends on ReplayKit (typically 30fps)
- **Latency:** ~50-100ms from screen to peer

## Troubleshooting

### Socket Connection Fails

**Symptoms:** "failure: socket file missing" in logs

**Solutions:**
1. Verify App Group identifier matches in both targets
2. Check that main app creates socket before broadcast starts
3. Ensure file path is correct:
   ```swift
   let sharedContainer = FileManager.default.containerURL(
       forSecurityApplicationGroupIdentifier: "group.com.ducmai.webrtc.broadcast"
   )
   ```

### Broadcast Picker Doesn't Appear

**Solutions:**
1. Update `preferredExtension` bundle ID in `PeerConnectionClient.swift`
2. Ensure broadcast extension target is built and included
3. Check signing & capabilities for broadcast extension

### No Video Frames Received

**Solutions:**
1. Check broadcast extension logs: `log stream --predicate 'subsystem == "com.webrtcdemo.broadcast"'`
2. Verify socket connection opened successfully
3. Ensure frames are being sent from SampleUploader
4. Check frame reader is processing messages

### Video Appears Rotated

The orientation is handled automatically based on `Buffer-Orientation` header. If incorrect:
1. Check `RPVideoSampleOrientationKey` from ReplayKit
2. Verify rotation mapping in `SocketConnectionFrameReader.swift`

## Debugging

### Enable Logging

Both files use OSLog:

**View Logs:**
```bash
# Main app logs
log stream --predicate 'subsystem == "com.webrtcdemo"' --level debug

# Broadcast extension logs  
log stream --predicate 'subsystem == "com.webrtcdemo.broadcast"' --level debug
```

### Check Socket File

```bash
# From iOS device shell (requires jailbreak or debugging privileges)
ls -la /private/var/mobile/Containers/Shared/AppGroup/<UUID>/rtc_SSFD
```

### Common Log Messages

**Success:**
- `"Broadcast started"`
- `"Connection opened successfully"`
- `"server stream open completed"`

**Errors:**
- `"failure: socket file missing"` - Main app hasn't created socket
- `"failure connecting"` - Network queue not running or socket closed
- `"CVPixelBufferCreate failed"` - Invalid frame dimensions

## Comparison with RPScreenRecorder

| Feature | Broadcast Extension | RPScreenRecorder |
|---------|-------------------|------------------|
| Scope | System-wide (all apps) | In-app only |
| User Control | iOS system UI | Programmatic |
| Audio | Supports mic + app | Supports mic + app |
| Complexity | High (IPC required) | Low |
| Reliability | Very stable | Stable |
| Latency | ~50-100ms | ~30-50ms |

## Future Enhancements

1. **Audio Support:**
   - Extend protocol to send audio frames
   - Use separate data channel for audio

2. **Performance:**
   - Implement H.264 encoding instead of JPEG
   - Reduce frame size for lower bandwidth

3. **Error Recovery:**
   - Auto-reconnect on socket failure
   - Buffer frames during connection issues

4. **UI Improvements:**
   - Custom broadcast picker UI
   - Screen share status indicator

## References

- [Flutter WebRTC iOS Broadcast](https://github.com/flutter-webrtc/flutter-webrtc/tree/main/ios/Classes/Broadcast)
- [LiveKit Broadcast Extension](https://github.com/livekit/client-sdk-flutter/tree/main/example/ios/LiveKit%20Broadcast%20Extension)
- [Apple ReplayKit Documentation](https://developer.apple.com/documentation/replaykit)
- [WebRTC iOS SDK](https://webrtc.github.io/webrtc-org/native-code/ios/)

## License

Same as parent project.
