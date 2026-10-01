//
//  VirtualBackgroundProcessor.swift
//  WebRTCDemo
//

import Foundation
import UIKit
import WebRTC
import Vision
import CoreImage
import CoreImage.CIFilterBuiltins
import Metal

/// Proxy between RTCCameraVideoCapturer and RTCVideoSource that replaces the background of camera frames.
/// Frames from any other capturer, and all frames while disabled, are forwarded untouched.
final class VirtualBackgroundProcessor: NSObject, RTCVideoCapturerDelegate {
    private let output: RTCVideoCapturerDelegate
    private let processingQueue = DispatchQueue(label: "com.ducmai.WebRTCDemo.virtualBackground", qos: .userInteractive)
    
    private let stateLock = NSLock()
    private var enabled = false
    private var isProcessing = false
    
    // Run segmentation on every Nth frame and reuse the last mask in between.
    private let segmentationInterval = 2
    
    // Everything below is only touched on processingQueue
    private let ciContext: CIContext
    private let segmentationRequest: VNGeneratePersonSegmentationRequest
    private var sequenceHandler = VNSequenceRequestHandler()
    private let sourceBackground: CIImage?
    private var cachedMask: CIImage?
    private var frameCounter = 0
    private var lastGeometry: (width: Int, height: Int, rotation: RTCVideoRotation)?
    private var outputPool: CVPixelBufferPool?
    private var outputPoolSize: (width: Int, height: Int)?
    private var preparedBackground: CIImage?
    private var preparedBackgroundSize: CGSize = .zero
    
    var isEnabled: Bool {
        get {
            stateLock.lock()
            defer { stateLock.unlock() }
            return enabled
        }
        set {
            stateLock.lock()
            enabled = newValue
            stateLock.unlock()
            
            if !newValue {
                processingQueue.async { [weak self] in
                    self?.resetSegmentationState()
                }
            }
        }
    }
    
    init(output: RTCVideoCapturerDelegate, backgroundImage: UIImage? = UIImage(named: "virtual_background")) {
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
        
        if let cgImage = backgroundImage?.cgImage {
            sourceBackground = CIImage(cgImage: cgImage)
        } else {
            print("virtual background image not found, falling back to blur")
            sourceBackground = nil
        }
        
        super.init()
    }
    
    // MARK: - RTCVideoCapturerDelegate
    func capturer(_ capturer: RTCVideoCapturer, didCapture frame: RTCVideoFrame) {
        let cameraPixelBuffer = (frame.buffer as? RTCCVPixelBuffer)?.pixelBuffer
        let isCameraFrame = capturer is RTCCameraVideoCapturer && cameraPixelBuffer != nil
        
        stateLock.lock()
        if isProcessing {
            // A processed frame is still in flight: drop this one instead of blocking the capture queue
            // or letting an unprocessed frame (with the real background) slip through.
            stateLock.unlock()
            return
        }
        let shouldProcess = enabled && isCameraFrame
        isProcessing = shouldProcess
        stateLock.unlock()
        
        guard shouldProcess, let pixelBuffer = cameraPixelBuffer else {
            output.capturer(capturer, didCapture: frame)
            return
        }
        
        processingQueue.async { [weak self] in
            guard let self = self else { return }
            if let processedFrame = self.process(frame: frame, pixelBuffer: pixelBuffer) {
                self.output.capturer(capturer, didCapture: processedFrame)
            }
            self.stateLock.lock()
            self.isProcessing = false
            self.stateLock.unlock()
        }
    }
    
    // MARK: - Processing
    private func process(frame: RTCVideoFrame, pixelBuffer: CVPixelBuffer) -> RTCVideoFrame? {
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        
        // Camera switch or device rotation: the previous mask no longer matches
        if let geometry = lastGeometry,
           geometry.width != width || geometry.height != height || geometry.rotation != frame.rotation {
            resetSegmentationState()
        }
        lastGeometry = (width, height, frame.rotation)
        
        // Work in upright space so the segmentation model sees an upright person,
        // then rotate back so the output keeps the input's buffer layout and rotation.
        let orientation = Self.orientation(for: frame.rotation)
        let upright = Self.normalized(CIImage(cvPixelBuffer: pixelBuffer).oriented(orientation))
        
        if cachedMask == nil || frameCounter % segmentationInterval == 0 {
            if let mask = segmentPerson(in: upright) {
                cachedMask = mask
            }
        }
        frameCounter += 1
        
        guard let mask = cachedMask else {
            return nil
        }
        
        let blend = CIFilter.blendWithMask()
        blend.inputImage = upright
        blend.backgroundImage = background(for: upright)
        blend.maskImage = mask
        guard let composited = blend.outputImage?.cropped(to: upright.extent) else {
            return nil
        }
        
        let restored = Self.normalized(composited.oriented(Self.inverse(of: orientation)))
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
    
    private func segmentPerson(in image: CIImage) -> CIImage? {
        do {
            try sequenceHandler.perform([segmentationRequest], on: image)
        } catch {
            print("person segmentation failed: \(error)")
            return nil
        }
        
        guard let maskBuffer = segmentationRequest.results?.first?.pixelBuffer else {
            return nil
        }
        
        let mask = CIImage(cvPixelBuffer: maskBuffer)
        let scaleX = image.extent.width / mask.extent.width
        let scaleY = image.extent.height / mask.extent.height
        return mask.transformed(by: CGAffineTransform(scaleX: scaleX, y: scaleY))
    }
    
    private func background(for frame: CIImage) -> CIImage {
        let size = frame.extent.size
        
        guard let source = sourceBackground else {
            return frame.clampedToExtent().applyingGaussianBlur(sigma: 20).cropped(to: frame.extent)
        }
        
        if size == preparedBackgroundSize, let prepared = preparedBackground {
            return prepared
        }
        
        // Aspect-fill and center-crop once per frame size, and keep it in a pixel buffer
        // so each frame only samples it instead of re-scaling the full-size JPEG.
        let scale = max(size.width / source.extent.width, size.height / source.extent.height)
        let scaled = source.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let offsetX = scaled.extent.minX + (scaled.extent.width - size.width) / 2
        let offsetY = scaled.extent.minY + (scaled.extent.height - size.height) / 2
        let cropped = scaled
            .transformed(by: CGAffineTransform(translationX: -offsetX, y: -offsetY))
            .cropped(to: CGRect(origin: .zero, size: size))
        
        var prepared = cropped
        if let buffer = Self.makePixelBuffer(width: Int(size.width), height: Int(size.height)) {
            ciContext.render(cropped, to: buffer)
            prepared = CIImage(cvPixelBuffer: buffer)
        }
        
        preparedBackground = prepared
        preparedBackgroundSize = size
        return prepared
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
    
    private func resetSegmentationState() {
        cachedMask = nil
        frameCounter = 0
        sequenceHandler = VNSequenceRequestHandler()
    }
    
    // MARK: - Helpers
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