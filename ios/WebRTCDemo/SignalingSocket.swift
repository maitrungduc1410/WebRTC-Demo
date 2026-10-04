//
//  SignalingSocket.swift
//  WebRTCDemo
//

import Foundation

/// A signaling socket of either server (1:1 signaling server or SFU): plain JSON text frames, each
/// with a "type", over a WebSocket. Every callback runs on the main queue, in the order the frames
/// arrived. There is no reconnect; the call ends with the socket.
final class SignalingSocket: NSObject {
    var onOpen: (() -> Void)?
    var onMessage: (([String: Any]) -> Void)?
    /// Called once, unless `close()` or `cancel()` came first. `opened` is false when the connection never succeeded.
    var onClose: ((_ opened: Bool) -> Void)?

    private let url: URL
    private var session: URLSession?
    private var task: URLSessionWebSocketTask?
    private var opened = false
    private var finished = false

    init(url: URL) {
        self.url = url
        super.init()
    }

    func connect() {
        guard task == nil, !finished else { return }
        let session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
        let task = session.webSocketTask(with: url)
        self.session = session
        self.task = task
        task.resume()
        receive(on: task)
    }

    func send(_ message: [String: Any], completion: (() -> Void)? = nil) {
        guard let task, !finished,
              let data = try? JSONSerialization.data(withJSONObject: message),
              let text = String(data: data, encoding: .utf8) else {
            completion?()
            return
        }
        task.send(.string(text)) { error in
            if let error {
                print("signaling: send failed: \(error)")
            }
            completion?()
        }
    }

    /// Sends `leave`, then closes the socket; no more callbacks run.
    func close() {
        guard !finished else { return }
        let task = self.task
        let session = self.session
        send(["type": "leave"]) {
            task?.cancel(with: .normalClosure, reason: nil)
            session?.invalidateAndCancel()
        }
        finished = true
    }

    /// Closes the socket without a goodbye; no more callbacks run.
    func cancel() {
        guard !finished else { return }
        finished = true
        task?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
    }

    private func receive(on task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self, task === self.task, !self.finished else { return }
                let text: String?
                switch result {
                case .success(.string(let string)):
                    text = string
                case .success(.data(let data)):
                    text = String(data: data, encoding: .utf8)
                case .success:
                    text = nil
                case .failure(let error):
                    print("signaling: receive failed: \(error)")
                    self.finish()
                    return
                }
                if let text,
                   let data = text.data(using: .utf8),
                   let message = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                    self.onMessage?(message)
                }
                // A handler may have closed the socket
                guard !self.finished else { return }
                self.receive(on: task)
            }
        }
    }

    private func finish() {
        guard !finished else { return }
        finished = true
        task?.cancel(with: .goingAway, reason: nil)
        session?.invalidateAndCancel()
        onClose?(opened)
    }
}

// MARK: - URLSessionWebSocketDelegate

extension SignalingSocket: URLSessionWebSocketDelegate {
    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didOpenWithProtocol protocol: String?
    ) {
        guard webSocketTask === task, !finished else { return }
        opened = true
        onOpen?()
    }

    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        guard webSocketTask === task else { return }
        finish()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard task === self.task, let error else { return }
        print("signaling: socket failed: \(error)")
        finish()
    }
}
