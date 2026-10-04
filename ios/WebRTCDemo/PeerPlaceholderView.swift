//
//  PeerPlaceholderView.swift
//  WebRTCDemo
//

import SwiftUI

extension Color {
    init(hex: UInt32) {
        self.init(
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255
        )
    }
}

enum Avatar {
    private static let palettes: [[Color]] = [
        [Color(hex: 0x7C4DFF), Color(hex: 0x448AFF)],
        [Color(hex: 0xFF6E40), Color(hex: 0xFF4081)],
        [Color(hex: 0x00BFA5), Color(hex: 0x00B0FF)],
        [Color(hex: 0xFFAB00), Color(hex: 0xFF5252)],
        [Color(hex: 0x651FFF), Color(hex: 0xD500F9)],
        [Color(hex: 0x00C853), Color(hex: 0x64DD17)],
    ]

    /// Stable colors for `seed`. Uses Java's `String.hashCode` so Android picks the same palette.
    static func colors(for seed: String) -> [Color] {
        var hash: Int32 = 0
        for unit in seed.utf16 {
            hash = hash &* 31 &+ Int32(unit)
        }
        return palettes[Int(hash.magnitude % UInt32(palettes.count))]
    }
}

/// Messenger-style "video off" state: the last frame blurred behind a gradient avatar whose rings
/// pulse with the participant's voice.
struct PeerPlaceholderView<Action: View>: View {
    var snapshot: PlatformImage?
    var seed: String
    var audioLevel: Double = 0
    var avatarSize: CGFloat = 112
    var title: String?
    var subtitle: String?
    @ViewBuilder var action: () -> Action

    var body: some View {
        let colors = Avatar.colors(for: seed)
        ZStack {
            Color.black
            Color.clear
                .overlay {
                    if let snapshot {
                        Image(platformImage: snapshot)
                            .resizable()
                            .interpolation(.low)
                            .scaledToFill()
                            .blur(radius: 36)
                            .scaleEffect(1.25)
                            .transition(.opacity)
                    } else {
                        LinearGradient(
                            colors: [colors[0].opacity(0.55), .black, colors[1].opacity(0.45)],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                        .transition(.opacity)
                    }
                }
                .animation(.easeInOut(duration: 0.4), value: snapshot)
            Color.black.opacity(0.35)

            VStack(spacing: 0) {
                SpeakingAvatar(colors: colors, level: audioLevel, size: avatarSize)
                if let title {
                    Text(title)
                        .font(.headline)
                        .foregroundStyle(.white)
                        .multilineTextAlignment(.center)
                        .padding(.top, 12)
                }
                if let subtitle {
                    Text(subtitle)
                        .font(.subheadline)
                        .foregroundStyle(.white.opacity(0.75))
                        .multilineTextAlignment(.center)
                        .padding(.top, 4)
                }
                action()
                    .padding(.top, 16)
            }
            .padding()
        }
        .clipped()
    }
}

extension PeerPlaceholderView where Action == EmptyView {
    init(
        snapshot: PlatformImage?,
        seed: String,
        audioLevel: Double = 0,
        avatarSize: CGFloat = 112,
        title: String? = nil,
        subtitle: String? = nil
    ) {
        self.init(
            snapshot: snapshot,
            seed: seed,
            audioLevel: audioLevel,
            avatarSize: avatarSize,
            title: title,
            subtitle: subtitle,
            action: { EmptyView() }
        )
    }
}

/// A gradient avatar that breathes while idle and grows two halo rings with the audio level.
struct SpeakingAvatar: View {
    var colors: [Color]
    var level: Double
    var size: CGFloat

    @State private var breathing = false

    var body: some View {
        let level = min(max(self.level * 3, 0), 1)
        ZStack {
            Circle()
                .fill(colors[1])
                .frame(width: size, height: size)
                .scaleEffect(1 + level * 0.55)
                .scaleEffect(breathing ? 1.06 : 1)
                .opacity(0.18 + level * 0.5)
            Circle()
                .fill(colors[0])
                .frame(width: size, height: size)
                .scaleEffect(1 + level * 0.25)
                .opacity(0.3 + level * 0.4)
            Circle()
                .fill(LinearGradient(colors: colors, startPoint: .topLeading, endPoint: .bottomTrailing))
                .frame(width: size, height: size)
                .overlay {
                    Image(systemName: "person.fill")
                        .resizable()
                        .scaledToFit()
                        .foregroundStyle(.white)
                        .padding(size * 0.22)
                }
                .shadow(color: .black.opacity(0.25), radius: 12, y: 4)
        }
        .frame(width: size * 1.7, height: size * 1.7)
        .animation(.spring(response: 0.3, dampingFraction: 0.55), value: level)
        .onAppear {
            withAnimation(.easeInOut(duration: 1.6).repeatForever(autoreverses: true)) {
                breathing = true
            }
        }
    }
}
