//
//  CallViewModel.swift
//  WebRTCDemo
//

import AVFoundation
import Observation
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
    /// Name of the remote sender in a group call.
    var sender: String? = nil
}

/// Someone else in a group call.
struct GroupParticipant: Identifiable, Equatable {
    let id: String
    var name: String
    var state: MediaState

    var shortId: String { String(id.prefix(4)) }
    /// What every client shows for this participant: several devices can share a name.
    var label: String { "\(name) · \(shortId)" }
}

struct Toast: Identifiable, Equatable {
    let id = UUID()
    let text: String
    var systemImage: String?
}

/// Owns one call and all state the call UI renders: local media, and either the 1:1 call (signaling
/// server WebSocket + `WebRTCClient`) or a group call through the SFU (`GroupCallClient`).
/// State is only mutated on the main queue.
@Observable
final class CallViewModel {
    let roomId: String
    let e2ee: Bool
    let isGroup: Bool

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
    /// 0...1 from our own microphone, for the bars on the local tile; 0 while muted.
    private(set) var micLevel: Double = 0
    var toast: Toast?

    // Group call only
    private(set) var participants: [GroupParticipant] = []
    private(set) var remoteVideoTracks: [String: RTCVideoTrack] = [:]
    private(set) var audioLevels: [String: Double] = [:]
    private(set) var activeSpeaker: String?
    /// Our id in the group room, from `joined`.
    private(set) var selfId: String?
    /// Set when the call ended on its own (signaling socket closed, room full, server error).
    private(set) var endedMessage: String?

    /// The remote peer is connected but its video should not be shown: hidden by us or turned off by them.
    var remoteVideoPaused: Bool { remoteVideoHidden || !remote.video }

    /// Sent with `join`; the others see this device as "name · short id".
    static let groupClientName = "iOS"

    /// This device as the others see it in a group call; nil until joined.
    var selfParticipant: GroupParticipant? {
        guard let selfId else { return nil }
        let presenting = sharing != .none
        return GroupParticipant(
            id: selfId,
            name: Self.groupClientName,
            state: MediaState(audio: micOn, video: presenting || cameraOn, screen: presenting)
        )
    }

    var hasRemote: Bool { phase == .connected && remoteTrack != nil }

    @ObservationIgnored private var signaling: SignalingSocket?
    @ObservationIgnored private var media: LocalMedia?
    @ObservationIgnored private var client: WebRTCClient?
    @ObservationIgnored private var group: GroupCallClient?
    @ObservationIgnored private var lastSpeech: Date?
    private let snapshotter = FrameSnapshotter()
    @ObservationIgnored private var started = false
    @ObservationIgnored private var chatVisible = false
    @ObservationIgnored private var broadcastActive = false
    @ObservationIgnored private var nextMessageId = 0
    @ObservationIgnored private var mediaStateWork: DispatchWorkItem?
    @ObservationIgnored private var audioLevelTimer: Timer?
    @ObservationIgnored private var appliedEffects = EffectsSelection()
    @ObservationIgnored private var effectsTask: Task<Void, Never>?
    @ObservationIgnored private let audioWatcher = AudioSessionWatcher()
    @ObservationIgnored private let idleMicMeter = IdleMicMeter()
    @ObservationIgnored private var micLevelTimer: Timer?

    let effectsCatalog = EffectsCatalog.shared

    private static let mediaStateDelay: TimeInterval = 0.3
    private static let dataChannelName = "MyApp Channel"
    /// Same level as web and Android, so everyone sees the same person highlighted.
    private static let activeSpeakerThreshold = 0.03
    private static let activeSpeakerHold: TimeInterval = 1
    private static let micLevelInterval: TimeInterval = 0.08
    /// Per tick, so the bars fall back smoothly after a word instead of dropping.
    private static let micLevelDecay = 0.75

    /// -50 dBFS (room noise) to -10 dBFS (loud speech) as 0...1, the same as web and Android.
    private static func micLevel(fromPeak peak: Double) -> Double {
        guard peak > 0 else { return 0 }
        return min(max((20 * log10(peak) + 50) / 40, 0), 1)
    }

    init(roomId: String, e2ee: Bool, isGroup: Bool = false) {
        self.roomId = roomId
        self.e2ee = e2ee
        self.isGroup = isGroup
        effects = EffectsStore.load(.shared)
        snapshotter.onSnapshot = { [weak self] image in
            self?.remoteSnapshot = image
        }
    }

    // MARK: - Lifecycle

    func start() {
        guard !started else { return }
        started = true
        UIApplication.shared.isIdleTimerDisabled = true
        audioWatcher.onProblem = { [weak self] message in
            self?.show(message, systemImage: "speaker.slash.fill")
        }
        Self.configureCallAudio()
        audioWatcher.start()
        if isGroup {
            startGroup()
        } else {
            startSignaling()
        }

        audioLevelTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            self?.pollRemoteAudioLevel()
        }
        micLevelTimer = Timer.scheduledTimer(withTimeInterval: Self.micLevelInterval, repeats: true) { [weak self] _ in
            self?.pollMicLevel()
        }
    }

    /// Upstream WebRTC defaults to play-and-record in voice chat mode; the webrtc-sdk fork (the
    /// `WebRTC-SDK` pod) copies the session's current category and mode instead, solo ambient at
    /// launch, and since M150 adds the Bluetooth HFP option. iOS rejects that pair (OSStatus -50), so
    /// the audio unit never starts: no microphone and no playout. See README > Troubleshooting.
    private static func configureCallAudio() {
        let config = RTCAudioSessionConfiguration.webRTC()
        config.category = AVAudioSession.Category.playAndRecord.rawValue
        config.mode = AVAudioSession.Mode.voiceChat.rawValue
        config.categoryOptions = [.allowBluetoothHFP]
        RTCAudioSessionConfiguration.setWebRTC(config)
    }

    func stop() {
        guard started else { return }
        started = false
        UIApplication.shared.isIdleTimerDisabled = false
        audioLevelTimer?.invalidate()
        audioLevelTimer = nil
        micLevelTimer?.invalidate()
        micLevelTimer = nil
        idleMicMeter.release()
        micLevel = 0
        audioWatcher.stop()
        mediaStateWork?.cancel()
        effectsTask?.cancel()

        if sharing == .screen {
            media?.stopScreenCapture()
        }
        if speakerOn {
            try? AVAudioSession.sharedInstance().overrideOutputAudioPort(.none)
        }
        remoteTrack?.remove(snapshotter)
        signaling?.close()
        signaling = nil
        client?.disconnect()
        client = nil
        group?.leave()
        group = nil
        media?.stopCapture()
        media = nil
    }

    // MARK: - Actions

    func toggleMic() {
        micOn.toggle()
        media?.toggleAudio(enable: micOn)
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
            media?.toggleVideo(enable: true)
            work = DispatchWorkItem { [weak self] in self?.sendMediaState() }
        } else {
            sendMediaState()
            work = DispatchWorkItem { [weak self] in
                guard let self, !self.cameraOn else { return }
                self.media?.toggleVideo(enable: false)
            }
        }
        mediaStateWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.mediaStateDelay, execute: work)
    }

    /// `completion` runs on the main queue once the other camera is capturing. It may never run if
    /// the switch is refused (one is already in progress), so callers must not wait forever.
    func switchCamera(completion: (() -> Void)? = nil) {
        guard sharing == .none, let media else {
            completion?()
            return
        }
        media.switchCamera { [weak self] isFront in
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
        group?.setRemoteAudioEnabled(!remoteAudioMuted)
    }

    /// Stops rendering the remote video on this device only; the remote peer is not notified.
    func toggleRemoteVideo() {
        remoteVideoHidden.toggle()
        remoteTrack?.isEnabled = !remoteVideoHidden
        remoteVideoTracks.values.forEach { $0.isEnabled = !remoteVideoHidden }
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
        guard let media, media.isEffectsAvailable else { return }
        let selection = effects
        guard effectsCatalog.hasEffects(selection) else {
            media.setEffects(nil)
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
            guard let self, !Task.isCancelled, self.effects == selection, let media = self.media else { return }
            if let scene {
                media.setEffects(scene)
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
        guard let media, sharing != .screen else { return }
        if sharing == .file {
            media.stopVideoFileSharing()
        }
        // A disabled track would send black frames instead of the screen
        if !cameraOn { setCameraOn(true) }
        media.startScreenCapture()
        sharing = .screen
        show("Starting screen sharing…", systemImage: "rectangle.on.rectangle")
    }

    func shareVideoFile(url: URL) {
        guard let media else { return }
        if sharing == .screen {
            media.stopScreenCapture()
            broadcastActive = false
        }
        if !cameraOn { setCameraOn(true) }
        media.shareVideoFile(fileURL: url)
        sharing = .file
        sendMediaState()
        show("Sharing video file", systemImage: "film")
    }

    func stopSharing() {
        switch sharing {
        case .none:
            return
        case .screen:
            media?.stopScreenCapture()
            broadcastActive = false
            show("Screen sharing stopped", systemImage: "rectangle.on.rectangle.slash")
        case .file:
            media?.stopVideoFileSharing()
            show("Stopped video sharing", systemImage: "film")
        }
        sharing = .none
        sendMediaState()
    }

    /// Called when the chat sheet opens. The channel normally exists since the call connected.
    func openChat() {
        chatVisible = true
        unread = 0
        // Group chat goes over the SFU WebSocket and is open as soon as we joined
        guard !isGroup, chat == .closed, phase == .connected, let client else { return }
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
        if isGroup {
            group?.sendChat(trimmed)
        } else {
            client?.sendDataChannelMessage(message: trimmed)
        }
        appendMessage(trimmed, isLocal: true)
    }

    func show(_ text: String, systemImage: String? = nil) {
        toast = Toast(text: text, systemImage: systemImage)
    }

    // MARK: - Signaling

    /// 1:1 signaling: the server pairs two sockets in a room and relays their messages (protocol in
    /// ARCHITECTURE.md). There is no reconnect; a closed socket ends the call.
    private func startSignaling() {
        guard let url = SignalingServer.webSocketURL(for: SignalingServer.current) else {
            endedMessage = "Invalid signaling server address"
            return
        }
        createClient()
        let socket = SignalingSocket(url: url)
        socket.onOpen = { [weak self] in
            guard let self else { return }
            self.signaling?.send(["type": "join", "roomId": self.roomId])
        }
        socket.onMessage = { [weak self] message in
            self?.handleSignaling(message)
        }
        socket.onClose = { [weak self] opened in
            self?.endCall(opened ? "Lost the connection to the signaling server" : "Couldn't reach the signaling server")
        }
        signaling = socket
        socket.connect()
    }

    private func handleSignaling(_ message: [String: Any]) {
        guard let type = message["type"] as? String, let client else { return }
        switch type {
        case "peer joined":
            // The other person just joined our room: we start the call
            idleMicMeter.stop()
            client.closePeerConnection()
            if phase == .waiting { phase = .connecting }
            // The key must go out before the offer; the server relays both in order on this socket
            if e2ee, let key = client.generateEncryptionKey() {
                signaling?.send(["type": "encryption key", "key": key.base64EncodedString()])
            }
            client.connect(dataChannelName: Self.dataChannelName) { [weak self] offer in
                DispatchQueue.main.async { self?.sendSDP(offer) }
            }

        case "offer":
            guard let sdp = message["sdp"] as? String else { return }
            if phase == .waiting { phase = .connecting }
            idleMicMeter.stop()
            client.receiveOffer(offerSDP: RTCSessionDescription(type: .offer, sdp: sdp)) { [weak self] answer in
                DispatchQueue.main.async { self?.sendSDP(answer) }
            }

        case "answer":
            guard let sdp = message["sdp"] as? String else { return }
            client.receiveAnswer(answerSDP: RTCSessionDescription(type: .answer, sdp: sdp))

        case "candidate":
            guard let json = message["candidate"] as? [String: Any],
                  let candidate = json["candidate"] as? String, !candidate.isEmpty else { return }
            client.receiveCandidate(
                candidate: RTCIceCandidate(
                    sdp: candidate,
                    sdpMLineIndex: (json["sdpMLineIndex"] as? NSNumber)?.int32Value ?? 0,
                    sdpMid: json["sdpMid"] as? String
                )
            )

        case "encryption key":
            guard let encoded = message["key"] as? String, let key = Data(base64Encoded: encoded), !key.isEmpty else {
                print("invalid encryption key payload")
                return
            }
            guard e2ee, client.setEncryptionKey(key) else {
                print("received encryption key but E2EE is disabled")
                return
            }
            signaling?.send(["type": "encryption key received"])

        case "encryption key received":
            print("remote peer received encryption key")

        case "media state":
            let state = message["state"] as? [String: Any] ?? [:]
            remote = MediaState(
                audio: state["audio"] as? Bool ?? true,
                video: state["video"] as? Bool ?? true,
                screen: state["screen"] as? Bool ?? false
            )

        case "error":
            let text = message["message"] as? String ?? "Signaling error"
            guard message["fatal"] as? Bool == true else {
                print("signaling server: \(text)")
                return
            }
            endCall(text == "Room is full" ? "That room already has two people in it" : text)

        default:
            break
        }
    }

    /// The 1:1 call is over without the user hanging up; the view offers to go back to the lobby.
    private func endCall(_ message: String) {
        guard endedMessage == nil else { return }
        signaling?.cancel()
        signaling = nil
        client?.disconnect()
        chat = .closed
        phase = .waiting
        endedMessage = message
    }

    private func createClient() {
        let client = WebRTCClient(media: startLocalMedia())
        client.delegate = self
        client.setRemoteAudioEnabled(!remoteAudioMuted)
        self.client = client
    }

    /// Opens the camera and microphone. Shared by both call modes.
    private func startLocalMedia() -> LocalMedia {
        let savedEffects = effectsCatalog.hasEffects(effects)
        let media = LocalMedia(
            videoTrack: true,
            audioTrack: true,
            customFrameCapturer: false,
            enableE2EE: e2ee,
            holdEffects: savedEffects
        )
        media.onScreenShareChanged = { [weak self] active in
            self?.onScreenShareChanged(active: active)
        }
        self.media = media
        localTrack = media.videoTrack
        frontCamera = media.isFrontCamera
        effectsAvailable = media.isEffectsAvailable
        if savedEffects { applyEffects() }
        return media
    }

    private func sendMediaState() {
        let presenting = sharing == .file || (sharing == .screen && broadcastActive)
        if isGroup {
            group?.sendMediaState(MediaState(audio: micOn, video: cameraOn, screen: presenting))
            return
        }
        let state: [String: Any] = ["audio": micOn, "video": cameraOn, "screen": presenting]
        signaling?.send(["type": "media state", "state": state])
    }

    private func sendSDP(_ description: RTCSessionDescription) {
        let type = description.type == .offer ? "offer" : "answer"
        signaling?.send(["type": type, "sdp": description.sdp])
    }

    private func sendCandidate(_ candidate: RTCIceCandidate) {
        let payload: [String: Any] = [
            "candidate": candidate.sdp,
            "sdpMid": candidate.sdpMid.map { $0 as Any } ?? NSNull(),
            "sdpMLineIndex": candidate.sdpMLineIndex,
        ]
        signaling?.send(["type": "candidate", "candidate": payload])
    }

    // MARK: - Helpers

    private func pollRemoteAudioLevel() {
        if isGroup {
            pollGroupAudioLevels()
            return
        }
        guard remoteVideoPaused, phase == .connected, let client else {
            if remoteAudioLevel != 0 { remoteAudioLevel = 0 }
            return
        }
        client.remoteAudioLevel { [weak self] level in
            self?.remoteAudioLevel = level
        }
    }

    /// While a peer connection sends audio, WebRTC measures the microphone; a 1:1 call waiting for
    /// the other person has none, so `idleMicMeter` records on its own until then.
    private func pollMicLevel() {
        guard micOn, endedMessage == nil else {
            idleMicMeter.stop()
            setMicLevel(0)
            return
        }
        if !isGroup, let client, !client.hasPeerConnection {
            showMicPeak(idleMicMeter.peak())
            return
        }
        idleMicMeter.stop()
        let show: (Double) -> Void = { [weak self] peak in self?.showMicPeak(peak) }
        if isGroup {
            group?.localAudioLevel(completion: show)
        } else {
            client?.localAudioLevel(completion: show)
        }
    }

    private func showMicPeak(_ peak: Double) {
        guard micOn else { return }
        setMicLevel(max(Self.micLevel(fromPeak: peak), micLevel * Self.micLevelDecay))
    }

    /// Skips changes too small to see: every write re-renders the bars.
    private func setMicLevel(_ level: Double) {
        let next = level < 0.01 ? 0 : level
        if abs(next - micLevel) >= 0.01 || (next == 0 && micLevel != 0) { micLevel = next }
    }

    private func appendMessage(_ text: String, isLocal: Bool, sender: String? = nil) {
        messages.append(ChatMessage(id: nextMessageId, text: text, isLocal: isLocal, date: Date(), sender: sender))
        nextMessageId += 1
        if messages.count > 100 {
            messages.removeFirst()
        }
        if !isLocal && !chatVisible {
            unread += 1
        }
    }

    private func onScreenShareChanged(active: Bool) {
        onMain {
            self.broadcastActive = active
            if !active && self.sharing == .screen {
                self.sharing = .none
                self.show("Screen sharing stopped", systemImage: "rectangle.on.rectangle.slash")
            }
            self.sendMediaState()
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
}

// MARK: - Group call (SFU)

extension CallViewModel {
    private func startGroup() {
        phase = .connecting
        guard let url = SFUServer.webSocketURL(for: SFUServer.current) else {
            endedMessage = "Invalid SFU server address"
            return
        }
        let group = GroupCallClient(url: url, roomId: roomId, name: Self.groupClientName, media: startLocalMedia())
        group.delegate = self
        group.setRemoteAudioEnabled(!remoteAudioMuted)
        self.group = group
        group.join()
    }

    private func updateGroupPhase() {
        if participants.isEmpty {
            phase = .waiting
            connectedSince = nil
        } else {
            phase = .connected
            connectedSince = connectedSince ?? Date()
        }
    }

    /// The loudest participant above the threshold is the active speaker; the highlight is held
    /// for a moment so it does not flicker between words.
    private func pollGroupAudioLevels() {
        guard let group, !participants.isEmpty else {
            if !audioLevels.isEmpty { audioLevels = [:] }
            activeSpeaker = nil
            return
        }
        group.audioLevels { [weak self] levels in
            guard let self else { return }
            self.audioLevels = levels
            let loudest = levels.max { $0.value < $1.value }
            if let loudest, loudest.value >= Self.activeSpeakerThreshold {
                self.activeSpeaker = loudest.key
                self.lastSpeech = Date()
            } else if let lastSpeech = self.lastSpeech, Date().timeIntervalSince(lastSpeech) > Self.activeSpeakerHold {
                self.activeSpeaker = nil
            }
        }
    }

    private func participantName(_ id: String) -> String {
        participants.first { $0.id == id }?.name ?? "Someone"
    }
}

// MARK: - GroupCallClientDelegate

extension CallViewModel: GroupCallClientDelegate {
    func groupCallDidJoin(participantId: String, participants: [GroupParticipantInfo]) {
        onMain {
            self.selfId = participantId
            self.participants = participants.map { GroupParticipant(id: $0.id, name: $0.name, state: $0.state) }
            self.chat = .open
            self.sendMediaState()
            self.updateGroupPhase()
        }
    }

    func groupCallParticipantJoined(_ participant: GroupParticipantInfo) {
        onMain {
            let joined = GroupParticipant(id: participant.id, name: participant.name, state: participant.state)
            if let index = self.participants.firstIndex(where: { $0.id == participant.id }) {
                self.participants[index] = joined
            } else {
                self.participants.append(joined)
            }
            self.updateGroupPhase()
            self.show("\(participant.name) joined", systemImage: "person.fill.badge.plus")
        }
    }

    func groupCallParticipantLeft(participantId: String) {
        onMain {
            let name = self.participantName(participantId)
            self.participants.removeAll { $0.id == participantId }
            self.remoteVideoTracks[participantId] = nil
            self.audioLevels[participantId] = nil
            if self.activeSpeaker == participantId { self.activeSpeaker = nil }
            self.updateGroupPhase()
            self.show("\(name) left", systemImage: "person.fill.xmark")
        }
    }

    func groupCallMediaStateChanged(_ state: MediaState, participantId: String) {
        onMain {
            guard let index = self.participants.firstIndex(where: { $0.id == participantId }) else { return }
            self.participants[index].state = state
        }
    }

    func groupCallDidReceiveChat(text: String, participantId: String, name: String) {
        onMain { self.appendMessage(text, isLocal: false, sender: name) }
    }

    func groupCallDidUpdateVideoTrack(_ track: RTCVideoTrack?, participantId: String) {
        onMain {
            track?.isEnabled = !self.remoteVideoHidden
            self.remoteVideoTracks[participantId] = track
        }
    }

    func groupCallPublishConnectionChanged(_ state: RTCIceConnectionState) {
        onMain {
            if state == .failed {
                self.show("Couldn't send media to the SFU server", systemImage: "exclamationmark.triangle.fill")
            }
        }
    }

    func groupCallDidReceiveError(message: String) {
        onMain { self.show(message, systemImage: "exclamationmark.triangle.fill") }
    }

    func groupCallDidEnd(message: String) {
        onMain {
            self.group = nil
            self.chat = .closed
            self.phase = .waiting
            self.endedMessage = message
        }
    }
}

/// Reports why WebRTC's audio unit did not start. Otherwise a dead microphone and silent playout
/// look exactly like a quiet call: video keeps flowing and nothing fails.
private final class AudioSessionWatcher: NSObject, RTCAudioSessionDelegate {
    /// Called on the main queue with a short message for the user.
    var onProblem: ((String) -> Void)?
    private let session = RTCAudioSession.sharedInstance()
    private var watching = false

    func start() {
        guard !watching else { return }
        watching = true
        session.add(self)
        if AVAudioApplication.shared.recordPermission == .denied {
            report("Microphone access is off for this app in Settings")
        }
        // A rejected configuration is only logged by WebRTC; nothing calls the delegate.
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            guard let self, self.watching else { return }
            if AVAudioSession.sharedInstance().category != .playAndRecord {
                self.report("Call audio didn't start: iOS refused the audio settings")
            }
        }
    }

    func stop() {
        guard watching else { return }
        watching = false
        session.remove(self)
    }

    private func report(_ message: String) {
        print("[audio] \(message)")
        DispatchQueue.main.async { [weak self] in self?.onProblem?(message) }
    }

    /// OSStatus values from Core Audio are usually four characters, e.g. '!pri'.
    private static func describe(_ error: Error) -> String {
        let code = (error as NSError).code
        let bytes = (0..<4).map { UInt8(truncatingIfNeeded: code >> (24 - $0 * 8)) }
        if bytes.allSatisfy({ (0x20...0x7e).contains($0) }), let text = String(bytes: bytes, encoding: .ascii) {
            return "'\(text)' \(code)"
        }
        return "\(code)"
    }

    func audioSession(_ audioSession: RTCAudioSession, audioUnitStartFailedWithError error: Error) {
        report("Audio couldn't start (\(Self.describe(error)))")
    }

    func audioSession(_ audioSession: RTCAudioSession, failedToSetActive active: Bool, error: Error) {
        report("Audio session couldn't \(active ? "start" : "stop") (\(Self.describe(error)))")
    }

    func audioSessionDidBeginInterruption(_ session: RTCAudioSession) {
        report("Audio interrupted by the system or another app")
    }

    func audioSessionMediaServerReset(_ session: RTCAudioSession) {
        report("iOS media services restarted")
    }
}

/// Measures the microphone while a 1:1 call waits for the other person: there is no peer
/// connection yet, so WebRTC records nothing. It must be stopped before WebRTC opens the microphone.
private final class IdleMicMeter {
    private var recorder: AVAudioRecorder?
    /// Our activation of the shared session, handed back in `release`; WebRTC counts its own.
    private var activatedSession = false
    /// Recording failed; not retried every tick.
    private var unavailable = false

    /// Linear peak (0...1) since the previous call; starts recording on first use.
    func peak() -> Double {
        if recorder == nil && !unavailable { start() }
        guard let recorder else { return 0 }
        recorder.updateMeters()
        return pow(10, Double(recorder.peakPower(forChannel: 0)) / 20)
    }

    func stop() {
        guard let recorder else { return }
        recorder.stop()
        self.recorder = nil
        // Keep the session WebRTC is about to record on active.
        if activatedSession { try? AVAudioSession.sharedInstance().setActive(true) }
    }

    func release() {
        stop()
        unavailable = false
        guard activatedSession else { return }
        activatedSession = false
        let session = RTCAudioSession.sharedInstance()
        session.lockForConfiguration()
        try? session.setActive(false)
        session.unlockForConfiguration()
    }

    private func start() {
        if !activatedSession {
            let session = RTCAudioSession.sharedInstance()
            session.lockForConfiguration()
            do {
                try session.setConfiguration(RTCAudioSessionConfiguration.webRTC(), active: true)
                activatedSession = true
            } catch {
                print("[audio] level meter couldn't start the audio session: \(error)")
            }
            session.unlockForConfiguration()
        }
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatAppleLossless,
            AVSampleRateKey: 16_000,
            AVNumberOfChannelsKey: 1,
        ]
        do {
            let recorder = try AVAudioRecorder(url: URL(fileURLWithPath: "/dev/null"), settings: settings)
            recorder.isMeteringEnabled = true
            guard recorder.record() else {
                unavailable = true
                return
            }
            self.recorder = recorder
        } catch {
            print("[audio] level meter couldn't record: \(error)")
            unavailable = true
        }
    }
}
