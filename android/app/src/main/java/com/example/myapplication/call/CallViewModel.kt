package com.example.myapplication.call

import android.app.Application
import android.graphics.Bitmap
import android.util.Log
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import com.example.myapplication.effects.EffectsSelection
import com.example.myapplication.settings.SignalingServer
import com.example.myapplication.webrtc.LocalMedia
import com.example.myapplication.webrtc.MediaState
import com.example.myapplication.webrtc.PeerConnectionClient
import com.example.myapplication.webrtc.RtcListener
import com.example.myapplication.ui.video.FrameSnapshotter
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.webrtc.DataChannel
import org.webrtc.MediaStream
import org.webrtc.VideoSink
import org.webrtc.VideoTrack

enum class ConnectionPhase { Waiting, Connecting, Connected }

enum class Sharing { None, Screen, File }

enum class ChatStatus { Closed, Opening, Open }

enum class EffectsStatus { Off, Loading, On }

data class ChatMessage(
    val id: Long,
    val text: String,
    val isLocal: Boolean,
    val timestamp: Long = System.currentTimeMillis(),
    /** The remote sender's name in a group call; null in a 1:1 call. */
    val sender: String? = null
)

data class CallUiState(
    val roomId: String,
    val e2ee: Boolean,
    /** Group call through the SFU; the remote fields below then apply to every participant. */
    val group: Boolean = false,
    val phase: ConnectionPhase = ConnectionPhase.Waiting,
    val connectedSince: Long? = null,
    val micOn: Boolean = true,
    val cameraOn: Boolean = true,
    val frontCamera: Boolean = true,
    val speakerOn: Boolean = false,
    val remoteAudioMuted: Boolean = false,
    val remoteVideoHidden: Boolean = false,
    val remote: MediaState = MediaState(),
    val sharing: Sharing = Sharing.None,
    /** The chosen background and sticker; [effectsStatus] says whether they are showing yet. */
    val effects: EffectsSelection = EffectsSelection(),
    val effectsStatus: EffectsStatus = EffectsStatus.Off,
    val chat: ChatStatus = ChatStatus.Closed,
    val messages: List<ChatMessage> = emptyList(),
    val unread: Int = 0
) {
    /** Remote video is present but should not be shown: hidden by us or turned off by them. */
    val remoteVideoPaused: Boolean get() = remoteVideoHidden || !remote.video
}

/**
 * Owns the 1:1 call for as long as the call screen exists (rotation included): the WebRTC client,
 * the shared EGL context, and all UI state. Renderers attach to the exposed tracks.
 */
class CallViewModel(app: Application, savedState: SavedStateHandle) : BaseCallViewModel(app, savedState), RtcListener {

    companion object {
        private const val TAG = "CallViewModel"
        private const val AUDIO_LEVEL_POLL_MS = 250L
    }

    private val serverAddress: String = savedState.get<String>(EXTRA_SERVER_ADDRESS) ?: SignalingServer.load(app)

    private val _remoteTrack = MutableStateFlow<VideoTrack?>(null)
    val remoteTrack: StateFlow<VideoTrack?> = _remoteTrack.asStateFlow()

    private val _remoteSnapshot = MutableStateFlow<Bitmap?>(null)
    val remoteSnapshot: StateFlow<Bitmap?> = _remoteSnapshot.asStateFlow()

    private val _remoteAudioLevel = MutableStateFlow(0f)
    val remoteAudioLevel: StateFlow<Float> = _remoteAudioLevel.asStateFlow()

    private val client = PeerConnectionClient(app, roomId, this, SignalingServer.webSocketUrl(serverAddress), eglBase, e2ee)
    override val media: LocalMedia get() = client.media

    init {
        pollRemoteAudioLevel()
    }

    override fun startEngine() = client.start()

    override fun releaseEngine() {
        detachRemoteTrack()
        client.onDestroy()
    }

    // ========== Actions ==========

    override fun toggleRemoteAudio() {
        val muted = !_ui.value.remoteAudioMuted
        client.toggleRemoteAudio(!muted)
        _ui.update { it.copy(remoteAudioMuted = muted) }
    }

    override fun toggleRemoteVideo() {
        val hidden = !_ui.value.remoteVideoHidden
        _remoteTrack.value?.safeSetEnabled(!hidden)
        _ui.update { it.copy(remoteVideoHidden = hidden) }
    }

    /** Called on the render thread with a small copy of a rendered remote frame. */
    fun onRemoteSnapshot(bitmap: Bitmap) {
        if (FrameSnapshotter.isUsable(bitmap)) _remoteSnapshot.value = bitmap
    }

    /** The channel normally exists since the call connected. */
    override fun openChat() {
        if (_ui.value.chat == ChatStatus.Closed && _ui.value.phase == ConnectionPhase.Connected) {
            client.ensureDataChannel()
            _ui.update { it.copy(chat = ChatStatus.Opening) }
        }
        super.openChat()
    }

    override fun sendMessage(text: String) {
        val trimmed = text.trim()
        if (trimmed.isEmpty() || _ui.value.chat != ChatStatus.Open) return
        client.sendDataChannelMessage(trimmed)
        appendMessage(trimmed, isLocal = true)
    }

    // ========== RtcListener (called from WebRTC threads and the main thread) ==========

    override fun onStatusChanged(newStatus: String) {
        Log.d(TAG, "status: $newStatus")
        _ui.update {
            when (newStatus) {
                "CONNECTING" -> if (it.phase == ConnectionPhase.Connected) it else it.copy(phase = ConnectionPhase.Connecting)
                "CONNECTED" -> it.copy(phase = ConnectionPhase.Connected)
                "DISCONNECTED" -> it.copy(phase = ConnectionPhase.Waiting)
                else -> it
            }
        }
    }

    override fun onAddRemoteStream(remoteStream: MediaStream) {
        val track = remoteStream.videoTracks.firstOrNull() ?: return
        detachRemoteTrack()
        track.safeSetEnabled(!_ui.value.remoteVideoHidden)
        _remoteTrack.value = track
    }

    override fun onRemoveRemoteStream() {
        detachRemoteTrack()
        _remoteSnapshot.value = null
        _ui.update { it.copy(remote = MediaState()) }
    }

    override fun onDataChannelMessage(message: String) {
        appendMessage(message, isLocal = false)
    }

    override fun onDataChannelStateChange(state: DataChannel.State) {
        when (state) {
            DataChannel.State.OPEN -> _ui.update { it.copy(chat = ChatStatus.Open) }
            DataChannel.State.CLOSED -> {
                val wasOpen = _ui.value.chat == ChatStatus.Open
                _ui.update { it.copy(chat = ChatStatus.Closed) }
                if (wasOpen) _events.tryEmit("Chat disconnected")
            }
            else -> {}
        }
    }

    override fun onPeersConnectionStatusChange(success: Boolean) {
        _ui.update {
            if (success) {
                it.copy(phase = ConnectionPhase.Connected, connectedSince = it.connectedSince ?: System.currentTimeMillis())
            } else {
                it.copy(phase = ConnectionPhase.Waiting, connectedSince = null, chat = ChatStatus.Closed)
            }
        }
        _events.tryEmit(if (success) "Connected" else "The other participant left")
    }

    override fun onRemoteMediaState(state: MediaState) {
        _ui.update { it.copy(remote = state) }
    }

    override fun onCallEnded(message: String) {
        endCall(message)
    }

    // ========== Helpers ==========

    private fun detachRemoteTrack() {
        _remoteTrack.value = null
    }

    // Stats are only needed while the avatar placeholder is on screen.
    private fun pollRemoteAudioLevel() {
        viewModelScope.launch {
            while (isActive) {
                delay(AUDIO_LEVEL_POLL_MS)
                val state = _ui.value
                if (_remoteTrack.value != null && state.remoteVideoPaused && !state.remoteAudioMuted) {
                    client.getRemoteAudioLevel { _remoteAudioLevel.value = it }
                } else if (_remoteAudioLevel.value != 0f) {
                    _remoteAudioLevel.value = 0f
                }
            }
        }
    }
}

// Tracks are disposed by the peer connection on its own thread; late UI calls must not crash.
fun VideoTrack.safeAddSink(sink: VideoSink) = runCatching { addSink(sink) }.let { }
fun VideoTrack.safeRemoveSink(sink: VideoSink) = runCatching { removeSink(sink) }.let { }
fun VideoTrack.safeSetEnabled(enabled: Boolean) = runCatching { setEnabled(enabled) }.let { }
