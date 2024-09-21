//
//  PeerConnectionClient.swift
//  WebRTCDemo
//
//  Created by Mai Trung Duc on 11/4/20.
//  Copyright © 2020 Mai Trung Duc. All rights reserved.
//

import Foundation
import WebRTC
import ReplayKit

protocol WebRTCClientDelegate {
    func didGenerateCandidate(iceCandidate: RTCIceCandidate)
    func didIceConnectionStateChanged(iceConnectionState: RTCIceConnectionState)
    func didReceiveMessage(message: String)
    func didConnectWebRTC()
    func didDisconnectWebRTC()
    func onDataChannelMessage(message: String)
    func onDataChannelStateChange(state: RTCDataChannelState)
    func onPeersConnectionStatusChange(connected: Bool)
}

class WebRTCClient: NSObject, RTCPeerConnectionDelegate, RTCVideoViewDelegate {
    private var peerConnectionFactory: RTCPeerConnectionFactory!
    private var peerConnection: RTCPeerConnection?
    private var videoCapturer: RTCVideoCapturer!
    private var localVideoTrack: RTCVideoTrack!
    private var localAudioTrack: RTCAudioTrack!
    private var localRenderView: RTCMTLVideoView?
    private var localView: UIView!
    private var remoteRenderView: RTCMTLVideoView?
    private var remoteView: UIView!
    private var remoteStream: RTCMediaStream?
    private var channels: (video: Bool, audio: Bool) = (false, false)
    private var customFrameCapturer: Bool = false
    private var dataChannel: RTCDataChannel!
    private var useFrontCamera = true
    private var videoSource: RTCVideoSource?
    
    var delegate: WebRTCClientDelegate?
    public private(set) var isConnected: Bool = false
    
    func localVideoView() -> UIView {
        return localView
    }
    
    func remoteVideoView() -> UIView {
        return remoteView
    }
    
    override init() {
        super.init()
        print("WebRTC Client initialize")
    }
    
    deinit {
        print("WebRTC Client Deinit")
        self.peerConnectionFactory = nil
        self.peerConnection = nil
    }
    
    // MARK: - Public functions
    func setup(videoTrack: Bool, audioTrack: Bool, customFrameCapturer: Bool){
        print("set up")
        self.channels.video = videoTrack
        self.channels.audio = audioTrack
        self.customFrameCapturer = customFrameCapturer
        
        let videoEncoderFactory = RTCDefaultVideoEncoderFactory()
        let videoDecoderFactory = RTCDefaultVideoDecoderFactory()
        self.peerConnectionFactory = RTCPeerConnectionFactory(encoderFactory: videoEncoderFactory, decoderFactory: videoDecoderFactory)
        
        setupView()
        setupLocalTracks()
        
        if self.channels.video {
            startCaptureLocalVideo(cameraPositon: .front, videoWidth: 640, videoHeight: 640*16/9, videoFps: 30)
            self.localVideoTrack?.add(self.localRenderView!)
        }
    }
    
    func setupLocalViewFrame(frame: CGRect){
        localView.frame = frame
        localRenderView?.frame = localView.frame
    }
    
    func setupRemoteViewFrame(frame: CGRect){
        remoteView.frame = frame
        remoteRenderView?.frame = remoteView.frame
    }
    
    // MARK: Connect
    func connect(onSuccess: @escaping (RTCSessionDescription) -> Void){
        self.peerConnection = setupPeerConnection()
        self.peerConnection!.delegate = self
        
        if self.channels.video {
            self.peerConnection!.add(localVideoTrack, streamIds: ["stream0"])
        }
        if self.channels.audio {
            self.peerConnection!.add(localAudioTrack, streamIds: ["stream0"])
        }
        
        makeOffer(onSuccess: onSuccess)
    }
    
    // MARK: HangUp
    func disconnect(){
        if dataChannel != nil {
            self.dataChannel.close()
        }
        
        if self.peerConnection != nil{
            self.peerConnection!.close()
        }
    }
    
    // MARK: Signaling Event
    func receiveOffer(offerSDP: RTCSessionDescription, onCreateAnswer: @escaping (RTCSessionDescription) -> Void){
        if(self.peerConnection == nil){
            print("offer received, create peerconnection")
            self.peerConnection = setupPeerConnection()
            self.peerConnection!.delegate = self
            if self.channels.video {
                self.peerConnection!.add(localVideoTrack, streamIds: ["stream-0"])
            }
            if self.channels.audio {
                self.peerConnection!.add(localAudioTrack, streamIds: ["stream-0"])
            }
            
        }
        
        print("set remote description")
        self.peerConnection!.setRemoteDescription(offerSDP) { (err) in
            if let error = err {
                print("failed to set remote offer SDP")
                print(error)
                return
            }
            
            print("succeed to set remote offer SDP")
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
        }
    }
    
    func receiveCandidate(candidate: RTCIceCandidate){
        self.peerConnection?.add(candidate, completionHandler: { err in
            if let error = err {
                print("failed to set ice candidate: \(error.localizedDescription)")
            }
        })
    }
    
    func captureCurrentFrame(sampleBuffer: CMSampleBuffer){
        if let capturer = self.videoCapturer as? RTCCustomFrameCapturer {
            capturer.capture(sampleBuffer)
        }
    }
    
    func captureCurrentFrame(sampleBuffer: CVPixelBuffer){
        if let capturer = self.videoCapturer as? RTCCustomFrameCapturer {
            capturer.capture(sampleBuffer)
        }
    }
    
    // MARK: - Private functions
    // MARK: - Setup
    private func setupPeerConnection() -> RTCPeerConnection{
        let rtcConf = RTCConfiguration()
        rtcConf.iceServers = [RTCIceServer(urlStrings: ["stun:stun.l.google.com:19302"])]
        let mediaConstraints = RTCMediaConstraints.init(mandatoryConstraints: nil, optionalConstraints: nil)
        let pc = self.peerConnectionFactory.peerConnection(with: rtcConf, constraints: mediaConstraints, delegate: nil)
        return pc!
    }
    
    private func setupView(){
        // local
        localRenderView = RTCMTLVideoView()
        localRenderView!.delegate = self
        localView = UIView()
        localView.addSubview(localRenderView!)
        // remote
        remoteRenderView = RTCMTLVideoView()
        remoteRenderView?.delegate = self
        remoteView = UIView()
        remoteView.addSubview(remoteRenderView!)
    }
    
    //MARK: - Local Media
    private func setupLocalTracks(){
        if self.channels.video == true {
            self.localVideoTrack = createVideoTrack()
        }
        if self.channels.audio == true {
            self.localAudioTrack = createAudioTrack()
        }
    }
    
    private func createAudioTrack() -> RTCAudioTrack {
        let audioConstrains = RTCMediaConstraints(mandatoryConstraints: nil, optionalConstraints: nil)
        let audioSource = self.peerConnectionFactory.audioSource(with: audioConstrains)
        let audioTrack = self.peerConnectionFactory.audioTrack(with: audioSource, trackId: "audio0")
        
        // audioTrack.source.volume = 10
        return audioTrack
    }
    
    private func createVideoTrack() -> RTCVideoTrack {
        videoSource = self.peerConnectionFactory.videoSource()
        
        if self.customFrameCapturer {
            self.videoCapturer = RTCCustomFrameCapturer(delegate: videoSource!)
        }else if TARGET_OS_SIMULATOR != 0 {
            print("now runnnig on simulator...")
            self.videoCapturer = RTCFileVideoCapturer(delegate: videoSource!)
        }
        else {
            self.videoCapturer = RTCCameraVideoCapturer(delegate: videoSource!)
        }
        let videoTrack = self.peerConnectionFactory.videoTrack(with: videoSource!, trackId: "video0")
        return videoTrack
    }
    
    private func startCaptureLocalVideo(cameraPositon: AVCaptureDevice.Position, videoWidth: Int, videoHeight: Int?, videoFps: Int) {
        if let capturer = self.videoCapturer as? RTCCameraVideoCapturer {
            var targetDevice: AVCaptureDevice?
            var targetFormat: AVCaptureDevice.Format?
            
            // find target device
            let devicies = RTCCameraVideoCapturer.captureDevices()
            devicies.forEach { (device) in
                if device.position ==  cameraPositon{
                    targetDevice = device
                }
            }
            
            // find target format
            let formats = RTCCameraVideoCapturer.supportedFormats(for: targetDevice!)
            formats.forEach { (format) in
                for _ in format.videoSupportedFrameRateRanges {
                    let description = format.formatDescription as CMFormatDescription
                    let dimensions = CMVideoFormatDescriptionGetDimensions(description)
                    
                    if dimensions.width == videoWidth && dimensions.height == videoHeight ?? 0{
                        targetFormat = format
                    } else if dimensions.width == videoWidth {
                        targetFormat = format
                    }
                }
            }
            
            capturer.startCapture(with: targetDevice!,
                                  format: targetFormat!,
                                  fps: videoFps)
        } else if let capturer = self.videoCapturer as? RTCFileVideoCapturer{
            print("setup file video capturer")
            if let _ = Bundle.main.path( forResource: "sample.mp4", ofType: nil ) {
                capturer.startCapturing(fromFileNamed: "sample.mp4") { (err) in
                    print(err)
                }
            }else{
                print("file did not faund")
            }
        }
    }
    
    // MARK: - Signaling Offer/Answer
    private func makeOffer(onSuccess: @escaping (RTCSessionDescription) -> Void) {
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
                    onSuccess(offerSDP)
                })
            }
            
        }
    }
    
    private func makeAnswer(onCreateAnswer: @escaping (RTCSessionDescription) -> Void){
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
                    onCreateAnswer(answerSDP)
                })
            }
        })
    }
    
    // MARK: - Connection Events
    private func onConnected(){
        self.isConnected = true
        
        DispatchQueue.main.async {
            self.remoteRenderView?.isHidden = false
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
            
            self.peerConnection!.close()
            self.peerConnection = nil
            self.remoteRenderView?.isHidden = true
            self.delegate?.didDisconnectWebRTC()
        }
    }
}

// MARK: - PeerConnection Delegeates
extension WebRTCClient {
    func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {
        
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didOpen dataChannel: RTCDataChannel) {
        print("did open data channel: ", dataChannel.readyState.rawValue)
                
        self.dataChannel = dataChannel
        self.dataChannel.delegate = self
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {
        print("signaling state changed: ", stateChanged)
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {
        print("did add stream")
        self.remoteStream = stream
        
        if let track = stream.videoTracks.first {
            print("video track found")
            track.add(remoteRenderView!)
        }
        
        if let audioTrack = stream.audioTracks.first{
            print("audio track found")
            audioTrack.source.volume = 8
        }
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {
        print("--- did remove stream ---")
        
        
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceConnectionState) {
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
            self.delegate?.didIceConnectionStateChanged(iceConnectionState: newState)
        }
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {
        
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didGenerate candidate: RTCIceCandidate) {
        self.delegate?.didGenerateCandidate(iceCandidate: candidate)
    }
    
    func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {
        
    }
}

// MARK: - RTCVideoView Delegate
extension WebRTCClient {
    func videoView(_ videoView: RTCVideoRenderer, didChangeVideoSize size: CGSize) {
        let isLandScape = size.width < size.height
        var renderView: RTCMTLVideoView?
        var parentView: UIView?
        if videoView.isEqual(localRenderView){
            print("local video size changed")
            renderView = localRenderView
            parentView = localView
        }
        
        if videoView.isEqual(remoteRenderView!){
            print("remote video size changed to: ", size)
            renderView = remoteRenderView
            parentView = remoteView
        }
        
        guard let _renderView = renderView, let _parentView = parentView else {
            return
        }
        
        if(isLandScape){
            let ratio = size.width / size.height
            _renderView.frame = CGRect(x: 0, y: 0, width: _parentView.frame.height * ratio, height: _parentView.frame.height)
            _renderView.center.x = _parentView.frame.width/2
        }else{
            let ratio = size.height / size.width
            _renderView.frame = CGRect(x: 0, y: 0, width: _parentView.frame.width, height: _parentView.frame.width * ratio)
            _renderView.center.y = _parentView.frame.height/2
        }
    }
}

// MARK: Data channel delegate
extension WebRTCClient: RTCDataChannelDelegate {
    func dataChannelDidChangeState(_ dataChannel: RTCDataChannel) {
        print("dataChannelDidChangeState", dataChannel.readyState.rawValue)
        self.delegate?.onDataChannelStateChange(state: dataChannel.readyState)
    }
    
    func dataChannel(_ dataChannel: RTCDataChannel, didReceiveMessageWith buffer: RTCDataBuffer) {
        if buffer.isBinary {
            print("Binary message")
        } else {
            if let receivedMessage = String(data: buffer.data, encoding: .utf8) {
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
    func toggleVideo(enable: Bool) {
        localVideoTrack.isEnabled = enable
    }
    
    func toggleAudio(enable: Bool) {
        localAudioTrack.isEnabled = enable
    }
    
    func switchCamera() {
        useFrontCamera.toggle()
        
        startCaptureLocalVideo(cameraPositon: useFrontCamera ? .front : .back, videoWidth: 640, videoHeight: 640*16/9, videoFps: 30)
    }
    
    func createDataChannel(dataChannelName: String, onSuccess: @escaping (RTCSessionDescription) -> Void) {
        print("createDataChannel:", dataChannelName)
        
        let config = RTCDataChannelConfiguration()
        dataChannel = peerConnection?.dataChannel(forLabel: dataChannelName, configuration: config)
        dataChannel.delegate = self
        makeOffer(onSuccess: onSuccess)
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
    
//    func startCapture(onSuccess: @escaping (RTCSessionDescription) -> Void) {
//        // Start screen recording using ReplayKit
//        RPScreenRecorder.shared().startCapture { (sampleBuffer, bufferType, error) in
//            if error != nil {
//                print("Error capturing screen: \(error?.localizedDescription ?? "")")
//                return
//            }
//            
//            if bufferType == .video {
//                // Process the sampleBuffer and send it to WebRTC
//                self.processFrame(sampleBuffer: sampleBuffer)
//            }
//        } completionHandler: { error in
//            if let error = error {
//                print("Error starting capture: \(error.localizedDescription)")
//                
//                return
//            }
//            print("screen sharing started")
//            
//            if let capturer = self.videoCapturer as? RTCCameraVideoCapturer {
//                capturer.stopCapture()
//            }
//            
//            self.updateVideoTrack(trackId: "screenTrack")
//            self.makeOffer(onSuccess: onSuccess)
//        }
//    }
//    
//    func stopCapture() {
//        RPScreenRecorder.shared().stopCapture { error in
//            if let error = error {
//                print("Error stopping capture: \(error.localizedDescription)")
//            }
//        }
//    }
//    
//    private func processFrame(sampleBuffer: CMSampleBuffer) {
//        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
//        let videoFrame = RTCVideoFrame(buffer: RTCCVPixelBuffer(pixelBuffer: pixelBuffer), rotation: ._0, timeStampNs: Int64(CACurrentMediaTime() * 1000000000))
//        
//        print("processFrame")
//        videoSource?.capturer(videoCapturer, didCapture: videoFrame)
//    }
//    
//    private func updateVideoTrack(trackId: String) {
//        // Remove the previous video track
//        if let stream = peerConnection?.localStreams.first, let videoTrack = stream.videoTracks.first {
//            stream.removeVideoTrack(videoTrack)
//        }
//
//        // Add the new video track
//        localVideoTrack = peerConnectionFactory.videoTrack(with: self.videoSource!, trackId: trackId)
//        
//        peerConnection?.localStreams.first?.addVideoTrack(localVideoTrack)
//    }
}

