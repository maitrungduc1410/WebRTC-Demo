//
//  ScreenShareCapturer.swift
//  WebRTCDemoMac
//

import AppKit
import CoreMedia
import QuartzCore
import ScreenCaptureKit
import WebRTC

/// A display or window the user can share.
struct ScreenShareSource: Identifiable, Hashable {
    enum Kind {
        case display, window
    }

    let id: String
    let kind: Kind
    let title: String
    var subtitle: String?
    var appIcon: NSImage?
    let filter: SCContentFilter

    static func == (lhs: ScreenShareSource, rhs: ScreenShareSource) -> Bool {
        lhs.id == rhs.id
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
    }
}

/// Feeds a display or window into an RTCVideoSource with ScreenCaptureKit.
///
/// The WebRTC build ships RTCDesktopCapturer, but it captures with CGDisplayStream and
/// CGWindowListCreateImage, which macOS 15 and later no longer support. ScreenCaptureKit is the
/// supported path and also gives the system's privacy indicator and "Stop Sharing" menu.
final class ScreenShareCapturer: RTCVideoCapturer, SCStreamOutput, SCStreamDelegate {
    /// Called once, on the capture queue, when the first frame arrives.
    var onStarted: (() -> Void)?
    /// Called once, on the capture queue, when capture ends without `stop()`: the window closed,
    /// the user stopped sharing from the menu bar, or capture failed.
    var onStopped: ((Error?) -> Void)?

    private static let maxLongSide: CGFloat = 1920
    private static let framesPerSecond: Int32 = 30
    /// ScreenCaptureKit only sends frames when something changes; the last one is repeated so the
    /// encoder keeps producing key frames and a still screen does not look frozen to the peer.
    private static let repeatInterval: CFTimeInterval = 0.5

    private let queue = DispatchQueue(label: "ScreenShareCapturer", qos: .userInteractive)
    private var stream: SCStream?

    // Only touched on `queue`.
    private var finished = false
    private var started = false
    private var lastPixelBuffer: CVPixelBuffer?
    private var lastDelivery: CFTimeInterval = 0
    private var repeatTimer: DispatchSourceTimer?

    func start(source: ScreenShareSource) {
        let configuration = SCStreamConfiguration()
        let filter = source.filter
        let scale = CGFloat(filter.pointPixelScale)
        var width = filter.contentRect.width * scale
        var height = filter.contentRect.height * scale
        let longSide = max(width, height)
        if longSide > Self.maxLongSide {
            width *= Self.maxLongSide / longSide
            height *= Self.maxLongSide / longSide
        }
        // Encoders want even dimensions.
        configuration.width = max(2, Int(width) & ~1)
        configuration.height = max(2, Int(height) & ~1)
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: Self.framesPerSecond)
        configuration.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        configuration.showsCursor = true
        configuration.scalesToFit = true
        configuration.queueDepth = 5
        if source.kind == .window {
            configuration.ignoreShadowsSingleWindow = true
        }

        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        do {
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        } catch {
            queue.async { self.finish(error) }
            return
        }
        self.stream = stream
        stream.startCapture { [weak self] error in
            guard let self, let error else { return }
            self.queue.async { self.finish(error) }
        }
        queue.async { self.startRepeatTimer() }
    }

    /// Stops capture without calling `onStopped`.
    func stop() {
        stream?.stopCapture { _ in }
        stream = nil
        queue.async {
            self.finished = true
            self.tearDown()
        }
    }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, !finished, sampleBuffer.isValid,
              Self.isComplete(sampleBuffer),
              let pixelBuffer = sampleBuffer.imageBuffer
        else { return }
        lastPixelBuffer = pixelBuffer
        deliver(pixelBuffer)
        if !started {
            started = true
            onStarted?()
        }
    }

    // MARK: SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { self.finish(error) }
    }

    // MARK: Private

    private func finish(_ error: Error?) {
        guard !finished else { return }
        finished = true
        tearDown()
        onStopped?(Self.isUserStop(error) ? nil : error)
    }

    private func tearDown() {
        repeatTimer?.cancel()
        repeatTimer = nil
        lastPixelBuffer = nil
    }

    private func startRepeatTimer() {
        guard !finished, repeatTimer == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.repeatInterval, repeating: Self.repeatInterval / 2)
        timer.setEventHandler { [weak self] in
            guard let self, !self.finished, let buffer = self.lastPixelBuffer,
                  CACurrentMediaTime() - self.lastDelivery >= Self.repeatInterval
            else { return }
            self.deliver(buffer)
        }
        timer.resume()
        repeatTimer = timer
    }

    private func deliver(_ pixelBuffer: CVPixelBuffer) {
        let now = CACurrentMediaTime()
        lastDelivery = now
        let frame = RTCVideoFrame(
            buffer: RTCCVPixelBuffer(pixelBuffer: pixelBuffer),
            rotation: ._0,
            timeStampNs: Int64(now * 1_000_000_000)
        )
        delegate?.capturer(self, didCapture: frame)
    }

    /// Idle and blank samples carry no new image.
    private static func isComplete(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              let status = SCFrameStatus(rawValue: rawStatus)
        else { return false }
        return status == .complete
    }

    private static func isUserStop(_ error: Error?) -> Bool {
        guard let error = error as NSError?, error.domain == SCStreamErrorDomain else { return false }
        return error.code == SCStreamError.Code.userStopped.rawValue
    }
}
