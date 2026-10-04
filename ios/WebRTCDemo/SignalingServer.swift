//
//  SignalingServer.swift
//  WebRTCDemo
//

import Foundation

/// The signaling server 1:1 calls signal through. It can be changed in the lobby and is remembered on this device.
enum SignalingServer {
    #if os(macOS)
    // The server usually runs on the same Mac during development.
    static let defaultURL = "http://localhost:4000"
    #else
    static let defaultURL = "http://192.168.0.10:4000"
    #endif

    /// UserDefaults key; only set when the address differs from the default.
    static let key = "signalingServer"

    static var current: String {
        get { UserDefaults.standard.string(forKey: key) ?? defaultURL }
        set {
            if newValue == defaultURL {
                UserDefaults.standard.removeObject(forKey: key)
            } else {
                UserDefaults.standard.set(newValue, forKey: key)
            }
        }
    }

    /// The WebSocket endpoint, `ws://host:port/ws`, of an address saved by `normalize`.
    static func webSocketURL(for address: String) -> URL? {
        guard var components = URLComponents(string: address), components.host != nil else { return nil }
        components.scheme = components.scheme == "https" ? "wss" : "ws"
        components.path = "/ws"
        return components.url
    }

    /// Turns a typed address into `scheme://host[:port]`, or nil when it is not an http(s) server.
    /// Any path is dropped: the server takes its WebSocket on /ws.
    static func normalize(_ input: String) -> String? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if text.range(of: "^[a-zA-Z][a-zA-Z0-9+.-]*://", options: .regularExpression) == nil {
            text = "http://" + text
        }
        guard var components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty
        else { return nil }
        components.scheme = scheme
        components.user = nil
        components.password = nil
        components.path = ""
        components.query = nil
        components.fragment = nil
        return components.string
    }
}
