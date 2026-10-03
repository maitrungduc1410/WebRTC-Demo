package com.example.myapplication.call

import android.app.Application
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import com.example.myapplication.effects.BackgroundKind
import com.example.myapplication.effects.EffectsCatalog
import com.example.myapplication.effects.EffectsSelection
import com.example.myapplication.effects.EffectsStore
import com.example.myapplication.settings.SignalingServer
import com.example.myapplication.ScreenCaptureService
import com.example.myapplication.webrtc.MediaState
import com.example.myapplication.webrtc.PeerConnectionClient
import com.example.myapplication.webrtc.RtcListener
import com.example.myapplication.webrtc.effects.EffectsScene
import com.example.myapplication.ui.video.FrameSnapshotter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.webrtc.DataChannel
import org.webrtc.EglBase
import org.webrtc.MediaStream
import org.webrtc.VideoSink
import org.webrtc.VideoTrack
import java.io.File
import java.lang.ref.WeakReference

enum class ConnectionPhase { Waiting, Connecting, Connected }

enum class Sharing { None, Screen, File }

enum class ChatStatus { Closed, Opening, Open }

enum class EffectsStatus { Off, Loading, On }

data class ChatMessage(
    val id: Long,
    val text: String,
    val isLocal: Boolean,
    val timestamp: Long = System.currentTimeMillis()
)

data class CallUiState(
    val roomId: String,
    val e2ee: Boolean,
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
 * Owns the call for as long as the call screen exists (rotation included): the WebRTC client,
 * the shared EGL context, and all UI state. Renderers attach to the exposed tracks.
 */
class CallViewModel(app: Application, savedState: SavedStateHandle) : AndroidViewModel(app), RtcListener {

    companion object {
        const val EXTRA_ROOM_ID = "com.example.webrtcdemoandroid.ROOM_ID"
        const val EXTRA_E2EE = "com.example.webrtcdemoandroid.E2EE"
        const val EXTRA_SERVER_ADDRESS = "com.example.webrtcdemoandroid.SERVER_ADDRESS"
        private const val TAG = "CallViewModel"
        private const val AUDIO_LEVEL_POLL_MS = 250L
    }

    private val roomId: String = savedState.get<String>(EXTRA_ROOM_ID).orEmpty()
    private val e2ee: Boolean = savedState.get<Boolean>(EXTRA_E2EE) ?: false
    private val serverAddress: String = savedState.get<String>(EXTRA_SERVER_ADDRESS) ?: SignalingServer.load(app)

    val eglBase: EglBase = EglBase.create()

    private val _ui = MutableStateFlow(CallUiState(roomId, e2ee))
    val ui: StateFlow<CallUiState> = _ui.asStateFlow()

    private val _localTrack = MutableStateFlow<VideoTrack?>(null)
    val localTrack: StateFlow<VideoTrack?> = _localTrack.asStateFlow()

    private val _remoteTrack = MutableStateFlow<VideoTrack?>(null)
    val remoteTrack: StateFlow<VideoTrack?> = _remoteTrack.asStateFlow()

    private val _remoteSnapshot = MutableStateFlow<Bitmap?>(null)
    val remoteSnapshot: StateFlow<Bitmap?> = _remoteSnapshot.asStateFlow()

    private val _remoteAudioLevel = MutableStateFlow(0f)
    val remoteAudioLevel: StateFlow<Float> = _remoteAudioLevel.asStateFlow()

    private val _events = MutableSharedFlow<String>(extraBufferCapacity = 8)
    val events: SharedFlow<String> = _events.asSharedFlow()

    private val audioManager = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val client = PeerConnectionClient(app, roomId, this, serverAddress, eglBase, e2ee)

    val effectsCatalog: EffectsCatalog = EffectsCatalog.load(app)
    /** What the camera currently shows; [CallUiState.effects] runs ahead of it while assets load. */
    private var appliedEffects = EffectsSelection()
    private var effectsJob: Job? = null

    private var started = false
    private var inForeground = true
    private var needsCameraRestart = false
    private var chatVisible = false
    private var nextMessageId = 0L

    init {
        ScreenCaptureService.peerConnectionClientRef = WeakReference(client)
        pollRemoteAudioLevel()
        val saved = EffectsStore.load(app, effectsCatalog)
        if (effectsCatalog.hasEffects(saved)) {
            // Nothing is shown or sent until the saved background is ready.
            client.holdEffects()
            _ui.update { it.copy(effects = saved) }
            applyEffects()
        }
    }

    // ========== Actions ==========

    /** Starts the camera and microphone; call once the permissions are granted. */
    fun startMedia() {
        if (started) return
        started = true
        client.start()
    }

    fun toggleMic() {
        val on = !_ui.value.micOn
        client.toggleAudio(on)
        _ui.update { it.copy(micOn = on) }
    }

    fun toggleCamera() = setCameraOn(!_ui.value.cameraOn)

    private fun setCameraOn(on: Boolean) {
        client.toggleVideo(on)
        _ui.update { it.copy(cameraOn = on) }
    }

    /** [onDone] runs on any thread once the new camera is running; it is not called if the switch fails. */
    fun switchCamera(onDone: () -> Unit = {}) {
        if (_ui.value.sharing != Sharing.None) return onDone()
        client.switchCamera { front ->
            _ui.update { it.copy(frontCamera = front) }
            onDone()
        }
    }

    fun toggleSpeaker() {
        val on = !_ui.value.speakerOn
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (on) {
                audioManager.availableCommunicationDevices
                    .firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
                    ?.let { audioManager.setCommunicationDevice(it) }
            } else {
                audioManager.clearCommunicationDevice()
            }
        } else {
            @Suppress("DEPRECATION")
            if (on) {
                audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
                audioManager.isSpeakerphoneOn = true
            } else {
                audioManager.isSpeakerphoneOn = false
                audioManager.mode = AudioManager.MODE_NORMAL
            }
        }
        _ui.update { it.copy(speakerOn = on) }
    }

    /** Only mutes local playout; the remote peer is not notified. */
    fun toggleRemoteAudio() {
        val muted = !_ui.value.remoteAudioMuted
        client.toggleRemoteAudio(!muted)
        _ui.update { it.copy(remoteAudioMuted = muted) }
    }

    /** Stops rendering (and decoding into sinks) the remote video locally; the remote peer is not notified. */
    fun toggleRemoteVideo() {
        val hidden = !_ui.value.remoteVideoHidden
        _remoteTrack.value?.safeSetEnabled(!hidden)
        _ui.update { it.copy(remoteVideoHidden = hidden) }
    }

    /** Called on the render thread with a small copy of a rendered remote frame. */
    fun onRemoteSnapshot(bitmap: Bitmap) {
        if (FrameSnapshotter.isUsable(bitmap)) _remoteSnapshot.value = bitmap
    }

    fun setEffects(selection: EffectsSelection) {
        _ui.update { it.copy(effects = selection) }
        EffectsStore.save(getApplication(), selection)
        applyEffects()
    }

    /** Brings the camera in line with the chosen effects; a newer choice cancels an older one. */
    private fun applyEffects() {
        val selection = _ui.value.effects
        effectsJob?.cancel()
        if (!effectsCatalog.hasEffects(selection)) {
            client.setEffects(null)
            appliedEffects = selection
            _ui.update { it.copy(effectsStatus = EffectsStatus.Off) }
            return
        }
        _ui.update { it.copy(effectsStatus = EffectsStatus.Loading) }
        effectsJob = viewModelScope.launch {
            val scene = withContext(Dispatchers.IO) { loadScene(selection) }
            // A newer choice owns the camera now.
            if (_ui.value.effects != selection) return@launch
            if (scene != null) {
                client.setEffects(scene)
                appliedEffects = selection
                _ui.update { it.copy(effectsStatus = EffectsStatus.On) }
                return@launch
            }
            _events.tryEmit("Couldn't load that effect")
            // Back to whatever was showing before; this also releases frames held at start.
            val previous = appliedEffects.takeIf { it != selection } ?: EffectsSelection()
            EffectsStore.save(getApplication(), previous)
            _ui.update { it.copy(effects = previous) }
            applyEffects()
        }
    }

    private fun loadScene(selection: EffectsSelection): EffectsScene? {
        val app = getApplication<Application>()
        val background = effectsCatalog.background(selection.background)
        val sticker = effectsCatalog.sticker(selection.sticker)
        val picture = if (background.kind == BackgroundKind.Image) {
            EffectsCatalog.decode(app, background.file ?: return null, maxSide = 1920) ?: return null
        } else {
            null
        }
        val stickerBitmap = sticker?.let { EffectsCatalog.decode(app, it.file, maxSide = 512) ?: return null }
        return EffectsScene(background, picture, sticker, stickerBitmap)
    }

    fun startScreenShare(projectionData: Intent) {
        if (!_ui.value.cameraOn) setCameraOn(true)
        ScreenCaptureService.mediaProjectionPermissionResultData = projectionData
        val app = getApplication<Application>()
        val intent = Intent(app, ScreenCaptureService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) app.startForegroundService(intent) else app.startService(intent)
        _ui.update { it.copy(sharing = Sharing.Screen) }
    }

    fun shareVideoFile(uri: Uri) {
        viewModelScope.launch {
            val path = withContext(Dispatchers.IO) { resolveVideoPath(uri) }
            if (path == null) {
                _events.tryEmit("Couldn't open that video")
                return@launch
            }
            if (_ui.value.sharing == Sharing.Screen) stopScreenCaptureService()
            if (!_ui.value.cameraOn) setCameraOn(true)
            client.createFileCapture(path)
            _ui.update { it.copy(sharing = Sharing.File) }
        }
    }

    fun stopSharing() {
        val sharing = _ui.value.sharing
        if (sharing == Sharing.None) return
        if (sharing == Sharing.Screen) stopScreenCaptureService()
        client.createDeviceCapture(false, null)
        _ui.update { it.copy(sharing = Sharing.None) }
    }

    /** Opens the chat sheet. The channel normally exists since the call connected. */
    fun openChat() {
        chatVisible = true
        if (_ui.value.chat == ChatStatus.Closed && _ui.value.phase == ConnectionPhase.Connected) {
            client.ensureDataChannel()
            _ui.update { it.copy(chat = ChatStatus.Opening) }
        }
        _ui.update { it.copy(unread = 0) }
    }

    fun closeChat() {
        chatVisible = false
    }

    fun sendMessage(text: String) {
        val trimmed = text.trim()
        if (trimmed.isEmpty() || _ui.value.chat != ChatStatus.Open) return
        client.sendDataChannelMessage(trimmed)
        appendMessage(trimmed, isLocal = true)
    }

    fun onForegroundChanged(foreground: Boolean) {
        inForeground = foreground
        if (foreground && needsCameraRestart) {
            needsCameraRestart = false
            client.createDeviceCapture(false, null)
        }
    }

    override fun onCleared() {
        if (_ui.value.sharing == Sharing.Screen) stopScreenCaptureService()
        if (_ui.value.speakerOn) toggleSpeaker()
        detachRemoteTrack()
        client.onDestroy()
        ScreenCaptureService.peerConnectionClientRef = null
        ScreenCaptureService.mediaProjectionPermissionResultData = null
        eglBase.release()
        super.onCleared()
    }

    // ========== RtcListener (called from WebRTC / socket threads) ==========

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

    override fun onAddLocalStream(localStream: MediaStream) {
        _localTrack.value = localStream.videoTracks.firstOrNull()
    }

    override fun onRemoveLocalStream(localStream: MediaStream) {
        _localTrack.value = null
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

    override fun onScreenSharingStopped() {
        viewModelScope.launch(Dispatchers.Main) {
            if (_ui.value.sharing != Sharing.Screen) return@launch
            _ui.update { it.copy(sharing = Sharing.None) }
            stopScreenCaptureService()
            _events.tryEmit("Screen sharing stopped")
            if (inForeground) {
                delay(300)
                client.createDeviceCapture(false, null)
            } else {
                needsCameraRestart = true
            }
        }
    }

    override fun onEffectsFailed() {
        _events.tryEmit("Effects stopped working on this device")
        setEffects(EffectsSelection())
    }

    override fun onRemoteMediaState(state: MediaState) {
        _ui.update { it.copy(remote = state) }
    }

    // ========== Helpers ==========

    private fun appendMessage(text: String, isLocal: Boolean) {
        _ui.update {
            it.copy(
                messages = it.messages + ChatMessage(nextMessageId++, text, isLocal),
                unread = if (isLocal || chatVisible) it.unread else it.unread + 1
            )
        }
    }

    private fun detachRemoteTrack() {
        _remoteTrack.value = null
    }

    private fun stopScreenCaptureService() {
        val app = getApplication<Application>()
        app.stopService(Intent(app, ScreenCaptureService::class.java))
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

    private fun resolveVideoPath(uri: Uri): String? {
        val app = getApplication<Application>()
        if (uri.scheme == "file") return uri.path
        app.contentResolver.query(uri, arrayOf(MediaStore.Video.Media.DATA), null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val index = cursor.getColumnIndex(MediaStore.Video.Media.DATA)
                val path = if (index >= 0) cursor.getString(index) else null
                if (path != null && File(path).canRead()) return path
            }
        }
        // Scoped storage usually hides the real path; decode from a private copy instead.
        return try {
            val file = File(app.cacheDir, "shared_video_${System.currentTimeMillis()}.mp4")
            app.contentResolver.openInputStream(uri)?.use { input ->
                file.outputStream().use { output -> input.copyTo(output) }
            } ?: return null
            file.absolutePath
        } catch (e: Exception) {
            Log.e(TAG, "Error copying video to cache", e)
            null
        }
    }
}

// Tracks are disposed by the peer connection on its own thread; late UI calls must not crash.
fun VideoTrack.safeAddSink(sink: VideoSink) = runCatching { addSink(sink) }.let { }
fun VideoTrack.safeRemoveSink(sink: VideoSink) = runCatching { removeSink(sink) }.let { }
fun VideoTrack.safeSetEnabled(enabled: Boolean) = runCatching { setEnabled(enabled) }.let { }
