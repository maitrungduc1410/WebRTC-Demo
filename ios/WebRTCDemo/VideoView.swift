//
//  VideoView.swift
//  WebRTCDemo
//

import SwiftUI
import WebRTC

/// Renders a WebRTC video track. `fill` crops to the view; otherwise the whole frame is letterboxed.
struct VideoView: UIViewRepresentable {
    let track: RTCVideoTrack?
    var fill = true
    var onVideoSize: ((CGSize) -> Void)?

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeUIView(context: Context) -> RTCMTLVideoView {
        let view = RTCMTLVideoView(frame: .zero)
        view.delegate = context.coordinator
        view.clipsToBounds = true
        view.backgroundColor = .clear
        return view
    }

    func updateUIView(_ view: RTCMTLVideoView, context: Context) {
        let coordinator = context.coordinator
        coordinator.onVideoSize = onVideoSize
        view.videoContentMode = fill ? .scaleAspectFill : .scaleAspectFit
        if coordinator.track !== track {
            coordinator.track?.remove(view)
            track?.add(view)
            coordinator.track = track
        }
    }

    static func dismantleUIView(_ view: RTCMTLVideoView, coordinator: Coordinator) {
        coordinator.track?.remove(view)
        coordinator.track = nil
    }

    final class Coordinator: NSObject, RTCVideoViewDelegate {
        var track: RTCVideoTrack?
        var onVideoSize: ((CGSize) -> Void)?

        func videoView(_ videoView: RTCVideoRenderer, didChangeVideoSize size: CGSize) {
            DispatchQueue.main.async { [weak self] in
                self?.onVideoSize?(size)
            }
        }
    }
}

/// A remote video (the 1:1 stage or a group tile), animating between filling its bounds and fitting inside them. The video view
/// always has the frame's aspect ratio, just large enough to cover the bounds, and fit only scales
/// it down, so the Metal view is never resized mid animation.
struct StageVideoView: View {
    let track: RTCVideoTrack?
    let fill: Bool
    var onVideoSize: (CGSize) -> Void = { _ in }

    @State private var videoSize: CGSize = .zero

    var body: some View {
        GeometryReader { proxy in
            let bounds = proxy.size
            let cover = Self.coverSize(for: videoSize, in: bounds)
            let fitScale = min(bounds.width / max(cover.width, 1), bounds.height / max(cover.height, 1))
            VideoView(track: track, fill: true) { size in
                videoSize = size
                onVideoSize(size)
            }
                .frame(width: cover.width, height: cover.height)
                .scaleEffect(fill ? 1 : fitScale)
                .position(x: bounds.width / 2, y: bounds.height / 2)
        }
        .clipped()
        .animation(.spring(response: 0.5, dampingFraction: 0.86), value: fill)
    }

    private static func coverSize(for video: CGSize, in bounds: CGSize) -> CGSize {
        guard video.width > 0, video.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = max(bounds.width / video.width, bounds.height / video.height)
        return CGSize(width: video.width * scale, height: video.height * scale)
    }
}

/// A video sink that keeps a tiny copy of the latest frame (every 500 ms) to show blurred while
/// the video is paused. Near-black frames, like the ones a disabled track produces, are skipped
/// so the last real picture survives.
final class FrameSnapshotter: NSObject, RTCVideoRenderer {
    var onSnapshot: ((UIImage) -> Void)?

    private static let width = 36
    private static let interval: CFTimeInterval = 0.5
    private static let minAverageLuma = 20

    private var lastCapture: CFTimeInterval = 0

    func setSize(_ size: CGSize) {}

    func renderFrame(_ frame: RTCVideoFrame?) {
        guard let frame else { return }
        let now = CACurrentMediaTime()
        guard now - lastCapture >= Self.interval else { return }
        lastCapture = now

        guard let image = Self.makeImage(from: frame) else { return }
        DispatchQueue.main.async { [weak self] in
            self?.onSnapshot?(image)
        }
    }

    /// Nearest-neighbour downscale of the I420 planes with a BT.601 YUV to RGB conversion.
    private static func makeImage(from frame: RTCVideoFrame) -> UIImage? {
        let buffer = frame.buffer.toI420()
        let srcWidth = Int(buffer.width)
        let srcHeight = Int(buffer.height)
        guard srcWidth > 0, srcHeight > 0 else { return nil }

        let width = min(Self.width, srcWidth)
        let height = max(1, srcHeight * width / srcWidth)
        let strideY = Int(buffer.strideY)
        let strideU = Int(buffer.strideU)
        let strideV = Int(buffer.strideV)
        let dataY = buffer.dataY
        let dataU = buffer.dataU
        let dataV = buffer.dataV

        var pixels = [UInt8](repeating: 255, count: width * height * 4)
        var lumaSum = 0
        for y in 0..<height {
            let sy = y * srcHeight / height
            for x in 0..<width {
                let sx = x * srcWidth / width
                let luma = Int(dataY[sy * strideY + sx])
                let u = Int(dataU[(sy / 2) * strideU + sx / 2]) - 128
                let v = Int(dataV[(sy / 2) * strideV + sx / 2]) - 128
                lumaSum += luma

                let c = max(luma - 16, 0) * 298
                let i = (y * width + x) * 4
                pixels[i] = clamp((c + 409 * v + 128) >> 8)
                pixels[i + 1] = clamp((c - 100 * u - 208 * v + 128) >> 8)
                pixels[i + 2] = clamp((c + 516 * u + 128) >> 8)
            }
        }
        guard lumaSum / (width * height) >= Self.minAverageLuma else { return nil }

        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let cgImage = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                provider: provider,
                decode: nil,
                shouldInterpolate: true,
                intent: .defaultIntent
              ) else { return nil }

        return UIImage(cgImage: cgImage, scale: 1, orientation: orientation(for: frame.rotation))
    }

    private static func clamp(_ value: Int) -> UInt8 {
        UInt8(min(max(value, 0), 255))
    }

    private static func orientation(for rotation: RTCVideoRotation) -> UIImage.Orientation {
        switch rotation {
        case ._90: return .right
        case ._180: return .down
        case ._270: return .left
        default: return .up
        }
    }
}
