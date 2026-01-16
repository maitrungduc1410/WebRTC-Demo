# iOS Screen Sharing - Setup Checklist

## ✅ Pre-Build Configuration Checklist

### 1. Xcode Project Setup

- [ ] **Broadcast Extension Target Exists**
  - Target name: `WebRTCDemoScreenBroadcast`
  - Template: Broadcast Upload Extension
  - Language: Swift

### 2. App Groups Configuration

- [ ] **Main App** (`WebRTCDemo` target):
  - [ ] Signing & Capabilities tab opened
  - [ ] App Groups capability added
  - [ ] Group `group.com.ducmai.webrtc.broadcast` is enabled
  - [ ] `WebRTCDemo.entitlements` file contains app group

- [ ] **Broadcast Extension** (`WebRTCDemoScreenBroadcast` target):
  - [ ] Signing & Capabilities tab opened
  - [ ] App Groups capability added
  - [ ] Same group `group.com.ducmai.webrtc.broadcast` is enabled
  - [ ] `WebRTCDemoScreenBroadcast.entitlements` file contains app group

### 3. Bundle Identifiers

- [ ] **Main App Bundle ID**: `com.ducmai.WebRTCDemo` (or your custom ID)
- [ ] **Extension Bundle ID**: `com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast`
  - Must be: `<MainAppBundleID>.<ExtensionName>`

### 4. Info.plist Configuration

- [ ] **Main App Info.plist** contains:
  ```xml
  <key>RTCAppGroupIdentifier</key>
  <string>group.com.ducmai.webrtc.broadcast</string>
  <key>RTCScreenSharingExtension</key>
  <string>com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast</string>
  ```

- [ ] **Broadcast Extension Info.plist** has correct structure:
  ```xml
  <key>NSExtension</key>
  <dict>
      <key>NSExtensionPointIdentifier</key>
      <string>com.apple.broadcast-services-upload</string>
      <key>NSExtensionPrincipalClass</key>
      <string>$(PRODUCT_MODULE_NAME).SampleHandler</string>
  </dict>
  ```

### 5. Bridging Header

- [ ] `WebRTCDemo-Bridging-Header.h` exists in main app
- [ ] Contains:
  ```objc
  #import "FlutterBroadcastScreenCapturer.h"
  #import "FlutterSocketConnection.h"
  #import "FlutterSocketConnectionFrameReader.h"
  ```
- [ ] Bridging header path is set in Build Settings:
  - Target: WebRTCDemo
  - Build Settings → Swift Compiler - General
  - Objective-C Bridging Header: `$(PROJECT_DIR)/WebRTCDemo/WebRTCDemo-Bridging-Header.h`

### 6. File Organization

**Main App** should have:
- [ ] `FlutterBroadcastScreenCapturer.h`
- [ ] `FlutterBroadcastScreenCapturer.m`
- [ ] `FlutterSocketConnection.h`
- [ ] `FlutterSocketConnection.m`
- [ ] `FlutterSocketConnectionFrameReader.h`
- [ ] `FlutterSocketConnectionFrameReader.m`
- [ ] `DarwinNotificationCenter.swift`
- [ ] Updated `PeerConnectionClient.swift`

**Broadcast Extension** should have:
- [ ] `SampleHandler.swift`
- [ ] `SampleUploader.swift`
- [ ] `SocketConnection.swift`
- [ ] `DarwinNotificationCenter.swift`
- [ ] `Atomic.swift`

### 7. Build Settings

- [ ] **Main App Target**:
  - [ ] Objective-C Bridging Header is set
  - [ ] Swift Language Version is compatible
  - [ ] Enable Bitcode: No (if using WebRTC pod)

- [ ] **Broadcast Extension Target**:
  - [ ] Deployment target: iOS 11.0 or higher
  - [ ] Swift Language Version is compatible

### 8. Scheme Configuration

- [ ] Main app scheme includes broadcast extension target
  - Edit Scheme → Build → Ensure `WebRTCDemoScreenBroadcast` is checked

### 9. Code Verification

- [ ] `PeerConnectionClient.swift` has:
  - [ ] `import ReplayKit` at the top
  - [ ] `startScreenCapture()` method implemented
  - [ ] `stopScreenCapture()` method implemented
  - [ ] `onBroadcastStarted()` callback
  - [ ] `onBroadcastStopped()` callback
  - [ ] Properties: `screenCapturer`, `screenShareVideoSource`, `originalCapturer`

## ✅ Build & Test Checklist

### 1. Build Process

- [ ] Clean build folder (Cmd+Shift+K)
- [ ] Build main app target (Cmd+B)
- [ ] No compilation errors
- [ ] No linker errors
- [ ] No bridging header errors

### 2. Runtime Prerequisites

- [ ] App runs on physical device (simulator not supported for broadcast)
- [ ] iOS 12.0+ device
- [ ] Device is enrolled in development program
- [ ] Provisioning profiles are valid

### 3. Initial Test

- [ ] App launches successfully
- [ ] Join a call or create peer connection
- [ ] Tap screen share button
- [ ] Broadcast picker appears
- [ ] Extension "WebRTCDemo" appears in list
- [ ] Can start broadcast
- [ ] 3-2-1 countdown appears
- [ ] Broadcast indicator appears in status bar

### 4. Functionality Test

- [ ] **Frame Delivery**:
  - [ ] Remote peer receives screen frames
  - [ ] Frames update in real-time
  - [ ] No lag or stuttering (check Console for errors)

- [ ] **Track Switching**:
  - [ ] Local video shows screen when broadcasting
  - [ ] Remote peer sees screen content
  - [ ] Can navigate between apps
  - [ ] Remote sees app changes

- [ ] **Stopping**:
  - [ ] Can stop from Control Center
  - [ ] App switches back to camera automatically
  - [ ] Remote peer sees camera again
  - [ ] No crashes or freezes

### 5. Edge Cases

- [ ] Start screen share → Kill app → Extension stops gracefully
- [ ] Start broadcast → Lock device → Broadcast continues
- [ ] Start broadcast → Phone call → Broadcast pauses
- [ ] Multiple start/stop cycles work correctly
- [ ] Socket reconnection works after app restart

## ✅ Debugging Checklist

### If Broadcast Picker Doesn't Appear:

- [ ] Check extension bundle ID matches `RTCScreenSharingExtension` in Info.plist
- [ ] Verify extension target is built and included in app bundle
- [ ] Check Console.app for errors from `RPSystemBroadcastPickerView`

### If Extension Doesn't Appear in Picker:

- [ ] Verify broadcast extension is installed with main app
- [ ] Check `NSExtensionPointIdentifier` is correct
- [ ] Ensure `NSExtensionPrincipalClass` matches SampleHandler
- [ ] Check extension's Info.plist is properly formatted
- [ ] Verify extension target is included in scheme

### If No Frames Are Received:

- [ ] **Check Socket Connection**:
  - [ ] Console.app shows "socket connected" from extension
  - [ ] Console.app shows "Client connected" from main app
  - [ ] No "Permission denied" errors
  
- [ ] **Check App Group**:
  - [ ] Both targets have same app group
  - [ ] App group capability is enabled (green checkmark in Xcode)
  - [ ] App group is provisioned in Apple Developer portal
  
- [ ] **Check File Permissions**:
  - [ ] Socket file can be created in app group container
  - [ ] Both processes can access shared container

### If Frames Are Choppy/Slow:

- [ ] Check JPEG quality setting (reduce if needed)
- [ ] Check resolution (scale factor in SampleUploader)
- [ ] Monitor CPU usage in Instruments
- [ ] Check network bandwidth to remote peer

### Console Logs to Watch:

**Broadcast Extension:**
```
socket connected
client connection did close
writeBufferToStream failure (if errors)
```

**Main App:**
```
startScreenCapture
Broadcast started - switching video track
Client connected
Server stream open completed
Received screen frame: WxH
```

## ✅ Performance Checklist

- [ ] **Memory Usage**:
  - [ ] Monitor in Xcode Memory Gauge
  - [ ] Should stay under 100MB for extension
  - [ ] No memory leaks over time

- [ ] **CPU Usage**:
  - [ ] Extension uses ~15-30% CPU (JPEG encoding)
  - [ ] Main app uses ~10-20% CPU (decoding + WebRTC)

- [ ] **Battery Impact**:
  - [ ] Test for 5+ minutes
  - [ ] Check Energy Impact in Settings

## ✅ Production Readiness

Before releasing to App Store:

- [ ] Test on multiple iOS versions (12, 13, 14, 15, 16+)
- [ ] Test on different device models (iPhone, iPad)
- [ ] Test in low bandwidth scenarios
- [ ] Add error handling for socket failures
- [ ] Add user-friendly error messages
- [ ] Implement analytics/logging
- [ ] Test with TestFlight beta users
- [ ] Privacy policy mentions screen recording
- [ ] App Store description mentions screen sharing

## Common Issues & Quick Fixes

| Issue | Quick Fix |
|-------|-----------|
| "No app group" error | Enable App Groups in Capabilities for both targets |
| "Socket permission denied" | Ensure same app group in both entitlements |
| Extension not in picker | Check bundle ID format and scheme inclusion |
| Frames not received | Verify socket file path and app group |
| Crash on track switch | Check peerConnection is not nil |
| Broadcast picker invisible | Try on physical device, not simulator |

## Final Verification

Run through this complete flow:

1. [ ] Build and install app on device
2. [ ] Open app and join a call
3. [ ] Tap "Screen Share" button
4. [ ] See broadcast picker
5. [ ] Select your extension
6. [ ] Tap "Start Broadcast"
7. [ ] See countdown
8. [ ] Screen sharing starts
9. [ ] Navigate to Home Screen
10. [ ] Remote peer sees Home Screen
11. [ ] Open Safari
12. [ ] Remote peer sees Safari
13. [ ] Swipe down Control Center
14. [ ] Tap broadcast indicator
15. [ ] Tap "Stop"
16. [ ] Screen sharing stops
17. [ ] Camera resumes
18. [ ] Remote peer sees camera

**If all 18 steps work: ✅ Implementation is successful!**

---

## Need Help?

Check these logs in Console.app:
1. Filter by "WebRTCDemo" for main app logs
2. Filter by "WebRTCDemoScreenBroadcast" for extension logs
3. Filter by "RPBroadcast" for system broadcast logs

Look for:
- Socket connection errors
- Permission errors
- Frame delivery confirmations
- Track switching events

---

**Last Updated**: January 12, 2026
**Implementation Version**: 1.0
**Tested iOS Versions**: 12.0+
