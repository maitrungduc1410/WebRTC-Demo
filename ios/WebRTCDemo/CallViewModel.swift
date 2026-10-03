//
//  CallViewModel.swift
//  WebRTCDemo
//

import AVFoundation
import Observation
import SocketIO
import UIKit
import WebRTC

enum ConnectionPhase {
    case waiting, connecting, connected
}

enum Sharing {
    case none, screen, file
}

enum ChatStatus {
    case closed, opening, open
}

enum EffectsStatus {
    case off, loading, on
}

/// What the remote peer reports about its own tracks through the "media state" event.
struct MediaState: Equatable {
    var audio = true
    var video = true
    var screen = false
}

struct ChatMessage: Identifiable, Equatable {
    let id: Int
    let text: String
    let isLocal: Bool
    let date: Date
}

struct Toast: Identifiable, Equatable {
    let id = UUID()
    let text: String
    var systemImage: String?
}

/// Owns one call: the Socket.IO signaling, the WebRTC client and all state the call UI renders.
/// State is only mutated on the main queue.
@Observable
final class CallViewModel {
    let roomId: String
    let e2ee: Bool

    private(set) var phase: ConnectionPhase = .waiting
    private(set) var connectedSince: Date?
    private(set) var micOn = true
    private(set) var cameraOn = true
    private(set) var frontCamera = true
    private(set) var speakerOn = false
    private(set) var remoteAudioMuted = false
    private(set) var remoteVideoHidden = false
    private(set) var remote = MediaState()
    private(set) var sharing: Sharing = .none
    /// The chosen background and sticker; `effectsStatus` says whether they are showing yet.
    private(set) var effects: EffectsSelection
    private(set) var effectsStatus: EffectsStatus = .off
    private(set) var effectsAvailable = false
    private(set) var chat: ChatStatus = .closed
    private(set) var messages: [ChatMessage] = []
    private(set) var unread = 0
    private(set) var localTrack: RTCVideoTrack?
    private(set) var remoteTrack: RTCVideoTrack?
    private(set) var remoteSnapshot: UIImage?
    private(set) var remoteAudioLevel: Double = 0
    var toast: Toast?

    /// The remote peer is connected but its video should not be shown: hidden by us or turned off by them.
    var remoteVideoPaused: Bool { remoteVideoHidden || !remote.video }

    var hasRemote: Bool { phase == .connected && remoteTrack != nil }

    private let manager: SocketManager
    private let socket: SocketIOClient
    @ObservationIgnored private var client: WebRTCClient?
    private let snapshotter = FrameSnapshotter()
    @ObservationIgnored private var started = false
    @ObservationIgnored private var chatVisible = false
    @ObservationIgnored private var broadcastActive = false
    @ObservationIgnored private var nextMessageId = 0
    @ObservationIgnored private var mediaStateWork: DispatchWorkItem?
    @ObservationIgnored private var audioLevelTimer: Timer?
    @ObservationIgnored private var appliedEffects = EffectsSelection()
    @ObservationIgnored private var effectsTask: Task<Void, Never>?

    let effectsCatalog = EffectsCatalog.shared

    private static let mediaStateDelay: TimeInterval = 0.3
    private static let dataChannelName = "MyApp Channel"

    init(roomId: String, e2ee: Bool) {
        self.roomId = roomId
        self.e2ee = e2ee
        effects = EffectsStore.load(.shared)
        let serverURL = URL(string: SignalingServer.current) ?? URL(string: SignalingServer.defaultURL)!
        manager = SocketManager(socketURL: serverURL, config: [.log(true), .compress])
        socket = manager.defaultSocket
        snapshotter.onSnapshot = { [weak self] image in
            self?.remoteSnapshot = image
        }
    }

    // MARK: - Lifecycle

    func start() {
        guard !started else { return }
        started = true
        UIApplication.shared.isIdleTimerDisabled = true
        setupSocketHandlers()
        socket.connect()

        audioLevelTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            self?.pollRemoteAudioLevel()
        }
    }

    func stop() {
        guard started else { return }
        started = false
        UIApplication.shared.isIdleTimerDisabled = false
        audioLevelTimer?.invalidate()
        audioLevelTimer = nil
        mediaStateWork?.cancel()
        effectsTask?.cancel()

        if sharing == .screen {
            client?.stopScreenCapture()
        }
        if speakerOn {
            try? AVAudioSession.sharedInstance().overrideOutputAudioPort(.none)
        }
        remoteTrack?.remove(snapshotter)
        socket.removeAllHandlers()
        socket.disconnect()
        client?.disconnect()
        client = nil
    }

    // MARK: - Actions

    func toggleMic() {
        micOn.toggle()
        client?.toggleAudio(enable: micOn)
        sendMediaState()
    }

    func toggleCamera() {
        setCameraOn(!cameraOn)
    }

    /// The peer is told first and the track disabled shortly after (and the reverse when turning
    /// on), so it shows the placeholder instead of a frozen or black frame.
    private func setCameraOn(_ on: Bool) {
        cameraOn = on
        mediaStateWork?.cancel()
        let work: DispatchWorkItem
        if on {
            client?.toggleVideo(enable: true)
            work = DispatchWorkItem { [weak self] in self?.sendMediaState() }
        } else {
            sendMediaState()
            work = DispatchWorkItem { [weak self] in
                guard let self, !self.cameraOn else { return }
                self.client?.toggleVideo(enable: false)
            }
        }
        mediaStateWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.mediaStateDelay, execute: work)
    }

    /// `completion` runs on the main queue once the other camera is capturing. It may never run if
    /// the switch is refused (one is already in progress), so callers must not wait forever.
    func switchCamera(completion: (() -> Void)? = nil) {
        guard sharing == .none, let client else {
            completion?()
            return
        }
        client.switchCamera { [weak self] isFront in
            self?.frontCamera = isFront
            completion?()
        }
    }

    func toggleSpeaker() {
        let on = !speakerOn
        do {
            try AVAudioSession.sharedInstance().overrideOutputAudioPort(on ? .speaker : .none)
            speakerOn = on
        } catch {
            print("Failed to toggle speaker: \(error)")
        }
    }

    /// Only mutes local playout; the remote peer is not notified.
    func toggleRemoteAudio() {
        remoteAudioMuted.toggle()
        client?.setRemoteAudioEnabled(!remoteAudioMuted)
    }

    /// Stops rendering the remote video on this device only; the remote peer is not notified.
    func toggleRemoteVideo() {
        remoteVideoHidden.toggle()
        remoteTrack?.isEnabled = !remoteVideoHidden
    }

    /// Remembers the choice and applies it to the camera.
    func setEffects(_ selection: EffectsSelection) {
        guard selection != effects else { return }
        effects = selection
        EffectsStore.save(selection)
        applyEffects()
    }

    /// Decodes the pictures off the main queue, then hands the scene to the camera. If they can't be
    /// loaded the previous choice comes back.
    private func applyEffects() {
        effectsTask?.cancel()
        guard let client, client.isEffectsAvailable else { return }
        let selection = effects
        guard effectsCatalog.hasEffects(selection) else {
            client.setEffects(nil)
            appliedEffects = selection
            effectsStatus = .off
            return
        }

        effectsStatus = .loading
        let catalog = effectsCatalog
        effectsTask = Task { @MainActor [weak self] in
            let scene = await Task.detached(priority: .userInitiated) {
                EffectsScene.load(selection, from: catalog)
            }.value
            guard let self, !Task.isCancelled, self.effects == selection, let client = self.client else { return }
            if let scene {
                client.setEffects(scene)
                self.appliedEffects = selection
                self.effectsStatus = .on
            } else {
                self.show("Couldn't load that effect", systemImage: "exclamationmark.triangle.fill")
                let fallback = self.appliedEffects != selection ? self.appliedEffects : EffectsSelection()
                self.effects = fallback
                EffectsStore.save(fallback)
                self.applyEffects()
            }
        }
    }

    func shareScreen() {
        guard let client, sharing != .screen else { return }
        if sharing == .file {
            client.stopVideoFileSharing()
        }
        // A disabled track would send black frames instead of the screen
        if !cameraOn { setCameraOn(true) }
        client.startScreenCapture()
        sharing = .screen
        show("Starting screen sharing…", systemImage: "rectangle.on.rectangle")
    }

    func shareVideoFile(url: URL) {
        guard let client else { return }
        if sharing == .screen {
            client.stopScreenCapture()
            broadcastActive = false
        }
        if !cameraOn { setCameraOn(true) }
        client.shareVideoFile(fileURL: url)
        sharing = .file
        sendMediaState()
        show("Sharing video file", systemImage: "film")
    }

    func stopSharing() {
        switch sharing {
        case .none:
            return
        case .screen:
            client?.stopScreenCapture()
            broadcastActive = false
            show("Screen sharing stopped", systemImage: "rectangle.on.rectangle.slash")
        case .file:
            client?.stopVideoFileSharing()
            show("Stopped video sharing", systemImage: "film")
        }
        sharing = .none
        sendMediaState()
    }

    /// Called when the chat sheet opens. The channel normally exists since the call connected.
    func openChat() {
        chatVisible = true
        unread = 0
        guard chat == .closed, phase == .connected, let client else { return }
        chat = .opening
        client.ensureDataChannel(dataChannelName: Self.dataChannelName) { [weak self] offer in
            DispatchQueue.main.async { self?.sendSDP(offer) }
        }
    }

    func closeChat() {
        chatVisible = false
    }

    func sendMessage(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, chat == .open else { return }
        client?.sendDataChannelMessage(message: trimmed)
        appendMessage(trimmed, isLocal: true)
    }

    func show(_ text: String, systemImage: String? = nil) {
        toast = Toast(text: text, systemImage: systemImage)
    }

    // MARK: - Signaling

    private func setupSocketHandlers() {
        socket.on(clientEvent: .connect) { [weak self] _, _ in
            guard let self else { return }
            print("socket connected")
            if let client = self.client {
                // The server dropped us from the room while we were offline; the peer still in it
                // starts a new call once we rejoin
                client.closePeerConnection()
            } else {
                self.createClient()
            }
            let payload: [String: Any] = ["roomId": self.roomId]
            self.socket.emit("join room", payload)
        }

        socket.on(clientEvent: .disconnect) { _, _ in
            print("socket disconnected")
        }

        socket.on("new user joined") { [weak self] _, _ in
            guard let self, let client = self.client else { return }
            client.closePeerConnection()
            if self.phase == .waiting { self.phase = .connecting }

            // The key must go out before the offer; the server relays both in order on this socket
            if self.e2ee, let key = client.generateEncryptionKey() {
                let payload: [String: Any] = ["roomId": self.roomId, "encryptionKey": key]
                self.socket.emit("send encryption key", payload)
            }
            client.connect(dataChannelName: Self.dataChannelName) { [weak self] offer in
                DispatchQueue.main.async { self?.sendSDP(offer) }
            }
        }

        socket.on("receive encryption key") { [weak self] data, _ in
            guard let self,
                  let payload = data.first as? [String: Any],
                  let key = payload["encryptionKey"] as? Data else {
                print("invalid encryption key payload")
                return
            }
            guard self.e2ee, self.client?.setEncryptionKey(key) == true else {
                print("received encryption key but E2EE is disabled")
                return
            }
            let ack: [String: Any] = ["roomId": self.roomId]
            self.socket.emit("encryption key received", ack)
        }

        socket.on("remote peer received encryption key") { _, _ in
            print("remote peer received encryption key")
        }

        socket.on("offer") { [weak self] data, _ in
            guard let self,
                  let payload = data.first as? [String: Any],
                  let offer = payload["offer"] as? [String: Any],
                  let sdp = offer["sdp"] as? String else { return }
            if self.phase == .waiting { self.phase = .connecting }
            self.client?.receiveOffer(offerSDP: RTCSessionDescription(type: .offer, sdp: sdp)) { [weak self] answer in
                DispatchQueue.main.async { self?.sendSDP(answer) }
            }
        }

        socket.on("answer") { [weak self] data, _ in
            guard let payload = data.first as? [String: Any],
                  let answer = payload["answer"] as? [String: Any],
                  let sdp = answer["sdp"] as? String else { return }
            self?.client?.receiveAnswer(answerSDP: RTCSessionDescription(type: .answer, sdp: sdp))
        }

        socket.on("new ice candidate") { [weak self] data, _ in
            guard let payload = data.first as? [String: Any],
                  let iceCandidate = payload["iceCandidate"] as? [String: Any],
                  let candidate = iceCandidate["candidate"] as? String,
                  let sdpMLineIndex = iceCandidate["sdpMLineIndex"] as? Int32 else { return }
            self?.client?.receiveCandidate(
                candidate: RTCIceCandidate(
                    sdp: candidate,
                    sdpMLineIndex: sdpMLineIndex,
                    sdpMid: iceCandidate["sdpMid"] as? String
                )
            )
        }

        socket.on("media state") { [weak self] data, _ in
            guard let payload = data.first as? [String: Any],
                  let state = payload["state"] as? [String: Any] else { return }
            self?.remote = MediaState(
                audio: state["audio"] as? Bool ?? true,
                video: state["video"] as? Bool ?? true,
                screen: state["screen"] as? Bool ?? false
            )
        }
    }

    private func createClient() {
        let client = WebRTCClient()
        client.delegate = self
        let savedEffects = effectsCatalog.hasEffects(effects)
        if savedEffects { client.holdEffects() }
        client.setup(videoTrack: true, audioTrack: true, customFrameCapturer: false, enableE2EE: e2ee)
        client.setRemoteAudioEnabled(!remoteAudioMuted)
        self.client = client
        localTrack = client.localVideoTrack
        frontCamera = client.isFrontCamera
        effectsAvailable = client.isEffectsAvailable
        if savedEffects { applyEffects() }
    }

    private func sendMediaState() {
        guard socket.status == .connected else { return }
        let presenting = sharing == .file || (sharing == .screen && broadcastActive)
        let state: [String: Any] = ["audio": micOn, "video": cameraOn, "screen": presenting]
        let payload: [String: Any] = ["roomId": roomId, "state": state]
        socket.emit("media state", payload)
    }

    private func sendSDP(_ description: RTCSessionDescription) {
        let type = description.type == .offer ? "offer" : "answer"
        let sdp: [String: Any] = ["type": type, "sdp": description.sdp]
        let payload: [String: Any] = ["roomId": roomId, type: sdp]
        socket.emit(type, payload)
    }

    private func sendCandidate(_ candidate: RTCIceCandidate) {
        let payload: [String: Any] = [
            "candidate": candidate.sdp,
            "sdpMid": candidate.sdpMid ?? "",
            "sdpMLineIndex": candidate.sdpMLineIndex,
        ]
        let message: [String: Any] = ["roomId": roomId, "iceCandidate": payload]
        socket.emit("new ice candidate", message)
    }

    // MARK: - Helpers

    private func pollRemoteAudioLevel() {
        guard remoteVideoPaused, phase == .connected, let client else {
            if remoteAudioLevel != 0 { remoteAudioLevel = 0 }
            return
        }
        client.remoteAudioLevel { [weak self] level in
            self?.remoteAudioLevel = level
        }
    }

    private func appendMessage(_ text: String, isLocal: Bool) {
        messages.append(ChatMessage(id: nextMessageId, text: text, isLocal: isLocal, date: Date()))
        nextMessageId += 1
        if messages.count > 100 {
            messages.removeFirst()
        }
        if !isLocal && !chatVisible {
            unread += 1
        }
    }

    private func onMain(_ block: @escaping () -> Void) {
        if Thread.isMainThread {
            block()
        } else {
            DispatchQueue.main.async(execute: block)
        }
    }
}

// MARK: - WebRTCClientDelegate

extension CallViewModel: WebRTCClientDelegate {
    func didGenerateCandidate(iceCandidate: RTCIceCandidate) {
        onMain { self.sendCandidate(iceCandidate) }
    }

    func didIceConnectionStateChanged(iceConnectionState: RTCIceConnectionState) {
        print("ICE Connection State: \(iceConnectionState.rawValue)")
    }

    func didConnectWebRTC() {}

    func didDisconnectWebRTC() {}

    func didReceiveRemoteVideoTrack(_ track: RTCVideoTrack?) {
        onMain {
            self.remoteTrack?.remove(self.snapshotter)
            track?.isEnabled = !self.remoteVideoHidden
            track?.add(self.snapshotter)
            self.remoteTrack = track
            if track == nil {
                self.remoteSnapshot = nil
            }
        }
    }

    func onDataChannelMessage(message: String) {
        onMain { self.appendMessage(message, isLocal: false) }
    }

    func onDataChannelStateChange(state: RTCDataChannelState) {
        onMain {
            switch state {
            case .open:
                self.chat = .open
            case .closed:
                let wasOpen = self.chat == .open
                self.chat = .closed
                if wasOpen { self.show("Chat disconnected", systemImage: "bubble.left.and.exclamationmark.bubble.right") }
            default:
                break
            }
        }
    }

    func onPeersConnectionStatusChange(connected: Bool) {
        onMain {
            if connected {
                self.phase = .connected
                self.connectedSince = self.connectedSince ?? Date()
                self.sendMediaState()
                self.show("Connected", systemImage: "checkmark.circle.fill")
            } else {
                self.phase = .waiting
                self.connectedSince = nil
                self.chat = .closed
                self.remote = MediaState()
                self.remoteSnapshot = nil
                self.show("The other participant left", systemImage: "person.fill.xmark")
            }
        }
    }

    func onScreenShareChanged(active: Bool) {
        onMain {
            self.broadcastActive = active
            if !active && self.sharing == .screen {
                self.sharing = .none
                self.show("Screen sharing stopped", systemImage: "rectangle.on.rectangle.slash")
            }
            self.sendMediaState()
        }
    }
}
