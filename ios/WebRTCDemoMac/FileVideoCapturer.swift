//
//  FileVideoCapturer.swift
//  WebRTCDemoMac
//

import AVFoundation
import QuartzCore
import WebRTC

/// Plays a video file into an RTCVideoSource in real time, looping until stopped.
///
/// The macOS slice of the WebRTC framework declares RTCFileVideoCapturer in its headers but does not
/// contain it, so the Mac reads the file with AVAssetReader itself.
final class FileVideoCapturer: RTCVideoCapturer {
    enum Failure: LocalizedError {
        case noVideoTrack, noFrames

        var errorDescription: String? {
            switch self {
            case .noVideoTrack: return "The file has no video track."
            case .noFrames: return "The video could not be decoded."
            }
        }
    }

    private let lock = NSLock()
    /// Bumped by every start and stop; a playback loop runs only while it still holds the current value.
    private var generation = 0

    /// `onError` is called on a background queue if the file cannot be played.
    func startCapturing(from url: URL, onError: @escaping (Error) -> Void) {
        let token = lock.withLock {
            generation += 1
            return generation
        }
        Task.detached(priority: .userInitiated) { [weak self] in
            do {
                try await self?.play(url: url, token: token)
            } catch {
                guard let self, self.isCurrent(token) else { return }
                onError(error)
            }
        }
    }

    func stopCapture() {
        lock.withLock { generation += 1 }
    }

    private func isCurrent(_ token: Int) -> Bool {
        lock.withLock { generation == token }
    }

    private func play(url: URL, token: Int) async throws {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw Failure.noVideoTrack
        }
        let rotation = Self.rotation(for: try await track.load(.preferredTransform))
        let settings: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any](),
        ]

        while isCurrent(token) {
            let reader = try AVAssetReader(asset: asset)
            let output = AVAssetReaderTrackOutput(track: track, outputSettings: settings)
            output.alwaysCopiesSampleData = false
            reader.add(output)
            guard reader.startReading() else {
                throw reader.error ?? Failure.noFrames
            }

            let loopStart = CACurrentMediaTime()
            var firstTime: CMTime?
            var frames = 0
            while isCurrent(token), let sample = output.copyNextSampleBuffer() {
                guard let pixelBuffer = sample.imageBuffer else { continue }
                let time = sample.presentationTimeStamp
                let start = firstTime ?? time
                firstTime = start
                let wait = loopStart + (time - start).seconds - CACurrentMediaTime()
                if wait > 0 {
                    try? await Task.sleep(for: .seconds(wait))
                }
                guard isCurrent(token) else { break }
                let frame = RTCVideoFrame(
                    buffer: RTCCVPixelBuffer(pixelBuffer: pixelBuffer),
                    rotation: rotation,
                    timeStampNs: Int64(CACurrentMediaTime() * 1_000_000_000)
                )
                delegate?.capturer(self, didCapture: frame)
                frames += 1
            }
            reader.cancelReading()
            if reader.status == .failed {
                throw reader.error ?? Failure.noFrames
            }
            if frames == 0 && isCurrent(token) {
                throw Failure.noFrames
            }
        }
    }

    /// Phone videos are stored sideways with a transform that turns them upright.
    private static func rotation(for transform: CGAffineTransform) -> RTCVideoRotation {
        let degrees = Int((atan2(transform.b, transform.a) * 180 / .pi).rounded())
        switch (degrees + 360) % 360 {
        case 90: return ._90
        case 180: return ._180
        case 270: return ._270
        default: return ._0
        }
    }
}
