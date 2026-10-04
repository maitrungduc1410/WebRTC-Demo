//
//  EffectsSheet.swift
//  WebRTCDemo
//

import SwiftUI

/// Picks a background (blur, picture or video) and a face sticker, with a live preview.
struct EffectsSheet: View {
    let model: CallViewModel
    #if os(macOS)
    var onClose: (() -> Void)?
    #endif

    private enum Kind: String, CaseIterable {
        case backgrounds = "Backgrounds"
        case filters = "Filters"
    }

    @State private var tab = Kind.backgrounds

    private var catalog: EffectsCatalog { model.effectsCatalog }
    private var enabled: Bool { model.effectsAvailable && model.sharing == .none }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Backgrounds and effects")
                        .font(.title3.weight(.semibold))
                    Text("Only your camera changes. Others in the call see what your preview shows.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
                .padding(.horizontal, 4)
                #if os(macOS)
                .padding(.trailing, onClose == nil ? 0 : 32)
                .frame(maxWidth: .infinity, alignment: .leading)
                .overlay(alignment: .topTrailing) {
                    if let onClose {
                        Button(action: onClose) {
                            Image(systemName: "xmark")
                                .font(.footnote.weight(.bold))
                                .frame(width: 28, height: 28)
                                .contentShape(.circle)
                        }
                        .buttonStyle(.plain)
                        .glassEffect(.regular.interactive(), in: .circle)
                        .help("Close (Esc)")
                        .accessibilityLabel("Close backgrounds and effects")
                    }
                }
                #endif

                preview

                Picker("Kind", selection: $tab) {
                    ForEach(Kind.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                #if os(macOS)
                .labelsHidden()
                .frame(maxWidth: .infinity)
                #endif

                Group {
                    switch tab {
                    case .backgrounds: backgroundsGrid
                    case .filters: stickersGrid
                    }
                }
                .disabled(!enabled)

                #if os(macOS)
                if tab == .backgrounds && !catalog.backgrounds.contains(where: { $0.kind == .image || $0.kind == .video }) {
                    Text("Add pictures and videos to the effects folder to see them here.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 4)
                }
                #endif
            }
            .padding(20)
        }
        .scrollBounceBehavior(.basedOnSize)
    }

    private var preview: some View {
        #if os(iOS)
        let mirror = model.frontCamera
        #endif
        return ZStack {
            Color.black
            if enabled && model.cameraOn {
                #if os(iOS)
                VideoView(track: model.localTrack, fill: true)
                    .scaleEffect(x: mirror ? -1 : 1, y: 1)
                #else
                // SwiftUI transforms and clips don't reliably reach the hosted NSView.
                VideoView(track: model.localTrack, fill: true, mirror: true, cornerRadius: 24)
                #endif
            } else {
                Text(previewMessage)
                    .font(.subheadline)
                    .foregroundStyle(.white.opacity(0.7))
            }
            if model.effectsStatus == .loading {
                ProgressView()
                    .controlSize(.large)
                    .tint(.white)
            }
        }
        .frame(height: 220)
        .clipShape(.rect(cornerRadius: 24, style: .continuous))
        .accessibilityLabel("Your camera preview")
    }

    private var previewMessage: String {
        if !model.effectsAvailable { return "Effects need a camera" }
        if model.sharing != .none { return "Effects are paused while you present" }
        return "Your camera is off"
    }

    private var backgroundsGrid: some View {
        let columns = [GridItem(.adaptive(minimum: 100), spacing: 10)]
        let blur = catalog.backgrounds.filter { $0.kind == .none || $0.kind == .blur }
        let pictures = catalog.backgrounds.filter { $0.kind == .image }
        let videos = catalog.backgrounds.filter { $0.kind == .video }
        return LazyVGrid(columns: columns, alignment: .leading, spacing: 10) {
            section("Blur", blur)
            section("Pictures", pictures)
            section("Videos", videos)
        }
    }

    @ViewBuilder
    private func section(_ title: String, _ options: [BackgroundOption]) -> some View {
        if !options.isEmpty {
            Section {
                ForEach(options) { option in
                    EffectTile(label: option.name, selected: model.effects.background == option.id, aspect: 16 / 9) {
                        var next = model.effects
                        next.background = option.id
                        model.setEffects(next)
                    } content: {
                        backgroundContent(option)
                    }
                }
            } header: {
                Text(title)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 4)
                    .padding(.top, 4)
                    #if os(macOS)
                    .accessibilityAddTraits(.isHeader)
                    #endif
            }
        }
    }

    @ViewBuilder
    private func backgroundContent(_ option: BackgroundOption) -> some View {
        if let thumbnail = option.thumbnail {
            AssetImage(url: thumbnail, fill: true)
                .overlay(alignment: .bottomTrailing) {
                    if option.kind == .video {
                        Image(systemName: "play.fill")
                            .font(.caption2.weight(.bold))
                            .foregroundStyle(.white)
                            .frame(width: 22, height: 22)
                            .background(.black.opacity(0.6), in: .circle)
                            .padding(6)
                    }
                }
        } else {
            VStack(spacing: 4) {
                Image(systemName: option.kind == .none ? "nosign" : option.id == "blur-strong" ? "circle.dotted.circle.fill" : "circle.dotted.circle")
                    .font(.title3)
                Text(option.name)
                    .font(.caption.weight(.medium))
            }
            .foregroundStyle(.primary)
        }
    }

    private var stickersGrid: some View {
        let columns = [GridItem(.adaptive(minimum: 72), spacing: 10)]
        return LazyVGrid(columns: columns, spacing: 10) {
            EffectTile(label: "No filter", selected: model.effects.sticker == nil, aspect: 1) {
                var next = model.effects
                next.sticker = nil
                model.setEffects(next)
            } content: {
                Image(systemName: "nosign")
                    .font(.title3)
                    .foregroundStyle(.primary)
            }
            ForEach(catalog.stickers) { sticker in
                EffectTile(label: sticker.name, selected: model.effects.sticker == sticker.id, aspect: 1) {
                    var next = model.effects
                    next.sticker = sticker.id
                    model.setEffects(next)
                } content: {
                    AssetImage(url: sticker.file, fill: false)
                        .padding(8)
                }
            }
        }
    }
}

private struct EffectTile<Content: View>: View {
    let label: String
    let selected: Bool
    let aspect: CGFloat
    let action: () -> Void
    @ViewBuilder let content: () -> Content

    @Environment(\.isEnabled) private var isEnabled
    #if os(macOS)
    @State private var hovering = false
    #endif

    var body: some View {
        Button(action: action) {
            Color.clear
                .aspectRatio(aspect, contentMode: .fit)
                #if os(macOS)
                .overlay { content().scaleEffect(hovering && isEnabled ? 1.06 : 1) }
                #else
                .overlay { content() }
                #endif
                .background(.fill.tertiary)
                .clipShape(.rect(cornerRadius: 16, style: .continuous))
                .overlay {
                    if selected {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .strokeBorder(Color.accentColor, lineWidth: 3)
                    }
                }
                #if os(macOS)
                .overlay {
                    if hovering && isEnabled && !selected {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .strokeBorder(.white.opacity(0.3), lineWidth: 1)
                    }
                }
                #endif
                .scaleEffect(selected ? 0.94 : 1)
                .opacity(isEnabled ? 1 : 0.4)
                .contentShape(.rect(cornerRadius: 16, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .sensoryFeedback(.selection, trigger: selected) { _, now in now }
        .animation(.spring(response: 0.35, dampingFraction: 0.75), value: selected)
        #if os(macOS)
        .onHover { hovering = $0 }
        .help(label)
        .animation(.easeOut(duration: 0.2), value: hovering)
        #endif
    }
}

/// A bundled picture, decoded small and off the main thread.
private struct AssetImage: View {
    let url: URL
    let fill: Bool

    #if os(iOS)
    @State private var image: UIImage?
    #else
    @State private var image: NSImage?
    #endif

    var body: some View {
        GeometryReader { proxy in
            if let image {
                Image(platformImage: image)
                    .resizable()
                    .aspectRatio(contentMode: fill ? .fill : .fit)
                    .frame(width: proxy.size.width, height: proxy.size.height)
                    .clipped()
            }
        }
        .task(id: url) {
            image = await Task.detached(priority: .userInitiated) { [url] in
                #if os(iOS)
                EffectsCatalog.decode(url, maxSide: 320).map { UIImage(cgImage: $0) }
                #else
                EffectsCatalog.decode(url, maxSide: 320).map { NSImage(cgImage: $0, size: .zero) }
                #endif
            }.value
        }
    }
}
