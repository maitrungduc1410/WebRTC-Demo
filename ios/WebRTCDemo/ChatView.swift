//
//  ChatView.swift
//  WebRTCDemo
//

import SwiftUI

/// In-call chat: over the WebRTC data channel in a 1:1 call, over the SFU WebSocket in a group call.
struct ChatView: View {
    let model: CallViewModel

    @State private var draft = ""
    @FocusState private var inputFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            header
            ScrollView {
                LazyVStack(spacing: 8) {
                    if model.messages.isEmpty {
                        ContentUnavailableView(
                            "No messages yet",
                            systemImage: "bubble.left.and.bubble.right",
                            description: Text(model.isGroup
                                ? "Messages go to everyone in the room through the SFU server."
                                : "Messages go straight to the other device over the data channel.")
                        )
                        .padding(.top, 24)
                    }
                    ForEach(model.messages) { message in
                        MessageBubble(message: message)
                            .transition(.asymmetric(
                                insertion: .move(edge: .bottom).combined(with: .opacity),
                                removal: .opacity
                            ))
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .animation(.spring(response: 0.4, dampingFraction: 0.8), value: model.messages)
            }
            .defaultScrollAnchor(.bottom)
            .scrollDismissesKeyboard(.interactively)

            inputBar
        }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Text("Chat")
                .font(.title3.weight(.semibold))
            Spacer()
            Group {
                switch model.chat {
                case .open:
                    Label("Connected", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                case .opening:
                    HStack(spacing: 6) {
                        ProgressView().controlSize(.small)
                        Text("Opening…")
                    }
                    .foregroundStyle(.secondary)
                case .closed:
                    Label(model.phase == .connected ? "Closed" : "Waiting for peer", systemImage: "bolt.horizontal.circle")
                        .foregroundStyle(.secondary)
                }
            }
            .font(.footnote.weight(.medium))
            .contentTransition(.opacity)
            .animation(.default, value: model.chat)
        }
        .padding(.horizontal, 20)
        .padding(.top, 20)
        .padding(.bottom, 8)
    }

    private var inputBar: some View {
        let canSend = model.chat == .open && !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        return GlassEffectContainer(spacing: 8) {
            HStack(spacing: 8) {
                TextField(model.chat == .open ? "Message" : "Chat isn't connected yet", text: $draft, axis: .vertical)
                    .lineLimit(1...4)
                    .focused($inputFocused)
                    .submitLabel(.send)
                    .onSubmit(send)
                    .padding(.horizontal, 16)
                    .padding(.vertical, 12)
                    .glassEffect(.regular.interactive(), in: .rect(cornerRadius: 22, style: .continuous))
                    .disabled(model.chat != .open)

                Button(action: send) {
                    Image(systemName: "arrow.up")
                        .font(.body.weight(.bold))
                        .frame(width: 44, height: 44)
                }
                .buttonStyle(.glassProminent)
                .buttonBorderShape(.circle)
                .disabled(!canSend)
                .sensoryFeedback(.impact(weight: .light), trigger: model.messages.count)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    private func send() {
        model.sendMessage(draft)
        draft = ""
        inputFocused = true
    }
}

struct MessageBubble: View {
    let message: ChatMessage

    var body: some View {
        HStack {
            if message.isLocal { Spacer(minLength: 48) }
            VStack(alignment: .leading, spacing: 4) {
                if let sender = message.sender, !message.isLocal {
                    Text(sender)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 6)
                }
                Text(message.text)
                    .foregroundStyle(message.isLocal ? Color.white : Color.primary)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                .background(
                    message.isLocal ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.fill.secondary),
                    in: .rect(cornerRadius: 20, style: .continuous)
                )
            }
            if !message.isLocal { Spacer(minLength: 48) }
        }
    }
}
