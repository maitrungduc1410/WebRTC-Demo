//
//  SampleHandler.swift
//  WebRTCDemoScreenBroadcast
//
//  Created by Duc Trung Mai on 9/23/24.
//

import ReplayKit
import WebRTC

let GROUP_NAME = "group.com.ducmai.webrtc.broadcast"


class SampleHandler: RPBroadcastSampleHandler {
    private var peerConnectionFactory: RTCPeerConnectionFactory!
    private var peerConnection: RTCPeerConnection?
    private var videoSource: RTCVideoSource?
    private var videoCapturer: RTCVideoCapturer!
    
    override func broadcastStarted(withSetupInfo setupInfo: [String : NSObject]?) {
        // User has requested to start the broadcast. Setup info from the UI extension can be supplied but optional.
        print(1111)
        
        // try to reuse existing Peer connection info
        let userDefaults = UserDefaults(suiteName: GROUP_NAME)
        guard let localSDP = userDefaults?.dictionary(forKey: "localSDP"),
              let remoteSDP = userDefaults?.dictionary(forKey: "remoteSDP"),
              let iceCandidates = userDefaults?.array(forKey: "iceCandidates") as? [[String: Any]] else {
            print("no data in UserDefaults")
            return
        }
        
        let videoEncoderFactory = RTCDefaultVideoEncoderFactory()
        let videoDecoderFactory = RTCDefaultVideoDecoderFactory()
        self.peerConnectionFactory = RTCPeerConnectionFactory(encoderFactory: videoEncoderFactory, decoderFactory: videoDecoderFactory)
        videoSource = self.peerConnectionFactory.videoSource()
        videoCapturer = RTCVideoCapturer(delegate: videoSource!)
        let videoTrack = self.peerConnectionFactory.videoTrack(with: videoSource!, trackId: "screenTrack")
        
        
        let rtcConf = RTCConfiguration()
        rtcConf.iceServers = [RTCIceServer(urlStrings: ["stun:stun.l.google.com:19302"])]
        let mediaConstraints = RTCMediaConstraints.init(mandatoryConstraints: nil, optionalConstraints: nil)
        peerConnection = self.peerConnectionFactory.peerConnection(with: rtcConf, constraints: mediaConstraints, delegate: nil)
        peerConnection!.delegate = self
        
        peerConnection!.add(videoTrack, streamIds: ["screenStream"])
        
//        print("localSDP: ----", localSDP)
//        print("remoteSDP: ++++", remoteSDP)
//        print("iceCandidates: .......", iceCandidates)
        print("3333: ", localSDP["type"] , remoteSDP["type"] )
        let localSdp = RTCSessionDescription(type: localSDP["type"] as! String == "offer" ? .offer : .answer, sdp: localSDP["sdp"] as! String)
        
        let remoteSdp = RTCSessionDescription(type: remoteSDP["type"] as! String == "offer" ? .offer : .answer, sdp: remoteSDP["sdp"] as! String)
        
        // Set remote description first, as we are joining an existing connection
        peerConnection!.setRemoteDescription(remoteSdp) { error in
            if let error = error {
                print("Error setting remote SDP: \(error.localizedDescription)")
            } else {
                print("Remote SDP set successfully")
            }
        }
        
        // Set local description (if necessary)
        peerConnection!.setLocalDescription(localSdp) { error in
            if let error = error {
                print("Error setting local SDP: \(error.localizedDescription)")
            } else {
                print("Local SDP set successfully")
            }
        }
        
        for candidate in iceCandidates {
            let ice = RTCIceCandidate(sdp: candidate["sdp"] as! String,
                                      sdpMLineIndex: candidate["sdpMLineIndex"] as! Int32,
                                      sdpMid: candidate["sdpMid"] as? String)
            
            peerConnection!.add(ice, completionHandler: { err in
                if let error = err {
                    print("failed to set ice candidate: \(error.localizedDescription)")
                    return
                }
                
                print("succeed to add ice candidate")
            })
        }
    }
    
    override func broadcastPaused() {
        print(22222)
        // User has requested to pause the broadcast. Samples will stop being delivered.
    }
    
    override func broadcastResumed() {
        print(333333)
        // User has requested to resume the broadcast. Samples delivery will resume.
    }
    
    override func broadcastFinished() {
        print(44444)
        // User has requested to finish the broadcast.
    }
    
    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        //        print(555555)
        switch sampleBufferType {
        case RPSampleBufferType.video:
            // Handle video sample buffer
            guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
            let videoFrame = RTCVideoFrame(buffer: RTCCVPixelBuffer(pixelBuffer: pixelBuffer), rotation: ._0, timeStampNs: Int64(CACurrentMediaTime() * 1000000000))
            
            // Send the image buffer to WebRTC
            videoSource?.capturer(videoCapturer, didCapture: videoFrame)
            break
        case RPSampleBufferType.audioApp:
            // Handle audio sample buffer for app audio
            break
        case RPSampleBufferType.audioMic:
            // Handle audio sample buffer for mic audio
            break
        @unknown default:
            // Handle other sample buffer types
            fatalError("Unknown type of sample buffer")
        }
    }
    
    // MARK: - Signaling Offer/Answer
    private func makeOffer() {
        self.peerConnection?.offer(for: RTCMediaConstraints.init(mandatoryConstraints: nil, optionalConstraints: nil)) { (sdp, err) in
            if let error = err {
                print("error with make offer")
                print(error)
                return
            }
            
            if let offerSDP = sdp {
                print("make offer, created local sdp")
                self.peerConnection!.setLocalDescription(offerSDP, completionHandler: { (err) in
                    if let error = err {
                        print("error with set local offer sdp")
                        print(error)
                        return
                    }
                    print("succeed to set local offer SDP")
//                    onSuccess(offerSDP)
                })
            }
            
        }
    }
    
    private func makeAnswer(){
        self.peerConnection!.answer(for: RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil), completionHandler: { (answerSessionDescription, err) in
            if let error = err {
                print("failed to create local answer SDP")
                print(error)
                return
            }
            
            print("succeed to create local answer SDP")
            if let answerSDP = answerSessionDescription{
                self.peerConnection!.setLocalDescription( answerSDP, completionHandler: { (err) in
                    if let error = err {
                        print("failed to set local ansewr SDP")
                        print(error)
                        return
                    }
                    
                    print("succeed to set local answer SDP")
//                    onCreateAnswer(answerSDP)
                })
            }
        })
    }
}

extension SampleHandler: RTCPeerConnectionDelegate {
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {
        print("signaling state changed: \(stateChanged)")
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {
        print("did add stream: ", stream.streamId)
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {
        print("did remove stream:", stream.streamId)
    }
    
    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {
        print("peerConnectionShouldNegotiate")
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
        print("ice connection state changed: \(newState)")
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {
        print("ice gathering state changed: \(newState)")
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        print("did generate ice")
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {
        print("did remote ice candidate")
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {
        print("did open data channel")
    }
}
