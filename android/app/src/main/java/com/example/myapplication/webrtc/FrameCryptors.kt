package com.example.myapplication.webrtc

import android.util.Log
import org.webrtc.FrameCryptor
import org.webrtc.MediaStreamTrack
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpReceiver
import org.webrtc.RtpSender

/**
 * The FrameCryptors of one peer connection, at most one per sender and per receiver. Dispose it
 * before the peer connection.
 */
class FrameCryptors(private val e2ee: E2eeManager) {

    companion object {
        private const val TAG = "FrameCryptors"
    }

    // Strong references: a garbage-collected FrameCryptor would leave the native transformer dangling.
    private val senderCryptors = mutableMapOf<String, FrameCryptor>()
    private val receiverCryptors = mutableMapOf<String, FrameCryptor>()
    private val pendingSenderIds = mutableSetOf<String>()
    private val pendingReceiverIds = mutableSetOf<String>()
    private var disposed = false

    // RtpSender/RtpReceiver/FrameCryptorFactory calls block on the signaling thread, which also
    // delivers onAddTrack; never make them while holding this monitor.
    fun attach(sender: RtpSender) {
        val attached = attachCryptor(sender.id(), senderCryptors, pendingSenderIds) {
            e2ee.createSenderCryptor(sender)
        }
        if (attached) Log.d(TAG, "E2EE sender cryptor attached (${sender.track()?.kind()})")
    }

    fun attach(receiver: RtpReceiver) {
        val attached = attachCryptor(receiver.id(), receiverCryptors, pendingReceiverIds) {
            e2ee.createReceiverCryptor(receiver)
        }
        if (attached) Log.d(TAG, "E2EE receiver cryptor attached (${receiver.track()?.kind()})")
    }

    /** Covers receivers that exist before onAddTrack fires (addTrack, setRemoteDescription). */
    fun attachAllReceivers(peerConnection: PeerConnection) {
        peerConnection.transceivers.forEach { attach(it.receiver) }
    }

    private fun attachCryptor(
        id: String,
        cryptors: MutableMap<String, FrameCryptor>,
        pendingIds: MutableSet<String>,
        create: () -> FrameCryptor?
    ): Boolean {
        synchronized(this) {
            if (disposed || cryptors.containsKey(id) || !pendingIds.add(id)) return false
        }
        var cryptor: FrameCryptor? = null
        var stored = false
        try {
            cryptor = create()
        } finally {
            synchronized(this) {
                pendingIds.remove(id)
                if (cryptor != null && !disposed) {
                    cryptors[id] = cryptor
                    stored = true
                }
            }
            if (cryptor != null && !stored) disposeCryptor(cryptor)
        }
        return stored
    }

    fun dispose() {
        val cryptors = synchronized(this) {
            disposed = true
            (senderCryptors.values + receiverCryptors.values).also {
                senderCryptors.clear()
                receiverCryptors.clear()
            }
        }
        cryptors.forEach { disposeCryptor(it) }
    }

    private fun disposeCryptor(cryptor: FrameCryptor) {
        try {
            cryptor.dispose()
        } catch (e: IllegalStateException) {
            Log.w(TAG, "FrameCryptor already disposed", e)
        }
    }
}

/**
 * Web and iOS reliably handle the FrameCryptor VP8 header layout, so negotiate VP8 first
 * and keep the remaining codecs as fallbacks. The SFU only forwards VP8.
 */
fun PeerConnection.preferVp8(factory: PeerConnectionFactory) {
    val codecs = factory.getRtpReceiverCapabilities(MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO).codecs
    val preferred = codecs.sortedBy { if (it.name.equals("VP8", ignoreCase = true)) 0 else 1 }
    transceivers
        .filter { it.mediaType == MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO && !it.isStopped }
        .forEach { transceiver ->
            val result = transceiver.setCodecPreferences(preferred)
            if (result.isError) {
                Log.w("VideoCodecs", "setCodecPreferences failed: ${result.error()?.message}")
            }
        }
}
