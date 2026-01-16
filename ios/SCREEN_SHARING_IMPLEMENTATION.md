# iOS Screen Sharing Implementation Guide

## Overview

This implementation enables full-screen sharing on iOS using WebRTC and a Broadcast Extension. The architecture uses a Unix domain socket to communicate between the broadcast extension and the main app.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                  iOS System                              │
│  ┌──────────────────────────────────────────────┐       │
│  │  User starts broadcast via picker           │       │
│  └──────────────────┬───────────────────────────┘       │
│                     │                                    │
│                     ▼                                    │
│  ┌──────────────────────────────────────────────┐       │
│  │  Broadcast Extension                         │       │
│  │  (WebRTCDemoScreenBroadcast)                │       │
│  │                                              │       │
│  │  - SampleHandler receives screen frames      │       │
│  │  - SampleUploader encodes frames to JPEG     │       │
│  │  - SocketConnection sends via Unix socket    │       │
│  └──────────────────┬───────────────────────────┘       │
│                     │ Unix Domain Socket                │
│                     │ (via App Group)                   │
│                     ▼                                    │
│  ┌──────────────────────────────────────────────┐       │
│  │  Main App (WebRTCDemo)                       │       │
│  │                                              │       │
│  │  - FlutterSocketConnection (server)          │       │
│  │  - FlutterSocketConnectionFrameReader        │       │
│  │  - FlutterBroadcastScreenCapturer            │       │
│  │  - Feeds frames to RTCVideoSource            │       │
│  └──────────────────┬───────────────────────────┘       │
│                     │                                    │
│                     ▼                                    │
│              WebRTC PeerConnection                       │
│              (sends to remote peer)                      │
└─────────────────────────────────────────────────────────┘
```

## Components

### Broadcast Extension (WebRTCDemoScreenBroadcast)

#### 1. SampleHandler.swift
- Entry point for the broadcast extension
- Receives screen frames from iOS via `processSampleBuffer`
- Manages the socket connection lifecycle
- Posts Darwin notifications when broadcast starts/stops

#### 2. SampleUploader.swift
- Converts CVPixelBuffer frames to JPEG
- Packages frames with HTTP headers containing metadata (width, height, orientation)
- Sends data in chunks over the socket connection
- Uses atomic property wrapper for thread-safe state

#### 3. SocketConnection.swift
- Client-side Unix domain socket implementation
- Connects to the socket file in the shared App Group container
- Manages input/output streams
- Handles connection errors and cleanup

#### 4. DarwinNotificationCenter.swift
- Posts system-wide Darwin notifications
- Used to notify main app when broadcast starts/stops

#### 5. Atomic.swift
- Property wrapper for thread-safe value access
- Uses NSLock for synchronization

### Main App (WebRTCDemo)

#### 1. FlutterSocketConnection.h/m
- **Server-side** Unix domain socket implementation
- Creates socket file in shared App Group container
- Listens for connections from broadcast extension
- Manages NSInputStream/NSOutputStream on a background thread

#### 2. FlutterSocketConnectionFrameReader.h/m
- Reads data from socket connection
- Parses HTTP-formatted messages
- Extracts JPEG data and converts back to CVPixelBuffer
- Creates RTCVideoFrame with proper rotation and timestamp
- Implements `RTCVideoCapturer` to feed frames to WebRTC

#### 3. FlutterBroadcastScreenCapturer.h/m
- High-level screen capturer implementation
- Manages lifecycle of socket connection and frame reader
- Retrieves app group identifier from Info.plist
- Constructs socket file path

#### 4. DarwinNotificationCenter.swift
- Observes Darwin notifications from broadcast extension
- Triggers callbacks when broadcast starts/stops

#### 5. PeerConnectionClient.swift (Updated)
- `startScreenCapture()`: Sets up screen sharing infrastructure
- `onBroadcastStarted()`: Replaces camera track with screen track
- `onBroadcastStopped()`: Switches back to camera
- `showBroadcastPicker()`: Displays system broadcast picker UI

## Configuration

### 1. App Group Setup

Both targets must use the same App Group:

**Main App** - `WebRTCDemo.entitlements`:
```xml
<key>com.apple.security.application-groups</key>
<array>
    <string>group.com.ducmai.webrtc.broadcast</string>
</array>
```

**Broadcast Extension** - `WebRTCDemoScreenBroadcast.entitlements`:
```xml
<key>com.apple.security.application-groups</key>
<array>
    <string>group.com.ducmai.webrtc.broadcast</string>
</array>
```

### 2. Info.plist Configuration

**Main App** - `Info.plist`:
```xml
<key>RTCAppGroupIdentifier</key>
<string>group.com.ducmai.webrtc.broadcast</string>
<key>RTCScreenSharingExtension</key>
<string>com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast</string>
```

### 3. Bridging Header

**WebRTCDemo-Bridging-Header.h**:
```objc
#import "FlutterBroadcastScreenCapturer.h"
#import "FlutterSocketConnection.h"
#import "FlutterSocketConnectionFrameReader.h"
```

## Usage Flow

### Starting Screen Share

```swift
// In your ViewController
webRTCClient.startScreenCapture()
```

**What happens:**
1. Creates `FlutterBroadcastScreenCapturer` with a new video source
2. Starts socket server (listens on Unix socket)
3. Shows `RPSystemBroadcastPickerView` to user
4. Registers Darwin notification observers
5. User taps "Start Broadcast" in system UI
6. Broadcast extension starts sending frames
7. `onBroadcastStarted()` callback switches video track
8. Screen frames flow through WebRTC peer connection

### Stopping Screen Share

```swift
// Can be triggered by:
// 1. User stopping broadcast in system UI
// 2. Calling explicitly
webRTCClient.stopScreenCapture()
```

**What happens:**
1. Stops the screen capturer
2. Closes socket connection
3. Removes notification observers
4. Switches video track back to camera
5. Restarts camera capture

## Communication Protocols

### Socket Protocol

**Frame Format:**
```
HTTP/1.1 200 OK
Content-Length: <bytes>
Buffer-Width: <width>
Buffer-Height: <height>
Buffer-Orientation: <orientation>

<JPEG data>
```

**Socket File Path:**
```
<App Group Container>/rtc_SSFD
```

### Darwin Notifications

- `iOS_BroadcastStarted` - Posted when broadcast extension starts
- `iOS_BroadcastStopped` - Posted when broadcast extension stops

## Code Review Summary

### Extension Code Review ✅

**SampleHandler.swift** - ✅ Good
- Properly manages socket lifecycle
- Handles errors gracefully
- Posts notifications at correct times
- Uses OSLog for debugging

**SampleUploader.swift** - ✅ Good
- Thread-safe with `@Atomic` wrapper
- Efficient chunked data transfer
- Proper JPEG compression
- Includes metadata in HTTP headers

**SocketConnection.swift** - ✅ Good
- Robust client socket implementation
- Proper error handling
- Background thread for network operations
- Clean separation of concerns

**DarwinNotificationCenter.swift** - ✅ Good
- Simple and focused
- Correct Darwin notification API usage

**Atomic.swift** - ✅ Good
- Thread-safe property wrapper
- Uses NSLock (appropriate for this use case)

### Recommendations

1. **Error Handling**: Consider adding delegate callbacks for socket errors
2. **Frame Rate**: Currently sends all frames - consider throttling if needed
3. **Quality**: JPEG compression quality is set to 1.0 (lossless) - adjust if bandwidth is a concern
4. **Memory**: Large frames are buffered - monitor memory usage

### Main App Code Review ✅

**FlutterSocketConnection.m** - ✅ Good
- Proper server socket implementation
- Background thread for accepting connections
- Clean stream management

**FlutterSocketConnectionFrameReader.m** - ✅ Good
- Correctly parses HTTP-framed messages
- Proper timestamp handling
- Converts JPEG → CVPixelBuffer → RTCVideoFrame
- Handles rotation correctly

**FlutterBroadcastScreenCapturer.m** - ✅ Good
- Clean abstraction
- Retrieves configuration from Info.plist
- Proper lifecycle management

**DarwinNotificationCenter.swift** - ✅ Good
- Observer pattern implementation
- Unmanaged memory handling for C callbacks

**PeerConnectionClient.swift** - ✅ Good
- Track replacement using sender.track
- Proper state management
- Cleanup on stop

## Testing Checklist

- [ ] Verify app group is configured in Xcode capabilities
- [ ] Confirm bundle ID for broadcast extension matches Info.plist
- [ ] Test broadcast picker appears
- [ ] Verify frames are received in main app
- [ ] Test video track replacement
- [ ] Test stopping broadcast from system UI
- [ ] Test stopping via code
- [ ] Verify camera resumes after stop
- [ ] Test with remote peer connection
- [ ] Check memory usage during long broadcasts

## Common Issues & Solutions

### Issue 1: Broadcast Picker Doesn't Appear
**Solution**: Ensure `RTCScreenSharingExtension` in Info.plist matches your broadcast extension's bundle identifier exactly.

### Issue 2: No Frames Received
**Solution**: 
- Check app group identifier matches in both targets
- Verify broadcast extension has app group entitlement
- Check Console.app for socket errors

### Issue 3: Crash When Switching Tracks
**Solution**: Ensure video track replacement happens on the correct thread and peerConnection is not nil.

### Issue 4: Permission Denied on Socket
**Solution**: Verify both targets have the app group capability enabled in Xcode signing & capabilities.

## Performance Considerations

- **Frame Encoding**: JPEG encoding happens in broadcast extension (separate process)
- **Socket Transfer**: Unix domain sockets are efficient (no network stack)
- **Memory**: Large frames are allocated - monitor with Instruments
- **CPU**: JPEG encoding/decoding uses CPU - consider hardware encoding in future

## Future Enhancements

1. **H.264 Encoding**: Use hardware encoder instead of JPEG for better compression
2. **Frame Throttling**: Add FPS limiting to reduce bandwidth
3. **Error Recovery**: Auto-reconnect on socket errors
4. **UI Feedback**: Show recording indicator in main app
5. **Audio Capture**: Add system audio capture (requires separate API)

## Security Notes

- Unix domain sockets are private to the app group
- Screen frames never leave the device through the socket
- WebRTC encryption still applies to transmitted data
- Broadcast extension runs in separate process (sandboxed)

---

**Implementation Status**: ✅ Complete and Ready for Testing

**Next Steps**:
1. Build and run the app
2. Test screen sharing with a remote peer
3. Monitor performance and adjust JPEG quality if needed
4. Consider implementing H.264 encoding for production use
