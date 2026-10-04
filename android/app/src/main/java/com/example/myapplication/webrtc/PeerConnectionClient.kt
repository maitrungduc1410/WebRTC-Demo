package com.example.myapplication.webrtc

import android.content.Context
import android.util.Log
import com.example.myapplication.R
import org.webrtc.*

/**
 * 1:1 call engine: one peer connection to the other participant, signaled over the signaling
 * server's WebSocket ([serverUrl], "ws://host:4000/ws"). Capture, sources, effects and the E2EE
 * key provider live in [media].
 */
class PeerConnectionClient(
    context: Context,
    private val roomId: String,
    private val callbacks: RtcListener,
    serverUrl: String,
    rootEglBase: EglBase,
    e2eeEnabled: Boolean = false
) {
    // Re-announces the local media state whenever a peer (re)connects.
    private val listener = object : RtcListener by callbacks {
        override fun onPeersConnectionStatusChange(success: Boolean) {
            if (success) sendMediaState() else media.micMeter.setIdleWanted(true)
            callbacks.onPeersConnectionStatusChange(success)
        }
    }

    val media: LocalMedia = LocalMedia(context, rootEglBase, e2eeEnabled, listener, onStateChange = ::sendMediaState)

    private val pcConstraints = MediaConstraints()
    private val dataChannelLabel = context.getString(R.string.dataChannelName)
    private val signalingHandler: SignalingHandler
    // Replaced on the main thread (socket events), read from WebRTC threads
    @Volatile
    private var peer: WebRtcPeer? = null
    @Volatile
    private var remoteAudioEnabled = true

    companion object {
        private const val TAG = "PeerConnectionClient"
    }

    init {
        // A sender that still holds the old track must keep it alive.
        media.replaceVideoTrack = { track -> peer?.takeUnless { it.isDisposed }?.replaceVideoTrack(track) ?: true }
        setupConstraints()
        signalingHandler = SignalingHandler(
            url = serverUrl,
            roomId = roomId,
            onPeerCreated = { createPeer() },
            // A peer disposes itself when ICE disconnects; a later offer must start a new one.
            getPeer = { peer?.takeUnless { it.isDisposed } },
            onRemoteMediaState = { listener.onRemoteMediaState(it) },
            onCallEnded = { listener.onCallEnded(it) },
            e2ee = media.e2ee
        )
    }

    private fun setupConstraints() {
        // Setup peer connection constraints
        pcConstraints.mandatory.apply {
            add(MediaConstraints.KeyValuePair("OfferToReceiveAudio", "true"))
            add(MediaConstraints.KeyValuePair("OfferToReceiveVideo", "true"))
            add(MediaConstraints.KeyValuePair("maxHeight", "1080"))
            add(MediaConstraints.KeyValuePair("maxWidth", "2400"))
            add(MediaConstraints.KeyValuePair("maxFrameRate", "30"))
            add(MediaConstraints.KeyValuePair("minFrameRate", "30"))
        }
        pcConstraints.optional.add(MediaConstraints.KeyValuePair("DtlsSrtpKeyAgreement", "true"))
    }

    private fun createPeer(): WebRtcPeer {
        // "peer joined" or an offer for a new call while one exists: the remote restarted it.
        dropPeer()
        media.micMeter.setIdleWanted(false)
        peer = WebRtcPeer(
            factory = media.factory!!,
            localStream = media.stream!!,
            pcConstraints = pcConstraints,
            listener = listener,
            signalingHandler = signalingHandler,
            dataChannelLabel = dataChannelLabel,
            e2ee = media.e2ee,
            remoteAudioEnabled = remoteAudioEnabled
        )
        return peer!!
    }

    /** Ends the current call but keeps local media running. */
    private fun dropPeer() {
        val old = peer ?: return
        peer = null
        if (old.isDisposed) return
        old.dispose()
        listener.onRemoveRemoteStream()
        listener.onPeersConnectionStatusChange(false)
    }

    // ========== Public API ==========

    /**
     * Creates the local media, then joins the room. The order matters: a peer already in the room
     * sends its offer as soon as we join, and answering it needs the local stream.
     */
    fun start() {
        media.start()
        // Until the other person joins there is no peer connection recording the microphone.
        media.micMeter.setIdleWanted(true)
        signalingHandler.connect()
    }

    /** Reads the remote `audioLevel` (0..1) from the inbound audio RTP stats. */
    fun getRemoteAudioLevel(callback: (Float) -> Unit) {
        val peer = peer ?: return
        peer.getStats { report ->
            val level = report.statsMap.values
                .firstOrNull { it.type == "inbound-rtp" && it.members["kind"] == "audio" }
                ?.members?.get("audioLevel") as? Double
            callback((level ?: 0.0).toFloat())
        }
    }

    private fun sendMediaState() {
        signalingHandler.sendMediaState(media.state)
    }

    fun toggleRemoteAudio(enable: Boolean) {
        remoteAudioEnabled = enable
        peer?.setRemoteAudioEnabled(enable)
    }

    /** Usually a no-op: the offerer creates the chat channel with the first offer. */
    fun ensureDataChannel() {
        peer?.ensureDataChannel()
    }

    fun sendDataChannelMessage(message: String) {
        peer?.sendDataChannelMessage(message)
    }

    fun onDestroy() {
        signalingHandler.close()

        media.stopCapture()

        Log.d(TAG, "Closing peer connection.")
        peer?.dispose()
        peer = null

        // After every cryptor (owned by the peer) is gone.
        media.release()

        Log.d(TAG, "Cleanup complete.")
    }
}
