package com.example.myapplication.webrtc

import android.util.Log
import io.socket.client.Socket
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
    private val getPeer: () -> WebRtcPeer?
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
        peer.createOffer()
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
        socket.off(Socket.EVENT_DISCONNECT, onDisconnect)
        socket.close()
    }
}
