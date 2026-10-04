package com.example.myapplication.webrtc.sfu

import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Base64
import android.util.Log
import com.example.myapplication.webrtc.FrameCryptors
import com.example.myapplication.webrtc.LocalMedia
import com.example.myapplication.webrtc.MediaState
import com.example.myapplication.webrtc.SignalingSocket
import com.example.myapplication.webrtc.preferVp8
import org.json.JSONObject
import org.webrtc.*
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * Group call engine: two peer connections to the SFU, signaled over a WebSocket.
 *
 * - publish: we offer once, with one sendonly audio and one sendonly video transceiver. Source
 *   switches use `setTrack` on its senders, so it is never renegotiated.
 * - subscribe: the SFU offers (again whenever tracks come and go) and we answer. Every forwarded
 *   track has stream id = participant id.
 *
 * Everything that touches a peer connection runs on the main thread, as do the public methods.
 * WebRTC and socket callbacks hop there first and are dropped once the peers are disposed, since
 * any call on a disposed native PeerConnection crashes the process.
 */
class GroupCallClient(
    context: Context,
    private val roomId: String,
    private val listener: GroupCallListener,
    serverUrl: String,
    rootEglBase: EglBase,
    e2eeEnabled: Boolean = false
) {
    companion object {
        private const val TAG = "GroupCallClient"
        private const val PUBLISH = "publish"
        private const val SUBSCRIBE = "subscribe"
    }

    /** Sent with `join`; the others see this device as "name · short id". */
    val name: String = "Android · ${Build.MODEL}".take(32)

    val media = LocalMedia(context, rootEglBase, e2eeEnabled, listener, onStateChange = ::sendMediaState)

    private val mainHandler = Handler(Looper.getMainLooper())
    private val signaling = SignalingSocket(serverUrl, SignalingEvents())
    private val e2eeEnabled = media.e2ee != null
    private val cryptors = media.e2ee?.let { FrameCryptors(it) }

    private var joined = false
    // Set once the call is over: callbacks still queued on the main thread must not touch the peers.
    private var ended = false
    private var lastFatalError: String? = null

    private var publishPc: PeerConnection? = null
    private var subscribePc: PeerConnection? = null
    private var videoSender: RtpSender? = null
    private val pendingCandidates = mapOf(PUBLISH to mutableListOf<IceCandidate>(), SUBSCRIBE to mutableListOf())
    private val pendingOffers = ArrayDeque<String>()
    private var answering = false

    private class RemoteTrack(val participantId: String, val receiver: RtpReceiver, val track: MediaStreamTrack)

    // Keyed by receiver id: the SFU recycles m-lines, so a receiver can move to another participant.
    private val remoteTracks = mutableMapOf<String, RemoteTrack>()
    private val departed = mutableSetOf<String>()
    private var remoteAudioEnabled = true

    init {
        media.replaceVideoTrack = { track -> videoSender?.setTrack(track, false) ?: true }
    }

    // ========== Public API ==========

    /** Opens the camera and microphone, then joins: the publish offer needs both tracks. */
    fun start() {
        media.start()
        signaling.connect()
    }

    fun sendChatMessage(text: String) {
        if (!joined) return
        signaling.send("chat") { put("text", text) }
    }

    /** Only mutes local playout; nobody is notified. Also applies to tracks added later. */
    fun toggleRemoteAudio(enable: Boolean) {
        remoteAudioEnabled = enable
        remoteTracks.values.forEach { (it.track as? AudioTrack)?.setEnabled(enable) }
    }

    /** Remote `audioLevel` (0..1) per participant, from each audio receiver's inbound-rtp stats. */
    fun getAudioLevels(callback: (Map<String, Float>) -> Unit) {
        val pc = subscribePc ?: return
        val audio = remoteTracks.values.filter { it.track is AudioTrack }
        if (audio.isEmpty()) return callback(emptyMap())
        val levels = ConcurrentHashMap<String, Float>()
        val remaining = AtomicInteger(audio.size)
        audio.forEach { remote ->
            pc.getStats(remote.receiver) { report ->
                val level = report.statsMap.values
                    .firstOrNull { it.type == "inbound-rtp" && it.members["kind"] == "audio" }
                    ?.members?.get("audioLevel") as? Double
                if (level != null) levels.merge(remote.participantId, level.toFloat()) { a, b -> maxOf(a, b) }
                if (remaining.decrementAndGet() == 0) onMain { callback(levels) }
            }
        }
    }

    fun onDestroy() {
        signaling.close()
        media.stopCapture()
        disposePeers()
        // After every cryptor is gone.
        media.release()
        Log.d(TAG, "Cleanup complete.")
    }

    // ========== Signaling ==========

    private inner class SignalingEvents : SignalingSocket.Events {
        override fun onOpen() {
            val material = media.e2ee?.generateKeyMaterial()
            signaling.send("join") {
                put("roomId", roomId)
                put("name", name)
                put("e2ee", e2eeEnabled)
                // Only used when we create the room; the room key comes back in `joined`.
                if (material != null) put("e2eeKey", Base64.encodeToString(material, Base64.NO_WRAP))
            }
        }

        override fun onMessage(type: String, message: JSONObject) {
            if (ended) return
            when (type) {
                "joined" -> onJoined(message)
                "answer" -> if (message.optString("pc") == PUBLISH) onPublishAnswer(message.getString("sdp"))
                "offer" -> if (message.optString("pc") == SUBSCRIBE) onSubscribeOffer(message.getString("sdp"))
                "candidate" -> onRemoteCandidate(message)
                "participant joined" -> message.optJSONObject("participant")?.let { listener.onParticipantJoined(parseParticipant(it)) }
                "participant left" -> onParticipantLeft(message.getString("participantId"))
                "media state" -> listener.onParticipantMediaState(
                    message.getString("participantId"),
                    parseMediaState(message.optJSONObject("state"))
                )
                "chat" -> listener.onChatMessage(
                    message.optString("participantId"),
                    message.optString("name"),
                    message.optString("text")
                )
                "error" -> {
                    val text = message.optString("message", "Server error")
                    Log.w(TAG, "Server error: $text (fatal=${message.optBoolean("fatal")})")
                    if (message.optBoolean("fatal")) lastFatalError = text else listener.onServerError(text)
                }
                else -> Log.d(TAG, "Ignoring message type '$type'")
            }
        }

        override fun onClosed(opened: Boolean) {
            endCall(
                lastFatalError
                    ?: if (opened) "Disconnected from the SFU server" else "Couldn't reach the SFU server"
            )
        }
    }

    private fun onJoined(message: JSONObject) {
        val participantId = message.getString("participantId")
        val e2ee = media.e2ee
        if (e2ee != null) {
            val key = message.optString("e2eeKey").takeIf { it.isNotEmpty() }
                ?.let { runCatching { Base64.decode(it, Base64.DEFAULT) }.getOrNull() }
            if (key == null || key.isEmpty()) {
                endCall("The server didn't send a valid E2EE key")
                return
            }
            e2ee.setSharedKey(key)
        }
        joined = true
        val others = message.optJSONArray("participants")
        val participants = (0 until (others?.length() ?: 0)).mapNotNull { others?.optJSONObject(it)?.let(::parseParticipant) }
        Log.d(TAG, "Joined room $roomId as $participantId with ${participants.size} other(s)")
        listener.onJoined(participantId, participants)
        sendMediaState()
        createPublishPeer()
    }

    private fun sendMediaState() {
        if (!joined || ended) return
        val state = media.state
        signaling.send("media state") {
            put("state", JSONObject().apply {
                put("audio", state.audio)
                put("video", state.video)
                put("screen", state.screen)
            })
        }
    }

    private fun onParticipantLeft(participantId: String) {
        departed += participantId
        remoteTracks.entries.removeAll { it.value.participantId == participantId }
        listener.onParticipantLeft(participantId)
    }

    private fun endCall(message: String) {
        if (ended) return
        Log.d(TAG, "Call ended: $message")
        signaling.close()
        disposePeers()
        listener.onCallEnded(message)
    }

    // ========== Publish peer connection ==========

    private fun createPublishPeer() {
        val factory = media.factory ?: return
        val audioTrack = media.audioTrack
        val videoTrack = media.videoTrack
        if (audioTrack == null || videoTrack == null) {
            endCall("Camera and microphone are not ready")
            return
        }
        val pc = factory.createPeerConnection(rtcConfiguration(), PeerObserver(PUBLISH)) ?: run {
            endCall("Couldn't create the publish connection")
            return
        }
        publishPc = pc

        val streamIds = listOf(media.stream!!.id)
        val init = { RtpTransceiver.RtpTransceiverInit(RtpTransceiver.RtpTransceiverDirection.SEND_ONLY, streamIds) }
        val audio = pc.addTransceiver(audioTrack, init())
        val video = pc.addTransceiver(videoTrack, init())
        videoSender = video.sender
        cryptors?.attach(audio.sender)
        cryptors?.attach(video.sender)
        pc.preferVp8(factory)

        pc.createOffer(sdpObserver("publish createOffer", onCreate = { offer ->
            pc.setLocalDescription(sdpObserver("publish setLocalDescription"), offer)
            signaling.send("offer") {
                put("pc", PUBLISH)
                put("sdp", offer.description)
            }
        }), MediaConstraints())
    }

    private fun onPublishAnswer(sdp: String) {
        val pc = publishPc ?: return
        pc.setRemoteDescription(sdpObserver("publish setRemoteDescription", onSet = {
            flushCandidates(PUBLISH, pc)
        }), SessionDescription(SessionDescription.Type.ANSWER, sdp))
    }

    // ========== Subscribe peer connection ==========

    /** Offers are answered one at a time, in order. */
    private fun onSubscribeOffer(sdp: String) {
        pendingOffers.addLast(sdp)
        if (!answering) answerNextOffer()
    }

    private fun answerNextOffer() {
        val sdp = pendingOffers.removeFirstOrNull() ?: return
        val pc = subscribePc ?: createSubscribePeer() ?: return
        answering = true
        val done = {
            answering = false
            answerNextOffer()
        }
        pc.setRemoteDescription(sdpObserver("subscribe setRemoteDescription", onSet = {
            flushCandidates(SUBSCRIBE, pc)
            // Before the answer, so the first frames of a new track are already decrypted.
            cryptors?.attachAllReceivers(pc)
            pc.createAnswer(sdpObserver("subscribe createAnswer", onCreate = { answer ->
                pc.setLocalDescription(sdpObserver("subscribe setLocalDescription", onSet = {
                    signaling.send("answer") {
                        put("pc", SUBSCRIBE)
                        put("sdp", answer.description)
                    }
                    done()
                }, onFailure = done), answer)
            }, onFailure = done), MediaConstraints())
        }, onFailure = done), SessionDescription(SessionDescription.Type.OFFER, sdp))
    }

    private fun createSubscribePeer(): PeerConnection? {
        val factory = media.factory ?: return null
        val pc = factory.createPeerConnection(rtcConfiguration(), PeerObserver(SUBSCRIBE))
        if (pc == null) {
            endCall("Couldn't create the subscribe connection")
            return null
        }
        subscribePc = pc
        return pc
    }

    private fun onRemoteTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) {
        if (subscribePc == null) return
        val participantId = streams.firstOrNull()?.id ?: return
        val track = receiver.track() ?: return
        cryptors?.attach(receiver)
        if (participantId in departed) return

        val receiverId = receiver.id()
        val previous = remoteTracks.put(receiverId, RemoteTrack(participantId, receiver, track))
        if (previous != null && previous.participantId != participantId && previous.track is VideoTrack) {
            listener.onRemoteVideoTrack(previous.participantId, null)
        }
        Log.d(TAG, "Remote ${track.kind()} track for $participantId")
        when (track) {
            is AudioTrack -> track.setEnabled(remoteAudioEnabled)
            is VideoTrack -> listener.onRemoteVideoTrack(participantId, track)
        }
    }

    private fun onRemoteTrackRemoved(receiver: RtpReceiver) {
        val remote = remoteTracks.remove(receiver.id()) ?: return
        if (remote.track is VideoTrack) listener.onRemoteVideoTrack(remote.participantId, null)
    }

    // ========== ICE ==========

    private fun onRemoteCandidate(message: JSONObject) {
        val name = message.optString("pc")
        val json = message.optJSONObject("candidate") ?: return
        val sdp = json.optString("candidate")
        if (sdp.isEmpty()) return
        val candidate = IceCandidate(
            if (json.isNull("sdpMid")) "" else json.optString("sdpMid"),
            json.optInt("sdpMLineIndex", 0),
            sdp
        )
        val pc = if (name == PUBLISH) publishPc else subscribePc
        if (pc?.remoteDescription != null) {
            pc.addIceCandidate(candidate)
        } else {
            pendingCandidates[name]?.add(candidate)
        }
    }

    private fun flushCandidates(name: String, pc: PeerConnection) {
        val pending = pendingCandidates[name] ?: return
        pending.forEach { pc.addIceCandidate(it) }
        pending.clear()
    }

    private fun sendCandidate(name: String, candidate: IceCandidate) {
        if (candidate.sdp.isNullOrEmpty()) return
        signaling.send("candidate") {
            put("pc", name)
            put("candidate", JSONObject().apply {
                put("candidate", candidate.sdp)
                put("sdpMid", candidate.sdpMid ?: JSONObject.NULL)
                put("sdpMLineIndex", candidate.sdpMLineIndex)
            })
        }
    }

    // ========== Peer connections ==========

    private fun rtcConfiguration() = PeerConnection.RTCConfiguration(
        listOf(PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer())
    ).apply {
        sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
        bundlePolicy = PeerConnection.BundlePolicy.MAXBUNDLE
    }

    private fun disposePeers() {
        ended = true
        joined = false
        videoSender = null
        // The UI lets go of the remote tracks before their peer connection disposes them.
        remoteTracks.values.filter { it.track is VideoTrack }.forEach { listener.onRemoteVideoTrack(it.participantId, null) }
        remoteTracks.clear()
        pendingOffers.clear()
        cryptors?.dispose()
        publishPc?.dispose()
        publishPc = null
        subscribePc?.dispose()
        subscribePc = null
    }

    /** Runs [block] on the main thread, unless the call ended in between. */
    private fun onMain(block: () -> Unit) {
        mainHandler.post { if (!ended) block() }
    }

    /** Callbacks arrive on the WebRTC signaling thread. */
    private inner class PeerObserver(private val name: String) : PeerConnection.Observer {
        override fun onIceCandidate(candidate: IceCandidate) {
            onMain { sendCandidate(name, candidate) }
        }

        override fun onConnectionChange(newState: PeerConnection.PeerConnectionState) {
            Log.d(TAG, "$name connection: $newState")
            if (newState == PeerConnection.PeerConnectionState.FAILED) {
                onMain { endCall("Lost the media connection to the SFU server") }
            }
        }

        override fun onAddTrack(receiver: RtpReceiver, mediaStreams: Array<out MediaStream>) {
            if (name == SUBSCRIBE) onMain { onRemoteTrack(receiver, mediaStreams) }
        }

        override fun onRemoveTrack(receiver: RtpReceiver) {
            if (name == SUBSCRIBE) onMain { onRemoteTrackRemoved(receiver) }
        }

        override fun onSignalingChange(state: PeerConnection.SignalingState) {}
        override fun onIceConnectionChange(state: PeerConnection.IceConnectionState) {}
        override fun onIceConnectionReceivingChange(receiving: Boolean) {}
        override fun onIceGatheringChange(state: PeerConnection.IceGatheringState) {}
        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>) {}
        override fun onAddStream(stream: MediaStream) {}
        override fun onRemoveStream(stream: MediaStream) {}
        override fun onDataChannel(dataChannel: DataChannel) {}
        override fun onRenegotiationNeeded() {}
    }

    /** An SdpObserver whose callbacks run on the main thread, and not at all once the call ended. */
    private fun sdpObserver(
        label: String,
        onCreate: (SessionDescription) -> Unit = {},
        onSet: () -> Unit = {},
        onFailure: () -> Unit = {}
    ) = object : SdpObserver {
        override fun onCreateSuccess(sdp: SessionDescription) = onMain { onCreate(sdp) }
        override fun onSetSuccess() = onMain(onSet)
        override fun onCreateFailure(error: String) {
            Log.e(TAG, "$label failed: $error")
            onMain(onFailure)
        }
        override fun onSetFailure(error: String) {
            Log.e(TAG, "$label failed: $error")
            onMain(onFailure)
        }
    }

    private fun parseParticipant(json: JSONObject) = Participant(
        id = json.optString("id"),
        name = json.optString("name"),
        state = parseMediaState(json.optJSONObject("state"))
    )

    private fun parseMediaState(json: JSONObject?) = MediaState(
        audio = json?.optBoolean("audio", true) ?: true,
        video = json?.optBoolean("video", true) ?: true,
        screen = json?.optBoolean("screen", false) ?: false
    )
}
