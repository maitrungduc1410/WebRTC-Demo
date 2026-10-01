package com.example.myapplication.webrtc

import android.util.Log
import org.webrtc.FrameCryptor
import org.webrtc.FrameCryptorAlgorithm
import org.webrtc.FrameCryptorFactory
import org.webrtc.FrameCryptorKeyDerivationAlgorithm
import org.webrtc.FrameCryptorKeyProvider
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpReceiver
import org.webrtc.RtpSender
import java.security.SecureRandom

/**
 * Owns the shared-key FrameCryptorKeyProvider and creates FrameCryptors for senders/receivers.
 * Cryptors are owned (and disposed) by the WebRtcPeer they belong to.
 *
 * The key provider options must stay identical to web (insertable streams) and iOS,
 * otherwise the derived AES key or the frame trailer differ and peers cannot decrypt each other.
 */
class E2eeManager(private val factory: PeerConnectionFactory) {

    companion object {
        private const val TAG = "E2eeManager"
        const val PARTICIPANT_LOCAL = "local"
        const val PARTICIPANT_REMOTE = "remote"
        private const val KEY_INDEX = 0
        private const val KEY_MATERIAL_LENGTH = 32
        private val RATCHET_SALT = "LKFrameEncryptionKey".toByteArray(Charsets.UTF_8)
    }

    private val keyProvider: FrameCryptorKeyProvider = FrameCryptorFactory.createFrameCryptorKeyProvider(
        true, // sharedKey
        RATCHET_SALT,
        0, // ratchetWindowSize
        ByteArray(0), // uncryptedMagicBytes
        -1, // failureTolerance
        16, // keyRingSize
        false, // discardFrameWhenCryptorNotReady
        FrameCryptorKeyDerivationAlgorithm.PBKDF2
    )
    private var disposed = false

    private val observer = FrameCryptor.Observer { participantId, newState ->
        when (newState) {
            FrameCryptor.FrameCryptionState.NEW,
            FrameCryptor.FrameCryptionState.OK,
            FrameCryptor.FrameCryptionState.KEYRATCHETED ->
                Log.d(TAG, "FrameCryptor[$participantId] state: $newState")
            else -> Log.w(TAG, "FrameCryptor[$participantId] state: $newState")
        }
    }

    fun generateKeyMaterial(): ByteArray {
        val material = ByteArray(KEY_MATERIAL_LENGTH)
        SecureRandom().nextBytes(material)
        return material
    }

    @Synchronized
    fun setSharedKey(material: ByteArray): Boolean {
        if (disposed) return false
        val ok = keyProvider.setSharedKey(KEY_INDEX, material)
        Log.d(TAG, "setSharedKey(index=$KEY_INDEX, ${material.size} bytes) -> $ok")
        return ok
    }

    @Synchronized
    fun createSenderCryptor(sender: RtpSender): FrameCryptor? {
        if (disposed) return null
        return configure(
            FrameCryptorFactory.createFrameCryptorForRtpSender(
                factory, sender, PARTICIPANT_LOCAL, FrameCryptorAlgorithm.AES_GCM, keyProvider
            )
        )
    }

    @Synchronized
    fun createReceiverCryptor(receiver: RtpReceiver): FrameCryptor? {
        if (disposed) return null
        return configure(
            FrameCryptorFactory.createFrameCryptorForRtpReceiver(
                factory, receiver, PARTICIPANT_REMOTE, FrameCryptorAlgorithm.AES_GCM, keyProvider
            )
        )
    }

    private fun configure(cryptor: FrameCryptor): FrameCryptor {
        cryptor.setKeyIndex(KEY_INDEX)
        // M150 forwards plaintext while a cryptor is disabled, so enable it immediately.
        // Until a key is set, frames are dropped on the missing-key path instead.
        cryptor.setEnabled(true)
        cryptor.setObserver(observer)
        return cryptor
    }

    /** Call after every WebRtcPeer (and its cryptors) is disposed, before the factory. */
    @Synchronized
    fun dispose() {
        if (disposed) return
        keyProvider.dispose()
        disposed = true
    }
}