//
//  EffectsProcessor.swift
//  WebRTCDemo
//

import AVFoundation
import CoreImage
import CoreImage.CIFilterBuiltins
import Foundation
import Metal
#if os(iOS)
import UIKit
#else
import AppKit
#endif
import Vision
import WebRTC

/// What to draw on camera frames: a background (blur, picture or video) and a face sticker.
struct EffectsScene {
    let background: BackgroundOption
    var backgroundImage: CIImage?
    var sticker: StickerOption?
    var stickerImage: CIImage?

    var needsMask: Bool { background.kind != .none }
    var needsFace: Bool { stickerImage != nil }
    var isActive: Bool { needsMask || needsFace }

    /// Decodes the pictures a selection needs, or nil if one is missing. Slow, so call it off the main thread.
    static func load(_ selection: EffectsSelection, from catalog: EffectsCatalog) -> EffectsScene? {
        let background = catalog.background(selection.background)
        var scene = EffectsScene(background: background)
        switch background.kind {
        case .image:
            guard let file = background.file, let image = EffectsCatalog.decode(file, maxSide: 1920) else { return nil }
            scene.backgroundImage = CIImage(cgImage: image)
        case .video:
            guard let file = background.file, FileManager.default.isReadableFile(atPath: file.path) else { return nil }
        case .none, .blur:
            break
        }
        if let sticker = catalog.sticker(selection.sticker) {
            guard let image = EffectsCatalog.decode(sticker.file, maxSide: 512) else { return nil }
            scene.sticker = sticker
            scene.stickerImage = CIImage(cgImage: image)
        }
        return scene
    }
}

/// Proxy between RTCCameraVideoCapturer and RTCVideoSource that applies an `EffectsScene` to camera frames.
/// Frames from any other capturer, and all frames while no scene is set, are forwarded untouched.
final class EffectsProcessor: NSObject, RTCVideoCapturerDelegate {
    private let output: RTCVideoCapturerDelegate
    private let processingQueue = DispatchQueue(label: "com.ducmai.WebRTCDemo.effects", qos: .userInteractive)

    private let stateLock = NSLock()
    private var scene: EffectsScene?
    private var video: BackgroundVideo?
    private var generation = 0
    private var holding = false
    private var isProcessing = false

    // Run segmentation and face detection on every Nth frame and reuse the results in between.
    #if os(iOS)
    private let analysisInterval = 2
    #else
    // Starts as on iOS and backs off where Vision is slow (Intel Macs have no Neural Engine).
    // Only touched on processingQueue.
    private var analysisInterval = 2
    private var analysisCost: Double?
    // Requests that have run once; a request's first run loads its model and is not timed.
    private var warmRequests = Set<ObjectIdentifier>()
    #endif
    // Shown behind a video background until its first frame is decoded.
    private static let videoFallbackBlur: CGFloat = 0.02

    // Everything below is only touched on processingQueue
    private let ciContext: CIContext
    private let segmentationRequest: VNGeneratePersonSegmentationRequest
    private let faceRequest = VNDetectFaceLandmarksRequest()
    private var sequenceHandler = VNSequenceRequestHandler()
    private var appliedGeneration = -1
    private var cachedMask: CIImage?
    private var smoother = PlacementSmoother()
    private var frameCounter = 0
    private var lastGeometry: (width: Int, height: Int, rotation: RTCVideoRotation)?
    private var outputPool: CVPixelBufferPool?
    private var outputPoolSize: (width: Int, height: Int)?
    private var preparedBackground: CIImage?
    private var preparedBackgroundSize: CGSize = .zero

    init(output: RTCVideoCapturerDelegate) {
        self.output = output

        if let device = MTLCreateSystemDefaultDevice() {
            ciContext = CIContext(mtlDevice: device, options: [.cacheIntermediates: false])
        } else {
            ciContext = CIContext(options: [.cacheIntermediates: false])
        }

        let request = VNGeneratePersonSegmentationRequest()
        request.qualityLevel = .balanced
        request.outputPixelFormat = kCVPixelFormatType_OneComponent8
        segmentationRequest = request

        super.init()
    }

    /// Drops camera frames until the next `setScene`, so a saved effect never starts with the raw camera.
    func holdFrames() {
        stateLock.lock()
        holding = true
        stateLock.unlock()
    }

    /// Nil, or a scene without any effect, forwards camera frames untouched. Call on the main queue.
    func setScene(_ scene: EffectsScene?) {
        let active = scene?.isActive == true ? scene : nil
        let videoURL = active?.background.kind == .video ? active?.background.file : nil

        stateLock.lock()
        let previous = video
        // The same video keeps playing when only the sticker changes.
        let next = videoURL.map { url in previous?.url == url ? previous! : BackgroundVideo(url: url) }
        self.scene = active
        video = next
        generation += 1
        holding = false
        stateLock.unlock()

        if previous !== next { previous?.stop() }
    }

    /// The next camera frames show another scene (camera switch, capture restart): drop old masks and faces.
    func invalidateAnalysis() {
        stateLock.lock()
        generation += 1
        stateLock.unlock()
    }

    // MARK: - RTCVideoCapturerDelegate
    func capturer(_ capturer: RTCVideoCapturer, didCapture frame: RTCVideoFrame) {
        let cameraPixelBuffer = (frame.buffer as? RTCCVPixelBuffer)?.pixelBuffer
        let isCameraFrame = capturer is RTCCameraVideoCapturer && cameraPixelBuffer != nil

        stateLock.lock()
        if isProcessing || (isCameraFrame && holding) {
            // A processed frame is still in flight, or a saved effect is still loading: drop this one
            // instead of blocking the capture queue or letting the unprocessed camera slip through.
            stateLock.unlock()
            return
        }
        let scene = isCameraFrame ? self.scene : nil
        let video = self.video
        let generation = self.generation
        isProcessing = scene != nil
        stateLock.unlock()

        guard let scene, let pixelBuffer = cameraPixelBuffer else {
            output.capturer(capturer, didCapture: frame)
            return
        }

        processingQueue.async { [weak self] in
            guard let self = self else { return }
            if let processedFrame = self.process(frame: frame, pixelBuffer: pixelBuffer, scene: scene, video: video, generation: generation) {
                self.output.capturer(capturer, didCapture: processedFrame)
            }
            self.stateLock.lock()
            self.isProcessing = false
            self.stateLock.unlock()
        }
    }

    // MARK: - Processing
    private func process(
        frame: RTCVideoFrame,
        pixelBuffer: CVPixelBuffer,
        scene: EffectsScene,
        video: BackgroundVideo?,
        generation: Int
    ) -> RTCVideoFrame? {
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)

        if generation != appliedGeneration {
            appliedGeneration = generation
            preparedBackground = nil
            preparedBackgroundSize = .zero
            resetAnalysis()
        }
        // Camera switch or device rotation: the previous mask and face no longer match
        if let geometry = lastGeometry,
           geometry.width != width || geometry.height != height || geometry.rotation != frame.rotation {
            resetAnalysis()
        }
        lastGeometry = (width, height, frame.rotation)

        // Work in upright space so the models see an upright person,
        // then rotate back so the output keeps the input's buffer layout and rotation.
        let orientation = Self.orientation(for: frame.rotation)
        let upright = Self.normalized(CIImage(cvPixelBuffer: pixelBuffer).oriented(orientation))

        if frameCounter % analysisInterval == 0 || (scene.needsMask && cachedMask == nil) {
            analyze(upright, scene: scene)
        }
        frameCounter += 1

        var composed = upright
        if scene.needsMask {
            guard let mask = cachedMask else {
                return nil
            }
            let blend = CIFilter.blendWithMask()
            blend.inputImage = upright
            blend.backgroundImage = background(for: upright, scene: scene, video: video)
            blend.maskImage = mask
            guard let composited = blend.outputImage?.cropped(to: upright.extent) else {
                return nil
            }
            composed = composited
        }
        if let stickerImage = scene.stickerImage, let placement = smoother.current {
            composed = Self.draw(stickerImage, at: placement, over: composed)
        }

        let restored = Self.normalized(composed.oriented(Self.inverse(of: orientation)))
        guard let outputBuffer = makeOutputBuffer(width: width, height: height) else {
            return nil
        }
        ciContext.render(
            restored,
            to: outputBuffer,
            bounds: CGRect(x: 0, y: 0, width: width, height: height),
            colorSpace: CGColorSpace(name: CGColorSpace.sRGB)
        )

        return RTCVideoFrame(
            buffer: RTCCVPixelBuffer(pixelBuffer: outputBuffer),
            rotation: frame.rotation,
            timeStampNs: frame.timeStampNs
        )
    }

    private func analyze(_ image: CIImage, scene: EffectsScene) {
        var requests: [VNRequest] = []
        if scene.needsMask { requests.append(segmentationRequest) }
        if scene.needsFace { requests.append(faceRequest) }
        #if os(macOS)
        let loadsModel = requests.contains { !warmRequests.contains(ObjectIdentifier($0)) }
        let started = CACurrentMediaTime()
        #endif
        do {
            try sequenceHandler.perform(requests, on: image)
        } catch {
            print("effects analysis failed: \(error)")
            return
        }
        #if os(macOS)
        if loadsModel {
            requests.forEach { warmRequests.insert(ObjectIdentifier($0)) }
        } else {
            adaptCadence(to: CACurrentMediaTime() - started)
        }
        #endif

        if scene.needsMask, let maskBuffer = segmentationRequest.results?.first?.pixelBuffer {
            let mask = CIImage(cvPixelBuffer: maskBuffer)
            let scaleX = image.extent.width / mask.extent.width
            let scaleY = image.extent.height / mask.extent.height
            cachedMask = mask.transformed(by: CGAffineTransform(scaleX: scaleX, y: scaleY))
        }

        if let sticker = scene.sticker, let stickerImage = scene.stickerImage {
            let size = image.extent.size
            let largest = faceRequest.results?.max { Self.area($0.boundingBox) < Self.area($1.boundingBox) }
            let aspect = stickerImage.extent.height / max(stickerImage.extent.width, 1)
            let placement = largest
                .flatMap { Self.facePoints($0, in: size) }
                .flatMap { StickerPlacement.place($0, sticker: sticker, aspect: aspect) }
            _ = smoother.update(placement)
        }
    }

    private func background(for frame: CIImage, scene: EffectsScene, video: BackgroundVideo?) -> CIImage {
        let size = frame.extent.size
        switch scene.background.kind {
        case .none:
            return frame
        case .blur:
            return Self.blurred(frame, amount: scene.background.blur)
        case .video:
            guard let videoFrame = video?.frame() else {
                return Self.blurred(frame, amount: Self.videoFallbackBlur)
            }
            return Self.cover(videoFrame, size: size)
        case .image:
            guard let source = scene.backgroundImage else {
                return Self.blurred(frame, amount: Self.videoFallbackBlur)
            }
            if size == preparedBackgroundSize, let prepared = preparedBackground {
                return prepared
            }
            // Cover-crop once per frame size, and keep it in a pixel buffer
            // so each frame only samples it instead of re-scaling the full-size picture.
            let cropped = Self.cover(source, size: size)
            var prepared = cropped
            if let buffer = Self.makePixelBuffer(width: Int(size.width), height: Int(size.height)) {
                ciContext.render(cropped, to: buffer)
                prepared = CIImage(cvPixelBuffer: buffer)
            }
            preparedBackground = prepared
            preparedBackgroundSize = size
            return prepared
        }
    }

    private func makeOutputBuffer(width: Int, height: Int) -> CVPixelBuffer? {
        if outputPool == nil || outputPoolSize?.width != width || outputPoolSize?.height != height {
            var pool: CVPixelBufferPool?
            CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, Self.pixelBufferAttributes(width: width, height: height), &pool)
            outputPool = pool
            outputPoolSize = (width, height)
        }

        guard let pool = outputPool else { return nil }
        var buffer: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &buffer)
        return buffer
    }

    #if os(macOS)
    /// Spreads analysis so it averages about 10 ms per frame of the 33 ms budget.
    private func adaptCadence(to seconds: Double) {
        let average = analysisCost.map { $0 * 0.9 + seconds * 0.1 } ?? seconds
        analysisCost = average
        analysisInterval = average > 0.03 ? 4 : average > 0.02 ? 3 : 2
    }
    #endif

    private func resetAnalysis() {
        cachedMask = nil
        smoother.reset()
        frameCounter = 0
        sequenceHandler = VNSequenceRequestHandler()
    }

    // MARK: - Helpers

    /// The eye, nose and mouth centers in pixels, y down like the placement math.
    private static func facePoints(_ face: VNFaceObservation, in size: CGSize) -> FacePoints? {
        func center(_ region: VNFaceLandmarkRegion2D?) -> CGPoint? {
            guard let region, region.pointCount > 0 else { return nil }
            let points = region.pointsInImage(imageSize: size)
            let sumX = points.reduce(0) { $0 + $1.x }
            let sumY = points.reduce(0) { $0 + $1.y }
            let count = CGFloat(points.count)
            // Vision's origin is the bottom left.
            return CGPoint(x: sumX / count, y: size.height - sumY / count)
        }
        guard let landmarks = face.landmarks,
              let eyeA = center(landmarks.leftEye),
              let eyeB = center(landmarks.rightEye),
              let nose = center(landmarks.nose),
              let mouth = center(landmarks.innerLips ?? landmarks.outerLips) else { return nil }
        return FacePoints(eyeA: eyeA, eyeB: eyeB, nose: nose, mouth: mouth)
    }

    /// Core Image is y up while the placement is y down and clockwise.
    private static func draw(_ sticker: CIImage, at placement: StickerPlacement, over image: CIImage) -> CIImage {
        let extent = sticker.extent
        let transform = CGAffineTransform(translationX: -extent.midX, y: -extent.midY)
            .concatenating(CGAffineTransform(scaleX: placement.width / extent.width, y: placement.height / extent.height))
            .concatenating(CGAffineTransform(rotationAngle: -placement.angle))
            .concatenating(CGAffineTransform(translationX: placement.x, y: image.extent.height - placement.y))
        return sticker.transformed(by: transform).composited(over: image).cropped(to: image.extent)
    }

    private static func blurred(_ frame: CIImage, amount: CGFloat) -> CIImage {
        frame.clampedToExtent()
            .applyingGaussianBlur(sigma: Double(amount * frame.extent.width))
            .cropped(to: frame.extent)
    }

    /// Scales to cover `size` and crops the overflow evenly, with the result's origin at zero.
    private static func cover(_ source: CIImage, size: CGSize) -> CIImage {
        let source = normalized(source)
        let scale = max(size.width / source.extent.width, size.height / source.extent.height)
        let scaled = source.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let offsetX = (scaled.extent.width - size.width) / 2
        let offsetY = (scaled.extent.height - size.height) / 2
        return scaled
            .transformed(by: CGAffineTransform(translationX: -offsetX, y: -offsetY))
            .cropped(to: CGRect(origin: .zero, size: size))
    }

    private static func area(_ rect: CGRect) -> CGFloat {
        rect.width * rect.height
    }

    private static func pixelBufferAttributes(width: Int, height: Int) -> CFDictionary {
        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any](),
            kCVPixelBufferMetalCompatibilityKey as String: true
        ]
        return attributes as CFDictionary
    }

    private static func makePixelBuffer(width: Int, height: Int) -> CVPixelBuffer? {
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(
            kCFAllocatorDefault,
            width,
            height,
            kCVPixelFormatType_32BGRA,
            pixelBufferAttributes(width: width, height: height),
            &buffer
        )
        return buffer
    }

    // RTCVideoRotation is the clockwise rotation needed for display, which is what the EXIF orientation encodes.
    private static func orientation(for rotation: RTCVideoRotation) -> CGImagePropertyOrientation {
        switch rotation {
        case ._90:
            return .right
        case ._180:
            return .down
        case ._270:
            return .left
        default:
            return .up
        }
    }

    private static func inverse(of orientation: CGImagePropertyOrientation) -> CGImagePropertyOrientation {
        switch orientation {
        case .right:
            return .left
        case .left:
            return .right
        default:
            return orientation
        }
    }

    private static func normalized(_ image: CIImage) -> CIImage {
        image.transformed(by: CGAffineTransform(translationX: -image.extent.minX, y: -image.extent.minY))
    }
}

/// A muted, looping background video. The processing queue polls it for the newest frame.
private final class BackgroundVideo {
    let url: URL
    private let player: AVPlayer
    private let output = AVPlayerItemVideoOutput(pixelBufferAttributes: [
        kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA
    ])
    private var loopObserver: NSObjectProtocol?
    // Only touched on the processing queue
    private var lastFrame: CIImage?

    init(url: URL) {
        self.url = url
        let item = AVPlayerItem(url: url)
        item.add(output)
        player = AVPlayer(playerItem: item)
        player.isMuted = true
        player.actionAtItemEnd = .none
        player.preventsDisplaySleepDuringVideoPlayback = false
        loopObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification,
            object: item,
            queue: .main
        ) { [weak player] _ in
            player?.seek(to: .zero)
        }
        player.play()
    }

    func stop() {
        player.pause()
        if let loopObserver {
            NotificationCenter.default.removeObserver(loopObserver)
        }
        loopObserver = nil
    }

    /// The newest decoded frame, the previous one when nothing new is ready, or nil before the first.
    func frame() -> CIImage? {
        let time = output.itemTime(forHostTime: CACurrentMediaTime())
        if output.hasNewPixelBuffer(forItemTime: time),
           let buffer = output.copyPixelBuffer(forItemTime: time, itemTimeForDisplay: nil) {
            lastFrame = CIImage(cvPixelBuffer: buffer)
        }
        return lastFrame
    }
}
