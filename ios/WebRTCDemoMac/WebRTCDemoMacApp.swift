//
//  WebRTCDemoMacApp.swift
//  WebRTCDemoMac
//

import AppKit
import SwiftUI

@main
struct WebRTCDemoMacApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        Window("WebRTC Demo", id: "main") {
            MacRootView()
        }
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentMinSize)
        .defaultSize(width: 1120, height: 740)
        .commands {
            CommandGroup(replacing: .newItem) {}
            // Nothing to print; this also frees ⇧⌘P (Page Setup) for the people list.
            CommandGroup(replacing: .printItem) {}
            CallCommands()
        }

        Settings {
            MacSettingsView()
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// One window is the whole app; closing it ends the call and quits.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}

private struct CallRoute: Identifiable, Equatable {
    let id = UUID()
    let roomId: String
    let e2ee: Bool
    let isGroup: Bool
}

/// The lobby, or the call it joined. The call slides in over the lobby and takes the window dark.
private struct MacRootView: View {
    @State private var call: CallRoute?
    /// The lobby leaves the hierarchy during a call, so its fields live here.
    @State private var lobby = LobbyForm()

    var body: some View {
        ZStack {
            if let call {
                MacCallView(roomId: call.roomId, e2ee: call.e2ee, isGroup: call.isGroup) {
                    self.call = nil
                }
                .id(call.id)
                .transition(.opacity.combined(with: .scale(scale: 1.03)))
            } else {
                LobbyView(form: lobby) { roomId, e2ee, group in
                    call = CallRoute(roomId: roomId, e2ee: e2ee, isGroup: group)
                }
                .transition(.opacity.combined(with: .scale(scale: 0.98)))
            }
        }
        .frame(minWidth: 680, minHeight: 500)
        .animation(.smooth(duration: 0.45), value: call)
    }
}
