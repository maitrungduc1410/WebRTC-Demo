//
//  VideoView.swift
//  WebRTCDemo
//

import SwiftUI
import WebRTC

#if os(iOS)
import UIKit
typealias PlatformImage = UIImage
#else
import AppKit
typealias PlatformImage = NSImage
#endif

extension Image {
    init(platformImage: PlatformImage) {
        #if os(iOS)
        self.init(uiImage: platformImage)
        #else
        self.init(nsImage: platformImage)
        #endif
    }
}

#if os(iOS)
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
#else
/// Renders a WebRTC video track. `fill` crops to the view; otherwise the whole frame is letterboxed.
///
/// The macOS `RTCMTLVideoView` has no content mode, so `VideoHostView` lays it out at the frame's
/// aspect ratio itself and clips it with its layer. Clipping, rounding and mirroring all happen in
/// AppKit, so they do not depend on how SwiftUI composites hosted views.
struct VideoView: NSViewRepresentable {
    let track: RTCVideoTrack?
    var fill = true
    var mirror = false
    var cornerRadius: CGFloat = 0
    var onVideoSize: ((CGSize) -> Void)?

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> VideoHostView {
        let view = VideoHostView()
        view.videoView.delegate = context.coordinator
        context.coordinator.host = view
        return view
    }

    func updateNSView(_ view: VideoHostView, context: Context) {
        let coordinator = context.coordinator
        coordinator.onVideoSize = onVideoSize
        view.fill = fill
        view.mirror = mirror
        view.setCornerRadius(cornerRadius, animated: context.transaction.animation != nil)
        if coordinator.track !== track {
            coordinator.track?.remove(view.videoView)
            track?.add(view.videoView)
            coordinator.track = track
        }
    }

    static func dismantleNSView(_ view: VideoHostView, coordinator: Coordinator) {
        coordinator.track?.remove(view.videoView)
        coordinator.track = nil
    }

    final class Coordinator: NSObject, RTCVideoViewDelegate {
        var track: RTCVideoTrack?
        var onVideoSize: ((CGSize) -> Void)?
        weak var host: VideoHostView?

        func videoView(_ videoView: RTCVideoRenderer, didChangeVideoSize size: CGSize) {
            DispatchQueue.main.async { [weak self] in
                self?.host?.videoSize = size
                self?.onVideoSize?(size)
            }
        }
    }
}

final class VideoHostView: NSView {
    let videoView = RTCMTLVideoView(frame: .zero)

    var videoSize: CGSize = .zero {
        didSet { if videoSize != oldValue { needsLayout = true } }
    }
    var fill = true {
        didSet {
            guard fill != oldValue else { return }
            animatesNextFit = window != nil
            needsLayout = true
        }
    }
    private var animatesNextFit = false
    var mirror = false {
        didSet { if mirror != oldValue { needsLayout = true } }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.masksToBounds = true
        layer?.cornerCurve = .continuous
        layer?.backgroundColor = NSColor.clear.cgColor
        addSubview(videoView)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override var isFlipped: Bool { true }

    /// Video is decoration: clicks and drags go to the SwiftUI content around it.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func setCornerRadius(_ radius: CGFloat, animated: Bool) {
        guard let layer, layer.cornerRadius != radius else { return }
        if animated {
            let animation = CABasicAnimation(keyPath: "cornerRadius")
            animation.fromValue = layer.cornerRadius
            animation.toValue = radius
            animation.duration = 0.45
            animation.timingFunction = CAMediaTimingFunction(name: .easeOut)
            layer.add(animation, forKey: "cornerRadius")
        }
        layer.cornerRadius = radius
    }

    /// The video view always covers the bounds; fit scales it down with a transform, so switching
    /// between fit and fill animates without resizing the Metal drawable.
    override func layout() {
        super.layout()
        let size = Self.coverSize(for: videoSize, in: bounds.size)
        let frame = CGRect(
            x: (bounds.width - size.width) / 2,
            y: (bounds.height - size.height) / 2,
            width: size.width,
            height: size.height
        )
        let fitScale = min(bounds.width / max(size.width, 1), bounds.height / max(size.height, 1))
        let scale = fill ? 1 : fitScale
        // View layers are anchored at their origin on macOS, so scale and flip around the center explicitly.
        let transform = CATransform3DMakeAffineTransform(
            CGAffineTransform(translationX: size.width / 2, y: size.height / 2)
                .scaledBy(x: mirror ? -scale : scale, y: scale)
                .translatedBy(x: -size.width / 2, y: -size.height / 2)
        )
        guard let layer = videoView.layer else {
            videoView.frame = frame
            return
        }
        let previous = layer.transform
        // The transform is reset first so AppKit lays the view out untransformed.
        layer.transform = CATransform3DIdentity
        videoView.frame = frame
        if animatesNextFit && !CATransform3DEqualToTransform(previous, transform) {
            let spring = CASpringAnimation(keyPath: "transform")
            spring.fromValue = previous
            spring.toValue = transform
            spring.mass = 1
            spring.stiffness = 160
            spring.damping = 22
            spring.duration = spring.settlingDuration
            layer.add(spring, forKey: "fit")
        }
        animatesNextFit = false
        layer.transform = transform
    }

    private static func coverSize(for video: CGSize, in bounds: CGSize) -> CGSize {
        guard video.width > 0, video.height > 0, bounds.width > 0, bounds.height > 0 else { return bounds }
        let scale = max(bounds.width / video.width, bounds.height / video.height)
        return CGSize(width: video.width * scale, height: video.height * scale)
    }
}
#endif

/// A remote video (the 1:1 stage or a group tile), animating between filling its bounds and fitting inside them. The video view
/// always has the frame's aspect ratio, just large enough to cover the bounds, and fit only scales
/// it down, so the Metal view is never resized mid animation.
struct StageVideoView: View {
    let track: RTCVideoTrack?
    let fill: Bool
    var onVideoSize: (CGSize) -> Void = { _ in }

    @State private var videoSize: CGSize = .zero

    var body: some View {
        #if os(macOS)
        VideoView(track: track, fill: fill, onVideoSize: onVideoSize)
        #else
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
        #endif
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
    var onSnapshot: ((PlatformImage) -> Void)?

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
    private static func makeImage(from frame: RTCVideoFrame) -> PlatformImage? {
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

        #if os(iOS)
        return UIImage(cgImage: cgImage, scale: 1, orientation: orientation(for: frame.rotation))
        #else
        guard let upright = rotated(cgImage, by: frame.rotation) else { return nil }
        return NSImage(cgImage: upright, size: CGSize(width: upright.width, height: upright.height))
        #endif
    }

    private static func clamp(_ value: Int) -> UInt8 {
        UInt8(min(max(value, 0), 255))
    }

    #if os(iOS)
    private static func orientation(for rotation: RTCVideoRotation) -> UIImage.Orientation {
        switch rotation {
        case ._90: return .right
        case ._180: return .down
        case ._270: return .left
        default: return .up
        }
    }
    #else
    /// NSImage has no orientation, so the tiny snapshot is turned upright here (clockwise, like RTCVideoRotation).
    private static func rotated(_ image: CGImage, by rotation: RTCVideoRotation) -> CGImage? {
        let angle: CGFloat
        switch rotation {
        case ._90: angle = -.pi / 2
        case ._180: angle = .pi
        case ._270: angle = .pi / 2
        default: return image
        }
        let quarterTurn = rotation == ._90 || rotation == ._270
        let width = quarterTurn ? image.height : image.width
        let height = quarterTurn ? image.width : image.height
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
        ) else { return nil }
        // Core Graphics is y-up, so a clockwise turn on screen is a negative angle here.
        context.translateBy(x: CGFloat(width) / 2, y: CGFloat(height) / 2)
        context.rotate(by: angle)
        context.draw(image, in: CGRect(x: -CGFloat(image.width) / 2, y: -CGFloat(image.height) / 2, width: CGFloat(image.width), height: CGFloat(image.height)))
        return context.makeImage()
    }
    #endif
}
