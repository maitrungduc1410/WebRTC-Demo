//
//  ScreenSharePicker.swift
//  WebRTCDemoMac
//

import AppKit
import Observation
import ScreenCaptureKit
import SwiftUI

/// Loads the displays and windows that can be shared, with live thumbnails.
@MainActor
@Observable
final class ScreenSharePickerModel {
    enum State: Equatable {
        case loading, denied, loaded
        case failed(String)
    }

    private(set) var state: State = .loading
    private(set) var displays: [ScreenShareSource] = []
    private(set) var windows: [ScreenShareSource] = []
    private(set) var thumbnails: [String: NSImage] = [:]

    /// Windows that are part of the system rather than something a person would present.
    private static let systemBundleIDs: Set<String> = [
        "com.apple.dock", "com.apple.WindowManager", "com.apple.controlcenter",
        "com.apple.notificationcenterui", "com.apple.Spotlight", "com.apple.wallpaper.agent",
    ]

    func load() async {
        state = .loading
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
            let ownBundleID = Bundle.main.bundleIdentifier
            let ownApps = content.applications.filter { $0.bundleIdentifier == ownBundleID }

            let mainDisplay = CGMainDisplayID()
            displays = content.displays
                .sorted { ($0.displayID == mainDisplay ? 0 : 1, $0.frame.minX) < ($1.displayID == mainDisplay ? 0 : 1, $1.frame.minX) }
                .enumerated()
                .map { index, display in
                    ScreenShareSource(
                        id: "display-\(display.displayID)",
                        kind: .display,
                        title: Self.screenName(for: display.displayID) ?? "Display \(index + 1)",
                        subtitle: "\(display.width) × \(display.height)",
                        filter: SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])
                    )
                }

            windows = content.windows
                .filter { window in
                    guard let app = window.owningApplication else { return false }
                    return window.windowLayer == 0
                        && window.frame.width >= 120 && window.frame.height >= 80
                        && app.bundleIdentifier != ownBundleID
                        && !Self.systemBundleIDs.contains(app.bundleIdentifier)
                }
                .map { window in
                    let app = window.owningApplication
                    let appName = app?.applicationName ?? ""
                    let title = window.title.flatMap { $0.isEmpty ? nil : $0 } ?? appName
                    return ScreenShareSource(
                        id: "window-\(window.windowID)",
                        kind: .window,
                        title: title,
                        subtitle: title == appName ? nil : appName,
                        appIcon: app.flatMap { NSRunningApplication(processIdentifier: $0.processID)?.icon },
                        filter: SCContentFilter(desktopIndependentWindow: window)
                    )
                }
                .sorted {
                    ($0.subtitle ?? $0.title).localizedStandardCompare($1.subtitle ?? $1.title) == .orderedAscending
                }

            state = .loaded
            await loadThumbnails()
        } catch {
            state = CGPreflightScreenCaptureAccess() ? .failed(error.localizedDescription) : .denied
        }
    }

    func requestAccess() {
        if !CGRequestScreenCaptureAccess() {
            if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
                NSWorkspace.shared.open(url)
            }
        }
    }

    private func loadThumbnails() async {
        await withTaskGroup(of: (String, NSImage?).self) { group in
            for source in displays + windows {
                let filter = source.filter
                let id = source.id
                group.addTask {
                    (id, await Self.thumbnail(for: filter))
                }
            }
            for await (id, image) in group {
                if let image { thumbnails[id] = image }
            }
        }
    }

    private nonisolated static func thumbnail(for filter: SCContentFilter) async -> NSImage? {
        let configuration = SCStreamConfiguration()
        let rect = filter.contentRect
        let scale = min(480 / max(rect.width, 1), 300 / max(rect.height, 1))
        configuration.width = max(2, Int(rect.width * scale * 2))
        configuration.height = max(2, Int(rect.height * scale * 2))
        configuration.showsCursor = false
        configuration.ignoreShadowsSingleWindow = true
        guard let image = try? await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration) else {
            return nil
        }
        return NSImage(cgImage: image, size: NSSize(width: image.width / 2, height: image.height / 2))
    }

    private static func screenName(for displayID: CGDirectDisplayID) -> String? {
        NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == displayID
        }?.localizedName
    }
}

/// A sheet for choosing what to share: whole displays or single windows, with thumbnails.
struct ScreenSharePicker: View {
    let onPick: (ScreenShareSource) -> Void
    let onCancel: () -> Void

    @State private var model = ScreenSharePickerModel()
    @State private var kind: ScreenShareSource.Kind = .display
    @State private var selection: ScreenShareSource?

    private var sources: [ScreenShareSource] {
        kind == .display ? model.displays : model.windows
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            Divider()
            footer
        }
        .frame(width: 780, height: 560)
        .task { await model.load() }
        .onChange(of: kind) { selection = nil }
    }

    private var header: some View {
        VStack(spacing: 14) {
            VStack(spacing: 4) {
                Text("Share your screen")
                    .font(.title2.weight(.semibold))
                Text("The other person sees what you pick, live.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Picker("Source", selection: $kind) {
                Label("Screens", systemImage: "display").tag(ScreenShareSource.Kind.display)
                Label("Windows", systemImage: "macwindow.on.rectangle").tag(ScreenShareSource.Kind.window)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 260)
        }
        .padding(.top, 22)
        .padding(.bottom, 16)
    }

    @ViewBuilder
    private var content: some View {
        switch model.state {
        case .loading:
            ProgressView()
                .controlSize(.large)
        case .denied:
            ContentUnavailableView {
                Label("Screen recording is off", systemImage: "rectangle.dashed.badge.record")
            } description: {
                Text("Allow WebRTC Demo under Screen & System Audio Recording in System Settings, then try again.")
            } actions: {
                HStack {
                    Button("Open System Settings", action: model.requestAccess)
                        .buttonStyle(.glassProminent)
                    Button("Try Again") { Task { await model.load() } }
                        .buttonStyle(.glass)
                }
            }
        case .failed(let message):
            ContentUnavailableView {
                Label("Couldn't list screens", systemImage: "exclamationmark.triangle")
            } description: {
                Text(message)
            } actions: {
                Button("Try Again") { Task { await model.load() } }
                    .buttonStyle(.glass)
            }
        case .loaded:
            if sources.isEmpty {
                ContentUnavailableView(
                    kind == .display ? "No screens" : "No windows",
                    systemImage: kind == .display ? "display" : "macwindow",
                    description: Text(kind == .display ? "No display can be captured." : "Open the window you want to share, then come back.")
                )
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 210, maximum: 340), spacing: 18)], spacing: 18) {
                        ForEach(sources) { source in
                            SourceTile(
                                source: source,
                                thumbnail: model.thumbnails[source.id],
                                selected: selection == source
                            )
                            .onTapGesture(count: 2) { onPick(source) }
                            .onTapGesture { selection = source }
                        }
                    }
                    .padding(22)
                }
                .animation(.snappy, value: model.thumbnails.count)
            }
        }
    }

    private var footer: some View {
        HStack {
            if let selection {
                Label(selection.title, systemImage: selection.kind == .display ? "display" : "macwindow")
                    .lineLimit(1)
                    .foregroundStyle(.secondary)
                    .transition(.opacity)
            }
            Spacer()
            Button("Cancel", role: .cancel, action: onCancel)
                .keyboardShortcut(.cancelAction)
            Button("Share") {
                if let selection { onPick(selection) }
            }
            .buttonStyle(.glassProminent)
            .keyboardShortcut(.defaultAction)
            .disabled(selection == nil)
        }
        .controlSize(.large)
        .padding(.horizontal, 22)
        .padding(.vertical, 16)
        .animation(.snappy, value: selection)
    }
}

private struct SourceTile: View {
    let source: ScreenShareSource
    let thumbnail: NSImage?
    let selected: Bool

    @State private var hovering = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ZStack {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(.quaternary)
                if let thumbnail {
                    Image(nsImage: thumbnail)
                        .resizable()
                        .scaledToFit()
                        .clipShape(.rect(cornerRadius: 6, style: .continuous))
                        .shadow(color: .black.opacity(0.25), radius: 6, y: 2)
                        .padding(10)
                        .transition(.opacity)
                } else {
                    Image(systemName: source.kind == .display ? "display" : "macwindow")
                        .font(.largeTitle)
                        .foregroundStyle(.tertiary)
                }
            }
            .aspectRatio(16 / 10, contentMode: .fit)
            .overlay {
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(selected ? Color.accentColor : .clear, lineWidth: 3)
            }

            HStack(spacing: 8) {
                if let icon = source.appIcon {
                    Image(nsImage: icon)
                        .resizable()
                        .frame(width: 20, height: 20)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(source.title)
                        .font(.callout.weight(.medium))
                        .lineLimit(1)
                    if let subtitle = source.subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
            }
            .padding(.horizontal, 4)
        }
        .padding(8)
        .background(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(selected ? Color.accentColor.opacity(0.14) : (hovering ? Color.primary.opacity(0.06) : .clear))
        )
        .contentShape(.rect(cornerRadius: 16))
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.15), value: hovering)
        .animation(.snappy, value: selected)
        .help(source.subtitle.map { "\(source.title) — \($0)" } ?? source.title)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }
}
