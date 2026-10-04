//
//  MacGroupStage.swift
//  WebRTCDemoMac
//

import SwiftUI
import WebRTC

/// The remote participants of a group call in rows of equal tiles. The column count is the one
/// that gives the largest tiles of roughly camera shape, as on the web; a short last row is centred.
/// Tiles are one flat list keyed by participant, so a join, a leave or a new column count only moves
/// them: their video views and double-click fit state survive.
struct MacGroupGrid: View {
    let model: CallViewModel

    var body: some View {
        let participants = model.participants
        GroupGridLayout(spacing: 12) {
            ForEach(participants) { participant in
                MacGroupTile(
                    participant: participant,
                    track: model.remoteVideoTracks[participant.id],
                    videoHidden: model.remoteVideoHidden,
                    speaking: model.activeSpeaker == participant.id,
                    audioLevel: model.audioLevels[participant.id] ?? 0
                )
                .transition(.scale(scale: 0.9).combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.5, dampingFraction: 0.85), value: participants.map(\.id))
    }
}

/// Equal cells in rows, filling the proposed space; a short last row is centred.
private struct GroupGridLayout: Layout {
    var spacing: CGFloat
    var tileAspect: CGFloat = 4 / 3

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        proposal.replacingUnspecifiedDimensions()
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let count = subviews.count
        guard count > 0 else { return }
        let columns = columns(for: count, in: bounds.size)
        let rows = (count + columns - 1) / columns
        let width = max((bounds.width - spacing * CGFloat(columns - 1)) / CGFloat(columns), 0)
        let height = max((bounds.height - spacing * CGFloat(rows - 1)) / CGFloat(rows), 0)
        let cell = ProposedViewSize(width: width, height: height)
        for (index, subview) in subviews.enumerated() {
            let row = index / columns
            let column = index % columns
            let inRow = min(columns, count - row * columns)
            let rowWidth = CGFloat(inRow) * width + CGFloat(inRow - 1) * spacing
            let x = bounds.minX + (bounds.width - rowWidth) / 2 + CGFloat(column) * (width + spacing)
            let y = bounds.minY + CGFloat(row) * (height + spacing)
            subview.place(at: CGPoint(x: x, y: y), anchor: .topLeading, proposal: cell)
        }
    }

    private func columns(for count: Int, in size: CGSize) -> Int {
        let width = max(size.width, 1)
        let height = max(size.height, 1)
        var best = (columns: 1, score: -CGFloat.infinity)
        for columns in 1...max(count, 1) {
            let rows = (count + columns - 1) / columns
            let cellWidth = (width - spacing * CGFloat(columns - 1)) / CGFloat(columns)
            let cellHeight = (height - spacing * CGFloat(rows - 1)) / CGFloat(rows)
            let score = min(cellWidth, cellHeight * tileAspect)
            if score > best.score { best = (columns, score) }
        }
        return best.columns
    }
}

/// One remote participant: video that fills the tile (or fits while they present; a double click
/// switches until they start or stop presenting), the gradient avatar while there is no video, a
/// "name · short id" label with the mic and presenting state, and a ring while they speak.
private struct MacGroupTile: View {
    let participant: GroupParticipant
    let track: RTCVideoTrack?
    let videoHidden: Bool
    let speaking: Bool
    let audioLevel: Double

    @State private var userFit: Bool?
    @State private var hovering = false

    private var fit: Bool { userFit ?? participant.state.screen }
    private var videoPaused: Bool { videoHidden || !participant.state.video || track == nil }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 22, style: .continuous)
        GeometryReader { proxy in
            let roomy = proxy.size.height >= 260
            ZStack {
                Color(white: 0.1)
                if let track {
                    StageVideoView(track: track, fill: !fit)
                }
                if videoPaused {
                    PeerPlaceholderView(
                        snapshot: nil,
                        seed: "peer-\(participant.id)",
                        audioLevel: participant.state.audio ? audioLevel : 0,
                        avatarSize: min(max(min(proxy.size.width, proxy.size.height) * 0.3, 36), 96),
                        title: roomy ? (videoHidden ? "You hid their video" : "Camera is off") : nil
                    )
                    .transition(.opacity)
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .clipShape(shape)
        .overlay {
            shape.strokeBorder(
                speaking ? Color(hex: 0x4ADE80) : .white.opacity(hovering ? 0.22 : 0.1),
                lineWidth: speaking ? 3 : 1
            )
        }
        .overlay(alignment: .bottomLeading) { nameLabel }
        .overlay(alignment: .topLeading) {
            if participant.state.screen {
                Label("Presenting", systemImage: "rectangle.on.rectangle")
                    .font(.caption.weight(.semibold))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .glassEffect(.regular, in: .capsule)
                    .padding(10)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.3), value: videoPaused)
        .animation(.easeInOut(duration: 0.2), value: speaking)
        .animation(.easeInOut(duration: 0.2), value: hovering)
        .animation(.spring(response: 0.35, dampingFraction: 0.7), value: participant.state)
        .contentShape(shape)
        .onTapGesture(count: 2, perform: toggleFit)
        .onHover { hovering = $0 }
        .onChange(of: participant.state.screen) { userFit = nil }
        .contextMenu {
            Button(fit ? "Fill Tile" : "Fit Whole Video", systemImage: fit ? "arrow.up.left.and.arrow.down.right" : "arrow.down.right.and.arrow.up.left", action: toggleFit)
                .disabled(videoPaused)
        }
        .help(videoPaused ? participant.label : "\(participant.label) — double-click to \(fit ? "fill the tile" : "fit the whole video")")
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityText)
        .accessibilityAction(named: fit ? "Fill tile" : "Fit whole video", toggleFit)
    }

    private func toggleFit() {
        userFit = !fit
    }

    private var nameLabel: some View {
        let status = ParticipantStatus(participant.state)
        return HStack(spacing: 5) {
            StatusMark(status: status)
            Text(participant.name)
                .lineLimit(1)
            Text(participant.shortId)
                .foregroundStyle(.secondary)
                .monospaced()
        }
        .font(.caption.weight(.semibold))
        .padding(.leading, 5)
        .padding(.trailing, 10)
        .padding(.vertical, 5)
        .glassEffect(.regular, in: .capsule)
        .animation(StatusMark.animation, value: status)
        .padding(10)
    }

    private var accessibilityText: String {
        var parts = [participant.label]
        if !participant.state.audio { parts.append("muted") }
        if participant.state.screen { parts.append("presenting") }
        if speaking { parts.append("speaking") }
        return parts.joined(separator: ", ")
    }
}

// MARK: - People

/// Everyone in the room, you first and highlighted, so you know which tile is yours on the other devices.
struct MacPeopleList: View {
    let model: CallViewModel

    private struct Person: Identifiable {
        let participant: GroupParticipant
        let isYou: Bool
        var id: String { participant.id }
    }

    private var people: [Person] {
        var people = model.participants.map { Person(participant: $0, isYou: false) }
        if let you = model.selfParticipant {
            people.insert(Person(participant: you, isYou: true), at: 0)
        }
        return people
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text("\(people.count) in call")
                    .font(.headline)
                Text("Others see you by the name on your row.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 6)

            ScrollView {
                VStack(spacing: 2) {
                    ForEach(people) { person in
                        MacPersonRow(
                            participant: person.participant,
                            isYou: person.isYou,
                            speaking: !person.isYou && model.activeSpeaker == person.participant.id
                        )
                    }
                }
            }
            .scrollBounceBehavior(.basedOnSize)
            .frame(maxHeight: 360)
        }
        .padding(14)
        .frame(width: 320)
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: people.map(\.id))
    }
}

private struct MacPersonRow: View {
    let participant: GroupParticipant
    let isYou: Bool
    let speaking: Bool

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 12, style: .continuous)
        HStack(spacing: 10) {
            avatar
            Text(participant.label)
                .font(isYou ? Font.body.weight(.semibold) : Font.body)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
            if isYou {
                Text("You")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Color.accentColor, in: .capsule)
            }
            HStack(spacing: 6) {
                if participant.state.screen {
                    Image(systemName: "rectangle.on.rectangle")
                        .help("Presenting")
                        .transition(.scale(scale: 0.5).combined(with: .opacity))
                } else if !participant.state.video {
                    Image(systemName: "video.slash.fill")
                        .help("Camera off")
                        .transition(.scale(scale: 0.5).combined(with: .opacity))
                }
                if !participant.state.audio {
                    Image(systemName: "mic.slash.fill")
                        .foregroundStyle(Color(hex: 0xE5484D))
                        .help("Muted")
                        .transition(.scale(scale: 0.5).combined(with: .opacity))
                }
            }
            .font(.callout)
            .foregroundStyle(.secondary)
            .animation(StatusMark.animation, value: participant.state)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background {
            if isYou {
                shape.fill(Color.accentColor.opacity(0.18))
                shape.strokeBorder(Color.accentColor.opacity(0.5), lineWidth: 1)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityText)
    }

    /// Same gradient as this person's camera-off placeholder on everyone's screen.
    private var avatar: some View {
        Circle()
            .fill(LinearGradient(colors: Avatar.colors(for: "peer-\(participant.id)"), startPoint: .topLeading, endPoint: .bottomTrailing))
            .overlay {
                Text(participant.name.prefix(1))
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white)
            }
            .frame(width: 28, height: 28)
            .padding(2)
            .overlay {
                Circle().strokeBorder(Color(hex: 0x4ADE80), lineWidth: 2).opacity(speaking ? 1 : 0)
            }
            .animation(.easeInOut(duration: 0.2), value: speaking)
    }

    private var accessibilityText: String {
        var parts = [participant.label]
        if isYou { parts.append("you") }
        if !participant.state.audio { parts.append("muted") }
        if participant.state.screen { parts.append("presenting") } else if !participant.state.video { parts.append("camera off") }
        if speaking { parts.append("speaking") }
        return parts.joined(separator: ", ")
    }
}

// MARK: - Status mark

enum ParticipantStatus: Equatable {
    case muted, presenting, live

    init(_ state: MediaState) {
        if !state.audio {
            self = .muted
        } else {
            self = state.screen ? .presenting : .live
        }
    }
}

/// One slot for the three marks of a label, as on the web: a red mic-off badge, the presenting icon
/// or a small emerald dot. They cross-fade and scale while the slot's width follows, so the pill
/// around it resizes smoothly when animated with `StatusMark.animation`.
struct StatusMark: View {
    let status: ParticipantStatus

    static let animation: Animation = .bouncy(duration: 0.3, extraBounce: 0.15)

    private var width: CGFloat {
        switch status {
        case .muted: return 18
        case .presenting: return 15
        case .live: return 8
        }
    }

    var body: some View {
        ZStack(alignment: .leading) {
            MicOffBadge()
                .statusMark(visible: status == .muted)
            Image(systemName: "rectangle.on.rectangle")
                .font(.system(size: 10, weight: .bold))
                .padding(.leading, 2)
                .statusMark(visible: status == .presenting)
            Circle()
                .fill(Color(hex: 0x34D399))
                .frame(width: 6, height: 6)
                .padding(.leading, 2)
                .statusMark(visible: status == .live)
        }
        .frame(width: width, height: 18, alignment: .leading)
        .accessibilityHidden(status == .live)
        .accessibilityLabel(status == .muted ? "Microphone muted" : "Presenting")
    }
}

/// White mic-off on a red circle, the muted mark on every label.
struct MicOffBadge: View {
    var body: some View {
        Image(systemName: "mic.slash.fill")
            .font(.system(size: 9, weight: .bold))
            .foregroundStyle(.white)
            .frame(width: 18, height: 18)
            .background(Color(hex: 0xE5484D), in: .circle)
    }
}

extension View {
    /// Shown, or shrunk to half size and faded out.
    func statusMark(visible: Bool) -> some View {
        scaleEffect(visible ? 1 : 0.5).opacity(visible ? 1 : 0)
    }
}
