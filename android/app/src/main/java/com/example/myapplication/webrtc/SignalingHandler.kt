package com.example.myapplication.webrtc

import android.util.Log
import io.socket.client.Socket
import org.json.JSONArray
import org.json.JSONObject
import org.webrtc.IceCandidate
import org.webrtc.SessionDescription

/**
 * Handles Socket.io signaling for WebRTC peer connection.
 */
class SignalingHandler(
    private val socket: Socket,
    private val roomId: String,
    private val onPeerCreated: () -> WebRtcPeer,
    private val getPeer: () -> WebRtcPeer?,
    private val e2ee: E2eeManager? = null
) {
    companion object {
        private const val TAG = "SignalingHandler"
    }

    fun setupListeners() {
        socket.on(Socket.EVENT_CONNECT, onConnect)
        socket.on("new user joined", onNewUserJoined)
        socket.on("offer", onOffer)
        socket.on("answer", onAnswer)
        socket.on("new ice candidate", onNewIceCandidate)
        socket.on("receive encryption key", onReceiveEncryptionKey)
        socket.on("remote peer received encryption key", onRemotePeerReceivedEncryptionKey)
        socket.on(Socket.EVENT_DISCONNECT, onDisconnect)
    }

    private val onConnect = io.socket.emitter.Emitter.Listener {
        val obj = JSONObject()
        try {
            obj.put("roomId", roomId)
            socket.emit("join room", obj)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private val onDisconnect = io.socket.emitter.Emitter.Listener {
        Log.d(TAG, "Socket disconnected")
    }

    private val onNewUserJoined = io.socket.emitter.Emitter.Listener {
        val peer = onPeerCreated()
        // The server relays on the same socket in order, so the key reaches the remote before the offer.
        e2ee?.let { sendEncryptionKey(it) }
        peer.createOffer()
    }

    private val onReceiveEncryptionKey = io.socket.emitter.Emitter.Listener { args ->
        val data = args.getOrNull(0) as? JSONObject
        val key = parseKey(data?.opt("encryptionKey"))
        if (e2ee == null) {
            Log.w(TAG, "Received an encryption key but E2EE is disabled; remote media will not decode")
            return@Listener
        }
        if (key == null) {
            Log.e(TAG, "receive encryption key: missing or non-binary encryptionKey")
            return@Listener
        }
        e2ee.setSharedKey(key)
        try {
            socket.emit("encryption key received", JSONObject().put("roomId", roomId))
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private val onRemotePeerReceivedEncryptionKey = io.socket.emitter.Emitter.Listener {
        Log.d(TAG, "Remote peer received encryption key")
    }

    /** Binary attachments arrive as ByteArray; a JSON number array is accepted as a fallback. */
    private fun parseKey(value: Any?): ByteArray? = when (value) {
        is ByteArray -> value
        is JSONArray -> ByteArray(value.length()) { value.getInt(it).toByte() }
        else -> null
    }

    private fun sendEncryptionKey(e2ee: E2eeManager) {
        val material = e2ee.generateKeyMaterial()
        e2ee.setSharedKey(material)
        try {
            // socket.io-client sends ByteArray values as binary attachments (ArrayBuffer on web).
            val payload = JSONObject().apply {
                put("roomId", roomId)
                put("encryptionKey", material)
            }
            socket.emit("send encryption key", payload)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private val onOffer = io.socket.emitter.Emitter.Listener { args ->
        val data = args[0] as JSONObject

        // Get or create peer connection
        val peer = getPeer() ?: onPeerCreated()

        try {
            val offer = data.getJSONObject("offer")
            val sdp = SessionDescription(
                SessionDescription.Type.fromCanonicalForm(offer.getString("type")),
                offer.getString("sdp")
            )
            peer.setRemoteDescription(sdp)
            peer.createAnswer()
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private val onAnswer = io.socket.emitter.Emitter.Listener { args ->
        val data = args[0] as JSONObject
        try {
            val answer = data.getJSONObject("answer")
            val sdp = SessionDescription(
                SessionDescription.Type.fromCanonicalForm(answer.getString("type")),
                answer.getString("sdp")
            )
            getPeer()?.setRemoteDescription(sdp)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private val onNewIceCandidate = io.socket.emitter.Emitter.Listener { args ->
        val data = args[0] as JSONObject
        try {
            val iceCandidate = data.getJSONObject("iceCandidate")
            val candidate = IceCandidate(
                iceCandidate.getString("sdpMid"),
                iceCandidate.getInt("sdpMLineIndex"),
                iceCandidate.getString("candidate")
            )
            getPeer()?.addIceCandidate(candidate)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    fun sendOffer(sdp: SessionDescription) {
        sendSdp(sdp, "offer")
    }

    fun sendAnswer(sdp: SessionDescription) {
        sendSdp(sdp, "answer")
    }

    private fun sendSdp(sdp: SessionDescription, type: String) {
        try {
            val payload = JSONObject()
            val desc = JSONObject().apply {
                put("type", sdp.type.canonicalForm())
                put("sdp", sdp.description)
            }

            payload.put(type, desc)
            payload.put("roomId", roomId)

            socket.emit(type, payload)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    fun sendIceCandidate(candidate: IceCandidate) {
        try {
            val payload = JSONObject()
            val iceCandidate = JSONObject().apply {
                put("sdpMLineIndex", candidate.sdpMLineIndex)
                put("sdpMid", candidate.sdpMid)
                put("candidate", candidate.sdp)
            }

            payload.put("iceCandidate", iceCandidate)
            payload.put("roomId", roomId)

            socket.emit("new ice candidate", payload)
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    fun disconnect() {
        socket.off(Socket.EVENT_CONNECT, onConnect)
        socket.off("new user joined", onNewUserJoined)
        socket.off("offer", onOffer)
        socket.off("answer", onAnswer)
        socket.off("new ice candidate", onNewIceCandidate)
        socket.off("receive encryption key", onReceiveEncryptionKey)
        socket.off("remote peer received encryption key", onRemotePeerReceivedEncryptionKey)
        socket.off(Socket.EVENT_DISCONNECT, onDisconnect)
        socket.close()
    }
}