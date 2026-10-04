//
//  MacCallToolbar.swift
//  WebRTCDemoMac
//

import SwiftUI

/// The floating glass toolbar of the Mac call window. Every control has a tooltip naming its shortcut.
struct MacCallToolbar: View {
    let model: CallViewModel
    let chatOpen: Bool
    let effectsOpen: Bool
    let remoteFit: Bool
    let canFit: Bool
    let floating: Bool
    let canFloat: Bool
    let onShareScreen: () -> Void
    let onShareFile: () -> Void
    let onChat: () -> Void
    let onEffects: () -> Void
    let onToggleFit: () -> Void
    let onToggleFloating: () -> Void
    let onHangUp: () -> Void

    @Namespace private var namespace

    private let size: CGFloat = 48

    var body: some View {
        GlassEffectContainer(spacing: 8) {
            HStack(spacing: 8) {
                MediaToggle(
                    systemImage: model.micOn ? "mic.fill" : "mic.slash.fill",
                    label: model.micOn ? "Mute" : "Unmute",
                    shortcut: "M",
                    menuHelp: "Microphone and speaker",
                    active: !model.micOn,
                    size: size,
                    action: model.toggleMic
                ) {
                    devicePicker("Microphone", devices: model.microphones, selection: model.microphoneID, select: model.selectMicrophone)
                    devicePicker("Speaker", devices: model.speakers, selection: model.speakerID, select: model.selectSpeaker)
                }
                .glassEffectID("mic", in: namespace)

                MediaToggle(
                    systemImage: model.cameraOn ? "video.fill" : "video.slash.fill",
                    label: model.cameraOn ? "Turn camera off" : "Turn camera on",
                    shortcut: "V",
                    menuHelp: "Camera",
                    active: !model.cameraOn,
                    size: size,
                    action: model.toggleCamera
                ) {
                    devicePicker("Camera", devices: model.cameras, selection: model.cameraID, select: model.selectCamera)
                }
                .disabled(model.sharing != .none)
                .glassEffectID("camera", in: namespace)

                shareButton
                    .glassEffectID("share", in: namespace)

                GlassCircleButton(
                    systemImage: "sparkles",
                    label: effectsLabel,
                    active: effectsOpen || (model.effectsStatus != .off && model.sharing == .none),
                    size: size,
                    shortcut: effectsOpen ? "Esc" : "B",
                    action: onEffects
                )
                .disabled(!model.effectsAvailable || model.sharing != .none)
                .glassEffectID("effects", in: namespace)

                GlassCircleButton(
                    systemImage: "bubble.left.and.bubble.right.fill",
                    label: chatOpen ? "Hide chat" : (model.unread > 0 ? "Chat, \(model.unread) unread" : "Chat"),
                    active: chatOpen,
                    size: size,
                    shortcut: "C",
                    action: onChat
                )
                .anchorPreference(key: ChatButtonBounds.self, value: .bounds) { $0 }
                .glassEffectID("chat", in: namespace)

                moreMenu
                    .glassEffectID("more", in: namespace)

                Button(action: onHangUp) {
                    Image(systemName: "phone.down.fill")
                        .font(.system(size: size * 0.38, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: size * 1.35, height: size)
                        .contentShape(.capsule)
                }
                .buttonStyle(.plain)
                .hoverLift()
                .glassEffect(.regular.tint(Color(hex: 0xE5484D)).interactive(), in: .capsule)
                .glassEffectID("hangup", in: namespace)
                .help("Leave call (⇧⌘E)")
                .accessibilityLabel("Leave call")
            }
        }
        .overlayPreferenceValue(ChatButtonBounds.self) { bounds in
            GeometryReader { proxy in
                if let bounds, model.unread > 0, !chatOpen {
                    let rect = proxy[bounds]
                    UnreadBadge(count: model.unread)
                        .position(x: rect.maxX - 6, y: rect.minY + 6)
                        .transition(.scale.combined(with: .opacity))
                }
            }
            .allowsHitTesting(false)
            .animation(.spring(response: 0.35, dampingFraction: 0.6), value: model.unread)
        }
        .animation(.spring(response: 0.4, dampingFraction: 0.8), value: model.sharing)
    }

    private var effectsLabel: String {
        if !model.effectsAvailable { return "Backgrounds and effects need a camera" }
        if model.sharing != .none { return "Backgrounds and effects are paused while you present" }
        if effectsOpen { return "Hide backgrounds and effects" }
        return model.effectsStatus == .loading ? "Backgrounds and effects, loading" : "Backgrounds and effects"
    }

    @ViewBuilder
    private var shareButton: some View {
        if model.sharing != .none {
            GlassCircleButton(
                systemImage: "rectangle.on.rectangle.slash",
                label: model.sharingTitle.map { "Stop sharing \($0)" } ?? "Stop sharing",
                active: true,
                size: size,
                action: model.stopSharing
            )
        } else {
            Menu {
                Button("Screen or Window… (⇧⌘S)", systemImage: "rectangle.on.rectangle", action: onShareScreen)
                Button("Video File… (⌘O)", systemImage: "film", action: onShareFile)
            } label: {
                GlassCircleLabel(systemImage: "rectangle.on.rectangle", size: size)
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .fixedSize()
            .hoverLift()
            .glassEffect(.regular.interactive(), in: .circle)
            .help("Share your screen, a window or a video")
            .accessibilityLabel("Share")
        }
    }

    private var moreMenu: some View {
        Menu {
            if model.isGroup {
                // Local only, and kept for people who join later.
                Section("Only on this Mac") {
                    Button(
                        model.remoteAudioMuted ? "Unmute Everyone" : "Mute Everyone",
                        systemImage: model.remoteAudioMuted ? "speaker.wave.2.fill" : "speaker.slash.fill",
                        action: model.toggleRemoteAudio
                    )
                    Button(
                        model.remoteVideoHidden ? "Show Everyone's Video" : "Hide Everyone's Video",
                        systemImage: model.remoteVideoHidden ? "eye.fill" : "eye.slash.fill",
                        action: model.toggleRemoteVideo
                    )
                }
            } else {
                Button(
                    model.remoteAudioMuted ? "Unmute Other Person" : "Mute Other Person",
                    systemImage: model.remoteAudioMuted ? "speaker.wave.2.fill" : "speaker.slash.fill",
                    action: model.toggleRemoteAudio
                )
                .disabled(!model.hasRemote)
                Button(
                    model.remoteVideoHidden ? "Show Their Video" : "Hide Their Video",
                    systemImage: model.remoteVideoHidden ? "eye.fill" : "eye.slash.fill",
                    action: model.toggleRemoteVideo
                )
                .disabled(!model.hasRemote)
            }
            Divider()
            if !model.isGroup {
                Button(
                    remoteFit ? "Fill Window (F)" : "Fit Video in Window (F)",
                    systemImage: remoteFit ? "arrow.up.left.and.arrow.down.right" : "arrow.down.right.and.arrow.up.left",
                    action: onToggleFit
                )
                .disabled(!canFit)
            }
            Button(
                floating ? "Return to Main Window (P)" : (model.isGroup ? "Float Active Speaker on Top (P)" : "Float Video on Top (P)"),
                systemImage: floating ? "pip.exit" : "pip.enter",
                action: onToggleFloating
            )
            .disabled(!canFloat && !floating)
        } label: {
            GlassCircleLabel(systemImage: "ellipsis", size: size)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .hoverLift()
        .glassEffect(.regular.interactive(), in: .circle)
        .help("More options")
        .accessibilityLabel("More")
    }

    /// An inline picker renders as a checkmarked section inside the menu.
    @ViewBuilder
    private func devicePicker(
        _ title: String,
        devices: [MediaDevice],
        selection: String?,
        select: @escaping (String) -> Void
    ) -> some View {
        if devices.isEmpty {
            Section(title) {
                Text("No \(title.lowercased()) found")
            }
        } else {
            Picker(title, selection: Binding(get: { selection ?? "" }, set: select)) {
                ForEach(devices) { device in
                    Text(device.name).tag(device.id)
                }
            }
            .pickerStyle(.inline)
        }
    }
}

/// A glass capsule with a toggle and a chevron that opens a device menu, like FaceTime's split buttons.
private struct MediaToggle<MenuContent: View>: View {
    let systemImage: String
    let label: String
    let shortcut: String
    let menuHelp: String
    let active: Bool
    let size: CGFloat
    let action: () -> Void
    @ViewBuilder let menu: () -> MenuContent

    var body: some View {
        HStack(spacing: 0) {
            Button(action: action) {
                GlassCircleLabel(systemImage: systemImage, active: active, size: size)
            }
            .buttonStyle(.plain)
            .help("\(label) (\(shortcut))")
            .accessibilityLabel(label)

            Menu(content: menu) {
                Image(systemName: "chevron.up")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(active ? Color.black.opacity(0.7) : Color.white.opacity(0.8))
                    .frame(width: 20, height: size)
                    .contentShape(.rect)
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .fixedSize()
            .help(menuHelp)
            .accessibilityLabel(menuHelp)
        }
        .padding(.trailing, 6)
        .hoverLift()
        .glassEffect(active ? .regular.tint(.white).interactive() : .regular.interactive(), in: .capsule)
    }
}
