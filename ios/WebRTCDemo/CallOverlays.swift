//
//  CallOverlays.swift
//  WebRTCDemo
//
//  Call screen pieces shared by the iOS and macOS call views.
//

import SwiftUI
#if os(iOS)
import UIKit
#else
import AppKit
#endif

struct RoomPill: View {
    let model: CallViewModel

    @State private var pulsing = false

    private var dotColor: Color {
        switch model.phase {
        case .connected: return Color(hex: 0x4ADE80)
        case .connecting: return Color(hex: 0xFBBF24)
        case .waiting: return .white.opacity(0.6)
        }
    }

    var body: some View {
        HStack(spacing: 8) {
            Circle()
                .fill(dotColor)
                .frame(width: 8, height: 8)
                .opacity(model.phase == .connected ? 1 : (pulsing ? 1 : 0.3))
            if model.e2ee {
                Image(systemName: "lock.fill")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Color(hex: 0x4ADE80))
                    .accessibilityLabel("End-to-end encrypted")
            }
            Text("Room \(model.roomId)")
                .font(.subheadline.weight(.semibold))
            Text("·")
                .foregroundStyle(.secondary)
            Group {
                switch model.phase {
                case .waiting:
                    Text("Waiting")
                case .connecting:
                    Text("Connecting…")
                case .connected:
                    if let since = model.connectedSince {
                        Text(since, style: .timer)
                    }
                }
            }
            .font(.subheadline.monospacedDigit())
            .foregroundStyle(.secondary)
            .contentTransition(.opacity)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .glassEffect(.regular, in: .capsule)
        .animation(.easeInOut(duration: 0.3), value: model.phase)
        .onAppear {
            withAnimation(.easeInOut(duration: 0.9).repeatForever(autoreverses: true)) {
                pulsing = true
            }
        }
    }
}

struct StatusChip: View {
    let systemImage: String
    let text: String
    /// Inside a button: the glass reacts to touches.
    var interactive = false

    var body: some View {
        Label(text, systemImage: systemImage)
            .font(.caption.weight(.semibold))
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .glassEffect(interactive ? .regular.interactive() : .regular, in: .capsule)
    }
}

struct WaitingCard: View {
    let model: CallViewModel

    @State private var copied = false

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: "antenna.radiowaves.left.and.right")
                .font(.title3.weight(.semibold))
                .symbolEffect(.variableColor.iterative.reversing, options: .repeat(.continuous))
                .frame(width: 48, height: 48)
                .glassEffect(.regular.tint(.accentColor.opacity(0.5)), in: .circle)

            VStack(alignment: .leading, spacing: 2) {
                Text(model.phase == .connecting ? "Connecting…" : "Waiting for others")
                    .font(.headline)
                    .contentTransition(.opacity)
                Text("Join room \(model.roomId) from another device")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 8)

            Button {
                #if os(iOS)
                UIPasteboard.general.string = model.roomId
                #else
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(model.roomId, forType: .string)
                #endif
                copied = true
            } label: {
                Label(copied ? "Copied" : "Copy", systemImage: copied ? "checkmark" : "doc.on.doc")
                    .contentTransition(.symbolEffect(.replace))
            }
            .buttonStyle(.glass)
            .sensoryFeedback(.success, trigger: copied) { _, isCopied in isCopied }
        }
        .padding(14)
        .frame(maxWidth: 460)
        .glassEffect(.regular, in: .rect(cornerRadius: 28, style: .continuous))
        .animation(.snappy, value: copied)
        .animation(.easeInOut, value: model.phase)
        .task(id: copied) {
            guard copied else { return }
            try? await Task.sleep(for: .seconds(1.5))
            copied = false
        }
    }
}

/// The last few chat messages float over the video for a few seconds.
struct RecentMessages: View {
    let messages: [ChatMessage]
    let visible: Bool

    private static let lifetime: TimeInterval = 6

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.5)) { context in
            let shown = visible
                ? messages.suffix(3).filter { context.date.timeIntervalSince($0.date) < Self.lifetime }
                : []
            VStack(alignment: .leading, spacing: 6) {
                ForEach(shown) { message in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(message.isLocal ? "You" : message.sender ?? "Peer")
                            .font(.caption2.weight(.semibold))
                            .foregroundStyle(.secondary)
                        Text(message.text)
                            .font(.subheadline)
                            .lineLimit(3)
                    }
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .glassEffect(
                        message.isLocal ? .regular.tint(.accentColor.opacity(0.55)) : .regular,
                        in: .rect(cornerRadius: 18, style: .continuous)
                    )
                    .transition(.move(edge: .leading).combined(with: .opacity))
                }
            }
            .animation(.spring(response: 0.4, dampingFraction: 0.8), value: shown.map(\.id))
        }
        .frame(maxWidth: 280, alignment: .leading)
        .allowsHitTesting(false)
    }
}

// MARK: - Microphone level

/// The three bars Google Meet shows on your own video, so you can see the call hears you.
/// Reads `micLevel` itself, so only the bars redraw while you talk.
struct MicLevelIndicator: View {
    let model: CallViewModel
    let large: Bool

    /// The middle bar moves the most.
    private static let barGains: [Double] = [0.6, 1, 0.6]

    var body: some View {
        let muted = !model.micOn
        let level = muted ? 0 : model.micLevel
        let barWidth: CGFloat = large ? 5 : 3
        let maxBar: CGFloat = large ? 22 : 12
        ZStack {
            if muted {
                Image(systemName: "mic.slash.fill")
                    .font((large ? Font.title3 : .caption).weight(.bold))
                    .foregroundStyle(Color(hex: 0xE5484D))
                    .transition(.scale.combined(with: .opacity))
            } else {
                HStack(spacing: large ? 4 : 3) {
                    ForEach(Self.barGains.indices, id: \.self) { index in
                        Capsule()
                            .fill(.white)
                            .frame(width: barWidth, height: barWidth + (maxBar - barWidth) * CGFloat(level * Self.barGains[index]))
                    }
                }
                .animation(.easeOut(duration: 0.1), value: level)
                .transition(.scale.combined(with: .opacity))
            }
        }
        .frame(width: large ? 48 : 28, height: large ? 48 : 28)
        .glassEffect(.regular.tint(.black.opacity(0.35)), in: .circle)
        .animation(.spring(response: 0.35, dampingFraction: 0.65), value: muted)
        .accessibilityLabel(muted ? "Microphone off" : "Your microphone level")
    }
}
