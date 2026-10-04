//
//  MacSettingsView.swift
//  WebRTCDemoMac
//

import SwiftUI

/// Settings › General: the signaling server (1:1 calls) and SFU server (group calls) addresses.
/// Saving normalizes them, like the iOS server sheet.
struct MacSettingsView: View {
    var body: some View {
        Form {
            ServerAddressSection(
                title: "Signaling server",
                footer: "The address printed when you start the signaling server. 1:1 calls you join after saving use it.",
                example: "http://192.168.1.10:4000",
                defaultURL: SignalingServer.defaultURL,
                load: { SignalingServer.current },
                save: { SignalingServer.current = $0 },
                normalize: SignalingServer.normalize,
                focusOnAppear: true
            )
            ServerAddressSection(
                title: "SFU server",
                footer: "The sfu-server that group calls go through. Port 4001 is used when you leave it out; ws:// addresses work too.",
                example: "http://192.168.1.10:4001",
                defaultURL: SFUServer.defaultURL,
                load: { SFUServer.current },
                save: { SFUServer.current = $0 },
                normalize: SFUServer.normalize
            )
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct ServerAddressSection: View {
    let title: String
    let footer: String
    let example: String
    let defaultURL: String
    let load: () -> String
    let save: (String) -> Void
    let normalize: (String) -> String?
    var focusOnAppear = false

    @State private var text = ""
    @State private var current = ""
    @State private var invalid = false
    /// The address saved in this session, to confirm it until the text is edited again.
    @State private var savedURL: String?
    @FocusState private var focused: Bool

    private var unchanged: Bool {
        normalize(text) == current
    }

    var body: some View {
        Section {
            TextField("Address", text: $text, prompt: Text(defaultURL))
                .font(.body.monospaced())
                .autocorrectionDisabled()
                .focused($focused)
                .onSubmit(commit)
                .onChange(of: text) { invalid = false }

            HStack {
                statusLabel
                Spacer()
                if text != defaultURL {
                    Button("Use Default") {
                        text = defaultURL
                        commit()
                    }
                }
                Button("Save", action: commit)
                    .keyboardShortcut(focused ? .defaultAction : nil)
                    .disabled(unchanged || text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        } header: {
            Text(title)
        } footer: {
            Text(footer)
                .foregroundStyle(.secondary)
        }
        .animation(.snappy, value: invalid)
        .animation(.snappy, value: savedURL == text)
        .onAppear {
            current = load()
            text = current
            if focusOnAppear { focused = true }
        }
    }

    @ViewBuilder
    private var statusLabel: some View {
        if invalid {
            Label("Use an address like \(example)", systemImage: "exclamationmark.circle.fill")
                .foregroundStyle(.red)
                .transition(.opacity)
        } else if savedURL == text {
            Label("Saved", systemImage: "checkmark.circle.fill")
                .foregroundStyle(.green)
                .transition(.opacity)
        }
    }

    private func commit() {
        guard let url = normalize(text) else {
            invalid = true
            return
        }
        save(url)
        current = url
        text = url
        savedURL = url
    }
}
