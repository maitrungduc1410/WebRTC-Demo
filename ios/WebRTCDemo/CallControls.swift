//
//  CallControls.swift
//  WebRTCDemo
//

import SwiftUI

/// A round Liquid Glass control. `active` turns it into a bright pill, like FaceTime's toggled buttons.
struct GlassCircleButton: View {
    let systemImage: String
    let label: String
    var active = false
    var size: CGFloat = 56
    /// Keyboard shortcut shown in the macOS tooltip.
    var shortcut: String? = nil
    let action: () -> Void
    #if os(macOS)
    @Environment(\.isEnabled) private var isEnabled
    #endif

    var body: some View {
        Button(action: action) {
            GlassCircleLabel(systemImage: systemImage, active: active, size: size)
        }
        .buttonStyle(.plain)
        #if os(macOS)
        .hoverLift()
        #endif
        .glassEffect(active ? .regular.tint(.white).interactive() : .regular.interactive(), in: .circle)
        .accessibilityLabel(label)
        #if os(macOS)
        .help(isEnabled ? shortcut.map { "\(label) (\($0))" } ?? label : label)
        #endif
    }
}

#if os(macOS)
/// Pointer feedback for glass controls: a slight lift while hovered, a dip while pressed.
struct HoverLift: ViewModifier {
    @State private var hovering = false
    @Environment(\.isEnabled) private var isEnabled

    func body(content: Content) -> some View {
        content
            .scaleEffect(hovering && isEnabled ? 1.07 : 1)
            .brightness(hovering && isEnabled ? 0.06 : 0)
            .animation(.spring(response: 0.3, dampingFraction: 0.6), value: hovering)
            .onHover { hovering = $0 }
    }
}

extension View {
    func hoverLift() -> some View {
        modifier(HoverLift())
    }
}
#endif

struct GlassCircleLabel: View {
    let systemImage: String
    var active = false
    var size: CGFloat = 56

    var body: some View {
        Image(systemName: systemImage)
            .font(.system(size: size * 0.36, weight: .semibold))
            .contentTransition(.symbolEffect(.replace))
            .foregroundStyle(active ? Color.black : Color.white)
            .frame(width: size, height: size)
            .contentShape(.circle)
    }
}

#if os(iOS)
/// The floating call toolbar: media toggles, sharing, chat, more and hang up.
struct CallToolbar: View {
    let model: CallViewModel
    let buttonSize: CGFloat
    let onShareScreen: () -> Void
    let onShareFromPhotos: () -> Void
    let onShareFromFiles: () -> Void
    let onChat: () -> Void
    let onMore: () -> Void
    let onHangUp: () -> Void

    @Namespace private var namespace

    var body: some View {
        GlassEffectContainer(spacing: 10) {
            HStack(spacing: 10) {
                GlassCircleButton(
                    systemImage: model.micOn ? "mic.fill" : "mic.slash.fill",
                    label: model.micOn ? "Mute" : "Unmute",
                    active: !model.micOn,
                    size: buttonSize,
                    action: model.toggleMic
                )
                .glassEffectID("mic", in: namespace)

                GlassCircleButton(
                    systemImage: model.cameraOn ? "video.fill" : "video.slash.fill",
                    label: model.cameraOn ? "Turn camera off" : "Turn camera on",
                    active: !model.cameraOn,
                    size: buttonSize,
                    action: model.toggleCamera
                )
                .glassEffectID("camera", in: namespace)

                shareButton
                    .glassEffectID("share", in: namespace)

                GlassCircleButton(
                    systemImage: "bubble.left.and.bubble.right.fill",
                    label: model.unread > 0 ? "Chat, \(model.unread) unread" : "Chat",
                    size: buttonSize,
                    action: onChat
                )
                .anchorPreference(key: ChatButtonBounds.self, value: .bounds) { $0 }
                .glassEffectID("chat", in: namespace)

                GlassCircleButton(systemImage: "ellipsis", label: "More", size: buttonSize, action: onMore)
                    .glassEffectID("more", in: namespace)

                Button(action: onHangUp) {
                    Image(systemName: "phone.down.fill")
                        .font(.system(size: buttonSize * 0.38, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: buttonSize * 1.3, height: buttonSize)
                        .contentShape(.capsule)
                }
                .buttonStyle(.plain)
                .glassEffect(.regular.tint(Color(hex: 0xE5484D)).interactive(), in: .capsule)
                .glassEffectID("hangup", in: namespace)
                .accessibilityLabel("Leave call")
            }
        }
        // The container composites its glass above anything attached to the buttons, so the badge
        // is drawn over the whole toolbar instead.
        .overlayPreferenceValue(ChatButtonBounds.self) { bounds in
            GeometryReader { proxy in
                if let bounds, model.unread > 0 {
                    let rect = proxy[bounds]
                    UnreadBadge(count: model.unread)
                        .position(x: rect.maxX - 6, y: rect.minY + 6)
                        .transition(.scale.combined(with: .opacity))
                }
            }
            .allowsHitTesting(false)
            .animation(.spring(response: 0.35, dampingFraction: 0.6), value: model.unread)
        }
    }

    @ViewBuilder
    private var shareButton: some View {
        if model.sharing != .none {
            GlassCircleButton(
                systemImage: "rectangle.on.rectangle.slash",
                label: "Stop sharing",
                active: true,
                size: buttonSize,
                action: model.stopSharing
            )
        } else {
            Menu {
                Button("Share Screen", systemImage: "rectangle.on.rectangle", action: onShareScreen)
                Button("Share from Photos", systemImage: "photo.on.rectangle", action: onShareFromPhotos)
                Button("Share from Files", systemImage: "folder", action: onShareFromFiles)
            } label: {
                GlassCircleLabel(systemImage: "rectangle.on.rectangle", size: buttonSize)
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .glassEffect(.regular.interactive(), in: .circle)
            .accessibilityLabel("Share")
        }
    }
}
#endif

struct ChatButtonBounds: PreferenceKey {
    static var defaultValue: Anchor<CGRect>? { nil }

    static func reduce(value: inout Anchor<CGRect>?, nextValue: () -> Anchor<CGRect>?) {
        value = value ?? nextValue()
    }
}

struct UnreadBadge: View {
    let count: Int

    var body: some View {
        Text(count > 99 ? "99+" : "\(count)")
            .font(.caption2.weight(.bold).monospacedDigit())
            .foregroundStyle(.white)
            .padding(.horizontal, 6)
            .frame(minWidth: 20, minHeight: 20)
            .background(Color(hex: 0xE5484D), in: .capsule)
            .overlay(Capsule().strokeBorder(.black.opacity(0.25), lineWidth: 1))
            .contentTransition(.numericText())
            .shadow(color: .black.opacity(0.3), radius: 3, y: 1)
    }
}

#if os(iOS)
/// Secondary call options, shown in a sheet so the toolbar stays small.
struct MoreSheet: View {
    let model: CallViewModel
    let remoteFit: Bool
    let onToggleFit: () -> Void
    let onSwitchCamera: () -> Void
    let onOpenEffects: () -> Void

    private let columns = [GridItem(.adaptive(minimum: 150), spacing: 12)]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Call options")
                    .font(.title3.weight(.semibold))
                    .padding(.horizontal, 4)

                LazyVGrid(columns: columns, spacing: 12) {
                    OptionTile(
                        systemImage: model.speakerOn ? "speaker.wave.3.fill" : "speaker.fill",
                        title: "Speaker",
                        detail: model.speakerOn ? "On" : "Off",
                        active: model.speakerOn,
                        action: model.toggleSpeaker
                    )
                    OptionTile(
                        systemImage: "sparkles",
                        title: "Effects",
                        detail: effectsDetail,
                        active: model.effectsStatus == .on,
                        action: onOpenEffects
                    )
                    .disabled(!model.effectsAvailable || model.sharing != .none)
                    OptionTile(
                        systemImage: model.remoteAudioMuted ? "speaker.slash.fill" : "speaker.wave.2.fill",
                        title: model.isGroup ? "Everyone's audio" : "Peer audio",
                        detail: model.remoteAudioMuted ? "Muted for you" : "Playing",
                        active: model.remoteAudioMuted,
                        action: model.toggleRemoteAudio
                    )
                    OptionTile(
                        systemImage: model.remoteVideoHidden ? "eye.slash.fill" : "eye.fill",
                        title: model.isGroup ? "Everyone's video" : "Peer video",
                        detail: model.remoteVideoHidden ? "Hidden for you" : "Showing",
                        active: model.remoteVideoHidden,
                        action: model.toggleRemoteVideo
                    )
                    // Group tiles pick fit or fill per participant
                    if !model.isGroup {
                        OptionTile(
                            systemImage: remoteFit ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right",
                            title: remoteFit ? "Fit to screen" : "Fill screen",
                            detail: "Double-tap the video",
                            active: remoteFit,
                            action: onToggleFit
                        )
                    }
                    OptionTile(
                        systemImage: "arrow.triangle.2.circlepath.camera.fill",
                        title: "Switch camera",
                        detail: model.frontCamera ? "Front" : "Back",
                        active: false,
                        action: onSwitchCamera
                    )
                    .disabled(model.sharing != .none)
                }

                Label(
                    model.isGroup
                        ? "Muting or hiding others only affects this device."
                        : "Muting or hiding the peer only affects this device.",
                    systemImage: "info.circle"
                )
                .font(.footnote)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 4)
            }
            .padding(20)
        }
        .scrollBounceBehavior(.basedOnSize)
    }

    private var effectsDetail: String {
        guard model.effectsAvailable else { return "Unavailable" }
        guard model.sharing == .none else { return "Paused while presenting" }
        switch model.effectsStatus {
        case .off: return "Backgrounds and filters"
        case .loading: return "Loading…"
        case .on: return "On"
        }
    }
}

struct OptionTile: View {
    let systemImage: String
    let title: String
    let detail: String
    let active: Bool
    let action: () -> Void

    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 10) {
                Image(systemName: systemImage)
                    .font(.title3.weight(.semibold))
                    .contentTransition(.symbolEffect(.replace))
                    .foregroundStyle(active ? Color.black : Color.primary)
                    .frame(width: 44, height: 44)
                    .background(active ? Color.white : Color.primary.opacity(0.1), in: .circle)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(.primary)
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .contentTransition(.opacity)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(
                active ? AnyShapeStyle(Color.accentColor.opacity(0.35)) : AnyShapeStyle(.fill.tertiary),
                in: .rect(cornerRadius: 22, style: .continuous)
            )
            .contentShape(.rect(cornerRadius: 22, style: .continuous))
            .opacity(isEnabled ? 1 : 0.45)
        }
        .buttonStyle(.plain)
        .sensoryFeedback(.selection, trigger: active)
        .animation(.spring(response: 0.35, dampingFraction: 0.75), value: active)
    }
}
#endif
