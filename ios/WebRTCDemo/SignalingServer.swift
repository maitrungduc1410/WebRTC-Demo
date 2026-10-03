//
//  SignalingServer.swift
//  WebRTCDemo
//

import Foundation

/// The Socket.IO server calls signal through. It can be changed in the lobby and is remembered on this device.
enum SignalingServer {
    static let defaultURL = "http://192.168.0.10:4000"

    private static let key = "signalingServer"

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

    /// Turns a typed address into `scheme://host[:port]`, or nil when it is not an http(s) server.
    /// Any path is dropped because Socket.IO would treat it as a namespace.
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
