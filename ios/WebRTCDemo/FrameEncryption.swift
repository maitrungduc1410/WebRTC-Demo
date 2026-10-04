//
//  FrameEncryption.swift
//  WebRTCDemo
//

import Foundation
import Security
import WebRTC

/// E2EE shared by the 1:1 and group engines: one `RTCFrameCryptorKeyProvider` holding the shared key
/// (slot 0) and the frame cryptors attached to senders and receivers. Options must match web and
/// Android byte for byte, see ARCHITECTURE.md section 9.
final class FrameEncryption: NSObject {
    private static let ratchetSalt = "LKFrameEncryptionKey"
    private static let keyLength = 32
    private static let keyIndex: Int32 = 0

    private let factory: RTCPeerConnectionFactory
    private let keyProvider: RTCFrameCryptorKeyProvider
    // Cryptors are created from both the main and signaling threads, so access goes through the lock
    private var cryptors: [String: RTCFrameCryptor] = [:]
    private let lock = NSLock()

    init(factory: RTCPeerConnectionFactory) {
        self.factory = factory
        keyProvider = RTCFrameCryptorKeyProvider(
            ratchetSalt: Data(FrameEncryption.ratchetSalt.utf8),
            ratchetWindowSize: 0,
            sharedKeyMode: true,
            uncryptedMagicBytes: nil,
            failureTolerance: -1,
            keyRingSize: 16,
            discardFrameWhenCryptorNotReady: false,
            keyDerivationAlgorithm: RTCKeyDerivationAlgorithm(rawValue: 0)! // PBKDF2
        )
        super.init()
    }

    /// Generates new key material, installs it as the shared key and returns it so it can be sent.
    func generateKey() -> Data? {
        var bytes = [UInt8](repeating: 0, count: FrameEncryption.keyLength)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        guard status == errSecSuccess else {
            print("failed to generate encryption key: \(status)")
            return nil
        }

        let key = Data(bytes)
        keyProvider.setSharedKey(key, with: FrameEncryption.keyIndex)
        print("generated encryption key (\(key.count) bytes)")
        return key
    }

    /// Installs key material received from signaling.
    func setKey(_ key: Data) {
        if key.count != FrameEncryption.keyLength {
            print("unexpected encryption key length: \(key.count)")
        }
        keyProvider.setSharedKey(key, with: FrameEncryption.keyIndex)
        print("received encryption key (\(key.count) bytes)")
    }

    /// `scope` keeps ids unique when several peer connections share this instance.
    func attachSenderCryptors(on peerConnection: RTCPeerConnection, scope: String = "") {
        for sender in peerConnection.senders where sender.track != nil {
            attachCryptor(id: "\(scope)sender-\(sender.senderId)") { factory, keyProvider in
                RTCFrameCryptor(
                    factory: factory,
                    rtpSender: sender,
                    participantId: "local",
                    algorithm: .aesGcm,
                    keyProvider: keyProvider
                )
            }
        }
    }

    func attachReceiverCryptors(on peerConnection: RTCPeerConnection, scope: String = "") {
        for receiver in peerConnection.receivers {
            attachReceiverCryptor(receiver, scope: scope)
        }
    }

    func attachReceiverCryptor(_ receiver: RTCRtpReceiver, participantId: String = "remote", scope: String = "") {
        guard receiver.track != nil else { return }

        attachCryptor(id: "\(scope)receiver-\(receiver.receiverId)") { factory, keyProvider in
            RTCFrameCryptor(
                factory: factory,
                rtpReceiver: receiver,
                participantId: participantId,
                algorithm: .aesGcm,
                keyProvider: keyProvider
            )
        }
    }

    /// Call only after the peer connection is closed: in M150 a disabled cryptor would forward frames unencrypted.
    func disposeCryptors() {
        lock.lock()
        let all = Array(cryptors.values)
        cryptors.removeAll()
        lock.unlock()

        all.forEach { $0.delegate = nil }
        if !all.isEmpty {
            print("disposed \(all.count) frame cryptors")
        }
    }

    // Sender cryptors are created on the main thread and block on the signaling thread,
    // receiver cryptors are created on the signaling thread, so the lock is not held while creating.
    private func attachCryptor(
        id: String,
        create: (RTCPeerConnectionFactory, RTCFrameCryptorKeyProvider) -> RTCFrameCryptor?
    ) {
        lock.lock()
        let exists = cryptors[id] != nil
        lock.unlock()
        if exists { return }

        guard let cryptor = create(factory, keyProvider) else {
            print("failed to create frame cryptor: \(id)")
            return
        }
        cryptor.keyIndex = FrameEncryption.keyIndex
        cryptor.delegate = self
        // M150 forwards frames unencrypted while a cryptor is disabled, so enable it right away
        cryptor.enabled = true

        lock.lock()
        cryptors[id] = cryptor
        lock.unlock()
        print("frame cryptor attached: \(id)")
    }
}

// MARK: - Frame Cryptor Delegate
extension FrameEncryption: RTCFrameCryptorDelegate {
    func frameCryptor(
        _ frameCryptor: RTCFrameCryptor,
        didStateChangeWithParticipantId participantId: String,
        with stateChanged: RTCFrameCryptorState
    ) {
        let state: String
        switch stateChanged {
        case .new:
            state = "new"
        case .ok:
            state = "ok"
        case .encryptionFailed:
            state = "encryptionFailed"
        case .decryptionFailed:
            state = "decryptionFailed"
        case .missingKey:
            state = "missingKey"
        case .keyRatcheted:
            state = "keyRatcheted"
        case .internalError:
            state = "internalError"
        @unknown default:
            state = "unknown(\(stateChanged.rawValue))"
        }
        print("frame cryptor state changed, participant: \(participantId), state: \(state)")
    }
}
