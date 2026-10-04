//
//  GroupCallClient.swift
//  WebRTCDemo
//

import Foundation
import WebRTC

/// A participant as described by the SFU (`joined`, `participant joined`).
struct GroupParticipantInfo {
    let id: String
    let name: String
    let state: MediaState
}

/// Called on the main queue.
protocol GroupCallClientDelegate: AnyObject {
    func groupCallDidJoin(participantId: String, participants: [GroupParticipantInfo])
    func groupCallParticipantJoined(_ participant: GroupParticipantInfo)
    func groupCallParticipantLeft(participantId: String)
    func groupCallMediaStateChanged(_ state: MediaState, participantId: String)
    func groupCallDidReceiveChat(text: String, participantId: String, name: String)
    /// `track` is nil when the participant's video receiver went away.
    func groupCallDidUpdateVideoTrack(_ track: RTCVideoTrack?, participantId: String)
    func groupCallPublishConnectionChanged(_ state: RTCIceConnectionState)
    func groupCallDidReceiveError(message: String)
    /// The call is over: fatal server error, or the WebSocket closed. Nothing else is reported afterwards.
    func groupCallDidEnd(message: String)
}

/// The group engine: a plain WebSocket to `sfu-server` and two peer connections to it.
///
/// - publish: this client offers once, with one sendonly audio and one sendonly video transceiver,
///   and never renegotiates. Source switches happen inside `LocalMedia`'s single video source.
/// - subscribe: the SFU offers (initially and on every change), this client only answers. Every
///   forwarded track has stream id = participantId, which is how receivers map to tiles.
///
/// All state lives on the main queue: WebSocket callbacks are delivered there, and peer connection
/// callbacks hop there. Receiver cryptors are attached on the signaling thread before the hop, so
/// no encrypted frame reaches a decoder unhandled. The message contract is in ARCHITECTURE.md, section 12.
final class GroupCallClient: NSObject {
    private enum Connection: String {
        case publish, subscribe
    }

    weak var delegate: GroupCallClientDelegate?
    private(set) var participantId: String?

    private let url: URL
    private let roomId: String
    private let name: String
    private let media: LocalMedia

    private var socket: SignalingSocket?
    private var ended = false

    private var publishConnection: RTCPeerConnection?
    private var subscribeConnection: RTCPeerConnection?
    // Remote candidates wait for the remote description, local ones for our SDP to be sent
    private var pendingRemoteCandidates: [Connection: [RTCIceCandidate]] = [:]
    private var pendingLocalCandidates: [Connection: [RTCIceCandidate]] = [:]
    private var localDescriptionSent: Set<Connection> = []
    private var answeringSubscribeOffer = false
    private var queuedSubscribeOffer: String?

    private var videoTracks: [String: RTCVideoTrack] = [:]
    private var audioReceivers: [String: RTCRtpReceiver] = [:]
    private var receiverOwners: [String: (participantId: String, isVideo: Bool)] = [:]
    private var isRemoteAudioEnabled = true

    private static let receiverCryptorScope = "subscribe-"

    init(url: URL, roomId: String, name: String, media: LocalMedia) {
        self.url = url
        self.roomId = roomId
        self.name = name
        self.media = media
        super.init()
    }

    deinit {
        print("GroupCallClient Deinit")
    }

    // MARK: - Public

    func join() {
        guard socket == nil, !ended else { return }
        print("group call: connecting to \(url)")
        let socket = SignalingSocket(url: url)
        socket.onOpen = { [weak self] in
            print("group call: socket open")
            self?.sendJoin()
        }
        socket.onMessage = { [weak self] message in
            self?.handle(message)
        }
        socket.onClose = { [weak self] opened in
            guard let self else { return }
            self.end(message: opened && self.participantId != nil
                ? "Disconnected from the SFU server"
                : "Couldn't reach the SFU server")
        }
        self.socket = socket
        socket.connect()
    }

    /// Sends `leave`, then closes the socket and both peer connections.
    func leave() {
        guard !ended else { return }
        socket?.close()
        tearDown()
    }

    func sendMediaState(_ state: MediaState) {
        guard participantId != nil else { return }
        send([
            "type": "media state",
            "state": ["audio": state.audio, "video": state.video, "screen": state.screen],
        ])
    }

    func sendChat(_ text: String) {
        guard participantId != nil else { return }
        send(["type": "chat", "text": text])
    }

    /// Only mutes local playout of every participant; nobody is notified.
    func setRemoteAudioEnabled(_ enabled: Bool) {
        isRemoteAudioEnabled = enabled
        audioReceivers.values.forEach { ($0.track as? RTCAudioTrack)?.isEnabled = enabled }
    }

    /// Audio level (0...1) per participantId, from each audio receiver's inbound-rtp stats, on the main queue.
    /// Our own microphone's `audioLevel` (0...1), delivered on the main queue.
    func localAudioLevel(completion: @escaping (Double) -> Void) {
        guard let connection = publishConnection else {
            completion(0)
            return
        }
        connection.statistics { report in
            let level = report.localAudioLevel
            DispatchQueue.main.async { completion(level) }
        }
    }

    func audioLevels(completion: @escaping ([String: Double]) -> Void) {
        guard let connection = subscribeConnection, !audioReceivers.isEmpty else {
            completion([:])
            return
        }
        let group = DispatchGroup()
        let lock = NSLock()
        var levels: [String: Double] = [:]
        for (participantId, receiver) in audioReceivers {
            group.enter()
            connection.statistics(for: receiver) { report in
                let inbound = report.statistics.values.first {
                    $0.type == "inbound-rtp" && ($0.values["kind"] as? String) == "audio"
                }
                let level = (inbound?.values["audioLevel"] as? NSNumber)?.doubleValue ?? 0
                lock.lock()
                levels[participantId] = level
                lock.unlock()
                group.leave()
            }
        }
        group.notify(queue: .main) {
            completion(levels)
        }
    }

    // MARK: - WebSocket

    private func send(_ message: [String: Any]) {
        guard !ended else { return }
        socket?.send(message)
    }

    private func sendJoin() {
        var message: [String: Any] = ["type": "join", "roomId": roomId, "name": name, "e2ee": media.encryption != nil]
        if let encryption = media.encryption {
            // Becomes the room key if we create the room; `joined` carries the key actually in use
            guard let key = encryption.generateKey() else {
                end(message: "Couldn't create an encryption key")
                return
            }
            message["e2eeKey"] = key.base64EncodedString()
        }
        send(message)
    }

    // MARK: - Messages

    private func handle(_ message: [String: Any]) {
        guard !ended, let type = message["type"] as? String else { return }

        switch type {
        case "joined":
            onJoined(message)
        case "answer":
            guard message["pc"] as? String == Connection.publish.rawValue,
                  let sdp = message["sdp"] as? String else { return }
            onPublishAnswer(sdp)
        case "offer":
            guard message["pc"] as? String == Connection.subscribe.rawValue,
                  let sdp = message["sdp"] as? String else { return }
            onSubscribeOffer(sdp)
        case "candidate":
            guard let pc = (message["pc"] as? String).flatMap(Connection.init(rawValue:)),
                  let candidate = Self.candidate(from: message["candidate"]) else { return }
            addRemoteCandidate(candidate, to: pc)
        case "participant joined":
            guard let participant = Self.participant(from: message["participant"]) else { return }
            delegate?.groupCallParticipantJoined(participant)
        case "participant left":
            guard let id = message["participantId"] as? String else { return }
            forgetParticipant(id)
            delegate?.groupCallParticipantLeft(participantId: id)
        case "media state":
            guard let id = message["participantId"] as? String else { return }
            delegate?.groupCallMediaStateChanged(Self.mediaState(from: message["state"]), participantId: id)
        case "chat":
            guard let id = message["participantId"] as? String,
                  let text = message["text"] as? String else { return }
            delegate?.groupCallDidReceiveChat(text: text, participantId: id, name: message["name"] as? String ?? "Guest")
        case "error":
            let text = message["message"] as? String ?? "Unknown error"
            print("group call: server error: \(text)")
            if message["fatal"] as? Bool == true {
                end(message: text)
            } else {
                delegate?.groupCallDidReceiveError(message: text)
            }
        default:
            // Unknown types are ignored for forward compatibility
            break
        }
    }

    private func onJoined(_ message: [String: Any]) {
        guard participantId == nil, let id = message["participantId"] as? String else { return }

        if let encryption = media.encryption {
            guard message["e2ee"] as? Bool == true,
                  let encoded = message["e2eeKey"] as? String,
                  let key = Data(base64Encoded: encoded) else {
                end(message: "The room did not provide an encryption key")
                return
            }
            encryption.setKey(key)
        }

        participantId = id
        let others = (message["participants"] as? [Any] ?? []).compactMap(Self.participant(from:))
        print("group call: joined as \(id) with \(others.count) other participant(s)")
        // The delegate sends the first media state, which the contract wants before the offer
        delegate?.groupCallDidJoin(participantId: id, participants: others)
        startPublishing(streamId: id)
    }

    // MARK: - Publish connection

    private func startPublishing(streamId: String) {
        let connection = media.makePeerConnection(delegate: self)
        publishConnection = connection

        // Exactly one audio and one video transceiver, even with the mic or camera off
        let tracks: [(RTCRtpMediaType, RTCMediaStreamTrack?)] = [(.audio, media.audioTrack), (.video, media.videoTrack)]
        for (kind, track) in tracks {
            let transceiverInit = RTCRtpTransceiverInit()
            transceiverInit.direction = .sendOnly
            transceiverInit.streamIds = [streamId]
            if let track {
                _ = connection.addTransceiver(with: track, init: transceiverInit)
            } else {
                _ = connection.addTransceiver(of: kind, init: transceiverInit)
            }
        }
        media.encryption?.attachSenderCryptors(on: connection, scope: "publish-")
        media.applyVideoCodecPreferences(on: connection)

        connection.offer(for: RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)) { [weak self] sdp, error in
            guard let sdp else {
                print("group call: failed to create publish offer: \(String(describing: error))")
                return
            }
            connection.setLocalDescription(sdp) { error in
                DispatchQueue.main.async {
                    guard let self, connection === self.publishConnection else { return }
                    if let error {
                        print("group call: failed to set publish offer: \(error)")
                        return
                    }
                    self.send(["type": "offer", "pc": Connection.publish.rawValue, "sdp": sdp.sdp])
                    self.didSendLocalDescription(for: .publish)
                }
            }
        }
    }

    private func onPublishAnswer(_ sdp: String) {
        guard let connection = publishConnection else { return }
        connection.setRemoteDescription(RTCSessionDescription(type: .answer, sdp: sdp)) { [weak self] error in
            DispatchQueue.main.async {
                guard let self, connection === self.publishConnection else { return }
                if let error {
                    print("group call: failed to set publish answer: \(error)")
                    return
                }
                self.flushRemoteCandidates(for: .publish)
            }
        }
    }

    // MARK: - Subscribe connection

    private func onSubscribeOffer(_ sdp: String) {
        // Offers are answered one at a time; a newer offer replaces one still waiting
        guard !answeringSubscribeOffer else {
            queuedSubscribeOffer = sdp
            return
        }
        answeringSubscribeOffer = true

        let connection: RTCPeerConnection
        if let existing = subscribeConnection {
            connection = existing
        } else {
            connection = media.makePeerConnection(delegate: self)
            subscribeConnection = connection
        }

        let encryption = media.encryption
        connection.setRemoteDescription(RTCSessionDescription(type: .offer, sdp: sdp)) { [weak self] error in
            if error == nil {
                // Also covers receivers reused by a renegotiation
                encryption?.attachReceiverCryptors(on: connection, scope: GroupCallClient.receiverCryptorScope)
            }
            DispatchQueue.main.async {
                guard let self, connection === self.subscribeConnection else { return }
                if let error {
                    print("group call: failed to set subscribe offer: \(error)")
                    self.finishSubscribeOffer()
                    return
                }
                self.reconcileReceivers(of: connection, offer: sdp)
                self.media.applyVideoCodecPreferences(on: connection)
                self.flushRemoteCandidates(for: .subscribe)
                self.answerSubscribeOffer(on: connection)
            }
        }
    }

    private func answerSubscribeOffer(on connection: RTCPeerConnection) {
        connection.answer(for: RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)) { [weak self] sdp, error in
            guard let sdp else {
                print("group call: failed to create subscribe answer: \(String(describing: error))")
                DispatchQueue.main.async { self?.finishSubscribeOffer() }
                return
            }
            connection.setLocalDescription(sdp) { error in
                DispatchQueue.main.async {
                    guard let self, connection === self.subscribeConnection else { return }
                    if let error {
                        print("group call: failed to set subscribe answer: \(error)")
                    } else {
                        self.send(["type": "answer", "pc": Connection.subscribe.rawValue, "sdp": sdp.sdp])
                        self.didSendLocalDescription(for: .subscribe)
                    }
                    self.finishSubscribeOffer()
                }
            }
        }
    }

    private func finishSubscribeOffer() {
        answeringSubscribeOffer = false
        if let next = queuedSubscribeOffer {
            queuedSubscribeOffer = nil
            onSubscribeOffer(next)
        }
    }

    // MARK: - Remote tracks

    /// `didAdd rtpReceiver` does not fire again when the SFU reuses an m-line for another participant
    /// without making it inactive first, so every offer is also matched by mid against its msids.
    private func reconcileReceivers(of connection: RTCPeerConnection, offer sdp: String) {
        let senders = Self.sendingStreams(in: sdp)
        for transceiver in connection.transceivers {
            let mid: String? = transceiver.mid
            if let mid, let participantId = senders[mid] {
                mapReceiver(transceiver.receiver, participantId: participantId)
            } else {
                unmapReceiver(transceiver.receiver.receiverId)
            }
        }
    }

    private func mapReceiver(_ receiver: RTCRtpReceiver, participantId: String) {
        guard let track = receiver.track else { return }
        let receiverId = receiver.receiverId
        if let previous = receiverOwners[receiverId] {
            if previous.participantId == participantId { return }
            unmapReceiver(receiverId)
        }

        if let video = track as? RTCVideoTrack {
            receiverOwners[receiverId] = (participantId, true)
            videoTracks[participantId] = video
            delegate?.groupCallDidUpdateVideoTrack(video, participantId: participantId)
        } else if let audio = track as? RTCAudioTrack {
            receiverOwners[receiverId] = (participantId, false)
            audioReceivers[participantId] = receiver
            audio.isEnabled = isRemoteAudioEnabled
        }
        print("group call: \(track.kind) track of \(participantId)")
    }

    private func unmapReceiver(_ receiverId: String) {
        guard let owner = receiverOwners.removeValue(forKey: receiverId) else { return }
        if owner.isVideo {
            guard videoTracks.removeValue(forKey: owner.participantId) != nil else { return }
            delegate?.groupCallDidUpdateVideoTrack(nil, participantId: owner.participantId)
        } else if audioReceivers[owner.participantId]?.receiverId == receiverId {
            audioReceivers[owner.participantId] = nil
        }
    }

    private func forgetParticipant(_ id: String) {
        videoTracks[id] = nil
        audioReceivers[id] = nil
        receiverOwners = receiverOwners.filter { $0.value.participantId != id }
    }

    // MARK: - ICE

    private func connection(for peerConnection: RTCPeerConnection) -> Connection? {
        if peerConnection === publishConnection { return .publish }
        if peerConnection === subscribeConnection { return .subscribe }
        return nil
    }

    private func peerConnection(for connection: Connection) -> RTCPeerConnection? {
        connection == .publish ? publishConnection : subscribeConnection
    }

    private func sendLocalCandidate(_ candidate: RTCIceCandidate, for connection: Connection) {
        guard localDescriptionSent.contains(connection) else {
            pendingLocalCandidates[connection, default: []].append(candidate)
            return
        }
        var payload: [String: Any] = ["candidate": candidate.sdp, "sdpMLineIndex": candidate.sdpMLineIndex]
        if let sdpMid = candidate.sdpMid {
            payload["sdpMid"] = sdpMid
        } else {
            payload["sdpMid"] = NSNull()
        }
        send(["type": "candidate", "pc": connection.rawValue, "candidate": payload])
    }

    private func didSendLocalDescription(for connection: Connection) {
        guard !localDescriptionSent.contains(connection) else { return }
        localDescriptionSent.insert(connection)
        let pending = pendingLocalCandidates.removeValue(forKey: connection) ?? []
        pending.forEach { sendLocalCandidate($0, for: connection) }
    }

    // The SFU puts all its candidates in its SDP and never trickles; this is for forward compatibility
    private func addRemoteCandidate(_ candidate: RTCIceCandidate, to connection: Connection) {
        guard let peerConnection = peerConnection(for: connection), peerConnection.remoteDescription != nil else {
            pendingRemoteCandidates[connection, default: []].append(candidate)
            return
        }
        peerConnection.add(candidate) { error in
            if let error {
                print("group call: failed to add \(connection.rawValue) candidate: \(error.localizedDescription)")
            }
        }
    }

    private func flushRemoteCandidates(for connection: Connection) {
        let pending = pendingRemoteCandidates.removeValue(forKey: connection) ?? []
        pending.forEach { addRemoteCandidate($0, to: connection) }
    }

    // MARK: - Teardown

    private func end(message: String) {
        guard !ended else { return }
        print("group call: ended: \(message)")
        socket?.cancel()
        tearDown()
        delegate?.groupCallDidEnd(message: message)
    }

    private func tearDown() {
        ended = true
        socket = nil
        publishConnection?.close()
        subscribeConnection?.close()
        publishConnection = nil
        subscribeConnection = nil
        // Dropped only after close(): in M150 a disabled cryptor would forward frames unencrypted
        media.encryption?.disposeCryptors()
        videoTracks.removeAll()
        audioReceivers.removeAll()
        receiverOwners.removeAll()
        pendingLocalCandidates.removeAll()
        pendingRemoteCandidates.removeAll()
    }

    // MARK: - Parsing

    private static func participant(from value: Any?) -> GroupParticipantInfo? {
        guard let object = value as? [String: Any], let id = object["id"] as? String else { return nil }
        return GroupParticipantInfo(
            id: id,
            name: object["name"] as? String ?? "Guest",
            state: mediaState(from: object["state"])
        )
    }

    private static func mediaState(from value: Any?) -> MediaState {
        let state = value as? [String: Any] ?? [:]
        return MediaState(
            audio: state["audio"] as? Bool ?? true,
            video: state["video"] as? Bool ?? true,
            screen: state["screen"] as? Bool ?? false
        )
    }

    /// mid -> stream id (participantId) of every m-section the SFU sends on (sendonly or sendrecv).
    static func sendingStreams(in sdp: String) -> [String: String] {
        var result: [String: String] = [:]
        var mid: String?
        var streamId: String?
        var sending = true

        func closeSection() {
            if let mid, let streamId, sending {
                result[mid] = streamId
            }
            mid = nil
            streamId = nil
            sending = true
        }

        for rawLine in sdp.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("m=") {
                closeSection()
                // Port 0 marks a rejected m-section
                sending = line.split(separator: " ").dropFirst().first != "0"
            } else if line.hasPrefix("a=mid:") {
                mid = String(line.dropFirst("a=mid:".count))
            } else if line.hasPrefix("a=msid:") {
                let stream = line.dropFirst("a=msid:".count).split(separator: " ").first.map(String.init)
                if let stream, stream != "-" { streamId = stream }
            } else if line == "a=recvonly" || line == "a=inactive" {
                sending = false
            }
        }
        closeSection()
        return result
    }

    private static func candidate(from value: Any?) -> RTCIceCandidate? {
        guard let object = value as? [String: Any],
              let sdp = object["candidate"] as? String, !sdp.isEmpty else { return nil }
        return RTCIceCandidate(
            sdp: sdp,
            sdpMLineIndex: (object["sdpMLineIndex"] as? NSNumber)?.int32Value ?? 0,
            sdpMid: object["sdpMid"] as? String
        )
    }
}

// MARK: - RTCPeerConnectionDelegate

extension GroupCallClient: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}

    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        DispatchQueue.main.async {
            guard let connection = self.connection(for: peerConnection) else { return }
            print("group call: \(connection.rawValue) ICE state \(newState.rawValue)")
            if connection == .publish {
                self.delegate?.groupCallPublishConnectionChanged(newState)
            }
        }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        DispatchQueue.main.async {
            guard let connection = self.connection(for: peerConnection) else { return }
            self.sendLocalCandidate(candidate, for: connection)
        }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}

    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {}

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didAdd rtpReceiver: RTCRtpReceiver,
        streams mediaStreams: [RTCMediaStream]
    ) {
        // Only the subscribe connection receives; attach before any frame can reach the decoder
        media.encryption?.attachReceiverCryptor(
            rtpReceiver,
            participantId: mediaStreams.first?.streamId ?? "remote",
            scope: GroupCallClient.receiverCryptorScope
        )
        guard let participantId = mediaStreams.first?.streamId else { return }
        DispatchQueue.main.async {
            guard peerConnection === self.subscribeConnection else { return }
            self.mapReceiver(rtpReceiver, participantId: participantId)
        }
    }

    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove rtpReceiver: RTCRtpReceiver) {
        let receiverId = rtpReceiver.receiverId
        DispatchQueue.main.async {
            guard peerConnection === self.subscribeConnection else { return }
            self.unmapReceiver(receiverId)
        }
    }
}
