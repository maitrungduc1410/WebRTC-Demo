//
//  PictureInPicture.swift
//  WebRTCDemo
//

import AVFoundation
import AVKit
import SwiftUI
import WebRTC

/// Video-call picture-in-picture for the remote participant.
///
/// The system window can only show an `AVSampleBufferDisplayLayer` (not the Metal view used in
/// the call screen), so while PiP is up the remote track is also rendered into one, inside an
/// `AVPictureInPictureVideoCallViewController`. Frames are only converted while the window is
/// visible.
@Observable
final class PictureInPictureController: NSObject, AVPictureInPictureControllerDelegate {
    private(set) var isPossible = false
    private(set) var isActive = false

    /// Starts PiP by itself when the app goes to the background.
    var startsAutomatically = false {
        didSet { controller?.canStartPictureInPictureAutomaticallyFromInline = startsAutomatically }
    }

    var track: RTCVideoTrack? {
        didSet {
            guard oldValue !== track else { return }
            oldValue?.remove(renderer)
            track?.add(renderer)
        }
    }

    private let renderer = PictureInPictureRenderer()
    private let contentController = AVPictureInPictureVideoCallViewController()
    @ObservationIgnored private var controller: AVPictureInPictureController?
    @ObservationIgnored private var possibleObservation: NSKeyValueObservation?
    @ObservationIgnored private weak var sourceView: UIView?

    static var isSupported: Bool { AVPictureInPictureController.isPictureInPictureSupported() }

    override init() {
        super.init()
        let videoView = renderer.view
        videoView.frame = contentController.view.bounds
        videoView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        contentController.view.addSubview(videoView)
        contentController.view.backgroundColor = .black
        contentController.preferredContentSize = CGSize(width: 9, height: 16)
        renderer.onVideoSize = { [weak self] size in
            self?.contentController.preferredContentSize = size
        }
    }

    /// The view the window animates from and back to: the remote video on the call screen.
    func attach(sourceView: UIView) {
        guard Self.isSupported, sourceView !== self.sourceView else { return }
        self.sourceView = sourceView
        possibleObservation = nil
        controller?.stopPictureInPicture()

        let source = AVPictureInPictureController.ContentSource(
            activeVideoCallSourceView: sourceView,
            contentViewController: contentController
        )
        let controller = AVPictureInPictureController(contentSource: source)
        controller.delegate = self
        controller.canStartPictureInPictureAutomaticallyFromInline = startsAutomatically
        possibleObservation = controller.observe(\.isPictureInPicturePossible, options: [.initial, .new]) { [weak self] controller, _ in
            let possible = controller.isPictureInPicturePossible
            DispatchQueue.main.async { self?.isPossible = possible }
        }
        self.controller = controller
    }

    func start() {
        controller?.startPictureInPicture()
    }

    /// Call when the call ends; the window must not outlive it.
    func tearDown() {
        startsAutomatically = false
        controller?.stopPictureInPicture()
        track = nil
        renderer.isRendering = false
    }

    // MARK: AVPictureInPictureControllerDelegate

    func pictureInPictureControllerWillStartPictureInPicture(_ controller: AVPictureInPictureController) {
        renderer.isRendering = true
        isActive = true
    }

    func pictureInPictureController(
        _ controller: AVPictureInPictureController,
        failedToStartPictureInPictureWithError error: Error
    ) {
        print("picture in picture failed to start: \(error)")
        renderer.isRendering = false
        isActive = false
    }

    func pictureInPictureControllerDidStopPictureInPicture(_ controller: AVPictureInPictureController) {
        renderer.isRendering = false
        isActive = false
    }

    func pictureInPictureController(
        _ controller: AVPictureInPictureController,
        restoreUserInterfaceForPictureInPictureStopWithCompletionHandler completionHandler: @escaping (Bool) -> Void
    ) {
        // The call screen stays in place underneath; there is nothing to rebuild.
        completionHandler(true)
    }
}

/// Marks the remote video on the call screen as the place the PiP window animates from.
struct PictureInPictureSourceView: UIViewRepresentable {
    let controller: PictureInPictureController

    func makeUIView(context: Context) -> UIView {
        let view = UIView()
        view.isUserInteractionEnabled = false
        view.backgroundColor = .clear
        controller.attach(sourceView: view)
        return view
    }

    func updateUIView(_ view: UIView, context: Context) {
        controller.attach(sourceView: view)
    }
}

/// An `RTCVideoRenderer` that feeds an `AVSampleBufferDisplayLayer`. Hardware-decoded frames
/// (`RTCCVPixelBuffer`) are enqueued as they are; software-decoded I420 frames (VP8) are copied
/// into NV12 pixel buffers first.
final class PictureInPictureRenderer: NSObject, RTCVideoRenderer {
    let view: SampleBufferVideoView
    var onVideoSize: ((CGSize) -> Void)?

    // Kept apart from the view: frames arrive on the decoder thread.
    private let sampleRenderer: AVSampleBufferVideoRenderer

    override init() {
        let view = SampleBufferVideoView()
        self.view = view
        sampleRenderer = view.renderer
        super.init()
    }

    private let lock = NSLock()
    private var rendering = false
    private var pool: CVPixelBufferPool?
    private var poolSize: (width: Int, height: Int) = (0, 0)
    private var lastLayout: (width: Int, height: Int, rotation: RTCVideoRotation)?

    var isRendering: Bool {
        get { lock.withLock { rendering } }
        set {
            lock.withLock {
                rendering = newValue
                if !newValue { lastLayout = nil }
            }
            if !newValue {
                sampleRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
            }
        }
    }

    func setSize(_ size: CGSize) {}

    func renderFrame(_ frame: RTCVideoFrame?) {
        guard let frame, isRendering, let pixelBuffer = pixelBuffer(for: frame.buffer) else { return }
        updateLayoutIfNeeded(width: CVPixelBufferGetWidth(pixelBuffer), height: CVPixelBufferGetHeight(pixelBuffer), rotation: frame.rotation)
        guard let sampleBuffer = Self.sampleBuffer(for: pixelBuffer) else { return }

        if sampleRenderer.status == .failed || sampleRenderer.requiresFlushToResumeDecoding {
            sampleRenderer.flush()
        }
        sampleRenderer.enqueue(sampleBuffer)
    }

    private func updateLayoutIfNeeded(width: Int, height: Int, rotation: RTCVideoRotation) {
        let changed: Bool = lock.withLock {
            if let last = lastLayout, last.width == width, last.height == height, last.rotation == rotation {
                return false
            }
            lastLayout = (width, height, rotation)
            return true
        }
        guard changed else { return }
        let quarterTurn = rotation == ._90 || rotation == ._270
        let displayed = quarterTurn ? CGSize(width: height, height: width) : CGSize(width: width, height: height)
        DispatchQueue.main.async { [weak self] in
            self?.view.rotation = rotation
            self?.onVideoSize?(displayed)
        }
    }

    private func pixelBuffer(for buffer: RTCVideoFrameBuffer) -> CVPixelBuffer? {
        if let cvBuffer = buffer as? RTCCVPixelBuffer,
           Int(cvBuffer.width) == CVPixelBufferGetWidth(cvBuffer.pixelBuffer),
           Int(cvBuffer.height) == CVPixelBufferGetHeight(cvBuffer.pixelBuffer) {
            return cvBuffer.pixelBuffer
        }
        return copyToNV12(buffer.toI420())
    }

    private func copyToNV12(_ i420: RTCYUVPlanarBuffer) -> CVPixelBuffer? {
        let width = Int(i420.width)
        let height = Int(i420.height)
        guard width > 0, height > 0, let pool = pixelBufferPool(width: width, height: height) else { return nil }

        var output: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &output) == kCVReturnSuccess, let output else { return nil }
        CVPixelBufferLockBaseAddress(output, [])
        defer { CVPixelBufferUnlockBaseAddress(output, []) }
        guard let yBase = CVPixelBufferGetBaseAddressOfPlane(output, 0),
              let uvBase = CVPixelBufferGetBaseAddressOfPlane(output, 1) else { return nil }

        let yStride = CVPixelBufferGetBytesPerRowOfPlane(output, 0)
        let srcYStride = Int(i420.strideY)
        for row in 0..<height {
            memcpy(yBase + row * yStride, i420.dataY + row * srcYStride, width)
        }

        let uvStride = CVPixelBufferGetBytesPerRowOfPlane(output, 1)
        let chromaWidth = Int(i420.chromaWidth)
        let chromaHeight = Int(i420.chromaHeight)
        let srcUStride = Int(i420.strideU)
        let srcVStride = Int(i420.strideV)
        let dataU = i420.dataU
        let dataV = i420.dataV
        for row in 0..<chromaHeight {
            let dst = (uvBase + row * uvStride).assumingMemoryBound(to: UInt8.self)
            let u = dataU + row * srcUStride
            let v = dataV + row * srcVStride
            for column in 0..<chromaWidth {
                dst[column * 2] = u[column]
                dst[column * 2 + 1] = v[column]
            }
        }
        return output
    }

    private func pixelBufferPool(width: Int, height: Int) -> CVPixelBufferPool? {
        if let pool, poolSize == (width, height) { return pool }
        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any]()
        ]
        var created: CVPixelBufferPool?
        CVPixelBufferPoolCreate(nil, nil, attributes as CFDictionary, &created)
        pool = created
        poolSize = (width, height)
        return created
    }

    private static func sampleBuffer(for pixelBuffer: CVPixelBuffer) -> CMSampleBuffer? {
        var format: CMVideoFormatDescription?
        guard CMVideoFormatDescriptionCreateForImageBuffer(
            allocator: nil,
            imageBuffer: pixelBuffer,
            formatDescriptionOut: &format
        ) == noErr, let format else { return nil }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMClockGetTime(CMClockGetHostTimeClock()),
            decodeTimeStamp: .invalid
        )
        var sampleBuffer: CMSampleBuffer?
        guard CMSampleBufferCreateReadyWithImageBuffer(
            allocator: nil,
            imageBuffer: pixelBuffer,
            formatDescription: format,
            sampleTiming: &timing,
            sampleBufferOut: &sampleBuffer
        ) == noErr, let sampleBuffer else { return nil }

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true) as? [NSMutableDictionary],
           let first = attachments.first {
            first[kCMSampleAttachmentKey_DisplayImmediately] = true
        }
        return sampleBuffer
    }
}

/// Hosts the display layer and applies the frame rotation WebRTC reports separately.
final class SampleBufferVideoView: UIView {
    private let displayLayer = AVSampleBufferDisplayLayer()
    var renderer: AVSampleBufferVideoRenderer { displayLayer.sampleBufferRenderer }

    var rotation: RTCVideoRotation = ._0 {
        didSet { setNeedsLayout() }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        displayLayer.videoGravity = .resizeAspectFill
        layer.addSublayer(displayLayer)
        clipsToBounds = true
        backgroundColor = .black
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let angle: CGFloat
        switch rotation {
        case ._90: angle = .pi / 2
        case ._180: angle = .pi
        case ._270: angle = -.pi / 2
        default: angle = 0
        }
        let quarterTurn = rotation == ._90 || rotation == ._270
        // The layer is laid out unrotated, then turned to fill the view.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        displayLayer.setAffineTransform(.identity)
        displayLayer.bounds = CGRect(
            origin: .zero,
            size: quarterTurn ? CGSize(width: bounds.height, height: bounds.width) : bounds.size
        )
        displayLayer.position = CGPoint(x: bounds.midX, y: bounds.midY)
        displayLayer.setAffineTransform(CGAffineTransform(rotationAngle: angle))
        CATransaction.commit()
    }
}
