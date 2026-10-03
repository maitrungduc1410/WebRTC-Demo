//
//  LobbyView.swift
//  WebRTCDemo
//

import SwiftUI

struct LobbyView: View {
    let onJoin: (_ roomId: String, _ e2ee: Bool) -> Void

    @State private var roomId = LobbyView.randomRoomId()
    @State private var e2ee = false
    @State private var shuffleTurns = 0.0
    @State private var joins = 0
    @State private var serverURL = SignalingServer.current
    @State private var editingServer = false
    @FocusState private var roomFieldFocused: Bool
    @Environment(\.verticalSizeClass) private var verticalSizeClass

    var body: some View {
        ZStack {
            LobbyBackdrop()
                .ignoresSafeArea()
                .onTapGesture { roomFieldFocused = false }

            ScrollView {
                Group {
                    // An iPhone on its side has room for two columns but not for the stacked layout.
                    if verticalSizeClass == .compact {
                        HStack(spacing: 40) {
                            header(spacing: 20)
                                .frame(maxWidth: .infinity)
                            form
                                .frame(maxWidth: 400)
                        }
                        .padding(.horizontal, 24)
                        .padding(.vertical, 16)
                        .frame(maxWidth: 900)
                    } else {
                        VStack(spacing: 32) {
                            header(spacing: 32)
                            form
                        }
                        .padding(24)
                        .frame(maxWidth: 460)
                    }
                }
                .frame(maxWidth: .infinity)
            }
            .defaultScrollAnchor(.center, for: .alignment)
            .scrollBounceBehavior(.basedOnSize)
            .scrollDismissesKeyboard(.interactively)
            .safeAreaInset(edge: .bottom) {
                serverButton
            }
        }
        .sheet(isPresented: $editingServer) {
            ServerSheet(current: serverURL) { url in
                SignalingServer.current = url
                serverURL = url
            }
        }
        .sensoryFeedback(.impact(weight: .medium), trigger: joins)
        .sensoryFeedback(.selection, trigger: shuffleTurns)
        .toolbar {
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button("Done") { roomFieldFocused = false }
            }
        }
    }

    private func header(spacing: CGFloat) -> some View {
        VStack(spacing: spacing) {
            HeroBadge()

            VStack(spacing: 8) {
                Text("WebRTC Demo")
                    .font(.largeTitle.weight(.bold))
                Text("Peer-to-peer video calls with chat, screen sharing and end-to-end encryption.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
        }
    }

    private var form: some View {
        GlassEffectContainer(spacing: 16) {
            VStack(spacing: 14) {
                HStack(spacing: 12) {
                    TextField("Room ID", text: $roomId)
                        .keyboardType(.numberPad)
                        .focused($roomFieldFocused)
                        .font(.title2.weight(.semibold).monospacedDigit())
                        .multilineTextAlignment(.center)
                        .padding(.vertical, 16)
                        .padding(.horizontal, 20)
                        .glassEffect(.regular.interactive(), in: .capsule)
                        .onChange(of: roomId) { _, value in
                            let digits = String(value.filter(\.isNumber).prefix(12))
                            if digits != value { roomId = digits }
                        }

                    Button {
                        withAnimation(.spring(response: 0.5, dampingFraction: 0.6)) {
                            shuffleTurns += 1
                            roomId = Self.randomRoomId()
                        }
                    } label: {
                        Image(systemName: "shuffle")
                            .font(.title3.weight(.semibold))
                            .rotationEffect(.degrees(shuffleTurns * 180))
                            .frame(width: 60, height: 60)
                            .contentShape(.circle)
                    }
                    .buttonStyle(.plain)
                    .glassEffect(.regular.interactive(), in: .circle)
                    .accessibilityLabel("Random room")
                }

                Toggle(isOn: $e2ee.animation(.snappy)) {
                    Label {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("End-to-end encryption")
                                .font(.body.weight(.medium))
                            Text(e2ee ? "Frames are encrypted on this device" : "Both peers must turn it on")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .contentTransition(.opacity)
                        }
                    } icon: {
                        Image(systemName: e2ee ? "lock.fill" : "lock.open.fill")
                            .contentTransition(.symbolEffect(.replace))
                            .foregroundStyle(e2ee ? Color(hex: 0x4ADE80) : .secondary)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.vertical, 14)
                .glassEffect(.regular, in: .rect(cornerRadius: 28, style: .continuous))

                Button(action: join) {
                    HStack(spacing: 10) {
                        Text("Join room")
                        Image(systemName: "arrow.right")
                    }
                    .font(.headline)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                }
                .buttonStyle(.glassProminent)
                .controlSize(.large)
                .disabled(roomId.isEmpty)
            }
        }
    }

    private var serverButton: some View {
        Button {
            roomFieldFocused = false
            editingServer = true
        } label: {
            HStack(spacing: 6) {
                Image(systemName: "server.rack")
                Text(serverURL)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Image(systemName: "pencil")
                    .font(.caption.weight(.semibold))
            }
            .font(.footnote)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 24)
        .accessibilityLabel("Signaling server, \(serverURL)")
        .accessibilityHint("Changes the signaling server")
    }

    private func join() {
        guard !roomId.isEmpty else { return }
        roomFieldFocused = false
        joins += 1
        onJoin(roomId, e2ee)
    }

    private static func randomRoomId() -> String {
        String(Int.random(in: 100000...999999))
    }
}

/// Edits the signaling server address. Saving normalizes it so the lobby always shows what calls will use.
private struct ServerSheet: View {
    let onSave: (String) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var text: String
    @State private var invalid = false
    @FocusState private var focused: Bool

    init(current: String, onSave: @escaping (String) -> Void) {
        self.onSave = onSave
        _text = State(initialValue: current)
    }

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 12) {
                TextField("http://192.168.1.10:4000", text: $text)
                    .keyboardType(.URL)
                    .textContentType(.URL)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .submitLabel(.done)
                    .onSubmit(save)
                    .focused($focused)
                    .font(.body.monospaced())
                    .padding(.horizontal, 20)
                    .padding(.vertical, 14)
                    .glassEffect(.regular.interactive(), in: .capsule)
                    .onChange(of: text) { invalid = false }

                Group {
                    if invalid {
                        Label("Use an address like http://192.168.1.10:4000", systemImage: "exclamationmark.circle.fill")
                            .foregroundStyle(.red)
                    } else {
                        Text("The address printed when you start the signaling server. It is saved on this device.")
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.footnote)
                .padding(.horizontal, 8)
                .transition(.opacity)

                if text != SignalingServer.defaultURL {
                    Button {
                        text = SignalingServer.defaultURL
                    } label: {
                        Label("Use default · \(SignalingServer.defaultURL)", systemImage: "arrow.counterclockwise")
                            .font(.footnote.weight(.medium))
                    }
                    .padding(.horizontal, 8)
                    .transition(.opacity)
                }

                Spacer(minLength: 0)
            }
            .padding(20)
            .animation(.snappy, value: invalid)
            .animation(.snappy, value: text == SignalingServer.defaultURL)
            .navigationTitle("Signaling server")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", role: .cancel) { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save", action: save)
                        .buttonStyle(.glassProminent)
                        .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
        }
        .presentationDetents([.medium])
        .sensoryFeedback(.error, trigger: invalid) { _, isInvalid in isInvalid }
        .onAppear { focused = true }
    }

    private func save() {
        guard let url = SignalingServer.normalize(text) else {
            invalid = true
            return
        }
        onSave(url)
        dismiss()
    }
}

/// A slowly drifting mesh gradient in the app's indigo/violet palette.
private struct LobbyBackdrop: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 30)) { context in
            let t = Float(context.date.timeIntervalSinceReferenceDate)
            MeshGradient(
                width: 3,
                height: 3,
                points: [
                    [0, 0], [0.5 + 0.1 * sin(t * 0.3), 0], [1, 0],
                    [0, 0.5 + 0.1 * cos(t * 0.25)],
                    [0.5 + 0.15 * sin(t * 0.4), 0.5 + 0.15 * cos(t * 0.35)],
                    [1, 0.5 + 0.1 * sin(t * 0.2)],
                    [0, 1], [0.5 + 0.1 * cos(t * 0.3), 1], [1, 1],
                ],
                colors: colors
            )
        }
    }

    private var colors: [Color] {
        if colorScheme == .dark {
            return [
                Color(hex: 0x0B0B1A), Color(hex: 0x1E1B4B), Color(hex: 0x0B0B1A),
                Color(hex: 0x312E81), Color(hex: 0x4C1D95), Color(hex: 0x1E3A8A),
                Color(hex: 0x0B0B1A), Color(hex: 0x3B0764), Color(hex: 0x0B0B1A),
            ]
        }
        return [
            Color(hex: 0xEEF2FF), Color(hex: 0xE0E7FF), Color(hex: 0xF5F3FF),
            Color(hex: 0xC7D2FE), Color(hex: 0xDDD6FE), Color(hex: 0xBFDBFE),
            Color(hex: 0xF5F3FF), Color(hex: 0xE9D5FF), Color(hex: 0xEEF2FF),
        ]
    }
}

/// The app mark: a glass disc with a breathing camera symbol and a slowly turning halo.
private struct HeroBadge: View {
    @State private var turning = false

    var body: some View {
        ZStack {
            Circle()
                .strokeBorder(
                    AngularGradient(
                        colors: [Color(hex: 0x818CF8), Color(hex: 0xC084FC), Color(hex: 0x60A5FA), Color(hex: 0x818CF8)],
                        center: .center
                    ),
                    lineWidth: 3
                )
                .frame(width: 128, height: 128)
                .rotationEffect(.degrees(turning ? 360 : 0))
                .blur(radius: 1)
                .opacity(0.8)

            Image(systemName: "video.fill")
                .font(.system(size: 40, weight: .semibold))
                .foregroundStyle(.white)
                .symbolEffect(.breathe)
                .frame(width: 104, height: 104)
                .glassEffect(.regular.tint(Color.accentColor.opacity(0.7)), in: .circle)
        }
        .onAppear {
            withAnimation(.linear(duration: 12).repeatForever(autoreverses: false)) {
                turning = true
            }
        }
        .accessibilityHidden(true)
    }
}

#Preview {
    LobbyView { _, _ in }
}

#Preview("Landscape", traits: .landscapeLeft) {
    LobbyView { _, _ in }
}
