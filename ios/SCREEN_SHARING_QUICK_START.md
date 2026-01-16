# iOS Screen Sharing - Quick Start Guide

## Prerequisites

Before using screen sharing, ensure:

1. **Xcode Configuration**:
   - Main app target has App Groups capability enabled
   - Broadcast extension target has App Groups capability enabled
   - Both use the same app group: `group.com.ducmai.webrtc.broadcast`

2. **Info.plist Keys** (Main App):
   ```xml
   <key>RTCAppGroupIdentifier</key>
   <string>group.com.ducmai.webrtc.broadcast</string>
   <key>RTCScreenSharingExtension</key>
   <string>com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast</string>
   ```

3. **Bridging Header** is properly configured in Xcode Build Settings

## Basic Usage

### Start Screen Sharing

```swift
// In CallViewController or wherever you have webRTCClient
webRTCClient.startScreenCapture()
```

This will:
- Set up the socket server
- Show the iOS system broadcast picker
- Wait for user to tap "Start Broadcast"
- Automatically switch from camera to screen when broadcast starts

### Stop Screen Sharing

```swift
webRTCClient.stopScreenCapture()
```

Or the user can stop from:
- iOS Control Center
- System broadcast UI

The app will automatically switch back to camera when stopped.

## User Experience

1. User taps "Share Screen" button in your app
2. iOS system picker appears
3. User selects "WebRTCDemo" (your broadcast extension)
4. User taps "Start Broadcast"
5. A 3-second countdown appears
6. Screen sharing starts - remote peer sees the screen
7. User sees broadcast indicator in status bar
8. To stop: swipe down Control Center → tap broadcast indicator → "Stop"

## Integration Example

### In CallViewController

```swift
// Add a screen share button
private let screenShareButton = UIButton(type: .system)

// Setup the button
screenShareButton.setTitle(isScreenSharing ? "Stop Screen" : "Share Screen", for: .normal)
screenShareButton.addTarget(self, action: #selector(toggleScreenShare), for: .touchUpInside)

@objc private func toggleScreenShare() {
    if isScreenSharing {
        webRTCClient.stopScreenCapture()
        isScreenSharing = false
        screenShareButton.setTitle("Share Screen", for: .normal)
    } else {
        webRTCClient.startScreenCapture()
        isScreenSharing = true
        screenShareButton.setTitle("Stop Screen", for: .normal)
    }
}

// Listen for broadcast events (optional)
DarwinNotificationCenter.shared.addObserver(
    self,
    for: .broadcastStarted
) {
    print("Broadcast actually started")
    // Update UI
}

DarwinNotificationCenter.shared.addObserver(
    self,
    for: .broadcastStopped
) {
    print("Broadcast actually stopped")
    // Update UI
}
```

## Important Notes

### Automatic Track Switching

The implementation automatically handles switching between camera and screen:
- **Camera → Screen**: Happens when broadcast starts
- **Screen → Camera**: Happens when broadcast stops
- No manual track management needed

### State Management

The `PeerConnectionClient` maintains:
```swift
private var isScreenSharing = false  // True when actively sharing screen
private var originalCapturer: RTCVideoCapturer?  // Saved camera capturer
private var screenCapturer: FlutterBroadcastScreenCapturer?  // Screen capturer
```

### Thread Safety

All WebRTC operations happen on appropriate threads:
- Socket operations: Background thread
- Frame delivery: Capturer thread
- Track updates: Main thread (via DispatchQueue.main)

## Troubleshooting

### Broadcast Picker Doesn't Show
```swift
// Ensure bundle ID is correct
picker.preferredExtension = "com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast"
```

### No Video After Starting
- Check Console.app for socket errors
- Verify app group is enabled in Capabilities
- Ensure broadcast extension target is included in scheme

### Permission Errors
- Both targets need App Groups capability
- Capability must be enabled in Apple Developer portal
- Clean build folder and rebuild

## Testing

### Test Locally
1. Build and run main app
2. Join a call (or create peer connection)
3. Tap screen share button
4. Start broadcast
5. Open another app - you should see it on remote end
6. Stop broadcast
7. Should return to camera

### Test With Remote Peer
1. Use web or Android client
2. Establish connection
3. Start screen share from iOS
4. Remote should see iOS screen
5. Navigate iOS UI - remote should see updates
6. Stop sharing - remote should see camera again

## Performance Tips

1. **Frame Rate**: Currently captures at system rate (~60fps)
   - Consider throttling for bandwidth savings
   
2. **Quality**: JPEG quality is at 1.0 (max quality)
   - Reduce in `SampleUploader.swift` if needed:
   ```swift
   let options: [CIImageRepresentationOption: Float] = [
       kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: 0.8  // Reduce to 0.8
   ]
   ```

3. **Resolution**: Currently full resolution
   - Scale transform in `SampleUploader.swift` can reduce:
   ```swift
   let scaleFactor = 1.0  // Change to 2.0 for half resolution
   ```

## Advanced Features

### Custom Broadcast Picker UI

```swift
func showCustomBroadcastPicker() {
    let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 200, height: 50))
    picker.preferredExtension = "com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast"
    picker.showsMicrophoneButton = false
    
    // Add to your view hierarchy (picker has built-in UI)
    view.addSubview(picker)
    picker.center = view.center
}
```

### Monitor Connection State

```swift
// In your capturer delegate
extension MyClass: RTCVideoCapturerDelegate {
    func capturer(_ capturer: RTCVideoCapturer, didCapture frame: RTCVideoFrame) {
        print("Received screen frame: \(frame.width)x\(frame.height)")
        // Update UI with frame info
    }
}
```

### Custom Socket Path

If you need a different socket file:
```swift
// Change in both SampleHandler.swift and FlutterBroadcastScreenCapturer.m
let customSocketName = "custom_socket_SSFD"
```

## Security Considerations

- Screen frames are only accessible within your app group
- Socket communication is local (no network)
- WebRTC still applies E2EE if configured
- Broadcast extension cannot access main app's memory

## API Reference

### WebRTCClient Methods

```swift
// Start screen sharing
func startScreenCapture()

// Stop screen sharing  
func stopScreenCapture()

// Show broadcast picker (called internally by startScreenCapture)
func showBroadcastPicker()

// Callbacks (called internally)
private func onBroadcastStarted()
private func onBroadcastStopped()
```

### DarwinNotificationCenter

```swift
// Listen for broadcast events
DarwinNotificationCenter.shared.addObserver(
    observer: AnyObject,
    for: DarwinNotification,
    callback: () -> Void
)

// Remove observer
DarwinNotificationCenter.shared.removeObserver(
    observer: AnyObject,
    for: DarwinNotification
)

// Post notification (used by extension)
DarwinNotificationCenter.shared.postNotification(DarwinNotification)
```

## What's Next?

- [ ] Add UI button for screen sharing in CallViewController
- [ ] Test with multiple remote peers
- [ ] Optimize quality/performance based on network
- [ ] Add screen share status indicator
- [ ] Implement analytics/monitoring

---

**Ready to test!** Build and run your app, join a call, and tap the screen share button.
