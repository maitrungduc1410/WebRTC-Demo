# ⚠️ CRITICAL: iOS Screen Sharing Limitations

## Apple's ReplayKit Restriction

**You CANNOT screen record your own app using Broadcast Extensions!**

This is an **intentional Apple security/privacy restriction** in ReplayKit.

## What This Means

### ❌ This Will NOT Work:
1. Start screen sharing in your app
2. Stay inside your app
3. Expect to see your app's UI being shared

**Why it doesn't work:**
- ReplayKit's `RPBroadcastSampleHandler` **does not receive frames** when you're inside the app that started the broadcast
- The extension connects to your socket server
- But `processSampleBuffer` is **never called** for your own app's content
- No frames = frozen screen on remote peer

### ✅ This WILL Work:
1. Start screen sharing in your app
2. **Exit to home screen** (don't kill the app, just press home button)
3. Navigate to other apps, home screen, control center, etc.
4. Remote peer will see everything **except your app**

## The Behavior You're Seeing

```
User in App:
- Extension connects ✅
- Socket opens ✅
- But no frames arrive ❌
- Remote sees frozen/black screen ❌

User exits to Home Screen:
- ReplayKit starts sending frames ✅
- Remote peer sees home screen ✅
- Navigate anywhere (other apps, etc.) ✅
- But if iOS backgrounds main app → socket closes → broadcast stops ❌
```

## Solutions

### Solution 1: Background Audio (Recommended)

Add background modes to keep app alive when backgrounded:

**Info.plist:**
```xml
<key>UIBackgroundModes</key>
<array>
    <string>audio</string>
    <string>voip</string>
</array>
```

This keeps your WebRTC connection and socket server alive when user exits the app.

### Solution 2: User Guidance

Tell users explicitly:
1. "Tap Start Broadcast"
2. **"Exit to home screen to share your screen"**
3. "Don't return to this app while sharing"
4. "Open other apps, home screen, etc."

### Solution 3: Picture-in-Picture Overlay

Show a small floating window/overlay so users can control the call without entering the full app.

## Common User Flow

```
1. Join call in your app
2. Tap "Share Screen" button
3. System broadcast picker appears
4. Select your broadcast extension
5. Tap "Start Broadcast"
6. ⚠️ IMMEDIATELY EXIT TO HOME SCREEN ⚠️
7. Now screen sharing works
8. Navigate anywhere (except back to your app)
9. To stop: Control Center → Stop broadcast
```

## Technical Details

### Why Extension Doesn't Get Frames for Own App

From Apple's documentation:
> "For privacy and security reasons, ReplayKit does not capture the UI of the app that initiated the broadcast."

This prevents:
- Apps from secretly recording themselves
- Capturing sensitive data displayed in the broadcasting app
- Security vulnerabilities

### What Gets Captured

✅ Home Screen
✅ Other Apps
✅ System UI (Control Center, Notifications)
✅ Lock Screen
✅ App Switcher

❌ Your App (the one that started broadcast)

## Workarounds That Don't Work

### ❌ "In-App Screen Sharing" with RPScreenRecorder
```swift
RPScreenRecorder.shared().startCapture { buffer, type, error in
    // This DOES capture your app
    // But requires the sample handler to be in the MAIN APP
    // Can't use broadcast extension
    // Limited to in-process capture only
}
```

**Problem:** Can't send to remote peer via broadcast extension architecture.

### ❌ Switching to Main App After Starting
```swift
// User starts broadcast
// Extension connects
// User returns to app
// ReplayKit STOPS sending frames
```

**Problem:** ReplayKit immediately stops sending frames when you return to your app.

## Recommended UI/UX

### Before Starting Broadcast

Show an alert:
```swift
let alert = UIAlertController(
    title: "Screen Sharing",
    message: "After starting broadcast, please EXIT TO HOME SCREEN to share your screen. Screen sharing will not work while you're in this app.",
    preferredStyle: .alert
)
alert.addAction(UIAlertAction(title: "Got it", style: .default) { _ in
    self.webRTCClient.startScreenCapture()
})
present(alert, animated: true)
```

### After Starting Broadcast

1. Show banner: "Screen Sharing Active - Exit to Home Screen"
2. Minimize your app automatically after 3 seconds
3. Show notification: "Open other apps to share your screen"

### During Broadcast

- Keep WebRTC connection alive (background modes)
- Keep socket server running
- Show persistent notification
- Provide stop button in notification

## Testing Checklist

- [ ] Start broadcast
- [ ] Exit to home screen
- [ ] Remote peer sees home screen ✅
- [ ] Open Safari
- [ ] Remote peer sees Safari ✅
- [ ] Return to your app
- [ ] Remote peer sees frozen/black screen ❌ (expected)
- [ ] Exit to home screen again
- [ ] Remote peer sees home screen again ✅
- [ ] Stop broadcast from Control Center
- [ ] Broadcast stops correctly ✅

## Code Changes Needed

### 1. Add Background Modes (Info.plist)
```xml
<key>UIBackgroundModes</key>
<array>
    <string>audio</string>
    <string>voip</string>
</array>
```

### 2. Ensure Audio Session Stays Active
```swift
// In AppDelegate or SceneDelegate
import AVFoundation

let audioSession = AVAudioSession.sharedInstance()
try? audioSession.setCategory(.playAndRecord, mode: .voiceChat)
try? audioSession.setActive(true)
```

### 3. Show User Guidance
```swift
func startScreenCapture() {
    // ... existing code ...
    
    // Show guidance
    DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
        let alert = UIAlertController(
            title: "Exit to Home Screen",
            message: "To share your screen, please press the home button now.",
            preferredStyle: .alert
        )
        alert.addAction(UIAlertAction(title: "OK", style: .default))
        // Present alert
    }
}
```

## Summary

**This is NOT a bug in your code.** This is how Apple designed ReplayKit for security reasons.

**The Fix:**
1. Add background modes
2. Tell users to exit the app
3. Keep socket server alive when backgrounded

**Users must understand:** Screen sharing works for everything EXCEPT your app.

---

**Last Updated:** January 12, 2026
**Apple Documentation:** [RPBroadcastSampleHandler](https://developer.apple.com/documentation/replaykit/rpbroadcastsamplehandler)
