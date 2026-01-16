package com.example.myapplication.webrtc

import android.util.Log
import org.webrtc.*
import java.nio.ByteBuffer

/**
 * Manages a single WebRTC peer connection including SDP negotiation,
 * ICE candidates, and data channel communication.
 */
class WebRtcPeer(
    factory: PeerConnectionFactory,
    localStream: MediaStream,
    private val pcConstraints: MediaConstraints,
    private val listener: RtcListener,
    private val signalingHandler: SignalingHandler
) : SdpObserver, PeerConnection.Observer, DataChannel.Observer {

    val peerConnection: PeerConnection
    private var dataChannel: DataChannel? = null

    companion object {
        private const val TAG = "WebRtcPeer"
    }

    init {
        Log.d(TAG, "Creating new peer connection")
        val rtcConfig = PeerConnection.RTCConfiguration(ArrayList()).apply {
            iceServers.add(
                PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer()
            )
        }

        peerConnection = factory.createPeerConnection(rtcConfig, this)!!

        // Add local tracks to peer connection
        localStream.audioTracks.firstOrNull()?.let { peerConnection.addTrack(it, listOf("ARDAMS")) }
        localStream.videoTracks.firstOrNull()?.let { peerConnection.addTrack(it, listOf("ARDAMS")) }

        listener.onStatusChanged("CONNECTING")
    }

    // ========== Public Methods ==========

    fun createOffer() {
        peerConnection.createOffer(this, pcConstraints)
    }

    fun createAnswer() {
        peerConnection.createAnswer(this, pcConstraints)
    }

    fun setRemoteDescription(sdp: SessionDescription) {
        peerConnection.setRemoteDescription(this, sdp)
    }

    fun addIceCandidate(candidate: IceCandidate) {
        if (peerConnection.remoteDescription != null) {
            peerConnection.addIceCandidate(candidate)
        }
    }

    fun createDataChannel(channelName: String) {
        Log.d(TAG, "Creating data channel: $channelName")
        val init = DataChannel.Init()
        dataChannel = peerConnection.createDataChannel(channelName, init)
        dataChannel?.registerObserver(this)
        peerConnection.createOffer(this, pcConstraints)
    }

    fun sendDataChannelMessage(message: String) {
        if (dataChannel?.state() == DataChannel.State.OPEN) {
            val buffer = ByteBuffer.wrap(message.toByteArray())
            val dataBuffer = DataChannel.Buffer(buffer, false)
            dataChannel?.send(dataBuffer)
        }
    }

    fun addTrack(track: MediaStreamTrack) {
        peerConnection.addTrack(track, listOf("ARDAMS"))
    }

    fun removeTrack(sender: RtpSender) {
        peerConnection.removeTrack(sender)
    }

    fun getSenders(): List<RtpSender> {
        return peerConnection.senders
    }
    
    fun replaceVideoTrack(newTrack: VideoTrack) {
        val senders = peerConnection.senders
        val videoSender = senders.find { it.track()?.kind() == "video" }
        videoSender?.setTrack(newTrack, true)
    }

    fun dispose() {
        dataChannel?.let {
            it.unregisterObserver()
            it.dispose()
            dataChannel = null
        }
        peerConnection.dispose()
    }

    // ========== SdpObserver Implementation ==========

    override fun onCreateSuccess(sdp: SessionDescription) {
        peerConnection.setLocalDescription(this, sdp)
        when (sdp.type) {
            SessionDescription.Type.OFFER -> signalingHandler.sendOffer(sdp)
            SessionDescription.Type.ANSWER -> signalingHandler.sendAnswer(sdp)
            else -> {}
        }
    }

    override fun onSetSuccess() {}
    override fun onCreateFailure(error: String) {
        Log.e(TAG, "SDP create failure: $error")
    }
    override fun onSetFailure(error: String) {
        Log.e(TAG, "SDP set failure: $error")
    }

    // ========== PeerConnection.Observer Implementation ==========

    override fun onSignalingChange(signalingState: PeerConnection.SignalingState) {}

    override fun onIceConnectionChange(iceConnectionState: PeerConnection.IceConnectionState) {
        when (iceConnectionState) {
            PeerConnection.IceConnectionState.DISCONNECTED -> {
                listener.onStatusChanged("DISCONNECTED")
                listener.onRemoveRemoteStream()
                dispose()
                listener.onPeersConnectionStatusChange(false)
            }
            PeerConnection.IceConnectionState.CONNECTED -> {
                Log.d(TAG, "Peers connected")
                listener.onStatusChanged("CONNECTED")
                listener.onPeersConnectionStatusChange(true)
            }
            else -> {}
        }
    }

    override fun onIceConnectionReceivingChange(receiving: Boolean) {}
    override fun onIceGatheringChange(iceGatheringState: PeerConnection.IceGatheringState) {}

    override fun onIceCandidate(candidate: IceCandidate) {
        signalingHandler.sendIceCandidate(candidate)
    }

    override fun onIceCandidatesRemoved(candidates: Array<IceCandidate>) {
        peerConnection.removeIceCandidates(candidates)
    }

    override fun onAddStream(mediaStream: MediaStream) {
        Log.d(TAG, "onAddStream ${mediaStream.id}")
        listener.onAddRemoteStream(mediaStream)
    }

    override fun onRemoveStream(mediaStream: MediaStream) {
        Log.d(TAG, "onRemoveStream ${mediaStream.id}")
        listener.onRemoveRemoteStream()
    }

    override fun onDataChannel(dataChannel: DataChannel) {
        Log.d(TAG, "onDataChannel ${dataChannel.state()}")
        this.dataChannel = dataChannel
        this.dataChannel?.registerObserver(this)
    }

    override fun onRenegotiationNeeded() {}

    // ========== DataChannel.Observer Implementation ==========

    override fun onBufferedAmountChange(amount: Long) {}

    override fun onStateChange() {
        Log.d(TAG, "DataChannel state: ${dataChannel?.state()}")
        dataChannel?.state()?.let { listener.onDataChannelStateChange(it) }
    }

    override fun onMessage(buffer: DataChannel.Buffer) {
        if (buffer.binary) {
            Log.d(TAG, "Binary message received")
        } else {
            val data = buffer.data
            val bytes = ByteArray(data.remaining())
            data.get(bytes)
            val message = String(bytes)
            listener.onDataChannelMessage(message)
        }
    }
}
