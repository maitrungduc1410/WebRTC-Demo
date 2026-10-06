//
//  CallViewModel.swift
//  WebRTCDemo
//

import AVFoundation
import Observation
#if os(iOS)
import UIKit
#else
import AppKit
import CoreAudio
#endif
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
    private(set) var remoteSnapshot: PlatformImage?
    private(set) var remoteAudioLevel: Double = 0
    /// 0...1 from our own microphone, for the bars on the local tile; 0 while muted.
    private(set) var micLevel: Double = 0
    var toast: Toast?

    #if os(macOS)
    private(set) var cameras: [MediaDevice] = []
    private(set) var cameraID: String?
    private(set) var microphones: [MediaDevice] = []
    private(set) var microphoneID: String?
    private(set) var speakers: [MediaDevice] = []
    private(set) var speakerID: String?
    /// What is being shared, for the local tile ("Sharing Safari").
    private(set) var sharingTitle: String?
    @ObservationIgnored private var displaySleepActivity: NSObjectProtocol?
    @ObservationIgnored private var deviceObservers: [NSObjectProtocol] = []
    @ObservationIgnored private var audioDeviceObserver: AudioDeviceListObserver?
    /// Whether the microphone / speaker has run in this call; only those are reopened after losing their device.
    @ObservationIgnored private var audioRan = (input: false, output: false)
    #endif

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
    #if os(iOS)
    static let groupClientName = "iOS"
    #else
    static let groupClientName = "Mac"
    #endif

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
    #if os(iOS)
    @ObservationIgnored private let audioWatcher = AudioSessionWatcher()
    #endif
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
        #if os(iOS)
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
        #else
        displaySleepActivity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiated, .idleDisplaySleepDisabled],
            reason: "Video call"
        )
        observeDevices()
        // The camera must be authorized before local media starts capturing.
        Self.requestMediaAccess { [weak self] camera, microphone in
            guard let self, self.started else { return }
            if !camera || !microphone {
                let missing = !camera && !microphone ? "Camera and microphone" : (camera ? "Microphone" : "Camera")
                self.show("\(missing) access is off in System Settings › Privacy & Security", systemImage: "exclamationmark.triangle.fill")
            }
            if self.isGroup {
                self.startGroup()
            } else {
                self.startSignaling()
            }
        }
        #endif

        audioLevelTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            self?.pollRemoteAudioLevel()
            #if os(macOS)
            self?.noteAudioActivity()
            #endif
        }
        micLevelTimer = Timer.scheduledTimer(withTimeInterval: Self.micLevelInterval, repeats: true) { [weak self] _ in
            self?.pollMicLevel()
        }
    }

    #if os(iOS)
    /// Upstream WebRTC defaults to play-and-record in voice chat mode; the webrtc-sdk fork (the
    /// WebRTC Swift package) copies the session's current category and mode instead, solo ambient at
    /// launch, and since M150 adds the Bluetooth HFP option. iOS rejects that pair (OSStatus -50), so
    /// the audio unit never starts: no microphone and no playout. See docs/guide/troubleshooting.md.
    private static func configureCallAudio() {
        let config = RTCAudioSessionConfiguration.webRTC()
        config.category = AVAudioSession.Category.playAndRecord.rawValue
        config.mode = AVAudioSession.Mode.voiceChat.rawValue
        config.categoryOptions = [.allowBluetoothHFP]
        RTCAudioSessionConfiguration.setWebRTC(config)
    }
    #endif

    func stop() {
        guard started else { return }
        started = false
        #if os(iOS)
        UIApplication.shared.isIdleTimerDisabled = false
        #else
        if let displaySleepActivity {
            ProcessInfo.processInfo.endActivity(displaySleepActivity)
            self.displaySleepActivity = nil
        }
        deviceObservers.forEach(NotificationCenter.default.removeObserver)
        deviceObservers = []
        audioDeviceObserver = nil
        audioRan = (false, false)
        sharingTitle = nil
        #endif
        audioLevelTimer?.invalidate()
        audioLevelTimer = nil
        micLevelTimer?.invalidate()
        micLevelTimer = nil
        idleMicMeter.release()
        micLevel = 0
        #if os(iOS)
        audioWatcher.stop()
        #endif
        mediaStateWork?.cancel()
        effectsTask?.cancel()

        #if os(iOS)
        if sharing == .screen {
            media?.stopScreenCapture()
        }
        if speakerOn {
            try? AVAudioSession.sharedInstance().overrideOutputAudioPort(.none)
        }
        #endif
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
            #if os(macOS)
            media?.setCameraEnabled(true)
            #endif
            media?.toggleVideo(enable: true)
            work = DispatchWorkItem { [weak self] in self?.sendMediaState() }
        } else {
            sendMediaState()
            work = DispatchWorkItem { [weak self] in
                guard let self, !self.cameraOn else { return }
                self.media?.toggleVideo(enable: false)
                #if os(macOS)
                // Unlike iOS, the Mac turns the camera (and its light) off, as the web client does.
                self.media?.setCameraEnabled(false)
                #endif
            }
        }
        mediaStateWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.mediaStateDelay, execute: work)
    }

    #if os(iOS)
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
    #endif

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

    #if os(iOS)
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
    #else
    /// Picking another screen or window while sharing switches to it.
    func shareScreen(_ source: ScreenShareSource) {
        guard let media else { return }
        // Local media stops the camera or file first, so the camera does not come on in between.
        media.startScreenCapture(source: source)
        broadcastActive = false
        sharing = .screen
        sharingTitle = source.title
        // A disabled track would send black frames instead of the screen
        if !cameraOn { setCameraOn(true) }
        sendMediaState()
        show("Sharing \(source.title)", systemImage: source.kind == .display ? "display" : "macwindow")
    }

    func shareVideoFile(url: URL) {
        guard let media else { return }
        media.shareVideoFile(fileURL: url) { [weak self] error in
            guard let self, self.sharing == .file else { return }
            print("Failed to share video file: \(error)")
            self.stopSharing()
            self.show("Couldn't play that video", systemImage: "exclamationmark.triangle.fill")
        }
        broadcastActive = false
        sharing = .file
        sharingTitle = url.deletingPathExtension().lastPathComponent
        if !cameraOn { setCameraOn(true) }
        sendMediaState()
        show("Sharing \(url.lastPathComponent)", systemImage: "film")
    }
    #endif

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
        #if os(macOS)
        sharingTitle = nil
        #endif
        sendMediaState()
    }

    #if os(macOS)
    // MARK: - Devices (macOS)

    private static let cameraPreferenceKey = "preferredCamera"
    private static let microphonePreferenceKey = "preferredMicrophone"
    private static let speakerPreferenceKey = "preferredSpeaker"

    func selectCamera(_ id: String) {
        UserDefaults.standard.set(id, forKey: Self.cameraPreferenceKey)
        media?.selectCamera(uniqueID: id)
        cameraID = id
    }

    func selectMicrophone(_ id: String) {
        guard let media, let device = microphones.first(where: { $0.id == id }) else { return }
        if media.selectAudioDevice(id: id, input: true) {
            UserDefaults.standard.set(id, forKey: Self.microphonePreferenceKey)
            microphoneID = id
        } else {
            show("Couldn't switch to \(Self.displayName(device))", systemImage: "mic.badge.xmark")
        }
    }

    func selectSpeaker(_ id: String) {
        guard let media, let device = speakers.first(where: { $0.id == id }) else { return }
        if media.selectAudioDevice(id: id, input: false) {
            UserDefaults.standard.set(id, forKey: Self.speakerPreferenceKey)
            speakerID = id
        } else {
            show("Couldn't switch to \(Self.displayName(device))", systemImage: "speaker.badge.exclamationmark")
        }
    }

    /// Re-reads cameras, microphones and speakers, e.g. after a device was plugged in.
    func refreshDevices() {
        cameras = LocalMedia.cameraDevices.map { MediaDevice(id: $0.uniqueID, name: $0.localizedName) }
        if let media {
            media.cameraDevicesChanged()
            cameraID = media.currentCameraID
        } else {
            cameraID = cameras.first?.id
        }
        guard let media, let adm = media.audioDeviceModule else { return }
        let previousMicrophones = microphones
        let previousSpeakers = speakers
        microphones = adm.inputDevices.map { MediaDevice(id: $0.deviceId, name: $0.name) }
        speakers = adm.outputDevices.map { MediaDevice(id: $0.deviceId, name: $0.name) }
        guard endedMessage == nil else {
            // The call is over: keep the pickers valid without touching audio or announcing anything.
            if !microphones.contains(where: { $0.id == microphoneID }) { microphoneID = microphones.first?.id }
            if !speakers.contains(where: { $0.id == speakerID }) { speakerID = speakers.first?.id }
            return
        }
        microphoneID = reconcileAudioDevice(microphoneID, previous: previousMicrophones, current: microphones, input: true, on: media)
        speakerID = reconcileAudioDevice(speakerID, previous: previousSpeakers, current: speakers, input: false, on: media)
    }

    /// Keeps the picker on the same physical device after the device list changed. The module's list
    /// starts with "default" (id "default"), then every device by its Core Audio id, and it remembers
    /// the picked device by position.
    private func reconcileAudioDevice(
        _ id: String?,
        previous: [MediaDevice],
        current: [MediaDevice],
        input: Bool,
        on media: LocalMedia
    ) -> String? {
        let symbol = input ? "mic.fill" : "speaker.wave.2.fill"
        // A direction that ran in this call and is now stopped lost its device and the module gave up
        // (e.g. the only mic was unplugged); it doesn't reopen it when a device shows up again.
        let reopen = phase == .connected && (input ? audioRan.input : audioRan.output)
            && !media.isAudioDeviceActive(input: input)
        if let id, let index = current.firstIndex(where: { $0.id == id }) {
            let oldIndex = previous.firstIndex(where: { $0.id == id })
            if reopen {
                // Same device as before, so nothing to announce.
                media.selectAudioDevice(id: id, input: input, start: true)
            } else if let oldIndex, oldIndex != index {
                // Still there, but another device moved it in the list: point the module at it again.
                media.selectAudioDevice(id: id, input: input)
            } else if let oldIndex, previous[oldIndex].name != current[index].name {
                // "default" now means another device; the module follows it by itself.
                show("Switched to \(Self.displayName(current[index]))", systemImage: symbol)
            }
            return id
        }
        guard let fallback = current.first else {
            guard id != nil else { return nil }
            if input {
                if micOn { toggleMic() }
                show("The microphone was disconnected", systemImage: "mic.slash.fill")
            } else {
                show("The speaker was disconnected", systemImage: "speaker.slash.fill")
            }
            return nil
        }
        // The picked device was unplugged, or the first one arrived after none were left. While audio
        // runs, the module itself moves to "default" (its HandleDeviceChange); a direction it left
        // stopped is set here, and reopened when the call needs it.
        if !media.isAudioDeviceActive(input: input) {
            guard media.selectAudioDevice(id: fallback.id, input: input, start: reopen) else {
                // Nothing is selected, so the next device change tries again.
                show(
                    "Couldn't switch to \(Self.displayName(fallback))",
                    systemImage: input ? "mic.badge.xmark" : "speaker.badge.exclamationmark"
                )
                return nil
            }
            // Only the device to use next time was set; nothing changed in the call.
            if id == nil && !reopen { return fallback.id }
        }
        show("Switched to \(Self.displayName(fallback))", systemImage: symbol)
        return fallback.id
    }

    /// Notes once each direction has run, as WebRTC starts it on its own (the speaker only with remote audio).
    private func noteAudioActivity() {
        guard let media, !(audioRan.input && audioRan.output) else { return }
        if !audioRan.input, media.isAudioDeviceActive(input: true) { audioRan.input = true }
        if !audioRan.output, media.isAudioDeviceActive(input: false) { audioRan.output = true }
    }

    /// "default (MacBook Pro Microphone)" from the module reads as just the device name.
    private static func displayName(_ device: MediaDevice) -> String {
        let prefix = "default ("
        guard device.id == "default", device.name.hasPrefix(prefix), device.name.hasSuffix(")") else { return device.name }
        return String(device.name.dropFirst(prefix.count).dropLast())
    }

    /// The camera picked in an earlier call, when it is still connected.
    private static var preferredCameraID: String? {
        guard let id = UserDefaults.standard.string(forKey: cameraPreferenceKey),
              LocalMedia.cameraDevices.contains(where: { $0.uniqueID == id }) else { return nil }
        return id
    }

    /// Applies the microphone and speaker picked in an earlier call, when they are still connected;
    /// otherwise the module's "default" entry (first in the list) is in use.
    private func restoreAudioPreferences(on media: LocalMedia) {
        let defaults = UserDefaults.standard
        guard let adm = media.audioDeviceModule else { return }
        let inputs = adm.inputDevices.map(\.deviceId)
        let outputs = adm.outputDevices.map(\.deviceId)
        microphoneID = inputs.first
        speakerID = outputs.first
        if let id = defaults.string(forKey: Self.microphonePreferenceKey), inputs.contains(id),
           media.selectAudioDevice(id: id, input: true) {
            microphoneID = id
        }
        if let id = defaults.string(forKey: Self.speakerPreferenceKey), outputs.contains(id),
           media.selectAudioDevice(id: id, input: false) {
            speakerID = id
        }
    }

    private func observeDevices() {
        audioDeviceObserver = AudioDeviceListObserver { [weak self] in
            self?.refreshDevices()
        }
        let center = NotificationCenter.default
        for name in [AVCaptureDevice.wasConnectedNotification, AVCaptureDevice.wasDisconnectedNotification] {
            deviceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                self?.refreshDevices()
            })
        }
    }

    private static func requestMediaAccess(then completion: @escaping (_ camera: Bool, _ microphone: Bool) -> Void) {
        AVCaptureDevice.requestAccess(for: .video) { camera in
            AVCaptureDevice.requestAccess(for: .audio) { microphone in
                DispatchQueue.main.async { completion(camera, microphone) }
            }
        }
    }
    #endif

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
        #if os(macOS)
        media.startCamera(preferredCameraID: Self.preferredCameraID)
        restoreAudioPreferences(on: media)
        refreshDevices()
        #endif
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
            #if os(macOS)
            idleMicMeter.inputID = microphoneID
            #endif
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
                #if os(macOS)
                self.sharingTitle = nil
                #endif
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
            // Like web's leave(): nothing of the room stays on screen behind the "Call ended" alert.
            self.participants = []
            self.remoteVideoTracks = [:]
            self.audioLevels = [:]
            self.activeSpeaker = nil
            self.lastSpeech = nil
            self.selfId = nil
            self.connectedSince = nil
            self.phase = .waiting
            self.endedMessage = message
        }
    }
}

#if os(iOS)
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
#else
/// Measures the microphone while a 1:1 call waits for the other person: there is no peer
/// connection yet, so WebRTC records nothing. It must be stopped before WebRTC opens the microphone.
/// macOS has no shared audio session, so it taps the input picked in the toolbar with its own engine.
/// The engine starts on `queue`, as opening a Bluetooth input can take a moment; `stop` waits for it,
/// since WebRTC opens the microphone right after. Nothing on `queue` waits for the main thread.
private final class IdleMicMeter {
    /// The audio module's id for the microphone: "default" (the system's) or a Core Audio device id.
    var inputID: String? {
        didSet {
            guard inputID != oldValue else { return }
            let id = inputID
            queue.async { [self] in
                tearDown()
                deviceID = id
                unavailable = false
            }
        }
    }
    private let queue = DispatchQueue(label: "IdleMicMeter")
    /// Filled on the audio thread, read and reset on the main thread.
    private let lock = NSLock()
    private var loudest: Float = 0
    /// Main thread: a start was requested since the last stop, so `stop` has something to wait for.
    private var requested = false
    // Only touched on `queue`.
    private var engine: AVAudioEngine?
    private var deviceID: String?
    /// Recording failed for this input; not retried every tick.
    private var unavailable = false

    /// Linear peak (0...1) since the previous call; starts recording on first use.
    func peak() -> Double {
        requested = true
        queue.async { [self] in
            // The engine stops itself when the input's configuration changes (another default input,
            // a new sample rate, sleep); it is set up again for the new one.
            if let engine, !engine.isRunning { tearDown() }
            if engine == nil && !unavailable { start() }
        }
        return lock.withLock {
            defer { loudest = 0 }
            return Double(loudest)
        }
    }

    /// Returns once the engine is stopped. `queue` is serial, so any start asked for earlier has
    /// already run and is torn down here.
    func stop() {
        guard requested else { return }
        requested = false
        queue.sync { tearDown() }
        lock.withLock { loudest = 0 }
    }

    func release() {
        stop()
        queue.sync { unavailable = false }
    }

    private func tearDown() {
        guard let engine else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        self.engine = nil
    }

    private func start() {
        // Asked when the call starts; until it is granted the bars stay flat.
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .authorized else { return }
        let engine = AVAudioEngine()
        let input = engine.inputNode
        if let device = deviceID.flatMap({ AudioDeviceID($0) }), let unit = input.audioUnit {
            var device = device
            let status = AudioUnitSetProperty(
                unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                &device, UInt32(MemoryLayout<AudioDeviceID>.size)
            )
            if status != noErr { print("[audio] level meter couldn't pick input \(device): \(status)") }
        }
        // The device's own format, read after picking it: a tap in any other sample rate raises an
        // Objective-C exception, which Swift can't catch.
        let format = input.inputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            unavailable = true
            return
        }
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buffer, _ in
            guard let self, let channels = buffer.floatChannelData else { return }
            var peak: Float = 0
            for channel in 0..<Int(buffer.format.channelCount) {
                for frame in 0..<Int(buffer.frameLength) {
                    peak = max(peak, abs(channels[channel][frame]))
                }
            }
            self.lock.withLock { self.loudest = max(self.loudest, peak) }
        }
        do {
            try engine.start()
            self.engine = engine
        } catch {
            print("[audio] level meter couldn't record: \(error)")
            input.removeTap(onBus: 0)
            unavailable = true
        }
    }
}
#endif
