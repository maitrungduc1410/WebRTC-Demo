//
//  SFUServer.swift
//  WebRTCDemo
//

import Foundation

/// The `sfu-server` group calls go through. Edited in the lobby and saved apart from the signaling server.
enum SFUServer {
    static let defaultPort = 4001

    /// Port 4001 on the default signaling host.
    static let defaultURL: String = {
        var components = URLComponents(string: SignalingServer.defaultURL)!
        components.port = defaultPort
        return components.string!
    }()

    /// UserDefaults key; only set when the address differs from the default.
    static let key = "sfuServer"

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

    /// Same rules as `SignalingServer.normalize`, plus: `ws://` / `wss://` are accepted and stored as
    /// `http://` / `https://`, and a missing port becomes 4001.
    static func normalize(_ input: String) -> String? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        let lowercased = text.lowercased()
        if lowercased.hasPrefix("ws://") {
            text = "http://" + text.dropFirst("ws://".count)
        } else if lowercased.hasPrefix("wss://") {
            text = "https://" + text.dropFirst("wss://".count)
        }
        guard let normalized = SignalingServer.normalize(text),
              var components = URLComponents(string: normalized) else { return nil }
        if components.port == nil {
            components.port = defaultPort
        }
        return components.string
    }

    /// The WebSocket endpoint, `ws://host:port/ws`, of an address saved by `normalize`.
    static func webSocketURL(for address: String) -> URL? {
        guard var components = URLComponents(string: address), components.host != nil else { return nil }
        components.scheme = components.scheme == "https" ? "wss" : "ws"
        components.path = "/ws"
        return components.url
    }
}
