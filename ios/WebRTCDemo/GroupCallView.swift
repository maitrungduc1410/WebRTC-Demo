//
//  GroupCallView.swift
//  WebRTCDemo
//

import PhotosUI
import SwiftUI
import UniformTypeIdentifiers
import WebRTC

/// The call screen of a group call (SFU): a grid of remote tiles, the local draggable tile and the
/// same toolbar, chat and More sheet as a 1:1 call. System picture-in-picture is 1:1 only.
struct GroupCallView: View {
    let onLeave: () -> Void

    @State private var model: CallViewModel
    @State private var controlsVisible = true
    @State private var sheet: CallSheet?
    @State private var interactions = 0
    @State private var cameraSwitches = 0
    @State private var showPhotoPicker = false
    @State private var photoItem: PhotosPickerItem?
    @State private var showFileImporter = false
    /// Shown once nothing else is presented, since an alert can't appear over a sheet that is still closing.
    @State private var endedAlert: String?

    private static let controlsAutoHide: Duration = .seconds(5)
    private static let toastLifetime: Duration = .seconds(2.5)

    init(roomId: String, e2ee: Bool, onLeave: @escaping () -> Void) {
        self.onLeave = onLeave
        _model = State(initialValue: CallViewModel(roomId: roomId, e2ee: e2ee, isGroup: true))
    }

    private var hasOthers: Bool { !model.participants.isEmpty }

    private var showControls: Bool { controlsVisible || !hasOthers }

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color.black
                    .ignoresSafeArea()

                if hasOthers {
                    ParticipantGrid(model: model, landscape: proxy.size.width > proxy.size.height, onTap: toggleControls)
                        .padding(.horizontal, 8)
                        .padding(.top, showControls ? 60 : 8)
                        .padding(.bottom, showControls ? 88 : 8)
                        .contentShape(.rect)
                        .onTapGesture(perform: toggleControls)
                        .transition(.opacity.combined(with: .scale(scale: 1.04)))
                }

                LocalTile(
                    model: model,
                    pip: hasOthers,
                    size: proxy.size,
                    insets: proxy.safeAreaInsets,
                    controlsVisible: showControls,
                    cameraSwitches: cameraSwitches,
                    onTap: toggleControls
                )

                chrome(width: proxy.size.width + proxy.safeAreaInsets.leading + proxy.safeAreaInsets.trailing)
            }
            .animation(.spring(response: 0.6, dampingFraction: 0.85), value: hasOthers)
            .animation(.spring(response: 0.45, dampingFraction: 0.85), value: showControls)
        }
        .ignoresSafeArea(.keyboard)
        .preferredColorScheme(.dark)
        .statusBarHidden(!showControls)
        .persistentSystemOverlays(showControls ? .automatic : .hidden)
        .onAppear { model.start() }
        .onDisappear { model.stop() }
        .task(id: AutoHide(visible: controlsVisible, hasOthers: hasOthers, sheet: sheet, interactions: interactions)) {
            guard controlsVisible, hasOthers, sheet == nil else { return }
            try? await Task.sleep(for: Self.controlsAutoHide)
            guard !Task.isCancelled else { return }
            withAnimation(.easeInOut(duration: 0.35)) { controlsVisible = false }
        }
        .task(id: model.toast?.id) {
            guard model.toast != nil else { return }
            try? await Task.sleep(for: Self.toastLifetime)
            guard !Task.isCancelled else { return }
            withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) { model.toast = nil }
        }
        .sensoryFeedback(.selection, trigger: model.micOn)
        .sensoryFeedback(.selection, trigger: model.cameraOn)
        .sensoryFeedback(.success, trigger: hasOthers) { _, connected in connected }
        .sheet(item: $sheet) { sheet in
            switch sheet {
            case .more:
                MoreSheet(
                    model: model,
                    remoteFit: false,
                    onToggleFit: {},
                    onSwitchCamera: switchCamera,
                    onOpenEffects: { self.sheet = .effects }
                )
                    .presentationDetents([.medium, .large])
                    .presentationDragIndicator(.visible)
            case .chat:
                ChatView(model: model)
                    .presentationDetents([.medium, .large])
                    .presentationDragIndicator(.visible)
                    .onAppear { model.openChat() }
                    .onDisappear { model.closeChat() }
            case .effects:
                EffectsSheet(model: model)
                    .presentationDetents([.large])
                    .presentationDragIndicator(.visible)
            case .people:
                PeopleSheet(model: model)
                    .presentationDetents([.medium, .large])
                    .presentationDragIndicator(.visible)
            }
        }
        .onChange(of: model.endedMessage) { _, message in
            // Nothing of the finished call stays open over the "Call ended" alert.
            guard let message else { return }
            let covered = sheet != nil || showPhotoPicker || showFileImporter
            sheet = nil
            showPhotoPicker = false
            showFileImporter = false
            Task { @MainActor in
                if covered { try? await Task.sleep(for: .milliseconds(600)) }
                endedAlert = message
            }
        }
        .alert(
            "Call ended",
            isPresented: Binding(get: { endedAlert != nil }, set: { _ in }),
            presenting: endedAlert
        ) { _ in
            Button("Back to lobby", action: leave)
        } message: { message in
            Text(message)
        }
        .photosPicker(isPresented: $showPhotoPicker, selection: $photoItem, matching: .videos)
        .onChange(of: photoItem) { _, item in
            guard let item else { return }
            photoItem = nil
            Task {
                if let video = try? await item.loadTransferable(type: PickedVideo.self) {
                    model.shareVideoFile(url: video.url)
                } else {
                    model.show("Couldn't open that video", systemImage: "exclamationmark.triangle.fill")
                }
            }
        }
        .fileImporter(
            isPresented: $showFileImporter,
            allowedContentTypes: [.movie, .video, .mpeg4Movie, .quickTimeMovie]
        ) { result in
            do {
                model.shareVideoFile(url: try VideoImport.copySecurityScoped(result.get()))
            } catch {
                model.show("Couldn't open that video", systemImage: "exclamationmark.triangle.fill")
            }
        }
    }

    // MARK: - Overlays

    private func chrome(width: CGFloat) -> some View {
        let buttonSize = min(56, max(44, (width - 82) / 6.3))
        return VStack(spacing: 12) {
            if showControls {
                topBar
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
            Spacer(minLength: 0)

            RecentMessages(messages: model.messages, visible: sheet != .chat)
                .frame(maxWidth: .infinity, alignment: .leading)

            if !hasOthers && model.endedMessage == nil {
                WaitingCard(model: model)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }

            if let toast = model.toast {
                Label(toast.text, systemImage: toast.systemImage ?? "info.circle.fill")
                    .font(.subheadline.weight(.medium))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .glassEffect(.regular, in: .capsule)
                    .id(toast.id)
                    .transition(.move(edge: .bottom).combined(with: .opacity).combined(with: .scale(scale: 0.9)))
            }

            if showControls {
                CallToolbar(
                    model: model,
                    buttonSize: buttonSize,
                    onShareScreen: model.shareScreen,
                    onShareFromPhotos: { showPhotoPicker = true },
                    onShareFromFiles: { showFileImporter = true },
                    onChat: { sheet = .chat },
                    onMore: { sheet = .more },
                    onHangUp: leave
                )
                .simultaneousGesture(TapGesture().onEnded { interactions += 1 })
                .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .padding(.horizontal, 16)
        .padding(.top, 4)
        .padding(.bottom, 8)
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: model.toast)
    }

    private var topBar: some View {
        HStack(spacing: 8) {
            GlassEffectContainer(spacing: 8) {
                HStack(spacing: 8) {
                    RoomPill(model: model)
                    if hasOthers {
                        Button { sheet = .people } label: {
                            StatusChip(systemImage: "person.2.fill", text: "\(model.participants.count + 1)", interactive: true)
                                .contentShape(.capsule)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("\(model.participants.count + 1) in call")
                        .accessibilityHint("Shows everyone in the call")
                        .transition(.scale.combined(with: .opacity))
                    }
                }
            }
            .animation(.spring(response: 0.4, dampingFraction: 0.75), value: model.participants.count)

            Spacer(minLength: 0)

            if model.sharing == .none {
                GlassCircleButton(
                    systemImage: "arrow.triangle.2.circlepath.camera.fill",
                    label: "Switch camera",
                    size: 44,
                    action: switchCamera
                )
                .transition(.scale.combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.4, dampingFraction: 0.8), value: model.sharing)
    }

    // MARK: - Actions

    private func toggleControls() {
        withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) {
            controlsVisible.toggle()
        }
    }

    /// The local tile does the switch so it can flip the preview around it.
    private func switchCamera() {
        interactions += 1
        cameraSwitches += 1
    }

    private func leave() {
        model.stop()
        onLeave()
    }
}

private struct AutoHide: Equatable {
    var visible: Bool
    var hasOthers: Bool
    var sheet: CallSheet?
    var interactions: Int
}

// MARK: - Grid

/// Lays the remote participants out in rows of equal tiles that fill the available space.
private struct ParticipantGrid: View {
    let model: CallViewModel
    let landscape: Bool
    let onTap: () -> Void

    private static let spacing: CGFloat = 8

    var body: some View {
        let participants = model.participants
        let columns = Self.columns(for: participants.count, landscape: landscape)
        let rows = stride(from: 0, to: participants.count, by: columns).map {
            Array(participants[$0..<min($0 + columns, participants.count)])
        }
        GeometryReader { proxy in
            let rowCount = CGFloat(max(rows.count, 1))
            let width = (proxy.size.width - Self.spacing * CGFloat(columns - 1)) / CGFloat(columns)
            let height = (proxy.size.height - Self.spacing * (rowCount - 1)) / rowCount
            VStack(spacing: Self.spacing) {
                ForEach(rows, id: \.first?.id) { row in
                    HStack(spacing: Self.spacing) {
                        ForEach(row) { participant in
                            ParticipantTile(
                                participant: participant,
                                track: model.remoteVideoTracks[participant.id],
                                videoHidden: model.remoteVideoHidden,
                                speaking: model.activeSpeaker == participant.id,
                                audioLevel: model.audioLevels[participant.id] ?? 0,
                                onTap: onTap
                            )
                            .frame(width: max(width, 0), height: max(height, 0))
                            .transition(.scale(scale: 0.85).combined(with: .opacity))
                        }
                    }
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .animation(.spring(response: 0.5, dampingFraction: 0.85), value: participants.map(\.id))
    }

    private static func columns(for count: Int, landscape: Bool) -> Int {
        switch count {
        case ...1: return 1
        case 2: return landscape ? 2 : 1
        case 3, 4: return 2
        case 5, 6: return landscape ? 3 : 2
        default: return landscape ? 4 : 3
        }
    }
}

/// One remote participant: video (cropped, or letterboxed while they present; double tap switches)
/// or the placeholder avatar, a name label with the mic state, and a ring while they are the active speaker.
private struct ParticipantTile: View {
    let participant: GroupParticipant
    let track: RTCVideoTrack?
    let videoHidden: Bool
    let speaking: Bool
    let audioLevel: Double
    let onTap: () -> Void

    /// Set by a double tap, until they start or stop presenting.
    @State private var userFit: Bool?

    private var fit: Bool { userFit ?? participant.state.screen }
    private var videoPaused: Bool { videoHidden || !participant.state.video || track == nil }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 22, style: .continuous)
        GeometryReader { proxy in
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
                        avatarSize: min(max(min(proxy.size.width, proxy.size.height) * 0.3, 36), 96)
                    )
                    .transition(.opacity)
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .clipShape(shape)
        .overlay {
            shape.strokeBorder(
                speaking ? Color(hex: 0x4ADE80) : .white.opacity(0.12),
                lineWidth: speaking ? 3 : 1
            )
        }
        .overlay(alignment: .bottomLeading) { nameLabel }
        .overlay(alignment: .topLeading) {
            if participant.state.screen {
                Label("Presenting", systemImage: "rectangle.on.rectangle")
                    .font(.caption2.weight(.semibold))
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .glassEffect(.regular, in: .capsule)
                    .padding(8)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.3), value: videoPaused)
        .animation(.easeInOut(duration: 0.2), value: speaking)
        .animation(.spring(response: 0.35, dampingFraction: 0.7), value: participant.state)
        .contentShape(shape)
        // Both on the tile, so a single tap waits until it is not the start of a double tap.
        .onTapGesture(count: 2) { userFit = !fit }
        .onTapGesture(perform: onTap)
        .onChange(of: participant.state.screen) { userFit = nil }
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityText)
        .accessibilityAction(named: fit ? "Fill tile" : "Fit whole video") { userFit = !fit }
    }

    private var nameLabel: some View {
        HStack(spacing: 4) {
            if !participant.state.audio {
                Image(systemName: "mic.slash.fill")
                    .foregroundStyle(Color(hex: 0xE5484D))
                    .transition(.scale.combined(with: .opacity))
            }
            Text(participant.name)
                .lineLimit(1)
            Text(participant.shortId)
                .foregroundStyle(.secondary)
                .monospaced()
        }
        .font(.caption.weight(.semibold))
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .glassEffect(.regular, in: .capsule)
        .padding(8)
    }

    private var accessibilityText: String {
        var parts = [participant.name]
        if !participant.state.audio { parts.append("muted") }
        if participant.state.screen { parts.append("presenting") }
        if speaking { parts.append("speaking") }
        return parts.joined(separator: ", ")
    }
}

// MARK: - People

/// Everyone in the room, you first and highlighted, so you know which tile is yours on the other devices.
private struct PeopleSheet: View {
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
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(people.count) in call")
                        .font(.title3.weight(.semibold))
                    Text("Others see you by the name on your row.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                .padding(.horizontal, 4)

                VStack(spacing: 4) {
                    ForEach(people) { person in
                        PersonRow(
                            participant: person.participant,
                            isYou: person.isYou,
                            speaking: !person.isYou && model.activeSpeaker == person.participant.id
                        )
                    }
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 24)
            .padding(.bottom, 16)
        }
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: people.map(\.id))
    }
}

private struct PersonRow: View {
    let participant: GroupParticipant
    let isYou: Bool
    let speaking: Bool

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 20, style: .continuous)
        HStack(spacing: 12) {
            avatar
            Text(participant.label)
                .font(isYou ? Font.body.weight(.semibold) : Font.body)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
            if isYou {
                Text("You")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 4)
                    .background(Color.accentColor, in: .capsule)
            }
            HStack(spacing: 8) {
                if participant.state.screen {
                    Image(systemName: "rectangle.on.rectangle")
                } else if !participant.state.video {
                    Image(systemName: "video.slash.fill")
                }
                if !participant.state.audio {
                    Image(systemName: "mic.slash.fill")
                        .foregroundStyle(Color(hex: 0xE5484D))
                }
            }
            .font(.subheadline)
            .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
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
                    .font(.subheadline.weight(.bold))
                    .foregroundStyle(.white)
            }
            .frame(width: 34, height: 34)
            .padding(3)
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
