//
//  EffectsCatalog.swift
//  WebRTCDemo
//

import CoreGraphics
import Foundation
import ImageIO

enum BackgroundKind: String {
    case none, blur, image, video
}

struct BackgroundOption: Identifiable, Equatable {
    let id: String
    let name: String
    let kind: BackgroundKind
    /// Blur radius as a fraction of the frame width.
    var blur: CGFloat = 0
    var file: URL?
    var thumbnail: URL?
}

enum StickerAnchor: String {
    case eyes, nose, mouth
}

/// Sizes and offsets are in units of the distance between the eyes; positive offsetY is up.
struct StickerOption: Identifiable, Equatable {
    let id: String
    let name: String
    let file: URL
    let anchor: StickerAnchor
    let width: CGFloat
    /// Stretches the artwork; without it the picture keeps its own aspect ratio.
    let height: CGFloat?
    let offsetX: CGFloat
    let offsetY: CGFloat
}

struct EffectsSelection: Equatable {
    var background = "none"
    var sticker: String?
}

/// The bundled backgrounds and stickers. The effects folder at the repository root is shared with
/// the web and Android apps and copied into the app bundle as a folder reference.
struct EffectsCatalog {
    let backgrounds: [BackgroundOption]
    let stickers: [StickerOption]

    static let builtIn: [BackgroundOption] = [
        BackgroundOption(id: "none", name: "None", kind: .none),
        BackgroundOption(id: "blur-light", name: "Slight blur", kind: .blur, blur: 0.008),
        BackgroundOption(id: "blur-strong", name: "Blur", kind: .blur, blur: 0.02),
    ]

    static let shared = load(root: Bundle.main.url(forResource: "effects", withExtension: nil))

    func background(_ id: String) -> BackgroundOption {
        backgrounds.first { $0.id == id } ?? backgrounds[0]
    }

    func sticker(_ id: String?) -> StickerOption? {
        guard let id else { return nil }
        return stickers.first { $0.id == id }
    }

    func hasEffects(_ selection: EffectsSelection) -> Bool {
        background(selection.background).kind != .none || sticker(selection.sticker) != nil
    }

    /// Drops anything that is no longer bundled.
    func sanitize(_ selection: EffectsSelection) -> EffectsSelection {
        EffectsSelection(background: background(selection.background).id, sticker: sticker(selection.sticker)?.id)
    }

    static func load(root: URL?) -> EffectsCatalog {
        guard let root else {
            print("effects folder not bundled")
            return EffectsCatalog(backgrounds: builtIn, stickers: [])
        }
        func file(_ path: Any?) -> URL? {
            guard let path = path as? String else { return nil }
            let url = root.appendingPathComponent(path)
            return FileManager.default.fileExists(atPath: url.path) ? url : nil
        }
        func list(_ manifest: String, _ key: String) -> [[String: Any]] {
            guard let data = try? Data(contentsOf: root.appendingPathComponent(manifest)),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [] }
            return json[key] as? [[String: Any]] ?? []
        }
        func number(_ value: Any?) -> CGFloat? {
            (value as? NSNumber).map { CGFloat($0.doubleValue) }
        }

        var reserved = Set(builtIn.map(\.id))
        var backgrounds = builtIn
        for entry in list("backgrounds.json", "backgrounds") {
            guard let id = entry["id"] as? String, !reserved.contains(id),
                  let kind = (entry["type"] as? String).flatMap(BackgroundKind.init(rawValue:)),
                  kind == .image || kind == .video,
                  let url = file(entry["file"]) else { continue }
            reserved.insert(id)
            backgrounds.append(BackgroundOption(
                id: id,
                name: entry["name"] as? String ?? id,
                kind: kind,
                file: url,
                thumbnail: file(entry["thumbnail"]) ?? (kind == .image ? url : nil)
            ))
        }

        let stickers: [StickerOption] = list("stickers.json", "stickers").compactMap { entry in
            guard let id = entry["id"] as? String, let url = file(entry["file"]) else { return nil }
            return StickerOption(
                id: id,
                name: entry["name"] as? String ?? id,
                file: url,
                anchor: (entry["anchor"] as? String).flatMap(StickerAnchor.init(rawValue:)) ?? .eyes,
                width: number(entry["width"]) ?? 1,
                height: number(entry["height"]),
                offsetX: number(entry["offsetX"]) ?? 0,
                offsetY: number(entry["offsetY"]) ?? 0
            )
        }
        return EffectsCatalog(backgrounds: backgrounds, stickers: stickers)
    }

    /// Decodes a bundled picture now, at most `maxSide` pixels on its long side.
    static func decode(_ url: URL, maxSide: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: maxSide,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }
}

/// Remembers the last choice across calls.
enum EffectsStore {
    private static let backgroundKey = "effectsBackground"
    private static let stickerKey = "effectsSticker"

    static func load(_ catalog: EffectsCatalog) -> EffectsSelection {
        let defaults = UserDefaults.standard
        return catalog.sanitize(EffectsSelection(
            background: defaults.string(forKey: backgroundKey) ?? "none",
            sticker: defaults.string(forKey: stickerKey)
        ))
    }

    static func save(_ selection: EffectsSelection) {
        let defaults = UserDefaults.standard
        defaults.set(selection.background, forKey: backgroundKey)
        if let sticker = selection.sticker {
            defaults.set(sticker, forKey: stickerKey)
        } else {
            defaults.removeObject(forKey: stickerKey)
        }
    }
}

// MARK: - Sticker placement

/// Face points in pixels, y down. The two eyes can come in either order.
struct FacePoints {
    var eyeA: CGPoint
    var eyeB: CGPoint
    var nose: CGPoint
    var mouth: CGPoint
}

/// Where to draw a sticker: its center, size and rotation (radians, clockwise on screen).
struct StickerPlacement: Equatable {
    var x: CGFloat
    var y: CGFloat
    var width: CGFloat
    var height: CGFloat
    var angle: CGFloat

    // Eyes to mouth over the distance between the eyes on a face looking at the camera. The larger
    // of the two scales is used so a sticker doesn't shrink when the head turns sideways.
    private static let eyesToMouthRatio: CGFloat = 1.2

    /// Places a sticker on a face. The face's own axes are used, so the sticker tilts with the head:
    /// "right" runs from one eye to the other, "up" from the mouth towards the eyes.
    static func place(_ face: FacePoints, sticker: StickerOption, aspect: CGFloat) -> StickerPlacement? {
        let eyes = CGPoint(x: (face.eyeA.x + face.eyeB.x) / 2, y: (face.eyeA.y + face.eyeB.y) / 2)
        let rough = CGPoint(x: eyes.x - face.mouth.x, y: eyes.y - face.mouth.y)
        // Up (0, -1) turns into right (1, 0).
        let towardsRight = CGPoint(x: -rough.y, y: rough.x)
        let inOrder = (face.eyeB.x - face.eyeA.x) * towardsRight.x + (face.eyeB.y - face.eyeA.y) * towardsRight.y >= 0
        let (left, right) = inOrder ? (face.eyeA, face.eyeB) : (face.eyeB, face.eyeA)

        let axis = CGPoint(x: right.x - left.x, y: right.y - left.y)
        let iod = hypot(axis.x, axis.y)
        guard iod >= 1 else { return nil }
        let ux = CGPoint(x: axis.x / iod, y: axis.y / iod)
        let uy = CGPoint(x: ux.y, y: -ux.x)
        let unit = max(iod, hypot(rough.x, rough.y) / eyesToMouthRatio)

        let anchor: CGPoint
        switch sticker.anchor {
        case .eyes: anchor = eyes
        case .nose: anchor = face.nose
        case .mouth: anchor = face.mouth
        }
        let width = sticker.width * unit
        return StickerPlacement(
            x: anchor.x + unit * (sticker.offsetX * ux.x + sticker.offsetY * uy.x),
            y: anchor.y + unit * (sticker.offsetX * ux.y + sticker.offsetY * uy.y),
            width: width,
            height: sticker.height.map { $0 * unit } ?? width * aspect,
            angle: atan2(ux.y, ux.x)
        )
    }
}

/// Evens out landmark jitter, and keeps the last placement through a few missed detections.
struct PlacementSmoother {
    var amount: CGFloat = 0.4
    var keepFrames = 6
    private(set) var current: StickerPlacement?
    private var missed = 0

    mutating func update(_ next: StickerPlacement?) -> StickerPlacement? {
        guard let next else {
            missed += 1
            if missed > keepFrames { current = nil }
            return current
        }
        missed = 0
        guard let prev = current else {
            current = next
            return next
        }
        let k = 1 - amount
        let delta = atan2(sin(next.angle - prev.angle), cos(next.angle - prev.angle))
        current = StickerPlacement(
            x: prev.x + (next.x - prev.x) * k,
            y: prev.y + (next.y - prev.y) * k,
            width: prev.width + (next.width - prev.width) * k,
            height: prev.height + (next.height - prev.height) * k,
            angle: prev.angle + delta * k
        )
        return current
    }

    mutating func reset() {
        current = nil
        missed = 0
    }
}
