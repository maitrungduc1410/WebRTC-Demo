//
//  WebRTCDemoApp.swift
//  WebRTCDemo
//

import SwiftUI

@main
struct WebRTCDemoApp: App {
    var body: some Scene {
        WindowGroup {
            RootView()
        }
    }
}

private struct CallRoute: Identifiable {
    let id = UUID()
    let roomId: String
    let e2ee: Bool
    let group: Bool
}

private struct RootView: View {
    @State private var call: CallRoute?

    var body: some View {
        NavigationStack {
            LobbyView { roomId, e2ee, group in
                call = CallRoute(roomId: roomId, e2ee: e2ee, group: group)
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .fullScreenCover(item: $call) { route in
            if route.group {
                GroupCallView(roomId: route.roomId, e2ee: route.e2ee) {
                    call = nil
                }
            } else {
                CallView(roomId: route.roomId, e2ee: route.e2ee) {
                    call = nil
                }
            }
        }
    }
}
