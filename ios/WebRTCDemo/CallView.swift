//
//  CallView.swift
//  WebRTCDemo
//

import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

enum CallSheet: String, Identifiable {
    case more, chat, effects, people
    var id: String { rawValue }
}

private struct AutoHideKey: Equatable {
    var visible: Bool
    var hasRemote: Bool
    var sheet: CallSheet?
    var interactions: Int
}

struct CallView: View {
    let onLeave: () -> Void

    @State private var model: CallViewModel
    @State private var controlsVisible = true
    @State private var sheet: CallSheet?
    @State private var userFit: Bool?
    @State private var remoteVideoSize: CGSize = .zero
    @State private var stageLandscape = false
    @State private var interactions = 0
    @State private var cameraSwitches = 0
    @State private var showPhotoPicker = false
    @State private var photoItem: PhotosPickerItem?
    @State private var showFileImporter = false
    /// Shown once nothing else is presented, since an alert can't appear over a sheet that is still closing.
    @State private var endedAlert: String?
    @State private var pictureInPicture = PictureInPictureController()

    private static let controlsAutoHide: Duration = .seconds(5)
    private static let toastLifetime: Duration = .seconds(2.5)

    init(roomId: String, e2ee: Bool, onLeave: @escaping () -> Void) {
        self.onLeave = onLeave
        _model = State(initialValue: CallViewModel(roomId: roomId, e2ee: e2ee))
    }

    /// Screen content is letterboxed by default so nothing is cut off; camera video fills the screen
    /// unless it is held the other way round, where filling would crop most of the picture away.
    private var defaultFit: Bool {
        let crossOrientation = remoteVideoSize.width > 0 && remoteVideoSize.height > 0
            && (remoteVideoSize.width > remoteVideoSize.height) != stageLandscape
        return model.remote.screen || crossOrientation
    }

    private var remoteFit: Bool { userFit ?? defaultFit }

    private var showControls: Bool { controlsVisible || !model.hasRemote }

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color.black
                    .ignoresSafeArea()

                if model.hasRemote {
                    remoteStage
                        .transition(.opacity.combined(with: .scale(scale: 1.06)))
                }

                LocalTile(
                    model: model,
                    pip: model.hasRemote,
                    size: proxy.size,
                    insets: proxy.safeAreaInsets,
                    controlsVisible: showControls,
                    cameraSwitches: cameraSwitches,
                    onTap: toggleControls
                )

                scrims
                chrome(width: proxy.size.width + proxy.safeAreaInsets.leading + proxy.safeAreaInsets.trailing)
            }
            .animation(.spring(response: 0.6, dampingFraction: 0.85), value: model.hasRemote)
        }
        .ignoresSafeArea(.keyboard)
        .preferredColorScheme(.dark)
        .statusBarHidden(!showControls)
        .persistentSystemOverlays(showControls ? .automatic : .hidden)
        .onAppear { model.start() }
        .onDisappear {
            pictureInPicture.tearDown()
            model.stop()
        }
        .onGeometryChange(for: Bool.self) { $0.size.width > $0.size.height } action: { stageLandscape = $0 }
        .onChange(of: defaultFit) { userFit = nil }
        .onChange(of: model.remoteTrack) { remoteVideoSize = .zero }
        .onChange(of: model.remoteTrack, initial: true) { pictureInPicture.track = model.remoteTrack }
        .onChange(of: model.hasRemote, initial: true) { pictureInPicture.startsAutomatically = model.hasRemote }
        .task(id: AutoHideKey(visible: controlsVisible, hasRemote: model.hasRemote, sheet: sheet, interactions: interactions)) {
            guard controlsVisible, model.hasRemote, sheet == nil else { return }
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
        .sensoryFeedback(.success, trigger: model.hasRemote) { _, connected in connected }
        .sheet(item: $sheet) { sheet in
            switch sheet {
            case .more:
                MoreSheet(
                    model: model,
                    remoteFit: remoteFit,
                    onToggleFit: { userFit = !remoteFit },
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
                // Group calls only.
                EmptyView()
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

    // MARK: - Remote participant

    private var remoteStage: some View {
        ZStack {
            PictureInPictureSourceView(controller: pictureInPicture)
            StageVideoView(track: model.remoteTrack, fill: !remoteFit) { remoteVideoSize = $0 }

            if model.remoteVideoPaused {
                PeerPlaceholderView(
                    snapshot: model.remoteSnapshot,
                    seed: "peer-\(model.roomId)",
                    audioLevel: model.remoteAudioLevel,
                    title: model.remoteVideoHidden ? "You hid their video" : "Camera is off",
                    subtitle: model.remote.audio ? nil : "Microphone muted"
                ) {
                    if model.remoteVideoHidden {
                        Button("Show video", systemImage: "eye.fill", action: model.toggleRemoteVideo)
                            .buttonStyle(.glass)
                    }
                }
                .transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.35), value: model.remoteVideoPaused)
        .ignoresSafeArea()
        .contentShape(.rect)
        .onTapGesture(count: 2) { userFit = !remoteFit }
        .onTapGesture(perform: toggleControls)
    }

    // MARK: - Overlays

    private var scrims: some View {
        VStack(spacing: 0) {
            LinearGradient(colors: [.black.opacity(0.55), .clear], startPoint: .top, endPoint: .bottom)
                .frame(height: 160)
            Spacer()
            LinearGradient(colors: [.clear, .black.opacity(0.55)], startPoint: .top, endPoint: .bottom)
                .frame(height: 220)
        }
        .ignoresSafeArea()
        .opacity(model.hasRemote && showControls ? 1 : 0)
        .animation(.easeInOut(duration: 0.3), value: showControls)
        .allowsHitTesting(false)
    }

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

            if !model.hasRemote && model.endedMessage == nil {
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
        .animation(.spring(response: 0.45, dampingFraction: 0.85), value: showControls)
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: model.toast)
    }

    private var topBar: some View {
        HStack(spacing: 8) {
            GlassEffectContainer(spacing: 8) {
                HStack(spacing: 8) {
                    RoomPill(model: model)
                    if model.phase == .connected && !model.remote.audio {
                        StatusChip(systemImage: "mic.slash.fill", text: "Muted")
                            .transition(.scale.combined(with: .opacity))
                    }
                    if model.phase == .connected && model.remote.screen {
                        StatusChip(systemImage: "rectangle.on.rectangle", text: "Presenting")
                            .transition(.scale.combined(with: .opacity))
                    }
                }
            }
            .animation(.spring(response: 0.4, dampingFraction: 0.75), value: model.remote)

            Spacer(minLength: 0)

            if model.hasRemote && pictureInPicture.isPossible {
                GlassCircleButton(
                    systemImage: "pip.enter",
                    label: "Picture in picture",
                    size: 44,
                    action: pictureInPicture.start
                )
                .transition(.scale.combined(with: .opacity))
            }

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
        pictureInPicture.tearDown()
        model.stop()
        onLeave()
    }
}

// MARK: - Local preview

private enum Corner {
    case topLeading, topTrailing, bottomLeading, bottomTrailing
}

/// The local camera: full screen while alone, then it shrinks into a draggable picture-in-picture
/// that snaps to the nearest corner. Double-tap the PiP to flip to the other camera.
struct LocalTile: View {
    let model: CallViewModel
    let pip: Bool
    /// Size of the safe area; positions are in its coordinate space.
    let size: CGSize
    let insets: EdgeInsets
    let controlsVisible: Bool
    /// Incremented by the parent for every camera switch request.
    let cameraSwitches: Int
    let onTap: () -> Void

    @State private var videoSize = CGSize(width: 3, height: 4)
    @State private var corner: Corner = .topTrailing
    @State private var drag: CGSize = .zero
    @State private var flip: Double = 0
    @State private var flipping = false
    @State private var snaps = 0

    private static let cameraSwitchTimeout: TimeInterval = 1.2

    private let margin: CGFloat = 16

    private var pipSize: CGSize {
        let aspect = min(max(videoSize.width / max(videoSize.height, 1), 0.4), 2)
        let width: CGFloat = aspect >= 1 ? 168 : 116
        return CGSize(width: width, height: width / aspect)
    }

    private func center(of corner: Corner) -> CGPoint {
        let tile = pipSize
        let top = (controlsVisible ? 64 : 8) + tile.height / 2
        let bottom = size.height - (controlsVisible ? 96 : 8) - tile.height / 2
        let leading = margin + tile.width / 2
        let trailing = size.width - margin - tile.width / 2
        switch corner {
        case .topLeading: return CGPoint(x: leading, y: top)
        case .topTrailing: return CGPoint(x: trailing, y: top)
        case .bottomLeading: return CGPoint(x: leading, y: bottom)
        case .bottomTrailing: return CGPoint(x: trailing, y: bottom)
        }
    }

    var body: some View {
        let fullSize = CGSize(
            width: size.width + insets.leading + insets.trailing,
            height: size.height + insets.top + insets.bottom
        )
        let fullCenter = CGPoint(
            x: (size.width + insets.trailing - insets.leading) / 2,
            y: (size.height + insets.bottom - insets.top) / 2
        )
        let frame = pip ? pipSize : fullSize
        let anchor = pip ? center(of: corner) : fullCenter
        let shape = RoundedRectangle(cornerRadius: pip ? 24 : 0, style: .continuous)

        content
            .frame(width: frame.width, height: frame.height)
            .clipShape(shape)
            .overlay {
                shape.strokeBorder(.white.opacity(pip ? 0.3 : 0), lineWidth: 1)
            }
            .shadow(color: .black.opacity(pip ? 0.35 : 0), radius: 18, y: 8)
            .rotation3DEffect(.degrees(flip), axis: (x: 0, y: 1, z: 0), perspective: 0.4)
            .contentShape(shape)
            .gesture(dragGesture, including: pip ? .all : .subviews)
            .onTapGesture(count: 2) {
                if pip { flipCamera() }
            }
            .onTapGesture(perform: onTap)
            .position(x: anchor.x + drag.width, y: anchor.y + drag.height)
            .animation(.spring(response: 0.6, dampingFraction: 0.82), value: pip)
            .animation(.spring(response: 0.45, dampingFraction: 0.8), value: controlsVisible)
            .animation(.spring(response: 0.4, dampingFraction: 0.8), value: videoSize)
            .sensoryFeedback(.impact(weight: .light), trigger: snaps)
            .sensoryFeedback(.impact(weight: .medium), trigger: model.frontCamera)
            .onChange(of: cameraSwitches) { flipCamera() }
            .accessibilityLabel("Your video")
    }

    private var content: some View {
        let presenting = model.sharing == .screen
        let mirror = model.frontCamera && model.sharing == .none
        return ZStack {
            Color(white: 0.1)
            if !presenting {
                VideoView(track: model.localTrack, fill: true) { videoSize = $0 }
                    .scaleEffect(x: mirror ? -1 : 1, y: 1)
            }
            if presenting {
                VStack(spacing: 6) {
                    Image(systemName: "rectangle.on.rectangle")
                        .font(.title2.weight(.semibold))
                        .symbolEffect(.pulse)
                    Text("Presenting")
                        .font(.caption.weight(.semibold))
                }
                .foregroundStyle(.white)
                .transition(.opacity)
            } else if !model.cameraOn {
                PeerPlaceholderView(
                    snapshot: nil,
                    seed: "you",
                    avatarSize: pip ? 48 : 112,
                    title: pip ? nil : "Your camera is off"
                )
                .transition(.opacity)
            }
        }
        .overlay(alignment: .bottomLeading) {
            if !model.micOn && pip {
                Image(systemName: "mic.slash.fill")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Color(hex: 0xE5484D))
                    .frame(width: 28, height: 28)
                    .glassEffect(.regular.tint(.black.opacity(0.35)), in: .circle)
                    .padding(8)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        // Your microphone: in the corner of the picture-in-picture (the badge above marks it muted),
        // on the left edge while the tile is the whole screen.
        .overlay(alignment: .bottomTrailing) {
            if model.micOn && pip {
                MicLevelIndicator(model: model, large: false)
                    .padding(8)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        .overlay(alignment: .leading) {
            if !pip {
                MicLevelIndicator(model: model, large: true)
                    .padding(.leading, insets.leading + margin)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.25), value: model.cameraOn)
        .animation(.spring(response: 0.35, dampingFraction: 0.65), value: model.micOn)
    }

    private var dragGesture: some Gesture {
        DragGesture(coordinateSpace: .global)
            .onChanged { drag = $0.translation }
            .onEnded { value in
                let start = center(of: corner)
                let endX = start.x + value.predictedEndTranslation.width
                let endY = start.y + value.predictedEndTranslation.height
                let leading = endX < size.width / 2
                let top = endY < size.height / 2
                withAnimation(.spring(response: 0.45, dampingFraction: 0.72)) {
                    corner = top ? (leading ? .topLeading : .topTrailing) : (leading ? .bottomLeading : .bottomTrailing)
                    drag = .zero
                }
                snaps += 1
            }
    }

    /// Turns the tile edge-on, switches while it is invisible, then shows the new camera from the
    /// other side. Waiting for the switch keeps the old camera's last frames off the back face.
    private func flipCamera() {
        guard model.sharing == .none, !flipping else { return }
        guard model.cameraOn else {
            model.switchCamera()
            return
        }
        flipping = true
        withAnimation(.easeIn(duration: 0.17)) {
            flip = 90
        } completion: {
            var revealed = false
            let reveal = {
                guard !revealed else { return }
                revealed = true
                flip = -90
                withAnimation(.spring(response: 0.42, dampingFraction: 0.72)) {
                    flip = 0
                } completion: {
                    flipping = false
                }
            }
            model.switchCamera(completion: reveal)
            DispatchQueue.main.asyncAfter(deadline: .now() + Self.cameraSwitchTimeout, execute: reveal)
        }
    }
}

// MARK: - Video import

/// A video picked from Photos, copied into the app's documents so it outlives the picker.
struct PickedVideo: Transferable {
    let url: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(contentType: .movie) { video in
            SentTransferredFile(video.url)
        } importing: { received in
            PickedVideo(url: try VideoImport.copyToDocuments(received.file))
        }
    }
}

enum VideoImport {
    static func copyToDocuments(_ url: URL) throws -> URL {
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let destination = documents.appendingPathComponent(url.lastPathComponent)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.copyItem(at: url, to: destination)
        return destination
    }

    static func copySecurityScoped(_ url: URL) throws -> URL {
        let accessing = url.startAccessingSecurityScopedResource()
        defer {
            if accessing { url.stopAccessingSecurityScopedResource() }
        }
        return try copyToDocuments(url)
    }
}
