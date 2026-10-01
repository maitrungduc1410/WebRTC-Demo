package com.example.myapplication.webrtc

import android.util.Log
import org.webrtc.*
import java.nio.ByteBuffer

/**
 * Manages a single WebRTC peer connection including SDP negotiation,
 * ICE candidates, and data channel communication.
 */
class WebRtcPeer(
    private val factory: PeerConnectionFactory,
    localStream: MediaStream,
    private val pcConstraints: MediaConstraints,
    private val listener: RtcListener,
    private val signalingHandler: SignalingHandler,
    private val e2ee: E2eeManager? = null,
    private var remoteAudioEnabled: Boolean = true
) : SdpObserver, PeerConnection.Observer, DataChannel.Observer {

    val peerConnection: PeerConnection
    private var dataChannel: DataChannel? = null
    private var disposed = false

    // Owned by the receivers delivered in onAddTrack, which live as long as the peer connection
    private val remoteAudioTracks = mutableListOf<AudioTrack>()

    // Strong references: a garbage-collected FrameCryptor would leave the native transformer dangling.
    private val senderCryptors = mutableMapOf<String, FrameCryptor>()
    private val receiverCryptors = mutableMapOf<String, FrameCryptor>()
    private val pendingSenderIds = mutableSetOf<String>()
    private val pendingReceiverIds = mutableSetOf<String>()

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
        localStream.audioTracks.firstOrNull()?.let { addTrack(it) }
        localStream.videoTracks.firstOrNull()?.let { addTrack(it) }

        listener.onStatusChanged("CONNECTING")
    }

    // ========== Public Methods ==========

    fun createOffer() {
        applyE2eeCodecPreferences()
        peerConnection.createOffer(this, pcConstraints)
    }

    fun createAnswer() {
        applyE2eeCodecPreferences()
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
        createOffer()
    }

    fun sendDataChannelMessage(message: String) {
        if (dataChannel?.state() == DataChannel.State.OPEN) {
            val buffer = ByteBuffer.wrap(message.toByteArray())
            val dataBuffer = DataChannel.Buffer(buffer, false)
            dataChannel?.send(dataBuffer)
        }
    }

    fun addTrack(track: MediaStreamTrack) {
        val sender = peerConnection.addTrack(track, listOf("ARDAMS"))
        if (sender != null) attachSenderCryptor(sender)
    }

    fun removeTrack(sender: RtpSender) {
        peerConnection.removeTrack(sender)
    }

    fun getSenders(): List<RtpSender> {
        return peerConnection.senders
    }
    
    /** Only mutes local playout; the remote peer is not notified. */
    fun setRemoteAudioEnabled(enabled: Boolean) {
        val tracks = synchronized(this) {
            if (disposed) return
            remoteAudioEnabled = enabled
            remoteAudioTracks.toList()
        }
        tracks.forEach { it.setEnabled(enabled) }
    }

    fun replaceVideoTrack(newTrack: VideoTrack) {
        val senders = peerConnection.senders
        val videoSender = senders.find { it.track()?.kind() == "video" }
        videoSender?.setTrack(newTrack, true)
    }

    fun dispose() {
        synchronized(this) {
            if (disposed) return
            disposed = true
            remoteAudioTracks.clear()
        }
        dataChannel?.let {
            it.unregisterObserver()
            it.dispose()
            dataChannel = null
        }
        disposeCryptors()
        peerConnection.dispose()
    }

    // ========== E2EE ==========

    // RtpSender/RtpReceiver/FrameCryptorFactory calls block on the signaling thread, which also
    // delivers onAddTrack; never make them while holding this monitor.
    private fun attachSenderCryptor(sender: RtpSender) {
        val e2ee = e2ee ?: return
        val attached = attachCryptor(sender.id(), senderCryptors, pendingSenderIds) {
            e2ee.createSenderCryptor(sender)
        }
        if (attached) Log.d(TAG, "E2EE sender cryptor attached (${sender.track()?.kind()})")
    }

    private fun attachReceiverCryptor(receiver: RtpReceiver) {
        val e2ee = e2ee ?: return
        val attached = attachCryptor(receiver.id(), receiverCryptors, pendingReceiverIds) {
            e2ee.createReceiverCryptor(receiver)
        }
        if (attached) Log.d(TAG, "E2EE receiver cryptor attached (${receiver.track()?.kind()})")
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

    /** Covers receivers created by addTrack (offerer side) before onAddTrack fires. */
    private fun attachAllReceiverCryptors() {
        if (e2ee == null) return
        peerConnection.transceivers.forEach { attachReceiverCryptor(it.receiver) }
    }

    private fun disposeCryptors() {
        val cryptors = synchronized(this) {
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

    /**
     * Web and iOS reliably handle the FrameCryptor VP8 header layout, so negotiate VP8 first
     * and keep the remaining codecs as fallbacks.
     */
    private fun applyE2eeCodecPreferences() {
        if (e2ee == null) return
        attachAllReceiverCryptors()
        val codecs = factory.getRtpReceiverCapabilities(MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO).codecs
        val preferred = codecs.sortedBy { if (it.name.equals("VP8", ignoreCase = true)) 0 else 1 }
        peerConnection.transceivers
            .filter { it.mediaType == MediaStreamTrack.MediaType.MEDIA_TYPE_VIDEO && !it.isStopped }
            .forEach { transceiver ->
                val result = transceiver.setCodecPreferences(preferred)
                if (result.isError) {
                    Log.w(TAG, "setCodecPreferences failed: ${result.error()?.message}")
                }
            }
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

    override fun onAddTrack(receiver: RtpReceiver, mediaStreams: Array<out MediaStream>) {
        Log.d(TAG, "onAddTrack ${receiver.track()?.kind()}")
        attachReceiverCryptor(receiver)

        val audioTrack = receiver.track() as? AudioTrack ?: return
        val enabled = synchronized(this) {
            if (disposed) return
            remoteAudioTracks.add(audioTrack)
            remoteAudioEnabled
        }
        audioTrack.setEnabled(enabled)
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