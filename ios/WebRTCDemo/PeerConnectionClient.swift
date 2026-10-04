//
//  PeerConnectionClient.swift
//  WebRTCDemo
//
//  Created by Mai Trung Duc on 11/4/20.
//  Copyright © 2020 Mai Trung Duc. All rights reserved.
//

import Foundation
import WebRTC

/// Callbacks may arrive on any thread (WebRTC signaling, socket or main).
protocol WebRTCClientDelegate: AnyObject {
    func didGenerateCandidate(iceCandidate: RTCIceCandidate)
    func didIceConnectionStateChanged(iceConnectionState: RTCIceConnectionState)
    func didConnectWebRTC()
    func didDisconnectWebRTC()
    func didReceiveRemoteVideoTrack(_ track: RTCVideoTrack?)
    func onDataChannelMessage(message: String)
    func onDataChannelStateChange(state: RTCDataChannelState)
    func onPeersConnectionStatusChange(connected: Bool)
}

/// The 1:1 engine: one RTCPeerConnection to the other peer plus the chat data channel. Local tracks,
/// capture and E2EE keys come from `LocalMedia`, which the group engine shares.
class WebRTCClient: NSObject, RTCPeerConnectionDelegate {
    private let media: LocalMedia
    private var peerConnection: RTCPeerConnection?
    private var remoteStream: RTCMediaStream?
    private var dataChannel: RTCDataChannel!

    // Only mutes local playout; the remote peer is not notified
    private var isRemoteAudioEnabled = true

    weak var delegate: WebRTCClientDelegate?
    public private(set) var isConnected: Bool = false

    var isE2EEEnabled: Bool { media.encryption != nil }

    init(media: LocalMedia) {
        self.media = media
        super.init()
        print("WebRTC Client initialize")
    }

    deinit {
        print("WebRTC Client Deinit")
        self.peerConnection = nil
    }

    // MARK: - Public functions

    // MARK: Connect
    /// The chat channel is part of the first offer: adding it later would renegotiate, and a
    /// renegotiation that changes the remote's receive parameters has frozen its video (Android).
    func connect(dataChannelName: String, onSuccess: @escaping (RTCSessionDescription) -> Void){
        // "peer joined" while a call exists means the remote left and came back
        closePeerConnection()
        self.peerConnection = media.makePeerConnection(delegate: self)

        if let localVideoTrack = media.videoTrack {
            self.peerConnection!.add(localVideoTrack, streamIds: ["stream0"])
        }
        if let localAudioTrack = media.audioTrack {
            self.peerConnection!.add(localAudioTrack, streamIds: ["stream0"])
        }
        media.encryption?.attachSenderCryptors(on: peerConnection!)
        media.applyVideoCodecPreferences(on: peerConnection!)
        openDataChannel(label: dataChannelName)

        makeOffer(onSuccess: onSuccess)
    }

    // MARK: HangUp
    /// Closes the call. Local capture belongs to `LocalMedia` and is stopped by its owner.
    func disconnect(){
        if dataChannel != nil {
            self.dataChannel.close()
        }

        if self.peerConnection != nil{
            self.peerConnection!.close()
        }
        // Dropped only after close(): in M150 a disabled cryptor would forward frames unencrypted
        media.encryption?.disposeCryptors()
    }

    /// Ends the current call but keeps local media, so a fresh offer can start a new one.
    func closePeerConnection() {
        guard let pc = peerConnection else { return }
        let wasConnected = isConnected
        isConnected = false
        peerConnection = nil
        dataChannel = nil
        pc.close()
        media.encryption?.disposeCryptors()
        remoteStream = nil
        delegate?.didReceiveRemoteVideoTrack(nil)
        if wasConnected {
            delegate?.didDisconnectWebRTC()
            delegate?.onPeersConnectionStatusChange(connected: false)
        }
    }

    // MARK: Signaling Event
    func receiveOffer(
        offerSDP: RTCSessionDescription,
        onCreateAnswer: @escaping (RTCSessionDescription) -> Void
    ){
        if(self.peerConnection == nil){
            print("offer received, create peerconnection")
            self.peerConnection = media.makePeerConnection(delegate: self)
            if let localVideoTrack = media.videoTrack {
                self.peerConnection!
                    .add(localVideoTrack, streamIds: ["stream-0"])
            }
            if let localAudioTrack = media.audioTrack {
                self.peerConnection!
                    .add(localAudioTrack, streamIds: ["stream-0"])
            }
            media.encryption?.attachSenderCryptors(on: peerConnection!)
        }

        print("set remote description")
        self.peerConnection!.setRemoteDescription(offerSDP) { (err) in
            if let error = err {
                print("failed to set remote offer SDP")
                print(error)
                return
            }

            print("succeed to set remote offer SDP")
            self.attachReceiverCryptors()
            if let peerConnection = self.peerConnection {
                self.media.applyVideoCodecPreferences(on: peerConnection)
            }
            self.makeAnswer(onCreateAnswer: onCreateAnswer)
        }
    }

    func receiveAnswer(answerSDP: RTCSessionDescription){
        self.peerConnection!.setRemoteDescription(answerSDP) { (err) in
            if let error = err {
                print("failed to set remote answer SDP")
                print(error)
                return
            }

            print("succeed to set remote answer SDP")
            self.attachReceiverCryptors()
        }
    }

    func receiveCandidate(candidate: RTCIceCandidate){
        self.peerConnection?.add(
candidate,
 completionHandler: { err in
     if let error = err {
         print(
            "failed to set ice candidate: \(error.localizedDescription)"
         )
     }
 })
    }

    // MARK: - Private functions

    private func attachReceiverCryptors() {
        guard let peerConnection = peerConnection else { return }
        media.encryption?.attachReceiverCryptors(on: peerConnection)
    }

    // MARK: - Signaling Offer/Answer
    private func makeOffer(
        onSuccess: @escaping (RTCSessionDescription) -> Void
    ) {
        self.peerConnection?
            .offer(for: RTCMediaConstraints.init(mandatoryConstraints: nil, optionalConstraints: nil)) { (
                sdp,
                err
            ) in
                if let error = err {
                    print("error with make offer")
                    print(error)
                    return
                }

                if let offerSDP = sdp {
                    print("make offer, created local sdp")
                    self.peerConnection!
                        .setLocalDescription(
                            offerSDP,
                            completionHandler: { (
                                err
                            ) in
                                if let error = err {
                                    print("error with set local offer sdp")
                                    print(error)
                                    return
                                }
                                print("succeed to set local offer SDP")
                                onSuccess(offerSDP)
                            })
                }

            }
    }

    private func makeAnswer(
        onCreateAnswer: @escaping (RTCSessionDescription) -> Void
    ){
        self.peerConnection!
            .answer(
                for: RTCMediaConstraints(
                    mandatoryConstraints: nil,
                    optionalConstraints: nil
                ),
                completionHandler: { (
                    answerSessionDescription,
                    err
                ) in
                    if let error = err {
                        print("failed to create local answer SDP")
                        print(error)
                        return
                    }

                    print("succeed to create local answer SDP")
                    if let answerSDP = answerSessionDescription{
                        self.peerConnection!
                            .setLocalDescription(
                                answerSDP,
                                completionHandler: { (
                                    err
                                ) in
                                    if let error = err {
                                        print("failed to set local ansewr SDP")
                                        print(error)
                                        return
                                    }

                                    print("succeed to set local answer SDP")
                                    onCreateAnswer(answerSDP)
                                })
                    }
                })
    }

    // MARK: - Connection Events
    private func onConnected(){
        self.isConnected = true

        DispatchQueue.main.async {
            self.delegate?.didConnectWebRTC()
        }
    }

    private func onDisConnected(){
        self.isConnected = false

        DispatchQueue.main.async {
            print("--- on disconnected ---")

            if let dataChannel = self.dataChannel, dataChannel.readyState == RTCDataChannelState.open {
                dataChannel.close()
            }

            self.peerConnection?.close()
            self.peerConnection = nil
            self.media.encryption?.disposeCryptors()
            self.remoteStream = nil
            self.delegate?.didReceiveRemoteVideoTrack(nil)
            self.delegate?.didDisconnectWebRTC()
        }
    }
}

// MARK: - PeerConnection Delegeates
extension WebRTCClient {
    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {

    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didOpen dataChannel: RTCDataChannel
    ) {
        print("did open data channel: ", dataChannel.readyState.rawValue)
        guard peerConnection === self.peerConnection else { return }

        self.dataChannel = dataChannel
        self.dataChannel.delegate = self
        // A channel opened by the remote peer may already be open, so no state change would follow
        self.delegate?.onDataChannelStateChange(state: dataChannel.readyState)
    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didChange stateChanged: RTCSignalingState
    ) {
        print("signaling state changed: ", stateChanged)
    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didAdd stream: RTCMediaStream
    ) {
        print("did add stream")
        guard peerConnection === self.peerConnection else { return }
        self.remoteStream = stream

        if let track = stream.videoTracks.first {
            print("video track found")
            DispatchQueue.main.async {
                self.delegate?.didReceiveRemoteVideoTrack(track)
            }
        }

        if let audioTrack = stream.audioTracks.first{
            print("audio track found")
            audioTrack.source.volume = 8
        }
    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didAdd rtpReceiver: RTCRtpReceiver,
        streams mediaStreams: [RTCMediaStream]
    ) {
        print("did add receiver: ", rtpReceiver.receiverId)
        media.encryption?.attachReceiverCryptor(rtpReceiver)
        applyRemoteAudioEnabled(rtpReceiver)
    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didRemove stream: RTCMediaStream
    ) {
        print("--- did remove stream ---")


    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didChange newState: RTCIceConnectionState
    ) {
        // A replaced connection must not tear down the current one
        guard peerConnection === self.peerConnection else { return }
        switch newState {

        case .connected, .completed:
            if !self.isConnected {
                self.onConnected()
                self.delegate?.onPeersConnectionStatusChange(connected: true)
            }
        default:
            if self.isConnected{
                self.onDisConnected()
                delegate?.onPeersConnectionStatusChange(connected: false)
            }
        }

        DispatchQueue.main.async {
            self.delegate?
                .didIceConnectionStateChanged(iceConnectionState: newState)
        }
    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didChange newState: RTCIceGatheringState
    ) {

    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didGenerate candidate: RTCIceCandidate
    ) {
        guard peerConnection === self.peerConnection else { return }
        self.delegate?.didGenerateCandidate(iceCandidate: candidate)
    }

    func peerConnection(
        _ peerConnection: RTCPeerConnection,
        didRemove candidates: [RTCIceCandidate]
    ) {

    }
}

// MARK: Data channel delegate
extension WebRTCClient: RTCDataChannelDelegate {
    func dataChannelDidChangeState(_ dataChannel: RTCDataChannel) {
        print("dataChannelDidChangeState", dataChannel.readyState.rawValue)
        guard dataChannel === self.dataChannel else { return }
        self.delegate?.onDataChannelStateChange(state: dataChannel.readyState)
    }

    func dataChannel(
        _ dataChannel: RTCDataChannel,
        didReceiveMessageWith buffer: RTCDataBuffer
    ) {
        if buffer.isBinary {
            print("Binary message")
        } else {
            if let receivedMessage = String(
                data: buffer.data,
                encoding: .utf8
            ) {
                print("Message received: \(receivedMessage)")
                self.delegate?.onDataChannelMessage(message: receivedMessage)
            } else {
                print("Failed to decode received message")
            }
        }
    }
}

// MARK: public methods
extension WebRTCClient {
    /// Audio level (0...1) of the remote peer's microphone, delivered on the main queue.
    /// True from the first offer or answer until the call ends; WebRTC records the microphone meanwhile.
    var hasPeerConnection: Bool { peerConnection != nil }

    /// Our own microphone's `audioLevel` (0...1), delivered on the main queue.
    func localAudioLevel(completion: @escaping (Double) -> Void) {
        guard let peerConnection else {
            completion(0)
            return
        }
        peerConnection.statistics { report in
            let level = report.localAudioLevel
            DispatchQueue.main.async { completion(level) }
        }
    }

    func remoteAudioLevel(completion: @escaping (Double) -> Void) {
        guard isConnected, let peerConnection = peerConnection else {
            completion(0)
            return
        }
        peerConnection.statistics { report in
            let inbound = report.statistics.values.first {
                $0.type == "inbound-rtp" && ($0.values["kind"] as? String) == "audio"
            }
            let level = (inbound?.values["audioLevel"] as? NSNumber)?.doubleValue ?? 0
            DispatchQueue.main.async {
                completion(level)
            }
        }
    }

    func setRemoteAudioEnabled(_ enabled: Bool) {
        isRemoteAudioEnabled = enabled
        peerConnection?.receivers.forEach { applyRemoteAudioEnabled($0) }
    }

    private func applyRemoteAudioEnabled(_ receiver: RTCRtpReceiver) {
        (receiver.track as? RTCAudioTrack)?.isEnabled = isRemoteAudioEnabled
    }

    /// Only needed when the remote offered without a data channel (older clients): add one and renegotiate.
    func ensureDataChannel(
        dataChannelName: String,
        onSuccess: @escaping (RTCSessionDescription) -> Void
    ) {
        if let dataChannel, dataChannel.readyState == .open || dataChannel.readyState == .connecting {
            return
        }
        openDataChannel(label: dataChannelName)
        makeOffer(onSuccess: onSuccess)
    }

    private func openDataChannel(label: String) {
        print("createDataChannel:", label)
        dataChannel = peerConnection?
            .dataChannel(forLabel: label, configuration: RTCDataChannelConfiguration())
        dataChannel?.delegate = self
    }

    func sendDataChannelMessage(message: String) {
        guard let dataChannel = dataChannel, dataChannel.readyState == .open else {
            print("Data channel is not open")
            return
        }

        // Convert the string message into a Data object
        if let data = message.data(using: .utf8) {
            let buffer = RTCDataBuffer(data: data, isBinary: false)

            // Send the message
            if dataChannel.sendData(buffer) {
                print("Message sent: \(message)")
            } else {
                print("Failed to send message")
            }
        } else {
            print("Failed to convert message to data")
        }
    }
}

// MARK: - E2EE
extension WebRTCClient {
    /// Generates new key material, installs it as the shared key and returns it so it can be sent to the remote peer.
    func generateEncryptionKey() -> Data? {
        return media.encryption?.generateKey()
    }

    /// Installs key material received from the remote peer. Returns false when E2EE is off.
    @discardableResult
    func setEncryptionKey(_ key: Data) -> Bool {
        guard let encryption = media.encryption else { return false }
        encryption.setKey(key)
        return true
    }
}

extension RTCStatisticsReport {
    /// The microphone as WebRTC measures it before encoding: the audio "media-source" `audioLevel`.
    var localAudioLevel: Double {
        let source = statistics.values.first {
            $0.type == "media-source" && ($0.values["kind"] as? String) == "audio"
        }
        return (source?.values["audioLevel"] as? NSNumber)?.doubleValue ?? 0
    }
}
