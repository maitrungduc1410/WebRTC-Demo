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
import ReplayKit
import AVFoundation
import PhotosUI
import UniformTypeIdentifiers

let SERVER_URL = "http://192.168.0.10:4000"

class CallViewController: UIViewController, WebRTCClientDelegate, UITextFieldDelegate {
    var roomId: String = ""
    let manager = SocketManager(socketURL: URL(string: SERVER_URL)!, config: [.log(true), .compress])
    var socket: SocketIOClient!
    var webRTCClient: WebRTCClient!
    var useCustomCapturer: Bool = false
    
    // UI State
    private var videoEnabled = true
    private var audioEnabled = true
    private var isSpeakerOn = false
    private var dataChannelReady = false
    private var isScreenSharing = false
    private var isVideoFileSharing = false
    private var peersConnected = false
    
    // UI Elements
    private let remoteVideoContainer = UIView()
    private let gradientOverlayTop = UIView()
    private let gradientOverlayBottom = UIView()
    
    // Top Bar
    private let topBar = UIView()
    private let topBarStack = UIStackView()
    private let centerInfoStack = UIStackView()
    private let roomIdLabel = UILabel()
    private let statusLabel = UILabel()
    private let statusContainer = UIView()
    private let connectionDot = UIView()
    private let switchCameraButton = UIButton(type: .system)
    private let spacerView = UIView()
    
    // Local Video
    private let localVideoContainer = UIView()
    private let localMuteIndicator = UIImageView()
    
    // Bottom Controls
    private let bottomControlsContainer = UIView()
    private let secondaryControlsStack = UIStackView()
    private let primaryControlsContainer = UIView()
    private let glassPanelView = UIView()
    private let primaryControlsStack = UIStackView()
    
    // Secondary Control Containers
    private let chatContainer = UIView()
    private let chatButton = UIButton(type: .system)
    private let chatLabel = UILabel()
    
    private let shareContainer = UIView()
    private let shareButton = UIButton(type: .system)
    private let shareLabel = UILabel()
    
    private let speakerContainer = UIView()
    private let speakerButton = UIButton(type: .system)
    private let speakerLabel = UILabel()
    
    // Primary Control Containers
    private let muteContainer = UIView()
    private let muteButton = UIButton(type: .system)
    private let muteLabel = UILabel()
    
    private let endContainer = UIView()
    private let endButton = UIButton(type: .system)
    private let endLabel = UILabel()
    
    private let videoContainer = UIView()
    private let videoButton = UIButton(type: .system)
    private let videoLabel = UILabel()
    
    // Messages
    private var messages: [Message] = []
    private let messagesOverlay = UITableView()
    
    // Message Bottom Sheet
    private var messageBottomSheet: UIView?
    private var bottomSheetTableView: UITableView?
    private var messageInputField: UITextField?
    private var bottomSheetBackdrop: UIView?
    private var initialSheetY: CGFloat = 0
    private var isBottomSheetDismissing = false
    
    // Dragging
    private var initialLocalVideoFrame: CGRect = .zero
    private var localVideoTopConstraint: NSLayoutConstraint!
    private var localVideoLeadingConstraint: NSLayoutConstraint?
    private var localVideoTrailingConstraint: NSLayoutConstraint?
    
    struct Message {
        let sender: String
        let text: String
        let timestamp: Date
        let isLocal: Bool
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        // Hide navigation bar and disable swipe back gesture
        navigationController?.setNavigationBarHidden(true, animated: false)
        navigationController?.interactivePopGestureRecognizer?.isEnabled = false
        
        #if targetEnvironment(simulator)
            useCustomCapturer = false
        #endif
        
        socket = manager.defaultSocket
        
        setupSocketHandlers()
        setupUI()
        
        // Keyboard notifications
        NotificationCenter.default.addObserver(self, selector: #selector(keyboardWillShow), name: UIResponder.keyboardWillShowNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(keyboardWillHide), name: UIResponder.keyboardWillHideNotification, object: nil)
        
        socket.connect()
    }
    
    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        navigationController?.setNavigationBarHidden(true, animated: animated)
    }
    
    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        socket.disconnect()
        webRTCClient?.disconnect()
        
        NotificationCenter.default.removeObserver(self, name: UIResponder.keyboardWillShowNotification, object: nil)
        NotificationCenter.default.removeObserver(self, name: UIResponder.keyboardWillHideNotification, object: nil)
    }
    
    // MARK: - Socket Setup
    private func setupSocketHandlers() {
        socket.on(clientEvent: .connect) { [weak self] data, ack in
            guard let self = self else { return }
            print("socket connected")
            
            let payload: [String: Any] = ["roomId": self.roomId]
            self.socket.emit("join room", payload)
            
            self.webRTCClient = WebRTCClient()
            self.webRTCClient.delegate = self
            self.webRTCClient.setup(videoTrack: true, audioTrack: true, customFrameCapturer: self.useCustomCapturer)
            self.setupVideoViews()
        }
        
        socket.on(clientEvent: .disconnect) { data, ack in
            print("socket disconnected")
        }
        
        socket.on("new user joined") { [weak self] data, ack in
            self?.webRTCClient.connect(onSuccess: { offerSDP in
                self?.sendSDP(sessionDescription: offerSDP)
            })
        }
        
        socket.on("offer") { [weak self] data, ack in
            guard let payload = data as? [[String: [String: Any]]],
                  let sdp = payload[0]["offer"]?["sdp"] as? String else { return }
            
            let offerSDP = RTCSessionDescription(type: .offer, sdp: sdp)
            self?.webRTCClient.receiveOffer(offerSDP: offerSDP, onCreateAnswer: { answerSDP in
                self?.sendSDP(sessionDescription: answerSDP)
            })
        }
        
        socket.on("answer") { [weak self] data, ack in
            guard let payload = data as? [[String: [String: Any]]],
                  let sdp = payload[0]["answer"]?["sdp"] as? String else { return }
            
            let answerSDP = RTCSessionDescription(type: .answer, sdp: sdp)
            self?.webRTCClient.receiveAnswer(answerSDP: answerSDP)
        }
        
        socket.on("new ice candidate") { [weak self] data, ack in
            guard let payload = data as? [[String: [String: Any]]],
                  let iceCandidate = payload[0]["iceCandidate"],
                  let candidate = iceCandidate["candidate"] as? String,
                  let sdpMLineIndex = iceCandidate["sdpMLineIndex"] as? Int32 else { return }
            
            self?.webRTCClient.receiveCandidate(
                candidate: RTCIceCandidate(
                    sdp: candidate,
                    sdpMLineIndex: sdpMLineIndex,
                    sdpMid: iceCandidate["sdpMid"] as? String
                )
            )
        }
    }
    
    // MARK: - UI Setup
    private func setupUI() {
        view.backgroundColor = UIColor(red: 0.11, green: 0.13, blue: 0.18, alpha: 1.0)
        
        setupRemoteVideo()
        setupGradients()
        setupTopBar()
        setupLocalVideo()
        setupBottomControls()
        setupMessagesOverlay()
        
        setupConstraints()
    }
    
    private func setupRemoteVideo() {
        remoteVideoContainer.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(remoteVideoContainer)
    }
    
    private func setupGradients() {
        // Top gradient
        gradientOverlayTop.translatesAutoresizingMaskIntoConstraints = false
        let topGradient = CAGradientLayer()
        topGradient.colors = [
            UIColor.black.withAlphaComponent(0.6).cgColor,
            UIColor.clear.cgColor
        ]
        topGradient.locations = [0, 1]
        topGradient.frame = CGRect(x: 0, y: 0, width: UIScreen.main.bounds.width, height: 200)
        gradientOverlayTop.layer.addSublayer(topGradient)
        view.addSubview(gradientOverlayTop)
        
        // Bottom gradient
        gradientOverlayBottom.translatesAutoresizingMaskIntoConstraints = false
        let bottomGradient = CAGradientLayer()
        bottomGradient.colors = [
            UIColor.clear.cgColor,
            UIColor.black.withAlphaComponent(0.6).cgColor
        ]
        bottomGradient.locations = [0, 1]
        bottomGradient.frame = CGRect(x: 0, y: 0, width: UIScreen.main.bounds.width, height: 250)
        gradientOverlayBottom.layer.addSublayer(bottomGradient)
        view.addSubview(gradientOverlayBottom)
    }
    
    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        
        // Update gradient frames
        if let topGradient = gradientOverlayTop.layer.sublayers?.first as? CAGradientLayer {
            topGradient.frame = gradientOverlayTop.bounds
        }
        if let bottomGradient = gradientOverlayBottom.layer.sublayers?.first as? CAGradientLayer {
            bottomGradient.frame = gradientOverlayBottom.bounds
        }
    }
    
    private func setupTopBar() {
        topBar.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(topBar)
        
        // Spacer
        spacerView.translatesAutoresizingMaskIntoConstraints = false
        
        // Room ID label
        roomIdLabel.text = "Room: \(roomId)"
        roomIdLabel.font = UIFont.systemFont(ofSize: 18, weight: .bold)
        roomIdLabel.textColor = .white
        roomIdLabel.textAlignment = .center
        
        // Status container with connection dot
        statusContainer.translatesAutoresizingMaskIntoConstraints = false
        
        connectionDot.backgroundColor = UIColor(red: 0.13, green: 0.80, blue: 0.47, alpha: 1.0)
        connectionDot.layer.cornerRadius = 4
        connectionDot.isHidden = true
        connectionDot.translatesAutoresizingMaskIntoConstraints = false
        statusContainer.addSubview(connectionDot)
        
        statusLabel.text = "Connecting..."
        statusLabel.font = UIFont.systemFont(ofSize: 12, weight: .medium)
        statusLabel.textColor = UIColor.white.withAlphaComponent(0.8)
        statusLabel.translatesAutoresizingMaskIntoConstraints = false
        statusContainer.addSubview(statusLabel)
        
        NSLayoutConstraint.activate([
            connectionDot.leadingAnchor.constraint(equalTo: statusContainer.leadingAnchor),
            connectionDot.centerYAnchor.constraint(equalTo: statusContainer.centerYAnchor),
            connectionDot.widthAnchor.constraint(equalToConstant: 8),
            connectionDot.heightAnchor.constraint(equalToConstant: 8),
            
            statusLabel.leadingAnchor.constraint(equalTo: connectionDot.trailingAnchor, constant: 8),
            statusLabel.centerYAnchor.constraint(equalTo: statusContainer.centerYAnchor),
            statusLabel.trailingAnchor.constraint(equalTo: statusContainer.trailingAnchor),
            statusLabel.topAnchor.constraint(equalTo: statusContainer.topAnchor),
            statusLabel.bottomAnchor.constraint(equalTo: statusContainer.bottomAnchor)
        ])
        
        // Center info stack
        centerInfoStack.axis = .vertical
        centerInfoStack.alignment = .center
        centerInfoStack.spacing = 4
        centerInfoStack.addArrangedSubview(roomIdLabel)
        centerInfoStack.addArrangedSubview(statusContainer)
        centerInfoStack.translatesAutoresizingMaskIntoConstraints = false
        
        // Switch camera button
        let switchCameraButtonConfig = UIImage.SymbolConfiguration(pointSize: 18, weight: .regular)
        switchCameraButton.setImage(UIImage(systemName: "arrow.triangle.2.circlepath.camera.fill", withConfiguration: switchCameraButtonConfig), for: .normal)
        switchCameraButton.tintColor = .white
        switchCameraButton.backgroundColor = .clear
        switchCameraButton.addTarget(self, action: #selector(switchCameraTapped), for: .touchUpInside)
        switchCameraButton.translatesAutoresizingMaskIntoConstraints = false
        
        // Top bar stack
        topBarStack.axis = .horizontal
        topBarStack.alignment = .center
        topBarStack.distribution = .equalSpacing
        topBarStack.translatesAutoresizingMaskIntoConstraints = false
        topBar.addSubview(topBarStack)
        
        topBarStack.addArrangedSubview(spacerView)
        topBarStack.addArrangedSubview(centerInfoStack)
        topBarStack.addArrangedSubview(switchCameraButton)
        
        NSLayoutConstraint.activate([
            spacerView.widthAnchor.constraint(equalToConstant: 40),
            spacerView.heightAnchor.constraint(equalToConstant: 40)
        ])
    }
    
    private func setupLocalVideo() {
        localVideoContainer.backgroundColor = UIColor(red: 0.26, green: 0.29, blue: 0.33, alpha: 1.0)
        localVideoContainer.layer.cornerRadius = 12
        localVideoContainer.layer.borderWidth = 2
        localVideoContainer.layer.borderColor = UIColor.white.withAlphaComponent(0.2).cgColor
        localVideoContainer.clipsToBounds = true
        localVideoContainer.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(localVideoContainer)
        
        // Add mute indicator
        localMuteIndicator.image = UIImage(systemName: "mic.slash.fill")
        localMuteIndicator.tintColor = .white
        localMuteIndicator.backgroundColor = UIColor.red.withAlphaComponent(0.9)
        localMuteIndicator.layer.cornerRadius = 10
        localMuteIndicator.clipsToBounds = true
        localMuteIndicator.contentMode = .center
        localMuteIndicator.isHidden = true
        localMuteIndicator.translatesAutoresizingMaskIntoConstraints = false
        localVideoContainer.addSubview(localMuteIndicator)
        
        NSLayoutConstraint.activate([
            localMuteIndicator.widthAnchor.constraint(equalToConstant: 20),
            localMuteIndicator.heightAnchor.constraint(equalToConstant: 20),
            localMuteIndicator.trailingAnchor.constraint(equalTo: localVideoContainer.trailingAnchor, constant: -8),
            localMuteIndicator.bottomAnchor.constraint(equalTo: localVideoContainer.bottomAnchor, constant: -8)
        ])
        
        // Setup dragging
        let panGesture = UIPanGestureRecognizer(target: self, action: #selector(handlePan(_:)))
        localVideoContainer.addGestureRecognizer(panGesture)
    }
    
    private func setupBottomControls() {
        bottomControlsContainer.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(bottomControlsContainer)
        
        // Secondary controls stack (Chat, Share, Speaker)
        secondaryControlsStack.axis = .horizontal
        secondaryControlsStack.distribution = .equalCentering
        secondaryControlsStack.spacing = 60
        secondaryControlsStack.translatesAutoresizingMaskIntoConstraints = false
        bottomControlsContainer.addSubview(secondaryControlsStack)
        
        // Chat
        setupSecondaryControl(
            container: chatContainer,
            button: chatButton,
            label: chatLabel,
            icon: "exclamationmark.bubble.fill",
            title: "Chat",
            action: #selector(chatButtonTapped)
        )
        chatButton.isEnabled = false
        secondaryControlsStack.addArrangedSubview(chatContainer)
        
        // Share
        setupSecondaryControl(
            container: shareContainer,
            button: shareButton,
            label: shareLabel,
            icon: "tv.fill",
            title: "Share",
            action: #selector(shareButtonTapped)
        )
        secondaryControlsStack.addArrangedSubview(shareContainer)
        
        // Speaker
        setupSecondaryControl(
            container: speakerContainer,
            button: speakerButton,
            label: speakerLabel,
            icon: "speaker.wave.3.fill",
            title: "Speaker",
            action: #selector(speakerButtonTapped)
        )
        secondaryControlsStack.addArrangedSubview(speakerContainer)
        
        // Glass panel for primary controls
        glassPanelView.backgroundColor = UIColor(red: 0.06, green: 0.1, blue: 0.13, alpha: 0.75)
        glassPanelView.layer.cornerRadius = 24
        glassPanelView.layer.borderWidth = 1
        glassPanelView.layer.borderColor = UIColor.white.withAlphaComponent(0.05).cgColor
        glassPanelView.translatesAutoresizingMaskIntoConstraints = false
        bottomControlsContainer.addSubview(glassPanelView)
        
        // Primary controls stack (Mute, End, Video)
        primaryControlsStack.axis = .horizontal
        primaryControlsStack.distribution = .equalSpacing
        primaryControlsStack.spacing = 90
        primaryControlsStack.translatesAutoresizingMaskIntoConstraints = false
        glassPanelView.addSubview(primaryControlsStack)
        
        // Mute
        setupPrimaryControl(
            container: muteContainer,
            button: muteButton,
            label: muteLabel,
            icon: "mic.fill",
            title: "Mute",
            size: 48,
            action: #selector(muteButtonTapped)
        )
        primaryControlsStack.addArrangedSubview(muteContainer)
        
        // End
        setupPrimaryControl(
            container: endContainer,
            button: endButton,
            label: endLabel,
            icon: "phone.down.fill",
            title: "End",
            size: 56,
            backgroundColor: .systemRed,
            action: #selector(endButtonTapped)
        )
        endLabel.font = UIFont.systemFont(ofSize: 10, weight: .bold)
        endLabel.textColor = .white
        primaryControlsStack.addArrangedSubview(endContainer)
        
        // Video
        setupPrimaryControl(
            container: videoContainer,
            button: videoButton,
            label: videoLabel,
            icon: "video.fill",
            title: "Video",
            size: 48,
            action: #selector(videoButtonTapped)
        )
        primaryControlsStack.addArrangedSubview(videoContainer)
    }
    
    private func setupSecondaryControl(container: UIView, button: UIButton, label: UILabel, icon: String, title: String, action: Selector) {
        container.translatesAutoresizingMaskIntoConstraints = false
        
        let config = UIImage.SymbolConfiguration(pointSize: 18, weight: .regular)
        button.setImage(UIImage(systemName: icon, withConfiguration: config), for: .normal)
        button.tintColor = .white
        button.backgroundColor = UIColor(red: 0.06, green: 0.1, blue: 0.13, alpha: 0.75)
        button.layer.cornerRadius = 28
        button.layer.borderWidth = 1
        button.layer.borderColor = UIColor.white.withAlphaComponent(0.05).cgColor
        button.contentEdgeInsets = UIEdgeInsets(top: 14, left: 14, bottom: 14, right: 14)
        button.addTarget(self, action: action, for: .touchUpInside)
        button.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(button)
        
        label.text = title
        label.font = UIFont.systemFont(ofSize: 10, weight: .medium)
        label.textColor = UIColor.white.withAlphaComponent(0.8)
        label.textAlignment = .center
        label.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(label)
        
        NSLayoutConstraint.activate([
            button.topAnchor.constraint(equalTo: container.topAnchor),
            button.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            button.widthAnchor.constraint(equalToConstant: 56),
            button.heightAnchor.constraint(equalToConstant: 56),
            
            label.topAnchor.constraint(equalTo: button.bottomAnchor, constant: 8),
            label.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            label.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            label.bottomAnchor.constraint(equalTo: container.bottomAnchor)
        ])
    }
    
    private func setupPrimaryControl(container: UIView, button: UIButton, label: UILabel, icon: String, title: String, size: CGFloat, backgroundColor: UIColor = UIColor.white.withAlphaComponent(0.1), action: Selector) {
        container.translatesAutoresizingMaskIntoConstraints = false
        
        let config = UIImage.SymbolConfiguration(pointSize: 18, weight: .regular)
        button.setImage(UIImage(systemName: icon, withConfiguration: config), for: .normal)
        button.tintColor = .white
        button.backgroundColor = backgroundColor
        button.layer.cornerRadius = size / 2
        button.addTarget(self, action: action, for: .touchUpInside)
        button.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(button)
        
        label.text = title
        label.font = UIFont.systemFont(ofSize: 10, weight: .medium)
        label.textColor = UIColor.white.withAlphaComponent(0.7)
        label.textAlignment = .center
        label.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(label)
        
        NSLayoutConstraint.activate([
            button.topAnchor.constraint(equalTo: container.topAnchor),
            button.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            button.widthAnchor.constraint(equalToConstant: size),
            button.heightAnchor.constraint(equalToConstant: size),
            
            label.topAnchor.constraint(equalTo: button.bottomAnchor, constant: 4),
            label.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            label.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            label.bottomAnchor.constraint(equalTo: container.bottomAnchor)
        ])
    }
    
    private func setupMessagesOverlay() {
        messagesOverlay.backgroundColor = .clear
        messagesOverlay.separatorStyle = .none
        messagesOverlay.register(MessageCell.self, forCellReuseIdentifier: "MessageCell")
        messagesOverlay.dataSource = self
        messagesOverlay.delegate = self
        messagesOverlay.isHidden = true
        messagesOverlay.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(messagesOverlay)
    }
    
    private func setupConstraints() {
        NSLayoutConstraint.activate([
            // Remote video (full screen)
            remoteVideoContainer.topAnchor.constraint(equalTo: view.topAnchor),
            remoteVideoContainer.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            remoteVideoContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            remoteVideoContainer.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            
            // Gradient overlays
            gradientOverlayTop.topAnchor.constraint(equalTo: view.topAnchor),
            gradientOverlayTop.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            gradientOverlayTop.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            gradientOverlayTop.heightAnchor.constraint(equalToConstant: 200),
            
            gradientOverlayBottom.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            gradientOverlayBottom.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            gradientOverlayBottom.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            gradientOverlayBottom.heightAnchor.constraint(equalToConstant: 250),
            
            // Top bar
            topBar.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor),
            topBar.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            topBar.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            
            topBarStack.topAnchor.constraint(equalTo: topBar.topAnchor, constant: 16),
            topBarStack.leadingAnchor.constraint(equalTo: topBar.leadingAnchor),
            topBarStack.trailingAnchor.constraint(equalTo: topBar.trailingAnchor),
            topBarStack.bottomAnchor.constraint(equalTo: topBar.bottomAnchor, constant: -16),
            
            // Local video (top-right corner)
            localVideoContainer.widthAnchor.constraint(equalToConstant: 112),
            localVideoContainer.heightAnchor.constraint(equalToConstant: 160),
            
            // Messages overlay
            messagesOverlay.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            messagesOverlay.topAnchor.constraint(equalTo: topBar.bottomAnchor, constant: 16),
            
            // set messagesOverlay width = 40% screen width
            messagesOverlay.widthAnchor.constraint(equalTo: view.widthAnchor, multiplier: 0.4),
//            messagesOverlay.widthAnchor.constraint(equalToConstant: 280),
            
            
            messagesOverlay.bottomAnchor.constraint(equalTo: bottomControlsContainer.topAnchor, constant: -16),
            
            // Bottom controls container
            bottomControlsContainer.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            bottomControlsContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            bottomControlsContainer.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -16),
            
            // Secondary controls stack - centered and wrap content
            secondaryControlsStack.topAnchor.constraint(equalTo: bottomControlsContainer.topAnchor),
            secondaryControlsStack.centerXAnchor.constraint(equalTo: bottomControlsContainer.centerXAnchor),
            
            // Glass panel
            glassPanelView.topAnchor.constraint(equalTo: secondaryControlsStack.bottomAnchor, constant: 12),
            glassPanelView.centerXAnchor.constraint(equalTo: bottomControlsContainer.centerXAnchor),
            glassPanelView.bottomAnchor.constraint(equalTo: bottomControlsContainer.bottomAnchor),
            glassPanelView.widthAnchor.constraint(equalTo: bottomControlsContainer.widthAnchor, multiplier: 0.95),
            
            // Primary controls stack
            primaryControlsStack.topAnchor.constraint(equalTo: glassPanelView.topAnchor, constant: 32),
            primaryControlsStack.centerXAnchor.constraint(equalTo: glassPanelView.centerXAnchor),
            primaryControlsStack.bottomAnchor.constraint(equalTo: glassPanelView.bottomAnchor, constant: -32)
        ])
        
        // Setup local video positioning constraints separately so we can update them
        localVideoTopConstraint = localVideoContainer.topAnchor.constraint(equalTo: topBar.bottomAnchor, constant: 16)
        localVideoTrailingConstraint = localVideoContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16)
        
        localVideoTopConstraint.isActive = true
        localVideoTrailingConstraint?.isActive = true
    }
    
    private func setupVideoViews() {
        guard let webRTCClient = webRTCClient else { return }
        
        // Setup remote video
        let remoteVideoView = webRTCClient.remoteVideoView()
        webRTCClient.setupRemoteViewFrame(frame: view.bounds)
        remoteVideoView.translatesAutoresizingMaskIntoConstraints = false
        remoteVideoContainer.addSubview(remoteVideoView)
        
        NSLayoutConstraint.activate([
            remoteVideoView.topAnchor.constraint(equalTo: remoteVideoContainer.topAnchor),
            remoteVideoView.leadingAnchor.constraint(equalTo: remoteVideoContainer.leadingAnchor),
            remoteVideoView.trailingAnchor.constraint(equalTo: remoteVideoContainer.trailingAnchor),
            remoteVideoView.bottomAnchor.constraint(equalTo: remoteVideoContainer.bottomAnchor)
        ])
        
        // Setup local video
        let localVideoView = webRTCClient.localVideoView()
        webRTCClient.setupLocalViewFrame(frame: CGRect(x: 0, y: 0, width: 112, height: 160))
        localVideoView.translatesAutoresizingMaskIntoConstraints = false
        
        // Make the RTCEAGLVideoView rounded
        if let rtcVideoView = localVideoView.subviews.last {
            rtcVideoView.layer.cornerRadius = 12
            rtcVideoView.clipsToBounds = true
        }
        
        localVideoContainer.addSubview(localVideoView)
        
        NSLayoutConstraint.activate([
            localVideoView.topAnchor.constraint(equalTo: localVideoContainer.topAnchor),
            localVideoView.leadingAnchor.constraint(equalTo: localVideoContainer.leadingAnchor),
            localVideoView.trailingAnchor.constraint(equalTo: localVideoContainer.trailingAnchor),
            localVideoView.bottomAnchor.constraint(equalTo: localVideoContainer.bottomAnchor)
        ])
        
        // Force layout update to ensure video renders
        localVideoContainer.layoutIfNeeded()
        
        // Bring mute indicator to front
        localVideoContainer.bringSubviewToFront(localMuteIndicator)
    }
    
    // MARK: - Actions
    @objc private func switchCameraTapped() {
        webRTCClient?.switchCamera()
    }
    
    @objc private func chatButtonTapped() {
        if dataChannelReady {
            showMessageBottomSheet()
        } else {
            webRTCClient?.createDataChannel(dataChannelName: "MyApp Channel", onSuccess: { [weak self] offerSDP in
                self?.sendSDP(sessionDescription: offerSDP)
            })
        }
    }
    
    @objc private func shareButtonTapped() {
        // If already sharing, stop immediately
        if isScreenSharing {
            handleScreenShare()
            return
        }
        
        if isVideoFileSharing {
            stopVideoFileSharing()
            return
        }
        
        // Otherwise show the menu
        let alertController = UIAlertController(title: nil, message: "Choose sharing option", preferredStyle: .actionSheet)
        
        // Share Screen option
        let screenShareAction = UIAlertAction(title: "Share Screen", style: .default) { [weak self] _ in
            self?.handleScreenShare()
        }
        alertController.addAction(screenShareAction)
        
        // Share from Photos option
        let photosAction = UIAlertAction(title: "Share from Photos", style: .default) { [weak self] _ in
            self?.presentPhotoPicker()
        }
        alertController.addAction(photosAction)
        
        // Share from Files option
        let filesAction = UIAlertAction(title: "Share from Files", style: .default) { [weak self] _ in
            self?.presentDocumentPicker()
        }
        alertController.addAction(filesAction)
        
        // Cancel option
        let cancelAction = UIAlertAction(title: "Cancel", style: .cancel, handler: nil)
        alertController.addAction(cancelAction)
        
        // For iPad support
        if let popoverController = alertController.popoverPresentationController {
            popoverController.sourceView = shareButton
            popoverController.sourceRect = shareButton.bounds
        }
        
        present(alertController, animated: true, completion: nil)
    }
    
    private func handleScreenShare() {
        if isScreenSharing {
            // Stop screen sharing
            webRTCClient?.stopScreenCapture()
            isScreenSharing = false
            
            let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
            shareButton.setImage(UIImage(systemName: "tv.fill", withConfiguration: config), for: .normal)
            switchCameraButton.isEnabled = true
            showToast(message: "Screen sharing stopped")
        } else {
            // Start screen sharing
            webRTCClient?.startScreenCapture()
            isScreenSharing = true
            isVideoFileSharing = false
            
            let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
            shareButton.setImage(UIImage(systemName: "tv.slash.fill", withConfiguration: config), for: .normal)
            switchCameraButton.isEnabled = false
            showToast(message: "Starting screen sharing...")
        }
    }
    
    private func presentPhotoPicker() {
        var configuration = PHPickerConfiguration()
        configuration.filter = .videos
        configuration.selectionLimit = 1
        
        let picker = PHPickerViewController(configuration: configuration)
        picker.delegate = self
        present(picker, animated: true)
    }
    
    private func presentDocumentPicker() {
        let documentPicker = UIDocumentPickerViewController(forOpeningContentTypes: [.movie, .video, .mpeg4Movie, .quickTimeMovie])
        documentPicker.delegate = self
        documentPicker.allowsMultipleSelection = false
        present(documentPicker, animated: true)
    }
    
    private func shareVideoFile(url: URL) {
        // Stop screen sharing if active
        if isScreenSharing {
            webRTCClient?.stopScreenCapture()
            isScreenSharing = false
        }
        
        // Use RTCFileVideoCapturer - much simpler than AVPlayer approach!
        webRTCClient?.shareVideoFile(fileURL: url)
        
        isVideoFileSharing = true
        isScreenSharing = false
        
        let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
        shareButton.setImage(UIImage(systemName: "tv.slash.fill", withConfiguration: config), for: .normal)
        switchCameraButton.isEnabled = false
        showToast(message: "Sharing video file")
    }
    
    
    private func stopVideoFileSharing() {
        // Stop file capturer via WebRTC client
        webRTCClient?.stopVideoFileSharing()
        
        isVideoFileSharing = false
        
        let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
        shareButton.setImage(UIImage(systemName: "tv.fill", withConfiguration: config), for: .normal)
        switchCameraButton.isEnabled = true
        showToast(message: "Stopped video sharing")
    }
    
    @objc private func speakerButtonTapped() {
        isSpeakerOn.toggle()
        let audioSession = AVAudioSession.sharedInstance()
        
        do {
            if isSpeakerOn {
                try audioSession.overrideOutputAudioPort(.speaker)
            } else {
                try audioSession.overrideOutputAudioPort(.none)
            }
            
            let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
            speakerButton.setImage(UIImage(systemName: isSpeakerOn ? "speaker.slash.fill" : "speaker.wave.3.fill", withConfiguration: config), for: .normal)
        } catch {
            print("Failed to toggle speaker: \(error)")
        }
    }
    
    @objc private func muteButtonTapped() {
        webRTCClient?.toggleAudio(enable: !audioEnabled)
        audioEnabled.toggle()
        
        let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
        muteButton.setImage(UIImage(systemName: audioEnabled ? "mic.fill" : "mic.slash.fill", withConfiguration: config), for: .normal)
        localMuteIndicator.isHidden = audioEnabled
    }
    
    @objc private func videoButtonTapped() {
        webRTCClient?.toggleVideo(enable: !videoEnabled)
        videoEnabled.toggle()
        
        let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
        videoButton.setImage(UIImage(systemName: videoEnabled ? "video.fill" : "video.slash.fill", withConfiguration: config), for: .normal)
        
        // Show black background when video is off (like web)
        localVideoContainer.backgroundColor = videoEnabled ? .clear : .black
    }
    
    @objc private func endButtonTapped() {
        navigationController?.popViewController(animated: true)
    }
    
    @objc private func handlePan(_ gesture: UIPanGestureRecognizer) {
        let translation = gesture.translation(in: view)
        
        switch gesture.state {
        case .began:
            initialLocalVideoFrame = localVideoContainer.frame
            // Deactivate trailing constraint, activate leading instead for absolute positioning
            localVideoTrailingConstraint?.isActive = false
            if localVideoLeadingConstraint == nil {
                localVideoLeadingConstraint = localVideoContainer.leadingAnchor.constraint(equalTo: view.leadingAnchor)
            }
            // Set to current position before activating to prevent flicker
            localVideoLeadingConstraint?.constant = initialLocalVideoFrame.minX
            localVideoLeadingConstraint?.isActive = true
            
        case .changed:
            var newX = initialLocalVideoFrame.origin.x + translation.x
            var newY = initialLocalVideoFrame.origin.y + translation.y
            
            // Constrain X
            let maxX = view.bounds.width - localVideoContainer.bounds.width
            newX = max(0, min(newX, maxX))
            
            // Constrain Y between top bar and bottom controls with 16pt padding from bottom
            let topBarBottom = topBar.frame.maxY
            let bottomControlsTop = bottomControlsContainer.frame.minY - 16
            newY = max(topBarBottom, min(newY, bottomControlsTop - localVideoContainer.bounds.height))
            
            // Update constraints instead of frame
            localVideoLeadingConstraint?.constant = newX
            localVideoTopConstraint.constant = newY - topBar.frame.maxY
            
        case .ended:
            snapToEdge()
            
        default:
            break
        }
    }
    
    private func snapToEdge() {
        let centerX = localVideoContainer.frame.midX
        let screenCenter = view.bounds.width / 2
        
        if centerX < screenCenter {
            // Snap to left
            localVideoLeadingConstraint?.isActive = false
            localVideoTrailingConstraint?.isActive = false
            localVideoLeadingConstraint = localVideoContainer.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16)
            localVideoLeadingConstraint?.isActive = true
        } else {
            // Snap to right
            localVideoLeadingConstraint?.isActive = false
            localVideoTrailingConstraint?.isActive = false
            localVideoTrailingConstraint = localVideoContainer.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16)
            localVideoTrailingConstraint?.isActive = true
        }
        
        UIView.animate(withDuration: 0.3, delay: 0, options: .curveEaseOut) {
            self.view.layoutIfNeeded()
        }
    }
    
    private func showMessageBottomSheet() {
        if messageBottomSheet == nil {
            setupMessageBottomSheet()
        }
        
        guard let backdrop = bottomSheetBackdrop,
              let sheet = messageBottomSheet else { return }
        
        // Add backdrop
        view.addSubview(backdrop)
        view.addSubview(sheet)
        
        // Initial position (off screen)
        sheet.frame.origin.y = view.bounds.height
        backdrop.alpha = 0
        
        // Animate in
        UIView.animate(withDuration: 0.3, delay: 0, options: .curveEaseOut) {
            sheet.frame.origin.y = self.view.bounds.height - sheet.frame.height
            backdrop.alpha = 1
        }
        
        // Scroll to bottom
        if messages.count > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
                guard let self = self else { return }
                let indexPath = IndexPath(row: self.messages.count - 1, section: 0)
                self.bottomSheetTableView?.scrollToRow(at: indexPath, at: .bottom, animated: false)
            }
        }
    }
    
    private func setupMessageBottomSheet() {
        // Backdrop
        let backdrop = UIView(frame: view.bounds)
        backdrop.backgroundColor = UIColor.black.withAlphaComponent(0.5)
        let tapGesture = UITapGestureRecognizer(target: self, action: #selector(dismissMessageBottomSheet))
        backdrop.addGestureRecognizer(tapGesture)
        bottomSheetBackdrop = backdrop
        
        // Bottom sheet container
        let sheetHeight: CGFloat = 500
        let sheet = UIView(frame: CGRect(x: 0, y: 0, width: view.bounds.width, height: sheetHeight))
        sheet.backgroundColor = UIColor(red: 0.11, green: 0.13, blue: 0.18, alpha: 1.0)
        sheet.layer.cornerRadius = 20
        sheet.layer.maskedCorners = [.layerMinXMinYCorner, .layerMaxXMinYCorner]
        messageBottomSheet = sheet
        
        // Add pan gesture for swipe down to dismiss
        let panGesture = UIPanGestureRecognizer(target: self, action: #selector(handleBottomSheetPan(_:)))
        sheet.addGestureRecognizer(panGesture)
        
        // Handle bar
        let handleBar = UIView()
        handleBar.backgroundColor = UIColor.white.withAlphaComponent(0.3)
        handleBar.layer.cornerRadius = 2
        handleBar.translatesAutoresizingMaskIntoConstraints = false
        sheet.addSubview(handleBar)
        
        // Header
        let headerLabel = UILabel()
        headerLabel.text = "In-call messages"
        headerLabel.font = UIFont.systemFont(ofSize: 18, weight: .bold)
        headerLabel.textColor = .white
        headerLabel.translatesAutoresizingMaskIntoConstraints = false
        sheet.addSubview(headerLabel)
        
        // TableView
        let tableView = UITableView()
        tableView.backgroundColor = .clear
        tableView.separatorStyle = .none
        tableView.register(MessageCell.self, forCellReuseIdentifier: "MessageCell")
        tableView.dataSource = self
        tableView.delegate = self
        tableView.translatesAutoresizingMaskIntoConstraints = false
        sheet.addSubview(tableView)
        bottomSheetTableView = tableView
        
        // Input container
        let inputContainer = UIView()
        inputContainer.backgroundColor = UIColor(red: 0.06, green: 0.1, blue: 0.13, alpha: 1.0)
        inputContainer.translatesAutoresizingMaskIntoConstraints = false
        sheet.addSubview(inputContainer)
        
        // Message input
        let messageInput = UITextField()
        messageInput.placeholder = "Type a message..."
        messageInput.textColor = .white
        messageInput.backgroundColor = UIColor.white.withAlphaComponent(0.1)
        messageInput.layer.cornerRadius = 20
        messageInput.returnKeyType = .send
        messageInput.enablesReturnKeyAutomatically = true
        messageInput.delegate = self
        messageInput.leftView = UIView(frame: CGRect(x: 0, y: 0, width: 16, height: 0))
        messageInput.leftViewMode = .always
        messageInput.rightView = UIView(frame: CGRect(x: 0, y: 0, width: 16, height: 0))
        messageInput.rightViewMode = .always
        messageInput.attributedPlaceholder = NSAttributedString(
            string: "Type a message...",
            attributes: [NSAttributedString.Key.foregroundColor: UIColor.white.withAlphaComponent(0.6)]
        )
        messageInput.translatesAutoresizingMaskIntoConstraints = false
        inputContainer.addSubview(messageInput)
        messageInputField = messageInput
        
        // Send button
        let sendButton = UIButton(type: .system)
        let config = UIImage.SymbolConfiguration(pointSize: 18, weight: .semibold)
        sendButton.setImage(UIImage(systemName: "paperplane.fill", withConfiguration: config), for: .normal)
        sendButton.tintColor = .white
        sendButton.backgroundColor = UIColor.systemBlue
        sendButton.layer.cornerRadius = 24
        sendButton.addTarget(self, action: #selector(sendMessageFromBottomSheet), for: .touchUpInside)
        sendButton.translatesAutoresizingMaskIntoConstraints = false
        inputContainer.addSubview(sendButton)
        
        // Layout constraints
        NSLayoutConstraint.activate([
            handleBar.topAnchor.constraint(equalTo: sheet.topAnchor, constant: 8),
            handleBar.centerXAnchor.constraint(equalTo: sheet.centerXAnchor),
            handleBar.widthAnchor.constraint(equalToConstant: 40),
            handleBar.heightAnchor.constraint(equalToConstant: 4),
            
            headerLabel.topAnchor.constraint(equalTo: handleBar.bottomAnchor, constant: 16),
            headerLabel.leadingAnchor.constraint(equalTo: sheet.leadingAnchor, constant: 20),
            
            tableView.topAnchor.constraint(equalTo: headerLabel.bottomAnchor, constant: 12),
            tableView.leadingAnchor.constraint(equalTo: sheet.leadingAnchor),
            tableView.trailingAnchor.constraint(equalTo: sheet.trailingAnchor),
            tableView.bottomAnchor.constraint(equalTo: inputContainer.topAnchor),
            
            inputContainer.leadingAnchor.constraint(equalTo: sheet.leadingAnchor),
            inputContainer.trailingAnchor.constraint(equalTo: sheet.trailingAnchor),
            inputContainer.bottomAnchor.constraint(equalTo: sheet.bottomAnchor),
            inputContainer.heightAnchor.constraint(equalToConstant: 80),
            
            messageInput.leadingAnchor.constraint(equalTo: inputContainer.leadingAnchor, constant: 16),
            messageInput.centerYAnchor.constraint(equalTo: inputContainer.centerYAnchor),
            messageInput.heightAnchor.constraint(equalToConstant: 44),
            
            sendButton.leadingAnchor.constraint(equalTo: messageInput.trailingAnchor, constant: 8),
            sendButton.trailingAnchor.constraint(equalTo: inputContainer.trailingAnchor, constant: -16),
            sendButton.centerYAnchor.constraint(equalTo: inputContainer.centerYAnchor),
            sendButton.widthAnchor.constraint(equalToConstant: 48),
            sendButton.heightAnchor.constraint(equalToConstant: 48)
        ])
    }
    
    @objc private func sendMessageFromBottomSheet() {
        guard let text = messageInputField?.text?.trimmingCharacters(in: .whitespaces),
              !text.isEmpty else { return }
        
        // Send message
        webRTCClient?.sendDataChannelMessage(message: text)
        
        // Add to local messages
        let message = Message(sender: "You", text: text, timestamp: Date(), isLocal: true)
        addMessage(message)
        
        // Clear input
        messageInputField?.text = ""
    }
    
    @objc private func dismissMessageBottomSheet() {
        guard let backdrop = bottomSheetBackdrop,
              let sheet = messageBottomSheet else { return }
        
        // Animate out
        UIView.animate(withDuration: 0.25, delay: 0, options: .curveEaseIn, animations: {
            sheet.frame.origin.y = self.view.bounds.height
            backdrop.alpha = 0
        }) { _ in
            backdrop.removeFromSuperview()
            sheet.removeFromSuperview()
            self.isBottomSheetDismissing = false
        }
        
        // Clear input
        messageInputField?.text = ""
        
        // Dismiss keyboard
        view.endEditing(true)
    }
    
    @objc private func keyboardWillShow(notification: NSNotification) {
        guard let sheet = messageBottomSheet,
              sheet.superview != nil,
              let keyboardFrame = notification.userInfo?[UIResponder.keyboardFrameEndUserInfoKey] as? CGRect,
              let duration = notification.userInfo?[UIResponder.keyboardAnimationDurationUserInfoKey] as? Double else {
            return
        }
        
        let keyboardHeight = keyboardFrame.height
        
        UIView.animate(withDuration: duration) {
            sheet.frame.origin.y = self.view.bounds.height - sheet.frame.height - keyboardHeight
        }
    }
    
    @objc private func keyboardWillHide(notification: NSNotification) {
        guard let sheet = messageBottomSheet,
              sheet.superview != nil,
              !isBottomSheetDismissing,
              let duration = notification.userInfo?[UIResponder.keyboardAnimationDurationUserInfoKey] as? Double else {
            return
        }
        
        UIView.animate(withDuration: duration) {
            sheet.frame.origin.y = self.view.bounds.height - sheet.frame.height
        }
    }
    
    @objc private func handleBottomSheetPan(_ gesture: UIPanGestureRecognizer) {
        guard let sheet = messageBottomSheet else { return }
        
        let translation = gesture.translation(in: view)
        let velocity = gesture.velocity(in: view)
        
        switch gesture.state {
        case .began:
            // Store initial position when pan starts
            initialSheetY = sheet.frame.origin.y
            
        case .changed:
            // Only allow dragging down
            if translation.y > 0 {
                sheet.frame.origin.y = initialSheetY + translation.y
            }
            
        case .ended:
            // Dismiss if dragged down more than 100pt or fast swipe down
            if translation.y > 100 || velocity.y > 1000 {
                isBottomSheetDismissing = true
                dismissMessageBottomSheet()
            } else {
                // Snap back to original position
                UIView.animate(withDuration: 0.3, delay: 0, options: .curveEaseOut) {
                    sheet.frame.origin.y = self.initialSheetY
                }
            }
            
        default:
            break
        }
    }

    private func addMessage(_ message: Message) {
        messages.append(message)
        
        if messages.count > 50 {
            messages.removeFirst()
        }
        
        messagesOverlay.isHidden = false
        messagesOverlay.reloadData()
        
        // Scroll overlay to show latest at bottom
        if !messages.isEmpty {
            messagesOverlay.scrollToRow(at: IndexPath(row: messages.count - 1, section: 0), at: .bottom, animated: true)
        }
        
        // Update bottom sheet if it's showing
        if let tableView = bottomSheetTableView,
           messageBottomSheet?.superview != nil {
            tableView.reloadData()
            
            // Scroll to bottom to show latest
            if !messages.isEmpty {
                let indexPath = IndexPath(row: messages.count - 1, section: 0)
                tableView.scrollToRow(at: indexPath, at: .bottom, animated: true)
            }
        }
    }
    
    // MARK: - Helpers
    private func sendSDP(sessionDescription: RTCSessionDescription) {
        var type = ""
        if sessionDescription.type == .offer {
            type = "offer"
        } else if sessionDescription.type == .answer {
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
    
    private func sendCandidate(iceCandidate: RTCIceCandidate) {
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
    
    func showToast(message: String, duration: TimeInterval = 3.0) {
        let toastLabel = UILabel()
        toastLabel.text = message
        toastLabel.textAlignment = .center
        toastLabel.font = UIFont.systemFont(ofSize: 14)
        toastLabel.numberOfLines = 0
        toastLabel.textColor = .white
        toastLabel.backgroundColor = UIColor.darkGray.withAlphaComponent(0.75)
        toastLabel.layer.cornerRadius = 20
        toastLabel.clipsToBounds = true
        
        let maxSize = CGSize(width: view.bounds.width - 40, height: view.bounds.height)
        var expectedSize = toastLabel.sizeThatFits(maxSize)
        expectedSize.width = min(maxSize.width, expectedSize.width)
        expectedSize.height = min(maxSize.height, expectedSize.height)
        
        toastLabel.frame = CGRect(x: 0, y: 0, width: expectedSize.width + 40, height: expectedSize.height + 20)
        toastLabel.center = CGPoint(x: view.center.x, y: view.frame.height - 160)
        
        view.addSubview(toastLabel)
        
        toastLabel.alpha = 0.0
        UIView.animate(withDuration: 0.5, animations: {
            toastLabel.alpha = 1.0
        }) { _ in
            UIView.animate(withDuration: 0.5, delay: duration, options: .curveEaseOut, animations: {
                toastLabel.alpha = 0.0
            }) { _ in
                toastLabel.removeFromSuperview()
            }
        }
    }
}

// MARK: - WebRTC Delegate
extension CallViewController {
    func didGenerateCandidate(iceCandidate: RTCIceCandidate) {
        sendCandidate(iceCandidate: iceCandidate)
    }
    
    func didIceConnectionStateChanged(iceConnectionState: RTCIceConnectionState) {
        print("ICE Connection State: \(iceConnectionState)")
    }
    
    func didReceiveMessage(message: String) {
        print("Received message: \(message)")
    }
    
    func didConnectWebRTC() {
        DispatchQueue.main.async {
            self.peersConnected = true
            self.statusLabel.text = "Connected"
            self.connectionDot.isHidden = false
            
            // Start pulsing animation
            let pulseAnimation = CABasicAnimation(keyPath: "opacity")
            pulseAnimation.fromValue = 1.0
            pulseAnimation.toValue = 0.3
            pulseAnimation.duration = 1.0
            pulseAnimation.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            pulseAnimation.autoreverses = true
            pulseAnimation.repeatCount = .infinity
            self.connectionDot.layer.add(pulseAnimation, forKey: "pulse")
        }
    }
    
    func didDisconnectWebRTC() {
        DispatchQueue.main.async {
            self.peersConnected = false
            self.statusLabel.text = "Disconnected"
            self.connectionDot.isHidden = true
            self.connectionDot.layer.removeAllAnimations()
        }
    }
    
    func onDataChannelMessage(message: String) {
        let msg = Message(sender: "Remote", text: message, timestamp: Date(), isLocal: false)
        DispatchQueue.main.async {
            self.addMessage(msg)
        }
    }
    
    func onDataChannelStateChange(state: RTCDataChannelState) {
        dataChannelReady = state == .open
        
        DispatchQueue.main.async {
            self.showToast(message: self.dataChannelReady ? "Data channel ready" : "Data channel closed")
            
            let config = UIImage.SymbolConfiguration(pointSize: 20, weight: .regular)
            self.chatButton.setImage(UIImage(systemName: self.dataChannelReady ? "checkmark.bubble.fill" : "exclamationmark.bubble.fill", withConfiguration: config), for: .normal)
        }
    }
    
    func onPeersConnectionStatusChange(connected: Bool) {
        DispatchQueue.main.async {
            self.chatButton.isEnabled = connected
        }
    }
}

// MARK: - Table View Data Source
extension CallViewController: UITableViewDataSource, UITableViewDelegate {
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        return messages.count // Show all messages in both
    }
    
    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: "MessageCell", for: indexPath) as! MessageCell
        cell.configure(with: messages[indexPath.row])
        cell.backgroundColor = .clear
        
        if tableView == messagesOverlay {
            cell.containerView.alpha = 0.7 // Semi-transparent for overlay
        } else {
            cell.containerView.alpha = 1.0 // Full opacity for bottom sheet
        }
        
        return cell
    }
}

// MARK: - UITextFieldDelegate
extension CallViewController {
    func textFieldShouldReturn(_ textField: UITextField) -> Bool {
        // Send message when return key is pressed
        sendMessageFromBottomSheet()
        return true
    }
}

// MARK: - PHPickerViewControllerDelegate
extension CallViewController: PHPickerViewControllerDelegate {
    func picker(_ picker: PHPickerViewController, didFinishPicking results: [PHPickerResult]) {
        picker.dismiss(animated: true)
        
        guard let provider = results.first?.itemProvider,
              provider.hasItemConformingToTypeIdentifier(UTType.movie.identifier) else {
            return
        }
        
        provider.loadFileRepresentation(forTypeIdentifier: UTType.movie.identifier) { [weak self] url, error in
            guard let url = url, error == nil else {
                print("Error loading video: \(error?.localizedDescription ?? "unknown")")
                return
            }
            
            // Copy to Documents directory for persistent storage
            let documentsURL = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            let destinationURL = documentsURL.appendingPathComponent(url.lastPathComponent)
            try? FileManager.default.removeItem(at: destinationURL)
            
            do {
                try FileManager.default.copyItem(at: url, to: destinationURL)
                DispatchQueue.main.async {
                    self?.shareVideoFile(url: destinationURL)
                }
            } catch {
                print("Error copying video file: \(error)")
            }
        }
    }
}

// MARK: - UIDocumentPickerDelegate
extension CallViewController: UIDocumentPickerDelegate {
    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else { return }
        
        // Start accessing security-scoped resource
        guard url.startAccessingSecurityScopedResource() else {
            showToast(message: "Cannot access file")
            return
        }
        
        defer {
            url.stopAccessingSecurityScopedResource()
        }
        
        // Copy to Documents directory for persistent storage
        let documentsURL = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let destinationURL = documentsURL.appendingPathComponent(url.lastPathComponent)
        try? FileManager.default.removeItem(at: destinationURL)
        
        do {
            try FileManager.default.copyItem(at: url, to: destinationURL)
            shareVideoFile(url: destinationURL)
        } catch {
            showToast(message: "Error loading video: \(error.localizedDescription)")
        }
    }
}
