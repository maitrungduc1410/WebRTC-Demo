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
}

private struct RootView: View {
    @State private var call: CallRoute?

    var body: some View {
        NavigationStack {
            LobbyView { roomId, e2ee in
                call = CallRoute(roomId: roomId, e2ee: e2ee)
            }
            .toolbar(.hidden, for: .navigationBar)
        }
        .fullScreenCover(item: $call) { route in
            CallView(roomId: route.roomId, e2ee: route.e2ee) {
                call = nil
            }
        }
    }
}
