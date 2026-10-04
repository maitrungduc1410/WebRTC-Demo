//
//  MacSupport.swift
//  WebRTCDemoMac
//

import AppKit
import CoreAudio
import SwiftUI

/// A camera, microphone or speaker the user can pick.
struct MediaDevice: Identifiable, Hashable {
    let id: String
    let name: String
}

/// Lets the window be dragged by an otherwise empty strip along its top edge, where the hidden title bar was.
struct WindowDragStrip: View {
    var height: CGFloat = 28

    var body: some View {
        Color.clear
            .frame(maxWidth: .infinity)
            .frame(height: height)
            .contentShape(.rect)
            .gesture(WindowDragGesture())
            .allowsWindowActivationEvents(true)
            .onTapGesture(count: 2) { NSApp.keyWindow?.performZoom(nil) }
    }
}

/// Holds the hosting window without keeping it alive.
final class WindowReference {
    weak var window: NSWindow?

    /// Fades the close, minimize and zoom buttons together with the rest of the call controls.
    func setTrafficLightsHidden(_ hidden: Bool) {
        guard let window else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.3
            for kind in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
                window.standardWindowButton(kind)?.animator().alphaValue = hidden ? 0 : 1
            }
        }
    }
}

/// Reports the NSWindow a SwiftUI view ends up in.
struct WindowAccessor: NSViewRepresentable {
    let reference: WindowReference

    func makeNSView(context: Context) -> WindowReportingView {
        let view = WindowReportingView()
        view.reference = reference
        return view
    }

    func updateNSView(_ view: WindowReportingView, context: Context) {}
}

final class WindowReportingView: NSView {
    var reference: WindowReference?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let window { reference?.window = window }
    }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

/// Remembers when the pointer last moved. Kept out of SwiftUI state so mouse moves do not re-render the call.
final class IdleTracker {
    private var lastActivity = Date()

    func poke() {
        lastActivity = Date()
    }

    var idleTime: TimeInterval {
        Date().timeIntervalSince(lastActivity)
    }
}

/// Calls `onChange` on the main queue when an audio device (microphone or speaker) is added or removed.
/// AVFoundation's capture notifications miss output-only devices.
final class AudioDeviceListObserver {
    private var address = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain
    )
    private let listener: AudioObjectPropertyListenerBlock

    init(onChange: @escaping () -> Void) {
        listener = { _, _ in onChange() }
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listener)
    }

    deinit {
        AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, .main, listener)
    }
}
