//
//  CallViewController.swift
//  WebRTCDemo
//
//  Created by Mai Trung Duc on 10/4/20.
//  Copyright © 2020 Mai Trung Duc. All rights reserved.
//

import UIKit
import SocketIO
import WebRTC

class CallViewController: UIViewController, WebRTCClientDelegate, CameraSessionDelegate {
    var roomId: String = ""
    let manager = SocketManager(socketURL: URL(string: "http://192.168.1.129:4000")!, config: [.log(true), .compress])
    var socket: SocketIOClient!
    var webRTCClient: WebRTCClient!
    var useCustomCapturer: Bool = true
    var cameraSession: CameraSession?
    
    override func viewDidLoad() {
        super.viewDidLoad()

        navigationItem.title = "Room ID: " + roomId
        
        #if targetEnvironment(simulator)
            useCustomCapturer = false
        #endif
        
        socket = manager.defaultSocket
        
        socket.on(clientEvent: .connect) {data, ack in
            print("socket connected")
            
            let payload: [String: Any] = [
                "roomId": self.roomId
            ]
            self.socket.emit("join room", payload)
            
            self.webRTCClient = WebRTCClient()
            self.webRTCClient.delegate = self
            self.webRTCClient.setup(videoTrack: true, audioTrack: true, customFrameCapturer: self.useCustomCapturer)
            self.setupUI()
        }
        
        socket.on(clientEvent: .disconnect) {data, ack in
            print("socket disconnected")
        }
        
        socket.on("new user joined") {data,ack in
            self.webRTCClient.connect(onSuccess: { (offerSDP: RTCSessionDescription) -> Void in
                self.sendSDP(sessionDescription: offerSDP)
            })
        }
        
        socket.on("offer") {data,ack in
            let payload = data as? [[String: [String: Any]]]
            let offerSDP = RTCSessionDescription(type: .offer, sdp: payload?[0]["offer"]?["sdp"] as! String)

            self.webRTCClient.receiveOffer(offerSDP: offerSDP, onCreateAnswer: {(answerSDP: RTCSessionDescription) -> Void in
                self.sendSDP(sessionDescription: answerSDP)
            })
        }
        socket.on("answer") {data,ack in
            let payload = data as? [[String: [String: Any]]]
            let answerSDP = RTCSessionDescription(type: .answer, sdp: payload?[0]["answer"]?["sdp"] as! String)
            
            self.webRTCClient.receiveAnswer(answerSDP: answerSDP)
        }
        socket.on("new ice candidate") {data,ack in
            let payload = data as? [[String: [String: Any]]]
            let iceCandidate: [String: Any] = (payload?[0]["iceCandidate"])!
            
            self.webRTCClient.receiveCandidate(
                candidate: RTCIceCandidate(sdp: iceCandidate["candidate"] as! String,
                sdpMLineIndex: iceCandidate["sdpMLineIndex"] as! Int32,
                sdpMid: iceCandidate["sdpMid"] as? String)
            )
        }
        socket.on("message") {data,ack in
            
        }
        
        socket.connect()
        
        // Uncomment this if you want redirect audio to Speaker
//        NotificationCenter.default.addObserver(self, selector: #selector(didSessionRouteChange(_:)), name: AVAudioSession.routeChangeNotification, object: nil)
    }
    
    override func viewWillDisappear(_ animated: Bool) {
        socket.disconnect()
        webRTCClient.disconnect()
    }
    
    private func sendSDP(sessionDescription: RTCSessionDescription){
        var type = ""
        if sessionDescription.type == .offer {
            type = "offer"
        }else if sessionDescription.type == .answer {
            type = "answer"
        }
        
        let sdp: [String: Any] = [
            "type": type,
            "sdp": sessionDescription.sdp
        ]
        
        let payload: [String: Any] = [
            "roomId": roomId,
            type: sdp
        ]
        
        socket.emit(type, payload)
    }
    
    private func sendCandidate(iceCandidate: RTCIceCandidate){
        let candidate: [String: Any] = [
            "candidate": iceCandidate.sdp,
            "sdpMid": iceCandidate.sdpMid!,
            "sdpMLineIndex": iceCandidate.sdpMLineIndex
        ]
        
        let payload: [String: Any] = [
            "roomId": roomId,
            "iceCandidate": candidate
        ]
        
        socket.emit("new ice candidate", payload)
    }
    
    // MARK: - UI
    private func setupUI(){
        if useCustomCapturer {
            print("--- use custom capturer ---")
            self.cameraSession = CameraSession()
            self.cameraSession?.delegate = self
            self.cameraSession?.setupSession()
        }
        
        let remoteVideoView = webRTCClient.remoteVideoView()
        webRTCClient.setupRemoteViewFrame(frame: CGRect(x: 0, y: 0, width: UIScreen.main.bounds.width, height: UIScreen.main.bounds.height * 0.7))
        remoteVideoView.center.y = self.view.center.y
        self.view.addSubview(remoteVideoView)
        
        let topBarHeight = (view.window?.windowScene?.statusBarManager?.statusBarFrame.height ?? 0.0) +
        (self.navigationController?.navigationBar.frame.height ?? 0.0)
        
        // set position of localVideoView to top left conner
        let localVideoView = webRTCClient.localVideoView()
        webRTCClient.setupLocalViewFrame(frame: CGRect(x: 8, y: topBarHeight + 8, width: 120, height: 160))
        localVideoView.subviews.last?.isUserInteractionEnabled = true
        self.view.addSubview(localVideoView)
    }
    
    // Uncomment this if you want redirect audio to Speaker
//    @objc func didSessionRouteChange(_ notification: Notification) {
//        guard let info = notification.userInfo,
//            let value = info[AVAudioSessionRouteChangeReasonKey] as? UInt,
//            let reason = AVAudioSession.RouteChangeReason(rawValue: value) else { return }
//        switch reason {
//            case .categoryChange: try? AVAudioSession.sharedInstance().overrideOutputAudioPort(.speaker)
//            default: break
//        }
//    }
}

// MARK: - WebRTCClient Delegate
extension CallViewController {
    func didGenerateCandidate(iceCandidate: RTCIceCandidate) {
        print("didGenerateCandidate")
        self.sendCandidate(iceCandidate: iceCandidate)
    }
    
    func didIceConnectionStateChanged(iceConnectionState: RTCIceConnectionState) {
        print("didIceConnectionStateChanged")
        print(iceConnectionState)
    }
    
    func didReceiveMessage(message: String) {
        print("didReceiveMessage")
        print(message)
    }
    
    func didConnectWebRTC() {
        print("didConnectWebRTC")
    }
    
    func didDisconnectWebRTC() {
        print("didDisconnectWebRTC")
    }
}

// MARK: - CameraSessionDelegate
extension CallViewController {
    func didOutput(_ sampleBuffer: CMSampleBuffer) {
        if useCustomCapturer {
            webRTCClient.captureCurrentFrame(sampleBuffer: sampleBuffer)
        }
    }
}
