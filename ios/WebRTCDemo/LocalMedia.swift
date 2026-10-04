//
//  LocalMedia.swift
//  WebRTCDemo
//

import AVFoundation
import Foundation
#if os(iOS)
import ReplayKit
import UIKit
#endif
import WebRTC

/// Everything on the sending side that the 1:1 engine (`WebRTCClient`) and the group engine
/// (`GroupCallClient`) share: the factory, one audio and one video track, and the single
/// `RTCVideoSource` that the camera (through the effects processor), a video file or the
/// screen (broadcast extension on iOS, ScreenCaptureKit on macOS) feed in turn. Switching sources
/// never touches a sender, so no renegotiation is needed and the sender's frame cryptor stays in
/// place (ARCHITECTURE.md section 8).
final class LocalMedia {
    let factory: RTCPeerConnectionFactory
    /// Nil when E2EE is off.
    let encryption: FrameEncryption?
    private(set) var videoTrack: RTCVideoTrack?
    private(set) var audioTrack: RTCAudioTrack?

    /// Called on the main queue when screen frames start flowing (true) and when the share ends by
    /// itself (false): the broadcast extension stopped, or on macOS the window closed or the user
    /// stopped it from the menu bar.
    var onScreenShareChanged: ((Bool) -> Void)?

    private var videoSource: RTCVideoSource?
    private var videoCapturer: RTCVideoCapturer!
    private var customFrameCapturer: Bool
    private var useFrontCamera = true
    private var isSwitchingCamera = false

    private var isFileSharingActive = false
    private var isScreenSharing = false

    #if os(iOS)
    // Video file sharing properties
    private var fileVideoCapturer: RTCFileVideoCapturer?

    // Screen sharing properties
    private var screenCapturer: FlutterBroadcastScreenCapturer?
    private var originalCapturer: RTCVideoCapturer?
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid
    private var audioSession: AVAudioSession?
    #else
    // RTCFileVideoCapturer is not in the macOS slice of the framework, hence the own capturers.
    private var macFileCapturer: FileVideoCapturer?
    private var desktopCapturer: ScreenShareCapturer?
    /// The user's camera switch; the camera only runs while nothing else is shared.
    private var cameraCaptureEnabled = true
    private var capturingCamera: AVCaptureDevice?
    /// `uniqueID` of the camera picked by the user, nil for the system default.
    private var preferredCameraID: String?
    #endif

    // Backgrounds and stickers (camera frames only)
    private var effectsProcessor: EffectsProcessor?
    private let holdsEffectFrames: Bool

    var isFrontCamera: Bool { useFrontCamera }

    /// `holdEffects`: a saved effect is about to load, so camera frames are dropped until
    /// `setEffects` and the call never starts with the raw camera.
    /// On macOS the camera starts with `startCamera(preferredCameraID:)`, once the owner knows which one.
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

        #if os(iOS)
        if videoTrack {
            startCaptureLocalVideo(
                cameraPositon: .front,
                videoWidth: 640,
                videoHeight: 640*16/9,
                videoFps: 30
            )
        }
        #endif
    }

    deinit {
        print("LocalMedia Deinit")
    }

    /// Stops every capturer. Called when the call ends.
    func stopCapture() {
        (videoCapturer as? RTCCameraVideoCapturer)?.stopCapture()
        #if os(iOS)
        (videoCapturer as? RTCFileVideoCapturer)?.stopCapture()
        fileVideoCapturer?.stopCapture()
        fileVideoCapturer = nil
        #else
        capturingCamera = nil
        macFileCapturer?.stopCapture()
        macFileCapturer = nil
        desktopCapturer?.stop()
        desktopCapturer = nil
        #endif
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

    #if os(iOS)
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
    #endif

    // MARK: - Effects

    var isEffectsAvailable: Bool {
        return effectsProcessor != nil
    }

    /// Nil sends the camera untouched.
    func setEffects(_ scene: EffectsScene?) {
        effectsProcessor?.setScene(scene)
    }
}

#if os(macOS)
// MARK: - macOS media sources
// Camera, screen and file all feed the same RTCVideoSource, as on iOS, so switching never renegotiates
// and the sender's cryptor stays attached. Called on the main queue.
extension LocalMedia {
    static var cameraDevices: [AVCaptureDevice] {
        RTCCameraVideoCapturer.captureDevices()
    }

    var currentCameraID: String? {
        capturingCamera?.uniqueID ?? resolveCamera()?.uniqueID
    }

    var audioDeviceModule: RTCAudioDeviceModule? {
        factory.audioDeviceModule
    }

    /// Whether the microphone (input) or speaker (output) is set up for the call.
    func isAudioDeviceActive(input: Bool) -> Bool {
        guard let adm = audioDeviceModule else { return false }
        return input ? adm.isRecordingInitialized : adm.isPlayoutInitialized
    }

    /// Moves the microphone or speaker to the device with `id` from the module's list.
    /// The Mac module only takes a new device while that direction is stopped, so a running one is
    /// stopped and started again around the change, as the module does itself when a device goes away.
    /// `start` also opens a stopped direction, which the module leaves stopped when its device vanished
    /// with nothing to fall back to, and does not reopen by itself when a device comes back.
    /// `trySet…Device` reports the native result inverted, so success is judged by the restart.
    @discardableResult
    func selectAudioDevice(id: String, input: Bool, start: Bool = false) -> Bool {
        guard let adm = audioDeviceModule else { return false }
        if input {
            guard let device = adm.inputDevices.first(where: { $0.deviceId == id }) else { return false }
            let initialized = adm.isRecordingInitialized
            let running = adm.isRecording
            if initialized { adm.stopRecording() }
            _ = adm.trySetInputDevice(device)
            guard initialized || start else { return true }
            return adm.initRecording() == 0 && (!(running || start) || adm.startRecording() == 0)
        } else {
            guard let device = adm.outputDevices.first(where: { $0.deviceId == id }) else { return false }
            let initialized = adm.isPlayoutInitialized
            let running = adm.isPlaying
            if initialized { adm.stopPlayout() }
            _ = adm.trySetOutputDevice(device)
            guard initialized || start else { return true }
            return adm.initPlayout() == 0 && (!(running || start) || adm.startPlayout() == 0)
        }
    }

    /// Opens the camera; nil picks the system default.
    func startCamera(preferredCameraID: String?) {
        self.preferredCameraID = preferredCameraID
        updateCameraCapture()
    }

    /// Turns the camera itself on or off (its light too). The track is enabled separately.
    func setCameraEnabled(_ enabled: Bool) {
        cameraCaptureEnabled = enabled
        updateCameraCapture()
    }

    func selectCamera(uniqueID: String) {
        preferredCameraID = uniqueID
        updateCameraCapture()
    }

    /// A camera was plugged in or removed.
    func cameraDevicesChanged() {
        if let current = capturingCamera, !Self.cameraDevices.contains(where: { $0.uniqueID == current.uniqueID }) {
            (videoCapturer as? RTCCameraVideoCapturer)?.stopCapture()
            capturingCamera = nil
        }
        updateCameraCapture()
    }

    /// Starts, stops or moves the capture session so it matches what should be on screen.
    /// RTCCameraVideoCapturer runs start and stop in order on its own queue, so they can follow each other directly.
    private func updateCameraCapture() {
        guard let capturer = videoCapturer as? RTCCameraVideoCapturer else { return }
        let shouldCapture = cameraCaptureEnabled && !isScreenSharing && !isFileSharingActive
        let device = shouldCapture ? resolveCamera() : nil

        if let current = capturingCamera, current.uniqueID != device?.uniqueID {
            capturer.stopCapture()
            capturingCamera = nil
        }
        guard let device, capturingCamera == nil else { return }
        guard let format = Self.preferredFormat(for: device) else {
            print("no usable format for camera \(device.localizedName)")
            return
        }
        effectsProcessor?.invalidateAnalysis()
        capturer.startCapture(with: device, format: format, fps: Self.frameRate(for: format)) { [weak capturer] error in
            if let error {
                print("failed to start camera: \(error)")
                return
            }
            // The peer gets unmirrored frames, so stickers and text read correctly; only the local
            // views mirror. AVFoundation may otherwise mirror front-facing cameras on its own.
            for output in capturer?.captureSession.outputs ?? [] {
                guard let connection = output.connection(with: .video), connection.isVideoMirroringSupported else { continue }
                connection.automaticallyAdjustsVideoMirroring = false
                connection.isVideoMirrored = false
            }
        }
        capturingCamera = device
    }

    private func resolveCamera() -> AVCaptureDevice? {
        let devices = Self.cameraDevices
        if let id = preferredCameraID, let device = devices.first(where: { $0.uniqueID == id }) {
            return device
        }
        if let systemDefault = AVCaptureDevice.default(for: .video),
           let device = devices.first(where: { $0.uniqueID == systemDefault.uniqueID }) {
            return device
        }
        return devices.first
    }

    /// The format closest to 1280x720 (Mac cameras are landscape), preferring ones that reach 30 fps.
    private static func preferredFormat(for device: AVCaptureDevice) -> AVCaptureDevice.Format? {
        let target = 1280 * 720
        return RTCCameraVideoCapturer.supportedFormats(for: device).min { a, b in
            score(a, target: target) < score(b, target: target)
        }
    }

    private static func score(_ format: AVCaptureDevice.Format, target: Int) -> Int {
        let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
        let pixels = Int(dimensions.width) * Int(dimensions.height)
        let maxRate = format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 0
        return abs(pixels - target) + (maxRate >= 29 ? 0 : 10_000_000)
    }

    /// AVFoundation throws when asked for a rate outside the format's ranges.
    private static func frameRate(for format: AVCaptureDevice.Format) -> Int {
        let maxRate = format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 30
        return max(1, min(30, Int(maxRate)))
    }

    // MARK: Screen and window sharing
    /// `onScreenShareChanged` reports when frames start flowing and when the share ends by itself.
    func startScreenCapture(source: ScreenShareSource) {
        guard let videoSource else { return }
        stopFileCapture()
        desktopCapturer?.stop()

        let capturer = ScreenShareCapturer(delegate: videoSource)
        desktopCapturer = capturer
        isScreenSharing = true
        updateCameraCapture()

        capturer.onStarted = { [weak self, weak capturer] in
            DispatchQueue.main.async {
                guard let self, let capturer, self.desktopCapturer === capturer else { return }
                self.onScreenShareChanged?(true)
            }
        }
        capturer.onStopped = { [weak self, weak capturer] error in
            DispatchQueue.main.async {
                guard let self, let capturer, self.desktopCapturer === capturer else { return }
                if let error { print("screen sharing stopped: \(error)") }
                self.stopScreenCapture()
                self.onScreenShareChanged?(false)
            }
        }
        capturer.start(source: source)
    }

    /// Stops sharing and brings the camera back. Does not call `onScreenShareChanged`.
    func stopScreenCapture() {
        guard let capturer = desktopCapturer else { return }
        desktopCapturer = nil
        capturer.stop()
        isScreenSharing = false
        updateCameraCapture()
    }

    // MARK: Video file sharing
    /// `onError` runs on the main queue when the file cannot be read.
    func shareVideoFile(fileURL: URL, onError: @escaping (Error) -> Void) {
        guard let videoSource else { return }
        if let screen = desktopCapturer {
            desktopCapturer = nil
            screen.stop()
            isScreenSharing = false
        }
        macFileCapturer?.stopCapture()

        let capturer = FileVideoCapturer(delegate: videoSource)
        macFileCapturer = capturer
        isFileSharingActive = true
        updateCameraCapture()
        capturer.startCapturing(from: fileURL) { error in
            DispatchQueue.main.async { onError(error) }
        }
    }

    func stopVideoFileSharing() {
        guard isFileSharingActive else { return }
        stopFileCapture()
        updateCameraCapture()
    }

    private func stopFileCapture() {
        macFileCapturer?.stopCapture()
        macFileCapturer = nil
        isFileSharingActive = false
    }
}
#endif
