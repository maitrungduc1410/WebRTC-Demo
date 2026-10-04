//
//  LocalMedia.swift
//  WebRTCDemo
//

import AVFoundation
import Foundation
import ReplayKit
import UIKit
import WebRTC

/// Everything on the sending side that the 1:1 engine (`WebRTCClient`) and the group engine
/// (`GroupCallClient`) share: the factory, one audio and one video track, and the single
/// `RTCVideoSource` that the camera (through the effects processor), a video file or the
/// screen broadcast feed in turn. Switching sources never touches a sender, so no renegotiation
/// is needed and the sender's frame cryptor stays in place (ARCHITECTURE.md section 8).
final class LocalMedia {
    let factory: RTCPeerConnectionFactory
    /// Nil when E2EE is off.
    let encryption: FrameEncryption?
    private(set) var videoTrack: RTCVideoTrack?
    private(set) var audioTrack: RTCAudioTrack?

    /// Called on the main queue when the broadcast extension starts or stops sending frames.
    var onScreenShareChanged: ((Bool) -> Void)?

    private var videoSource: RTCVideoSource?
    private var videoCapturer: RTCVideoCapturer!
    private var customFrameCapturer: Bool
    private var useFrontCamera = true
    private var isSwitchingCamera = false

    // Video file sharing properties
    private var fileVideoCapturer: RTCFileVideoCapturer?
    private var isFileSharingActive = false

    // Screen sharing properties
    private var screenCapturer: FlutterBroadcastScreenCapturer?
    private var originalCapturer: RTCVideoCapturer?
    private var isScreenSharing = false
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid
    private var audioSession: AVAudioSession?

    // Backgrounds and stickers (camera frames only)
    private var effectsProcessor: EffectsProcessor?
    private let holdsEffectFrames: Bool

    var isFrontCamera: Bool { useFrontCamera }

    /// `holdEffects`: a saved effect is about to load, so camera frames are dropped until
    /// `setEffects` and the call never starts with the raw camera.
    init(videoTrack: Bool, audioTrack: Bool, customFrameCapturer: Bool, enableE2EE: Bool, holdEffects: Bool = false) {
        print("set up local media")
        self.customFrameCapturer = customFrameCapturer
        holdsEffectFrames = holdEffects
        factory = RTCPeerConnectionFactory(
            encoderFactory: RTCDefaultVideoEncoderFactory(),
            decoderFactory: RTCDefaultVideoDecoderFactory()
        )
        encryption = enableE2EE ? FrameEncryption(factory: factory) : nil

        if videoTrack {
            self.videoTrack = createVideoTrack()
        }
        if audioTrack {
            self.audioTrack = createAudioTrack()
        }

        if videoTrack {
            startCaptureLocalVideo(
                cameraPositon: .front,
                videoWidth: 640,
                videoHeight: 640*16/9,
                videoFps: 30
            )
        }
    }

    deinit {
        print("LocalMedia Deinit")
    }

    /// Stops every capturer. Called when the call ends.
    func stopCapture() {
        (videoCapturer as? RTCCameraVideoCapturer)?.stopCapture()
        (videoCapturer as? RTCFileVideoCapturer)?.stopCapture()
        fileVideoCapturer?.stopCapture()
        fileVideoCapturer = nil
        effectsProcessor?.setScene(nil)
    }

    // MARK: - Peer connections

    func makePeerConnection(delegate: RTCPeerConnectionDelegate?) -> RTCPeerConnection {
        let rtcConf = RTCConfiguration()
        rtcConf.iceServers = [RTCIceServer(
            urlStrings: ["stun:stun.l.google.com:19302"]
        )]
        let mediaConstraints = RTCMediaConstraints.init(
            mandatoryConstraints: nil,
            optionalConstraints: nil
        )
        let pc = factory.peerConnection(
            with: rtcConf,
            constraints: mediaConstraints,
            delegate: delegate
        )
        return pc!
    }

    // VP8 always comes first: H264 uses the VideoToolbox hardware encoder, which iOS invalidates while
    // the app is in the background, so a screen share would freeze as soon as the user leaves the app.
    // VP8 is encoded in software and keeps running. It is also the codec web/Android prefer under E2EE,
    // and the only video codec the SFU accepts.
    func applyVideoCodecPreferences(on peerConnection: RTCPeerConnection) {
        let codecs = factory.rtpReceiverCapabilities(forKind: "video").codecs
        let isVP8: (RTCRtpCodecCapability) -> Bool = { $0.mimeType.lowercased() == "video/vp8" }
        let preferred = codecs.filter(isVP8) + codecs.filter { !isVP8($0) }

        for transceiver in peerConnection.transceivers where transceiver.mediaType == .video {
            transceiver.codecPreferences = preferred
        }
    }

    // MARK: - Tracks

    func toggleVideo(enable: Bool) {
        videoTrack?.isEnabled = enable
    }

    func toggleAudio(enable: Bool) {
        audioTrack?.isEnabled = enable
    }

    private func createAudioTrack() -> RTCAudioTrack {
        let audioConstrains = RTCMediaConstraints(
            mandatoryConstraints: nil,
            optionalConstraints: nil
        )
        let audioSource = factory.audioSource(
            with: audioConstrains
        )
        let audioTrack = factory.audioTrack(
            with: audioSource,
            trackId: "audio0"
        )
        return audioTrack
    }

    private func createVideoTrack() -> RTCVideoTrack {
        videoSource = factory.videoSource()

        if customFrameCapturer {
            videoCapturer = RTCCustomFrameCapturer(delegate: videoSource!)
        }
        else {
            #if targetEnvironment(simulator)
            print("now runnnig on simulator...")
            videoCapturer = RTCFileVideoCapturer(delegate: videoSource!)
            #else
            // The capturer only keeps a weak delegate, so the processor is retained here
            let processor = EffectsProcessor(output: videoSource!)
            if holdsEffectFrames { processor.holdFrames() }
            effectsProcessor = processor
            videoCapturer = RTCCameraVideoCapturer(delegate: processor)
            #endif
        }
        let videoTrack = factory.videoTrack(
            with: videoSource!,
            trackId: "video0"
        )
        return videoTrack
    }

    private func startCaptureLocalVideo(
        cameraPositon: AVCaptureDevice.Position,
        videoWidth: Int,
        videoHeight: Int?,
        videoFps: Int
    ) {
        effectsProcessor?.invalidateAnalysis()
        if let capturer = videoCapturer as? RTCCameraVideoCapturer {
            var targetDevice: AVCaptureDevice?
            var targetFormat: AVCaptureDevice.Format?

            // find target device
            let devices = RTCCameraVideoCapturer.captureDevices()
            devices.forEach { (device) in
                if device.position ==  cameraPositon{
                    targetDevice = device
                }
            }

            // find target format
            let formats = RTCCameraVideoCapturer.supportedFormats(
                for: targetDevice!
            )
            formats.forEach { (format) in
                for _ in format.videoSupportedFrameRateRanges {
                    let description = format.formatDescription as CMFormatDescription
                    let dimensions = CMVideoFormatDescriptionGetDimensions(
                        description
                    )
                    print("found format: ", dimensions.width, dimensions.height)
                    if dimensions.width == videoWidth && dimensions.height == videoHeight ?? 0{
                        targetFormat = format
                    } else if dimensions.width == videoWidth {
                        targetFormat = format
                    }
                }
            }

            // Keeps the camera running while the call is in picture-in-picture; without it iOS
            // pauses capture as soon as the app leaves the foreground
            let session = capturer.captureSession
            if session.isMultitaskingCameraAccessSupported && !session.isMultitaskingCameraAccessEnabled {
                session.beginConfiguration()
                session.isMultitaskingCameraAccessEnabled = true
                session.commitConfiguration()
            }

            capturer.startCapture(with: targetDevice!,
                                  format: targetFormat!,
                                  fps: videoFps)
        } else if let capturer = videoCapturer as? RTCFileVideoCapturer{
            print("setup file video capturer")
            if let _ = Bundle.main.path(
                forResource: "video.mp4",
                ofType: nil
            ) {
                capturer.startCapturing(fromFileNamed: "video.mp4") { (err) in
                    print(err)
                }
            }else{
                print("file did not faund")
            }
        }
    }

    // MARK: - Camera

    /// `completion` receives whether the front camera is now active, on the main queue.
    func switchCamera(completion: ((Bool) -> Void)? = nil) {
        print("switch camera")

        // Prevent multiple simultaneous switches
        guard !isSwitchingCamera else {
            print("Camera switch already in progress")
            return
        }

        guard let capturer = videoCapturer as? RTCCameraVideoCapturer else {
            print("Camera capturer not available")
            return
        }

        // Don't switch if screen sharing is active
        guard !isScreenSharing else {
            print("Cannot switch camera while screen sharing")
            return
        }

        isSwitchingCamera = true

        // Stop current capture and start new one
        capturer.stopCapture { [weak self] in
            guard let self = self else { return }
            useFrontCamera.toggle()
            let newPosition: AVCaptureDevice.Position = useFrontCamera ? .front : .back

            startCaptureLocalVideo(
                cameraPositon: newPosition,
                videoWidth: 640,
                videoHeight: 640*16/9,
                videoFps: 30
            )

            self.isSwitchingCamera = false
            print(
                "Camera switched successfully to \(newPosition == .front ? "front" : "back")"
            )
            let isFront = self.useFrontCamera
            DispatchQueue.main.async {
                completion?(isFront)
            }
        }
    }

    // MARK: - Screen Sharing

    func startScreenCapture() {
        print("startScreenCapture")

        guard !isScreenSharing else {
            print("Screen sharing already active")
            return
        }

        // Stop camera capture first
        if let cameraCapturer = videoCapturer as? RTCCameraVideoCapturer {
            cameraCapturer.stopCapture()
        }

        // Save the original capturer
        originalCapturer = videoCapturer

        // Reuse the SAME video source (just like file sharing)
        guard let videoSource = videoSource else {
            print("Video source not available")
            return
        }

        // Create the broadcast screen capturer with existing video source
        screenCapturer = FlutterBroadcastScreenCapturer(delegate: videoSource)

        // Start the broadcast capturer (this sets up the socket server)
        screenCapturer?.startCapture()

        // Show the broadcast picker to let user start broadcasting
        showBroadcastPicker()

        // Listen for broadcast started notification, this is sent from broadcast extension
        DarwinNotificationCenter.shared.addObserver(
            self,
            for: .broadcastStarted
        ) { [weak self] in
            DispatchQueue.main.async {
                self?.onBroadcastStarted()
            }
        }

        // Listen for broadcast stopped notification, this is sent from broadcast extension
        DarwinNotificationCenter.shared.addObserver(
            self,
            for: .broadcastStopped
        ) { [weak self] in
            DispatchQueue.main.async {
                self?.onBroadcastStopped()
            }
        }
        // Start background task to keep socket server alive when app backgrounds
        startBackgroundTask()
        setupAudioSessionForBackground()

        isScreenSharing = true
        print("Screen sharing setup complete, waiting for broadcast to start...")
    }

    private func onBroadcastStarted() {
        print("Broadcast started - screen capture is now active")
        isScreenSharing = true
        // No need to replace tracks! The video track already uses videoSource,
        // and screen frames are now feeding into that same source
        onScreenShareChanged?(true)
    }

    private func onBroadcastStopped() {
        print("Broadcast stopped - stopping screen capture")
        guard isScreenSharing else { return }
        stopScreenCapture()
        onScreenShareChanged?(false)
    }

    func stopScreenCapture() {
        print("stopScreenCapture")

        guard isScreenSharing else {
            print("Screen sharing not active")
            return
        }

        // Stop the screen capturer
        screenCapturer?.stopCapture()
        screenCapturer = nil

        // Remove observers
        DarwinNotificationCenter.shared.removeObserver(self, for: .broadcastStarted)
        DarwinNotificationCenter.shared.removeObserver(self, for: .broadcastStopped)

        // End background task. The audio session stays active: deactivating it stops the call's audio I/O
        endBackgroundTask()

        // Restart camera capture (same as file sharing)
        if originalCapturer is RTCCameraVideoCapturer {
            let cameraPosition: AVCaptureDevice.Position = useFrontCamera ? .front : .back
            startCaptureLocalVideo(
                cameraPositon: cameraPosition,
                videoWidth: 640,
                videoHeight: 640*16/9,
                videoFps: 30
            )
        }

        isScreenSharing = false
        print("Switched back to camera")
    }

    // MARK: - Background Task Management

    private func startBackgroundTask() {
        endBackgroundTask() // End any existing task first

        backgroundTask = UIApplication.shared.beginBackgroundTask { [weak self] in
            print("Background task expiring, ending task")
            self?.endBackgroundTask()
        }

        print("Background task started: \(backgroundTask.rawValue)")
    }

    private func endBackgroundTask() {
        guard backgroundTask != .invalid else { return }

        print("Ending background task: \(backgroundTask.rawValue)")
        UIApplication.shared.endBackgroundTask(backgroundTask)
        backgroundTask = .invalid
    }

    func showBroadcastPicker() {
        // Show the system broadcast picker
        let picker = RPSystemBroadcastPickerView(
            frame: CGRect(x: 0, y: 0, width: 50, height: 50)
        )
        picker.preferredExtension = "com.ducmai.WebRTCDemo.WebRTCDemoScreenBroadcast"
        picker.showsMicrophoneButton = false

        // Find the button and trigger it
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            for subview in picker.subviews {
                if let button = subview as? UIButton {
                    button.sendActions(for: .touchUpInside)
                    break
                }
            }
        }
    }

    // have an audio session in background to keep app alive
    func setupAudioSessionForBackground() {
        audioSession = AVAudioSession.sharedInstance()
        do {
            // Ensure audio session is active - this keeps app alive in background
            try audioSession!.setCategory(.playAndRecord, mode: .voiceChat, options: [.allowBluetooth, .defaultToSpeaker, .mixWithOthers])
            try audioSession!.setActive(true, options: [])

            print("✅ Audio session activated for background screen sharing")
        } catch {
            print("❌ Failed to setup audio session for background: \(error)")
        }
    }

    // MARK: - Video File Sharing

    func shareVideoFile(fileURL: URL) {
        print("shareVideoFile is called")

        guard !isFileSharingActive else {
            print("Already sharing a file")
            return
        }

        // Stop camera capture first
        if let cameraCapturer = videoCapturer as? RTCCameraVideoCapturer {
            cameraCapturer.stopCapture()
        }

        // Reuse the SAME video source (don't create a new one)
        guard let videoSource = videoSource else {
            print("Video source not available")
            return
        }

        // Create RTCFileVideoCapturer with the existing video source
        let fileVideoCapturer = RTCFileVideoCapturer(delegate: videoSource)
        self.fileVideoCapturer = fileVideoCapturer

        // NOTE: file capturing may take few seconds after startCapturing is called
        fileVideoCapturer.startCapturing(fromFileURL: fileURL) { error in
            print("Error starting file video capture: \(error)")
        }

        // The video track already uses this videoSource, so frames will flow automatically
        isFileSharingActive = true
    }

    func stopVideoFileSharing() {
        print("stopVideoFileSharing")

        guard isFileSharingActive else {
            print("Not sharing a file")
            return
        }

        // Stop file capturer
        fileVideoCapturer?.stopCapture()
        fileVideoCapturer = nil

        // Restart camera capture
        let cameraPosition: AVCaptureDevice.Position = useFrontCamera ? .front : .back
        startCaptureLocalVideo(
            cameraPositon: cameraPosition,
            videoWidth: 640,
            videoHeight: 640*16/9,
            videoFps: 30
        )

        isFileSharingActive = false
    }

    // MARK: - Effects

    var isEffectsAvailable: Bool {
        return effectsProcessor != nil
    }

    /// Nil sends the camera untouched.
    func setEffects(_ scene: EffectsScene?) {
        effectsProcessor?.setScene(scene)
    }
}
