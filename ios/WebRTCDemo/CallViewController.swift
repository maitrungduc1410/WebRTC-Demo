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
import FlexLayout
import PinLayout
import ReplayKit

let SERVER_URL = "http://192.168.1.79:4000"

class CallViewController: UIViewController, WebRTCClientDelegate, CameraSessionDelegate {
    var roomId: String = ""
    let manager = SocketManager(socketURL: URL(string: SERVER_URL)!, config: [.log(true), .compress])
    var socket: SocketIOClient!
    var webRTCClient: WebRTCClient!
    var useCustomCapturer: Bool = false
    var cameraSession: CameraSession?
    
    // Create the container view for FlexLayout
    private let flexContainer = UIView()
    // Add buttons to the stack view
    private let buttonIcons = ["mic.slash.fill", "video.slash.fill", "arrow.triangle.2.circlepath.camera.fill", "speaker.wave.3.fill", "exclamationmark.bubble.fill", "tv.fill", "phone.down.fill"]
    
    private var videoEnabled = true
    private var audioEnabled = true
    private var isSpeakerOn = false
    private var dataChannelReady = false
    private var isScreenSharing = false;
    private var remoteVideoSize: CGSize?
    private var isRemoteStreamAdded = false
    
    private var broadcastPicker: RPSystemBroadcastPickerView = {
        let view = RPSystemBroadcastPickerView()
        view.preferredExtension = "com.ducmai.example.WebRTCDemo.WebRTCDemoScreenBroadcast"  // Replace with your extension's bundle ID
        view.showsMicrophoneButton = false // Hide microphone button if not needed
        view.isHidden = true // Hide the picker UI
        return view
    }()
    
    
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
        
        view.addSubview(flexContainer)
        
        // Setup flexContainer layout
        flexContainer.flex.direction(.row).wrap(.wrap).justifyContent(.center).alignItems(.center).gap(8).define { flex in
            for (index, icon) in buttonIcons.enumerated() {
                let button = createButton(iconName: icon, tag: index)
                
                // Custom background for the "phone.down.fill" button (last one)
                if icon == "phone.down.fill" {
                    button.backgroundColor = .red
                }
                
                flex.addItem(button).size(48)
            }
        }
        
        view.addSubview(broadcastPicker)
    }
    
    // Create a button with an icon
    private func createButton(iconName: String, tag: Int) -> UIButton {
        let button = UIButton(type: .custom)
        button.setImage(UIImage(systemName: iconName), for: .normal)
        
        // Set the image color to white
        button.tintColor = .white
        
        // Set button background color to transparent (except for phone_down which will get red later)
        button.backgroundColor = .clear
        
        // Set circular border
        button.layer.borderColor = UIColor.white.cgColor
        button.layer.borderWidth = 1
        button.layer.cornerRadius = 24 // Half of the button size (48/2 = 24)
        
        // Set tag for identifying the button in action method
        
        button.tag = tag
        
        if tag == 4 { // disable message button
            button.isEnabled = false
        }
        
        // Add target for button tap event
        button.addTarget(self, action: #selector(buttonTapped(_:)), for: .touchUpInside)
        
        
        return button
    }
    
    // Handle button tap events
    @objc private func buttonTapped(_ sender: UIButton) {
        guard let webRTCClient = self.webRTCClient else { return }
        switch sender.tag {
        case 0:
            print("Mic button tapped")
            // Handle mic button tap
            
            webRTCClient.toggleAudio(enable: !audioEnabled)
            audioEnabled = !audioEnabled
            sender.setImage(UIImage(systemName: audioEnabled ? "mic.slash.fill" : "mic.fill"), for: .normal)
        case 1:
            webRTCClient.toggleVideo(enable: !videoEnabled)
            videoEnabled = !videoEnabled
            sender.setImage(UIImage(systemName: videoEnabled ? "video.slash.fill" : "video.fill"), for: .normal)
        case 2:
            print("Camera switch button tapped")
            // Handle camera switch button tap
            webRTCClient.switchCamera()
        case 3:
            print("Speaker button tapped")
            // Handle speaker button tap
            
            isSpeakerOn.toggle()
            let audioSession = AVAudioSession.sharedInstance()
            do {
                if isSpeakerOn {
                    // Route to speaker
                    try audioSession.overrideOutputAudioPort(.speaker)
                } else {
                    // Route to internal microphone (default)
                    try audioSession.overrideOutputAudioPort(.none)
                }
                print("Toggled audio output: \(isSpeakerOn ? "Speaker" : "Microphone")")
                
                sender.setImage(UIImage(systemName: isSpeakerOn ? "speaker.slash.fill" : "speaker.wave.3.fill"), for: .normal)
            } catch {
                print("Failed to toggle audio output: \(error)")
            }
        case 4:
            print("Message button tapped")
            // Handle message button tap
            
            if dataChannelReady {
                let alertController = UIAlertController(title: "Send Message", message: "", preferredStyle: .alert)
                
                // Add a text field to the alert
                alertController.addTextField { (textField) in
                    textField.placeholder = "Message..."
                }
                
                // "Send" action
                let sendAction = UIAlertAction(title: "Send", style: .default) { [weak alertController] _ in
                    if let textField = alertController?.textFields?.first, let text = textField.text {
                        // Handle the input text
                        print("Message: \(text)")
                        webRTCClient.sendDataChannelMessage(message: text)
                        // You can add your logic for handling the message here
                    }
                }
                
                // "Close" action
                let closeAction = UIAlertAction(title: "Close", style: .cancel, handler: nil)
                
                // Add the actions to the alert
                alertController.addAction(sendAction)
                alertController.addAction(closeAction)
                
                // Present the alert controller
                self.present(alertController, animated: true, completion: nil)
            } else {
                webRTCClient.createDataChannel(dataChannelName: "MyApp Channel", onSuccess: { (offerSDP: RTCSessionDescription) -> Void in
                    self.sendSDP(sessionDescription: offerSDP)
                })
            }
        case 5:
            print("Share screen button tapped")
            // Handle share screen button tap
            
            // TODO: for now we can only share screen of the app only, when go back to homescreen it won't work
            
            // Programmatically trigger the RPSystemBroadcastPickerView's button tap
            // for view in broadcastPicker.subviews {
            //     if let button = view as? UIButton {
            //         button.sendActions(for: .allTouchEvents) // Simulate button tap
            //     }
            // }
            
//            if isScreenSharing {
//                webRTCClient.stopCapture()
//                
//                isScreenSharing = false
//            } else {
//                webRTCClient.startCapture(onSuccess: { (offerSDP: RTCSessionDescription) -> Void in
//                    self.sendSDP(sessionDescription: offerSDP)
//                    self.isScreenSharing = true
//                })
//            }
//            webRTCClient.createDeviceCapture(isScreencast: !isScreenSharing)
        case 6:
            print("Hang up button tapped")
            // Handle hang up button tap
            navigationController?.popViewController(animated: true)
        default:
            break
        }
    }
    
    
    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        
        flexContainer.pin.horizontally(16) // margin horizontally
        flexContainer.flex.layout(mode: .adjustHeight) // make the view layout first
        flexContainer.pin.bottom(view.safeAreaInsets.bottom) // then update its bottom (don't do this before the view finish layouting)
    }
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
    
    func onDataChannelMessage(message: String) {
        print("onDataChannelMessage:--------", message)
        
        DispatchQueue.main.async {
            self.showToast(message: "Received message: \(message)")
        }
    }
    
    func onDataChannelStateChange(state: RTCDataChannelState) {
        dataChannelReady = state == RTCDataChannelState.open
        
        DispatchQueue.main.async {
            self.showToast(message: self.dataChannelReady ? "Data channel ready" : "Data channel closed")
            if let messageButton = self.flexContainer.viewWithTag(4) as? UIButton {
                messageButton.setImage(UIImage(systemName: self.dataChannelReady ? "checkmark.bubble.fill" : "exclamationmark.bubble.fill"), for: .normal)
            }
        }
    }
    
    func onPeersConnectionStatusChange(connected: Bool) {
        DispatchQueue.main.async {
            if let messageButton = self.flexContainer.viewWithTag(4) as? UIButton {
                messageButton.isEnabled = connected
            }
        }
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

extension CallViewController {
    func showToast(message: String, duration: TimeInterval = 3.0) {
        let toastLabel = UILabel()
        toastLabel.text = message
        toastLabel.textAlignment = .center
        toastLabel.font = UIFont.systemFont(ofSize: 14)
        toastLabel.numberOfLines = 0
        
        // Consistent style for both dark and light themes
        toastLabel.textColor = .white
        toastLabel.backgroundColor = UIColor.darkGray.withAlphaComponent(0.75) // Semi-transparent dark background
        
        // Styling: Rounded corners on both left and right sides
        toastLabel.layer.cornerRadius = 20
        toastLabel.clipsToBounds = true
        
        // Set the size and position
        let maxSize = CGSize(width: self.view.bounds.size.width - 40, height: self.view.bounds.size.height)
        var expectedSize = toastLabel.sizeThatFits(maxSize)
        expectedSize.width = min(maxSize.width, expectedSize.width)
        expectedSize.height = min(maxSize.height, expectedSize.height)
        
        // Set the frame for the toast
        toastLabel.frame = CGRect(x: 0, y: 0, width: expectedSize.width + 40, height: expectedSize.height + 20)
        toastLabel.center = CGPoint(x: self.view.center.x, y: self.view.frame.size.height - 160)
        
        self.view.addSubview(toastLabel)
        
        // Fade in animation
        toastLabel.alpha = 0.0
        UIView.animate(withDuration: 0.5, animations: {
            toastLabel.alpha = 1.0
        }) { (isCompleted) in
            // Fade out after the duration
            UIView.animate(withDuration: 0.5, delay: duration, options: .curveEaseOut, animations: {
                toastLabel.alpha = 0.0
            }) { (isCompleted) in
                toastLabel.removeFromSuperview()
            }
        }
    }
}
