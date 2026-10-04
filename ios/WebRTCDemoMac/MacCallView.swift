//
//  MacCallView.swift
//  WebRTCDemoMac
//

import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

/// The Mac call window: the other person's video edge to edge under a transparent title bar, a
/// draggable self view, a glass toolbar that hides when the pointer rests, and chat or effects in a
/// side panel. A group call (SFU) puts the others in a grid between the top bar and the toolbar,
/// which then stay up, as on the web.
struct MacCallView: View {
    let onLeave: () -> Void

    @State private var model: CallViewModel
    @State private var controlsVisible = true
    @State private var panel: SidePanel?
    @State private var chatTyping = false
    @State private var userFit: Bool?
    @State private var remoteVideoSize: CGSize = .zero
    @State private var stageLandscape = true
    @State private var windowWidth: CGFloat = 0
    @State private var showPicker = false
    /// Shown once the share picker or people list is gone; an alert can't appear over a closing sheet.
    @State private var endedAlert: String?
    @State private var peopleOpen = false
    @State private var hoveringChrome = false
    @State private var menusOpen = 0
    @State private var floating = FloatingCallWindow()
    @State private var idle = IdleTracker()
    @State private var windowReference = WindowReference()

    private static let autoHideDelay: TimeInterval = 4
    private static let toastLifetime: Duration = .seconds(2.5)
    private static let chatPanelWidth: CGFloat = 360
    /// The web client's effects sheet width: three background and four sticker columns.
    private static let effectsPanelWidth: CGFloat = 420
    /// Below this window width the panel floats over the video instead of taking a column.
    private static let sidePanelMinWidth: CGFloat = 1000

    init(roomId: String, e2ee: Bool, isGroup: Bool = false, onLeave: @escaping () -> Void) {
        self.onLeave = onLeave
        _model = State(initialValue: CallViewModel(roomId: roomId, e2ee: e2ee, isGroup: isGroup))
    }

    /// Someone else is in the call: the remote peer's video in 1:1, anyone in a group call. False once
    /// the call has ended, since a group call keeps its last participants and frozen tracks then.
    private var hasOthers: Bool {
        guard model.endedMessage == nil else { return false }
        return model.isGroup ? !model.participants.isEmpty : model.hasRemote
    }

    private var chatOpen: Bool { panel == .chat }
    private var effectsOpen: Bool { panel == .effects }
    private var panelWidth: CGFloat { effectsOpen ? Self.effectsPanelWidth : Self.chatPanelWidth }
    private var sidePanel: Bool { panel != nil && windowWidth >= Self.sidePanelMinWidth }

    /// Screen content is letterboxed by default so nothing is cut off; camera video fills the window
    /// unless it is held the other way round, where filling would crop most of the picture away.
    private var defaultFit: Bool {
        let crossOrientation = remoteVideoSize.width > 0 && remoteVideoSize.height > 0
            && (remoteVideoSize.width > remoteVideoSize.height) != stageLandscape
        return model.remote.screen || crossOrientation
    }

    private var remoteFit: Bool { userFit ?? defaultFit }

    private var showControls: Bool { controlsVisible || !hasOthers || model.isGroup }

    private var canAutoHide: Bool {
        !model.isGroup && model.hasRemote && !hoveringChrome && menusOpen == 0 && !showPicker && !floating.isShown
            && !(chatOpen && !sidePanel) && !effectsOpen
    }

    var body: some View {
        HStack(spacing: 0) {
            stage
            if sidePanel {
                panelContent
                    .frame(width: panelWidth)
                    .padding([.vertical, .trailing], 10)
                    .padding(.top, 28)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
            }
        }
        .background(Color.black)
        .ignoresSafeArea()
        .preferredColorScheme(.dark)
        .background(WindowAccessor(reference: windowReference))
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { windowWidth = $0 }
        .animation(.spring(response: 0.45, dampingFraction: 0.86), value: sidePanel)
        .onContinuousHover { phase in
            idle.poke()
            if case .active = phase, !controlsVisible { reveal() }
        }
        .onAppear { model.start() }
        .onDisappear(perform: tearDown)
        .onChange(of: showControls) {
            windowReference.setTrafficLightsHidden(!showControls)
            if !showControls { hideCursorIfInside() }
        }
        .onChange(of: chatOpen) {
            if chatOpen { model.openChat() } else { model.closeChat(); chatTyping = false }
            idle.poke()
        }
        .onChange(of: model.sharing) {
            // Effects only apply to the camera.
            if model.sharing != .none && effectsOpen { panel = nil }
        }
        .onChange(of: defaultFit) { userFit = nil }
        .onChange(of: model.remoteTrack) { remoteVideoSize = .zero }
        .onChange(of: remoteFit) { floating.setFill(!remoteFit, model: model, onHangUp: leave) }
        .onChange(of: hasOthers) { _, connected in
            if !connected { floating.hide() }
        }
        .onChange(of: model.endedMessage) { _, message in
            // The alert is in the call window, which may be minimized behind the floating panel.
            guard let message else { return }
            let covered = showPicker || peopleOpen
            floating.hide()
            peopleOpen = false
            showPicker = false
            panel = nil
            bringWindowBack()
            Task { @MainActor in
                if covered { try? await Task.sleep(for: .milliseconds(600)) }
                endedAlert = message
            }
        }
        .task { await autoHideLoop() }
        .task(id: model.toast?.id) {
            guard model.toast != nil else { return }
            try? await Task.sleep(for: Self.toastLifetime)
            guard !Task.isCancelled else { return }
            withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) { model.toast = nil }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didBeginTrackingNotification)) { _ in
            menusOpen += 1
        }
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didEndTrackingNotification)) { _ in
            menusOpen = max(0, menusOpen - 1)
            idle.poke()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didMiniaturizeNotification)) { note in
            // Minimizing mid-call keeps the other person on screen, like system picture in picture.
            guard note.object as? NSWindow === windowReference.window, hasOthers else { return }
            startFloating()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didDeminiaturizeNotification)) { note in
            guard note.object as? NSWindow === windowReference.window else { return }
            floating.hide()
        }
        .sheet(isPresented: $showPicker) {
            ScreenSharePicker(
                onPick: { source in
                    showPicker = false
                    model.shareScreen(source)
                },
                onCancel: { showPicker = false }
            )
        }
        .alert(
            "Call ended",
            isPresented: Binding(get: { endedAlert != nil }, set: { _ in }),
            presenting: endedAlert
        ) { _ in
            Button("Back to Lobby", action: leave)
                .keyboardShortcut(.defaultAction)
        } message: { message in
            Text(message)
        }
        .focusedSceneValue(\.callActions, callActions)
        .sensoryFeedback(.alignment, trigger: hasOthers) { _, connected in connected }
        .background {
            Button("Close Panel") { panel = nil }
                .keyboardShortcut(.cancelAction)
                .disabled(panel == nil)
                .opacity(0)
                .accessibilityHidden(true)
        }
    }

    // MARK: - Stage

    private var stage: some View {
        GeometryReader { proxy in
            ZStack {
                Color.black

                if model.isGroup {
                    if hasOthers && !floating.isShown {
                        MacGroupGrid(model: model)
                            .padding(.horizontal, 20)
                            .padding(.top, 64)
                            .padding(.bottom, 92)
                            .transition(.opacity.combined(with: .scale(scale: 1.04)))
                    }
                } else if model.hasRemote && !floating.isShown {
                    remoteStage
                        .transition(.opacity.combined(with: .scale(scale: 1.04)))
                }

                MacLocalTile(
                    model: model,
                    pip: hasOthers,
                    size: proxy.size,
                    controlsVisible: showControls,
                    label: model.selfParticipant.map { "You · \($0.label)" }
                )
                .opacity(floating.isShown ? 0 : 1)

                scrims

                WindowDragStrip(height: 40)
                    .frame(maxHeight: .infinity, alignment: .top)

                if floating.isShown {
                    floatingNotice
                        .transition(.opacity.combined(with: .scale(scale: 0.96)))
                }

                chrome(size: proxy.size)
            }
            .animation(.spring(response: 0.6, dampingFraction: 0.85), value: hasOthers)
            .animation(.spring(response: 0.45, dampingFraction: 0.85), value: floating.isShown)
        }
        .onGeometryChange(for: Bool.self) { $0.size.width >= $0.size.height } action: { stageLandscape = $0 }
    }

    private var remoteStage: some View {
        ZStack {
            StageVideoView(track: model.remoteTrack, fill: !remoteFit) { size in
                remoteVideoSize = size
                floating.setVideoSize(size)
            }

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
        .contentShape(.rect)
        .onTapGesture(count: 2, perform: toggleFit)
    }

    private var scrims: some View {
        VStack(spacing: 0) {
            LinearGradient(colors: [.black.opacity(0.5), .clear], startPoint: .top, endPoint: .bottom)
                .frame(height: 140)
            Spacer()
            LinearGradient(colors: [.clear, .black.opacity(0.5)], startPoint: .top, endPoint: .bottom)
                .frame(height: 200)
        }
        .opacity(!model.isGroup && model.hasRemote && showControls ? 1 : 0)
        .animation(.easeInOut(duration: 0.3), value: showControls)
        .allowsHitTesting(false)
    }

    private var floatingNotice: some View {
        VStack(spacing: 16) {
            Image(systemName: "pip")
                .font(.system(size: 44, weight: .regular))
                .foregroundStyle(.secondary)
                .symbolEffect(.pulse)
            VStack(spacing: 4) {
                Text("Your call is in a floating window")
                    .font(.title3.weight(.semibold))
                Text(model.isGroup
                    ? "It shows whoever is speaking, on top of other apps and on every Space."
                    : "It stays on top of other apps and follows you across Spaces.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Button("Bring It Back Here", systemImage: "pip.exit", action: stopFloating)
                .buttonStyle(.glassProminent)
                .controlSize(.large)
        }
        .multilineTextAlignment(.center)
        .padding(32)
    }

    // MARK: - Chrome

    private func chrome(size: CGSize) -> some View {
        VStack(spacing: 12) {
            if showControls {
                topBar
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
            Spacer(minLength: 0)

            RecentMessages(messages: model.messages, visible: !chatOpen)
                .frame(maxWidth: .infinity, alignment: .leading)

            if !hasOthers && !floating.isShown && model.endedMessage == nil {
                WaitingCard(model: model)
                    .onHover { hoveringChrome = $0 }
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }

            if let toast = model.toast {
                Label(toast.text, systemImage: toast.systemImage ?? "info.circle.fill")
                    .font(.callout.weight(.medium))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .glassEffect(.regular, in: .capsule)
                    .id(toast.id)
                    .transition(.move(edge: .bottom).combined(with: .opacity).combined(with: .scale(scale: 0.9)))
            }

            if showControls {
                MacCallToolbar(
                    model: model,
                    chatOpen: chatOpen,
                    effectsOpen: effectsOpen,
                    remoteFit: remoteFit,
                    canFit: canFit,
                    floating: floating.isShown,
                    canFloat: hasOthers,
                    onShareScreen: { showPicker = true },
                    onShareFile: chooseVideoFile,
                    onChat: toggleChat,
                    onEffects: toggleEffects,
                    onToggleFit: toggleFit,
                    onToggleFloating: toggleFloating,
                    onHangUp: leave
                )
                .onHover { hoveringChrome = $0 }
                .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .padding(.horizontal, 20)
        .padding(.top, 12)
        .padding(.bottom, 20)
        .overlay(alignment: .trailing) {
            if panel != nil && !sidePanel {
                panelContent
                    .frame(width: min(panelWidth, size.width - 40))
                    .padding(.vertical, 52)
                    .padding(.trailing, 12)
                    .shadow(color: .black.opacity(0.35), radius: 24, y: 10)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
            }
        }
        .animation(.spring(response: 0.45, dampingFraction: 0.85), value: showControls)
        .animation(.spring(response: 0.4, dampingFraction: 0.85), value: model.toast)
        .animation(.spring(response: 0.45, dampingFraction: 0.86), value: panel)
    }

    private var topBar: some View {
        HStack(spacing: 8) {
            // Room for the window's close, minimize and zoom buttons.
            Color.clear.frame(width: 84, height: 1)
            Spacer(minLength: 0)
            GlassEffectContainer(spacing: 8) {
                HStack(spacing: 8) {
                    RoomPill(model: model)
                    if model.isGroup && model.selfId != nil {
                        peopleButton
                            .transition(.scale.combined(with: .opacity))
                    }
                    if !model.isGroup && model.phase == .connected && !model.remote.audio {
                        StatusChip(systemImage: "mic.slash.fill", text: "Muted")
                            .transition(.scale.combined(with: .opacity))
                    }
                    if !model.isGroup && model.phase == .connected && model.remote.screen {
                        StatusChip(systemImage: "rectangle.on.rectangle", text: "Presenting")
                            .transition(.scale.combined(with: .opacity))
                    }
                    if model.sharing != .none {
                        StatusChip(
                            systemImage: model.sharing == .screen ? "record.circle" : "film",
                            text: model.sharingTitle.map { "Sharing \($0)" } ?? "Sharing"
                        )
                        .transition(.scale.combined(with: .opacity))
                    }
                }
            }
            .animation(.spring(response: 0.4, dampingFraction: 0.75), value: model.remote)
            .animation(.spring(response: 0.4, dampingFraction: 0.75), value: model.sharing)
            .animation(.spring(response: 0.4, dampingFraction: 0.75), value: model.participants.count)
            Spacer(minLength: 0)
            HStack(spacing: 8) {
                if canFit {
                    GlassCircleButton(
                        systemImage: remoteFit ? "arrow.up.left.and.arrow.down.right" : "arrow.down.right.and.arrow.up.left",
                        label: remoteFit ? "Fill window" : "Fit video in window",
                        size: 36,
                        shortcut: "F",
                        action: toggleFit
                    )
                }
                if hasOthers {
                    GlassCircleButton(
                        systemImage: floating.isShown ? "pip.exit" : "pip.enter",
                        label: floating.isShown ? "Return to main window" : "Float video on top",
                        active: floating.isShown,
                        size: 36,
                        shortcut: "P",
                        action: toggleFloating
                    )
                }
            }
            .frame(width: 84, alignment: .trailing)
            .transition(.scale.combined(with: .opacity))
        }
        .onHover { hoveringChrome = $0 }
    }

    /// The head count; opens everyone in the room, you first.
    private var peopleButton: some View {
        let count = model.participants.count + 1
        return Button { peopleOpen.toggle() } label: {
            Label("\(count)", systemImage: "person.2.fill")
                .font(.caption.weight(.semibold))
                .monospacedDigit()
                .contentTransition(.numericText())
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .hoverLift()
        .glassEffect(peopleOpen ? .regular.tint(.white.opacity(0.25)).interactive() : .regular.interactive(), in: .capsule)
        .popover(isPresented: $peopleOpen, arrowEdge: .bottom) {
            MacPeopleList(model: model)
        }
        .help("Everyone in the call (⇧⌘P)")
        .accessibilityLabel("\(count) in call")
        .accessibilityHint("Shows everyone in the call")
    }

    @ViewBuilder
    private var panelContent: some View {
        switch panel {
        case .chat: chatPanel
        case .effects: effectsPanel
        case nil: EmptyView()
        }
    }

    private var chatPanel: some View {
        ChatView(
            model: model,
            onClose: { panel = nil },
            onInputFocusChange: { chatTyping = $0 }
        )
        .glassEffect(.regular, in: .rect(cornerRadius: 26, style: .continuous))
        .onHover { hoveringChrome = $0 }
    }

    private var effectsPanel: some View {
        EffectsSheet(model: model, onClose: { panel = nil })
            .glassEffect(.regular, in: .rect(cornerRadius: 26, style: .continuous))
            .onHover { hoveringChrome = $0 }
    }

    // MARK: - Menu bar

    private var callActions: CallActions {
        CallActions(
            micOn: model.micOn,
            cameraOn: model.cameraOn,
            canToggleCamera: model.sharing == .none,
            chatOpen: chatOpen,
            effectsOpen: effectsOpen,
            canOpenEffects: model.effectsAvailable && model.sharing == .none,
            fit: remoteFit,
            canFit: canFit,
            floating: floating.isShown,
            canFloat: hasOthers,
            sharing: model.sharing != .none,
            isGroup: model.isGroup,
            canShowPeople: model.isGroup && model.selfId != nil,
            remoteAudioMuted: model.remoteAudioMuted,
            remoteVideoHidden: model.remoteVideoHidden,
            canControlRemote: model.isGroup || model.hasRemote,
            typing: chatTyping,
            toggleMic: { withReveal(model.toggleMic) },
            toggleCamera: { withReveal(model.toggleCamera) },
            toggleChat: toggleChat,
            openEffects: { withReveal(openEffects) },
            toggleFit: toggleFit,
            toggleFloating: toggleFloating,
            showPeople: { peopleOpen = true },
            shareScreen: { showPicker = true },
            shareFile: chooseVideoFile,
            stopSharing: model.stopSharing,
            toggleRemoteAudio: model.toggleRemoteAudio,
            toggleRemoteVideo: model.toggleRemoteVideo,
            leave: leave
        )
    }

    /// Whole-window fit is 1:1 only; group tiles switch one by one with a double click.
    private var canFit: Bool { !model.isGroup && model.hasRemote }

    // MARK: - Actions

    private func reveal() {
        withAnimation(.spring(response: 0.4, dampingFraction: 0.85)) { controlsVisible = true }
    }

    /// Shortcuts show the controls so the change is visible.
    private func withReveal(_ action: () -> Void) {
        idle.poke()
        reveal()
        action()
    }

    private func toggleChat() {
        idle.poke()
        panel = chatOpen ? nil : .chat
    }

    private func toggleEffects() {
        idle.poke()
        if effectsOpen {
            panel = nil
        } else if model.effectsAvailable && model.sharing == .none {
            panel = .effects
        }
    }

    private func openEffects() {
        idle.poke()
        if model.effectsAvailable && model.sharing == .none { panel = .effects }
    }

    private func toggleFit() {
        guard canFit else { return }
        idle.poke()
        userFit = !remoteFit
    }

    private func toggleFloating() {
        floating.isShown ? stopFloating() : startFloating()
    }

    private func startFloating() {
        guard hasOthers else { return }
        floating.show(
            model: model,
            fill: !remoteFit,
            near: windowReference.window,
            onReturn: bringWindowBack,
            onHangUp: leave
        )
        if remoteVideoSize != .zero { floating.setVideoSize(remoteVideoSize) }
    }

    private func stopFloating() {
        floating.hide()
        bringWindowBack()
    }

    private func bringWindowBack() {
        guard let window = windowReference.window else { return }
        if window.isMiniaturized { window.deminiaturize(nil) }
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    private func chooseVideoFile() {
        let panel = NSOpenPanel()
        panel.title = "Share a Video"
        panel.prompt = "Share"
        panel.message = model.isGroup ? "The video plays to everyone in a loop." : "The video plays to the other person in a loop."
        panel.allowedContentTypes = [.movie, .video, .mpeg4Movie, .quickTimeMovie]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        let handler: (NSApplication.ModalResponse) -> Void = { response in
            guard response == .OK, let url = panel.url else { return }
            model.shareVideoFile(url: url)
        }
        if let window = windowReference.window {
            panel.beginSheetModal(for: window, completionHandler: handler)
        } else {
            panel.begin(completionHandler: handler)
        }
    }

    private func leave() {
        tearDown()
        onLeave()
    }

    private func tearDown() {
        floating.hide()
        windowReference.setTrafficLightsHidden(false)
        NSCursor.setHiddenUntilMouseMoves(false)
        model.stop()
    }

    /// Hides the controls once the pointer has rested for a few seconds. Polling keeps pointer moves
    /// from restarting a timer on every event.
    private func autoHideLoop() async {
        while !Task.isCancelled {
            try? await Task.sleep(for: .milliseconds(400))
            if controlsVisible && canAutoHide && idle.idleTime >= Self.autoHideDelay {
                withAnimation(.easeInOut(duration: 0.35)) { controlsVisible = false }
            }
        }
    }

    private func hideCursorIfInside() {
        guard let window = windowReference.window, window.isKeyWindow,
              window.frame.contains(NSEvent.mouseLocation)
        else { return }
        NSCursor.setHiddenUntilMouseMoves(true)
    }
}

private enum SidePanel {
    case chat, effects
}

// MARK: - Local preview

private enum Corner {
    case topLeading, topTrailing, bottomLeading, bottomTrailing
}

/// The local camera: fills the window while alone, then morphs into a tile that can be dragged to
/// any corner, where it snaps. Its size follows the window.
private struct MacLocalTile: View {
    let model: CallViewModel
    let pip: Bool
    let size: CGSize
    let controlsVisible: Bool
    /// Your name as the others see it ("You · Mac · 3f2a"), shown on the tile in a group call.
    var label: String? = nil

    @State private var videoSize = CGSize(width: 16, height: 9)
    @State private var corner: Corner = .topTrailing
    @State private var drag: CGSize = .zero
    @State private var dragging = false
    @State private var snaps = 0

    private let margin: CGFloat = 20

    private var pipSize: CGSize {
        let aspect = min(max(videoSize.width / max(videoSize.height, 1), 0.4), 2.4)
        let base = min(max(size.width * 0.2, 170), 300)
        let width = aspect >= 1 ? base : base * 0.62
        return CGSize(width: width, height: width / aspect)
    }

    private func center(of corner: Corner) -> CGPoint {
        let tile = pipSize
        // Clear of the top bar and the window drag strip, and of the toolbar at the bottom.
        let top = (controlsVisible ? 72 : 44) + tile.height / 2
        let bottom = size.height - (controlsVisible ? 100 : 20) - tile.height / 2
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
        let frame = pip ? pipSize : size
        let anchor = pip ? center(of: corner) : CGPoint(x: size.width / 2, y: size.height / 2)
        let radius: CGFloat = pip ? 18 : 0
        let shape = RoundedRectangle(cornerRadius: radius, style: .continuous)

        content(cornerRadius: radius)
            .frame(width: frame.width, height: frame.height)
            .clipShape(shape)
            .overlay {
                shape.strokeBorder(.white.opacity(pip ? 0.25 : 0), lineWidth: 1)
            }
            .shadow(color: .black.opacity(pip ? 0.4 : 0), radius: 20, y: 8)
            .scaleEffect(dragging ? 1.03 : 1)
            .contentShape(shape)
            .gesture(dragGesture, including: pip ? .all : .subviews)
            .pointerStyle(pip ? (dragging ? .grabActive : .grabIdle) : nil)
            .position(x: anchor.x + drag.width, y: anchor.y + drag.height)
            .animation(.spring(response: 0.6, dampingFraction: 0.82), value: pip)
            .animation(.spring(response: 0.45, dampingFraction: 0.8), value: controlsVisible)
            .animation(.spring(response: 0.4, dampingFraction: 0.8), value: videoSize)
            .animation(.spring(response: 0.3, dampingFraction: 0.7), value: dragging)
            .sensoryFeedback(.alignment, trigger: snaps)
            .help(pip ? "Drag to any corner" : "")
            .accessibilityLabel("Your video")
    }

    private func content(cornerRadius: CGFloat) -> some View {
        let presenting = model.sharing == .screen
        return ZStack {
            Color(white: 0.1)
            if !presenting {
                VideoView(
                    track: model.localTrack,
                    fill: true,
                    mirror: model.sharing == .none,
                    cornerRadius: cornerRadius
                ) { videoSize = $0 }
            }
            if presenting {
                VStack(spacing: 6) {
                    Image(systemName: "rectangle.on.rectangle")
                        .font(pip ? .title2.weight(.semibold) : .largeTitle.weight(.semibold))
                        .symbolEffect(.pulse)
                    Text(model.sharingTitle.map { "Sharing \($0)" } ?? "Presenting")
                        .font(pip ? .caption.weight(.semibold) : .headline)
                        .lineLimit(1)
                        .padding(.horizontal, 10)
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
            if let label, pip {
                HStack(spacing: 5) {
                    // Collapsed to no width while the mic is on, leaving "You" at the pill's padding.
                    MicOffBadge()
                        .statusMark(visible: !model.micOn)
                        .frame(width: model.micOn ? 0 : 18, height: 18, alignment: .leading)
                        .padding(.trailing, model.micOn ? -1 : 0)
                        .accessibilityHidden(model.micOn)
                        .accessibilityLabel("Microphone muted")
                    Text(label)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.white)
                .padding(.leading, 5)
                .padding(.trailing, 9)
                .padding(.vertical, 4)
                .glassEffect(.regular.tint(.black.opacity(0.35)), in: .capsule)
                .animation(StatusMark.animation, value: model.micOn)
                .padding(8)
                // Leaves the corner to the microphone level.
                .padding(.trailing, model.micOn ? 32 : 0)
                .transition(.opacity)
            } else if !model.micOn && pip {
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
        // on the leading edge while the tile is the whole stage.
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
                    .padding(.leading, margin)
                    .transition(.scale.combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.25), value: model.cameraOn)
        .animation(.spring(response: 0.35, dampingFraction: 0.65), value: model.micOn)
    }

    private var dragGesture: some Gesture {
        DragGesture(coordinateSpace: .global)
            .onChanged { value in
                dragging = true
                drag = value.translation
            }
            .onEnded { value in
                let start = center(of: corner)
                let endX = start.x + value.predictedEndTranslation.width
                let endY = start.y + value.predictedEndTranslation.height
                let leading = endX < size.width / 2
                let top = endY < size.height / 2
                withAnimation(.spring(response: 0.45, dampingFraction: 0.72)) {
                    corner = top ? (leading ? .topLeading : .topTrailing) : (leading ? .bottomLeading : .bottomTrailing)
                    drag = .zero
                    dragging = false
                }
                snaps += 1
            }
    }
}
