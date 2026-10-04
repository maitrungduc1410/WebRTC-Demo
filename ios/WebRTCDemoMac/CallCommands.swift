//
//  CallCommands.swift
//  WebRTCDemoMac
//

import SwiftUI

/// What the menu bar needs from the call in the focused window: current state plus the actions.
struct CallActions {
    var micOn: Bool
    var cameraOn: Bool
    var canToggleCamera: Bool
    var chatOpen: Bool
    var effectsOpen: Bool
    var canOpenEffects: Bool
    var fit: Bool
    var canFit: Bool
    var floating: Bool
    var canFloat: Bool
    var sharing: Bool
    var isGroup: Bool
    var canShowPeople: Bool
    var remoteAudioMuted: Bool
    var remoteVideoHidden: Bool
    /// 1:1: the other person is there. Group: always, so the choice also covers later joiners.
    var canControlRemote: Bool
    /// The chat field has focus, so single letters must reach it instead of the menu.
    var typing: Bool

    var toggleMic: @MainActor () -> Void
    var toggleCamera: @MainActor () -> Void
    var toggleChat: @MainActor () -> Void
    var openEffects: @MainActor () -> Void
    var toggleFit: @MainActor () -> Void
    var toggleFloating: @MainActor () -> Void
    var showPeople: @MainActor () -> Void
    var shareScreen: @MainActor () -> Void
    var shareFile: @MainActor () -> Void
    var stopSharing: @MainActor () -> Void
    var toggleRemoteAudio: @MainActor () -> Void
    var toggleRemoteVideo: @MainActor () -> Void
    var leave: @MainActor () -> Void
}

extension FocusedValues {
    @Entry var callActions: CallActions?
}

/// The Call menu. Single-letter shortcuts match the tooltips on the toolbar.
struct CallCommands: Commands {
    @FocusedValue(\.callActions) private var call

    var body: some Commands {
        CommandMenu("Call") {
            let keys = call.map { !$0.typing } ?? false

            Button(call?.micOn == false ? "Unmute Microphone" : "Mute Microphone") { call?.toggleMic() }
                .keyboardShortcut("m", modifiers: [])
                .disabled(!keys)
            Button(call?.cameraOn == false ? "Turn Camera On" : "Turn Camera Off") { call?.toggleCamera() }
                .keyboardShortcut("v", modifiers: [])
                .disabled(!keys || call?.canToggleCamera != true)
            // As on the web, B only opens the panel; Esc or its close button closes it.
            Button("Backgrounds and Effects…") { call?.openEffects() }
                .keyboardShortcut("b", modifiers: [])
                .disabled(!keys || call?.canOpenEffects != true)

            Divider()

            Button(call?.chatOpen == true ? "Hide Chat" : "Show Chat") { call?.toggleChat() }
                .keyboardShortcut("c", modifiers: [])
                .disabled(!keys)
            // Group tiles switch fit and fill one by one with a double click, as on the web.
            Button(call?.fit == true ? "Fill Window" : "Fit Video in Window") { call?.toggleFit() }
                .keyboardShortcut("f", modifiers: [])
                .disabled(!keys || call?.canFit != true)
            Button(call?.floating == true ? "Return to Main Window" : (call?.isGroup == true ? "Float Active Speaker on Top" : "Float Video on Top")) { call?.toggleFloating() }
                .keyboardShortcut("p", modifiers: [])
                .disabled(!keys || (call?.canFloat != true && call?.floating != true))
            if call?.isGroup == true {
                Button("Show Everyone in the Call…") { call?.showPeople() }
                    .keyboardShortcut("p", modifiers: [.command, .shift])
                    .disabled(call?.canShowPeople != true)
            }

            Divider()

            Button("Share Screen or Window…") { call?.shareScreen() }
                .keyboardShortcut("s", modifiers: [.command, .shift])
                .disabled(call == nil)
            Button("Share Video File…") { call?.shareFile() }
                .keyboardShortcut("o", modifiers: .command)
                .disabled(call == nil)
            Button("Stop Sharing") { call?.stopSharing() }
                .disabled(call?.sharing != true)

            Divider()

            if call?.isGroup == true {
                Button(call?.remoteAudioMuted == true ? "Unmute Everyone" : "Mute Everyone") { call?.toggleRemoteAudio() }
                    .disabled(call?.canControlRemote != true)
                Button(call?.remoteVideoHidden == true ? "Show Everyone's Video" : "Hide Everyone's Video") { call?.toggleRemoteVideo() }
                    .disabled(call?.canControlRemote != true)
            } else {
                Button(call?.remoteAudioMuted == true ? "Unmute Other Person" : "Mute Other Person") { call?.toggleRemoteAudio() }
                    .disabled(call?.canControlRemote != true)
                Button(call?.remoteVideoHidden == true ? "Show Their Video" : "Hide Their Video") { call?.toggleRemoteVideo() }
                    .disabled(call?.canControlRemote != true)
            }

            Divider()

            Button("Leave Call") { call?.leave() }
                .keyboardShortcut("e", modifiers: [.command, .shift])
                .disabled(call == nil)
        }
    }
}
