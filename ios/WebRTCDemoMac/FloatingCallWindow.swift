//
//  FloatingCallWindow.swift
//  WebRTCDemoMac
//

import AppKit
import Observation
import SwiftUI
import WebRTC

/// The Mac's picture in picture: a small always-on-top panel with the other person's video (in a group
/// call, whoever is speaking) that stays visible over other apps and on every Space, including
/// full-screen ones.
@MainActor
@Observable
final class FloatingCallWindow {
    private(set) var isShown = false

    @ObservationIgnored private var panel: NSPanel?
    @ObservationIgnored private var panelDelegate: PanelDelegate?
    @ObservationIgnored private var videoSize: CGSize = .zero
    @ObservationIgnored private var onClose: (() -> Void)?

    private static let initialWidth: CGFloat = 360
    private static let maxHeight: CGFloat = 420
    private static let margin: CGFloat = 20

    /// `onReturn` runs when the panel goes away by itself (closed with ⌘W or the return button).
    func show(
        model: CallViewModel,
        fill: Bool,
        near window: NSWindow?,
        onReturn: @escaping () -> Void,
        onHangUp: @escaping () -> Void
    ) {
        if let panel {
            panel.orderFrontRegardless()
            return
        }
        let size = Self.size(forWidth: Self.initialWidth, aspect: videoSize)
        let panel = NSPanel(
            contentRect: NSRect(origin: .zero, size: size),
            styleMask: [.titled, .closable, .resizable, .fullSizeContentView, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.isMovableByWindowBackground = true
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.backgroundColor = .black
        panel.animationBehavior = .utilityWindow
        panel.minSize = NSSize(width: 220, height: 124)
        panel.title = "WebRTC Demo"
        for kind in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
            panel.standardWindowButton(kind)?.isHidden = true
        }

        let host = NSHostingView(rootView: FloatingCallView(
            model: model,
            fill: fill,
            onReturn: { [weak self] in self?.close(notify: true) },
            onHangUp: onHangUp
        ))
        host.sizingOptions = []
        panel.contentView = host

        if let visible = (window?.screen ?? NSScreen.main)?.visibleFrame {
            panel.setFrameOrigin(NSPoint(
                x: visible.maxX - size.width - Self.margin,
                y: visible.minY + Self.margin
            ))
        }

        let delegate = PanelDelegate { [weak self] in self?.close(notify: true, closing: true) }
        panel.delegate = delegate
        self.panel = panel
        panelDelegate = delegate
        onClose = onReturn
        applyAspectRatio(animate: false)
        panel.orderFrontRegardless()
        isShown = true
    }

    /// Closes the panel without calling `onReturn`.
    func hide() {
        close(notify: false)
    }

    /// Keeps the panel at the shape of the incoming video.
    func setVideoSize(_ size: CGSize) {
        guard size.width > 0, size.height > 0, size != videoSize else { return }
        videoSize = size
        applyAspectRatio(animate: true)
    }

    /// Updates fit/fill without rebuilding the panel.
    func setFill(_ fill: Bool, model: CallViewModel, onHangUp: @escaping () -> Void) {
        guard let host = panel?.contentView as? NSHostingView<FloatingCallView> else { return }
        host.rootView = FloatingCallView(
            model: model,
            fill: fill,
            onReturn: { [weak self] in self?.close(notify: true) },
            onHangUp: onHangUp
        )
    }

    /// `closing` is set when AppKit is already closing the panel.
    private func close(notify: Bool, closing: Bool = false) {
        guard let panel else { return }
        self.panel = nil
        panel.delegate = nil
        panelDelegate = nil
        if !closing {
            panel.orderOut(nil)
            panel.close()
        }
        isShown = false
        let callback = onClose
        onClose = nil
        if notify { callback?() }
    }

    private func applyAspectRatio(animate: Bool) {
        guard let panel, videoSize.width > 0, videoSize.height > 0 else { return }
        panel.contentAspectRatio = videoSize
        var frame = panel.frame
        let size = Self.size(forWidth: frame.width, aspect: videoSize)
        // Keep the corner nearest the screen edge in place.
        frame.origin.y += frame.height - size.height
        frame.origin.x += frame.width - size.width
        frame.size = size
        panel.setFrame(frame, display: true, animate: animate)
    }

    private static func size(forWidth width: CGFloat, aspect: CGSize) -> CGSize {
        guard aspect.width > 0, aspect.height > 0 else { return CGSize(width: width, height: width * 9 / 16) }
        var size = CGSize(width: width, height: width * aspect.height / aspect.width)
        if size.height > maxHeight {
            size = CGSize(width: maxHeight * aspect.width / aspect.height, height: maxHeight)
        }
        return size
    }
}

private final class PanelDelegate: NSObject, NSWindowDelegate {
    let onClose: () -> Void

    init(onClose: @escaping () -> Void) {
        self.onClose = onClose
    }

    func windowWillClose(_ notification: Notification) {
        onClose()
    }
}

/// The floating panel's content: the remote video with controls that appear on hover.
struct FloatingCallView: View {
    let model: CallViewModel
    let fill: Bool
    let onReturn: () -> Void
    let onHangUp: () -> Void

    @State private var hovering = false

    /// Group calls show the active speaker, else the first camera, else the first person, as on the web.
    private var featured: GroupParticipant? {
        let participants = model.participants
        return participants.first { $0.id == model.activeSpeaker }
            ?? participants.first { $0.state.video && model.remoteVideoTracks[$0.id] != nil }
            ?? participants.first
    }

    private var stageTrack: RTCVideoTrack? {
        guard model.isGroup else { return model.remoteTrack }
        return featured.flatMap { model.remoteVideoTracks[$0.id] }
    }

    private var showsVideo: Bool {
        guard model.isGroup else { return model.hasRemote && !model.remoteVideoPaused }
        guard let featured else { return false }
        return stageTrack != nil && featured.state.video && !model.remoteVideoHidden
    }

    var body: some View {
        ZStack {
            Color.black

            if showsVideo {
                // Presenters are letterboxed so nothing is cut off.
                StageVideoView(track: stageTrack, fill: model.isGroup ? !(featured?.state.screen ?? false) : fill)
                    .id(model.isGroup ? featured?.id : nil)
                    .transition(.opacity)
            } else if model.isGroup {
                PeerPlaceholderView(
                    snapshot: nil,
                    seed: featured.map { "peer-\($0.id)" } ?? "group-waiting",
                    audioLevel: featured.flatMap { model.audioLevels[$0.id] } ?? 0,
                    avatarSize: 56,
                    title: featured == nil ? "Waiting…" : nil
                )
                .id(featured?.id)
                .transition(.opacity)
            } else {
                PeerPlaceholderView(
                    snapshot: model.remoteSnapshot,
                    seed: "peer-\(model.roomId)",
                    audioLevel: model.remoteAudioLevel,
                    avatarSize: 56,
                    title: model.hasRemote ? nil : "Waiting…"
                )
                .transition(.opacity)
            }

            if model.isGroup, let featured {
                featuredLabel(featured)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
                    .opacity(hovering ? 0 : 1)
                    .allowsHitTesting(false)
            }

            if model.cameraOn && model.sharing == .none {
                VideoView(track: model.localTrack, fill: true, mirror: true, cornerRadius: 10)
                    .frame(width: 88, height: 56)
                    .shadow(color: .black.opacity(0.4), radius: 6, y: 2)
                    .padding(10)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
                    .opacity(hovering ? 0 : 1)
                    .allowsHitTesting(false)
            }

            LinearGradient(colors: [.clear, .black.opacity(0.6)], startPoint: .center, endPoint: .bottom)
                .opacity(hovering ? 1 : 0)
                .allowsHitTesting(false)

            controls
                .frame(maxHeight: .infinity, alignment: .bottom)
                .padding(.bottom, 12)
                .opacity(hovering ? 1 : 0)
                .scaleEffect(hovering ? 1 : 0.92, anchor: .bottom)
                .allowsHitTesting(hovering)
        }
        .overlay(alignment: .topLeading) {
            // Your microphone: its level, or red when muted.
            MicLevelIndicator(model: model, large: false)
                .padding(10)
        }
        .contentShape(.rect)
        .gesture(WindowDragGesture())
        .onTapGesture(count: 2, perform: onReturn)
        .onHover { hovering = $0 }
        .animation(.spring(response: 0.35, dampingFraction: 0.85), value: hovering)
        .animation(.easeInOut(duration: 0.3), value: model.remoteVideoPaused)
        .animation(.easeInOut(duration: 0.3), value: featured?.id)
        .animation(.easeInOut(duration: 0.3), value: showsVideo)
        .animation(.spring(response: 0.35, dampingFraction: 0.65), value: model.micOn)
        .preferredColorScheme(.dark)
        .ignoresSafeArea()
    }

    private func featuredLabel(_ participant: GroupParticipant) -> some View {
        HStack(spacing: 4) {
            if !participant.state.audio {
                Image(systemName: "mic.slash.fill")
                    .foregroundStyle(Color(hex: 0xE5484D))
            }
            Text(participant.label)
                .lineLimit(1)
        }
        .font(.caption2.weight(.semibold))
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .glassEffect(.regular.tint(.black.opacity(0.35)), in: .capsule)
        .padding(10)
    }

    private var controls: some View {
        GlassEffectContainer(spacing: 8) {
            HStack(spacing: 8) {
                GlassCircleButton(
                    systemImage: model.micOn ? "mic.fill" : "mic.slash.fill",
                    label: model.micOn ? "Mute" : "Unmute",
                    active: !model.micOn,
                    size: 36,
                    shortcut: "M",
                    action: model.toggleMic
                )
                GlassCircleButton(
                    systemImage: model.cameraOn ? "video.fill" : "video.slash.fill",
                    label: model.cameraOn ? "Turn camera off" : "Turn camera on",
                    active: !model.cameraOn,
                    size: 36,
                    shortcut: "V",
                    action: model.toggleCamera
                )
                .disabled(model.sharing != .none)
                GlassCircleButton(
                    systemImage: "pip.exit",
                    label: "Back to the call window",
                    size: 36,
                    shortcut: "P",
                    action: onReturn
                )
                Button(action: onHangUp) {
                    Image(systemName: "phone.down.fill")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: 48, height: 36)
                        .contentShape(.capsule)
                }
                .buttonStyle(.plain)
                .hoverLift()
                .glassEffect(.regular.tint(Color(hex: 0xE5484D)).interactive(), in: .capsule)
                .help("Leave call")
                .accessibilityLabel("Leave call")
            }
        }
    }
}
