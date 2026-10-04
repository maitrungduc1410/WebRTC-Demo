package com.example.myapplication.webrtc

import android.util.Base64
import android.util.Log
import org.json.JSONObject
import org.webrtc.IceCandidate
import org.webrtc.SessionDescription

/**
 * Signaling of a 1:1 call over the signaling server's WebSocket (protocol in ARCHITECTURE.md).
 * Socket events arrive on the main thread; the send functions may be called from any thread.
 */
class SignalingHandler(
    url: String,
    private val roomId: String,
    private val onPeerCreated: () -> WebRtcPeer,
    private val getPeer: () -> WebRtcPeer?,
    private val onRemoteMediaState: (MediaState) -> Unit,
    /** The socket closed or the server refused us; there is no reconnect. */
    private val onCallEnded: (String) -> Unit,
    private val e2ee: E2eeManager? = null
) : SignalingSocket.Events {

    companion object {
        private const val TAG = "SignalingHandler"
    }

    private val socket = SignalingSocket(url, this)

    fun connect() = socket.connect()

    /** Sends `leave` and closes the socket. */
    fun close() = socket.close()

    // ========== SignalingSocket.Events ==========

    override fun onOpen() {
        socket.send("join") { put("roomId", roomId) }
    }

    override fun onMessage(type: String, message: JSONObject) {
        when (type) {
            "peer joined" -> onPeerJoined()
            "offer" -> onOffer(message.optString("sdp"))
            "answer" -> getPeer()?.setRemoteDescription(SessionDescription(SessionDescription.Type.ANSWER, message.optString("sdp")))
            "candidate" -> onRemoteCandidate(message.optJSONObject("candidate"))
            "encryption key" -> onEncryptionKey(message.optString("key"))
            "encryption key received" -> Log.d(TAG, "Remote peer received the encryption key")
            "media state" -> onMediaState(message.optJSONObject("state"))
            "error" -> onServerError(message)
        }
    }

    override fun onClosed(opened: Boolean) {
        onCallEnded(if (opened) "Lost the connection to the signaling server" else "Couldn't reach the signaling server")
    }

    // ========== Incoming ==========

    /** The other person just joined our room: we start the call. */
    private fun onPeerJoined() {
        val peer = onPeerCreated()
        // The server relays in order on one socket, so the key reaches the remote before the offer.
        e2ee?.let { sendEncryptionKey(it) }
        peer.createOffer()
    }

    private fun onOffer(sdp: String) {
        // A peer disposes itself when ICE disconnects; a later offer starts a new one.
        val peer = getPeer() ?: onPeerCreated()
        peer.setRemoteDescription(SessionDescription(SessionDescription.Type.OFFER, sdp))
        peer.createAnswer()
    }

    private fun onRemoteCandidate(json: JSONObject?) {
        val sdp = json?.optString("candidate").orEmpty()
        if (sdp.isEmpty()) return
        val candidate = IceCandidate(
            if (json!!.isNull("sdpMid")) "" else json.optString("sdpMid"),
            json.optInt("sdpMLineIndex", 0),
            sdp
        )
        getPeer()?.addIceCandidate(candidate)
    }

    private fun onEncryptionKey(encoded: String) {
        if (e2ee == null) {
            Log.w(TAG, "Received an encryption key but E2EE is disabled; remote media will not decode")
            return
        }
        val key = try {
            Base64.decode(encoded, Base64.DEFAULT)
        } catch (e: IllegalArgumentException) {
            null
        }
        if (key == null || key.isEmpty()) {
            Log.e(TAG, "encryption key: missing or not base64")
            return
        }
        e2ee.setSharedKey(key)
        socket.send("encryption key received")
    }

    private fun onMediaState(state: JSONObject?) {
        state ?: return
        onRemoteMediaState(
            MediaState(
                audio = state.optBoolean("audio", true),
                video = state.optBoolean("video", true),
                screen = state.optBoolean("screen", false)
            )
        )
    }

    private fun onServerError(message: JSONObject) {
        val text = message.optString("message")
        if (!message.optBoolean("fatal")) {
            Log.w(TAG, "Server: $text")
            return
        }
        onCallEnded(if (text == "Room is full") "That room already has two people in it" else text)
    }

    // ========== Outgoing ==========

    private fun sendEncryptionKey(e2ee: E2eeManager) {
        val material = e2ee.generateKeyMaterial()
        e2ee.setSharedKey(material)
        socket.send("encryption key") { put("key", Base64.encodeToString(material, Base64.NO_WRAP)) }
    }

    fun sendMediaState(state: MediaState) {
        socket.send("media state") {
            put("state", JSONObject().apply {
                put("audio", state.audio)
                put("video", state.video)
                put("screen", state.screen)
            })
        }
    }

    fun sendOffer(sdp: SessionDescription) {
        socket.send("offer") { put("sdp", sdp.description) }
    }

    fun sendAnswer(sdp: SessionDescription) {
        socket.send("answer") { put("sdp", sdp.description) }
    }

    fun sendIceCandidate(candidate: IceCandidate) {
        socket.send("candidate") {
            put("candidate", JSONObject().apply {
                put("candidate", candidate.sdp)
                put("sdpMid", candidate.sdpMid ?: JSONObject.NULL)
                put("sdpMLineIndex", candidate.sdpMLineIndex)
            })
        }
    }
}
