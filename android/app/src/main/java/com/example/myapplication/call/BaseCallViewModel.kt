package com.example.myapplication.call

import android.app.Application
import android.content.Context
import android.content.Intent
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import com.example.myapplication.ScreenCaptureService
import com.example.myapplication.effects.BackgroundKind
import com.example.myapplication.effects.EffectsCatalog
import com.example.myapplication.effects.EffectsSelection
import com.example.myapplication.effects.EffectsStore
import com.example.myapplication.webrtc.LocalMedia
import com.example.myapplication.webrtc.LocalMediaListener
import com.example.myapplication.webrtc.effects.EffectsScene
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
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.webrtc.EglBase
import org.webrtc.MediaStream
import org.webrtc.VideoTrack
import java.io.File
import java.lang.ref.WeakReference

/**
 * What the 1:1 ([CallViewModel]) and the group ([GroupCallViewModel]) calls have in common: the
 * shared EGL context, the local media and its controls (effects included), the speaker, sharing
 * and the chat list.
 */
abstract class BaseCallViewModel(app: Application, savedState: SavedStateHandle) : AndroidViewModel(app), LocalMediaListener {

    companion object {
        const val EXTRA_ROOM_ID = "com.example.webrtcdemoandroid.ROOM_ID"
        const val EXTRA_E2EE = "com.example.webrtcdemoandroid.E2EE"
        const val EXTRA_SERVER_ADDRESS = "com.example.webrtcdemoandroid.SERVER_ADDRESS"
        const val EXTRA_GROUP = "com.example.webrtcdemoandroid.GROUP"
        const val EXTRA_SFU_ADDRESS = "com.example.webrtcdemoandroid.SFU_ADDRESS"
        private const val TAG = "CallViewModel"
    }

    protected val roomId: String = savedState.get<String>(EXTRA_ROOM_ID).orEmpty()
    protected val e2ee: Boolean = savedState.get<Boolean>(EXTRA_E2EE) ?: false

    val eglBase: EglBase = EglBase.create()

    protected val _ui = MutableStateFlow(CallUiState(roomId, e2ee))
    val ui: StateFlow<CallUiState> = _ui.asStateFlow()

    private val _localTrack = MutableStateFlow<VideoTrack?>(null)
    val localTrack: StateFlow<VideoTrack?> = _localTrack.asStateFlow()

    protected val _events = MutableSharedFlow<String>(extraBufferCapacity = 8)
    val events: SharedFlow<String> = _events.asSharedFlow()

    private val _callEnded = MutableStateFlow<String?>(null)
    /** Set when the call ended on its own (not by hanging up); the screen should close. */
    val callEnded: StateFlow<String?> = _callEnded.asStateFlow()

    private val audioManager = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    val effectsCatalog: EffectsCatalog = EffectsCatalog.load(app)
    /** What the camera currently shows; [CallUiState.effects] runs ahead of it while assets load. */
    private var appliedEffects = EffectsSelection()
    private var effectsJob: Job? = null

    /** Owned by the call engine; only use it from subclass methods, not during construction. */
    protected abstract val media: LocalMedia

    /** 0..1 from our own microphone, for the bars on the local tile. */
    val micLevel: StateFlow<Float> get() = media.micMeter.level

    private var started = false
    private var inForeground = true
    private var needsCameraRestart = false
    protected var chatVisible = false
        private set
    private var nextMessageId = 0L

    protected abstract fun startEngine()
    protected abstract fun releaseEngine()

    // ========== Actions ==========

    /** Starts the camera and microphone; call once the permissions are granted. */
    fun startMedia() {
        if (started) return
        started = true
        ScreenCaptureService.localMediaRef = WeakReference(media)
        val saved = EffectsStore.load(getApplication(), effectsCatalog)
        if (effectsCatalog.hasEffects(saved)) {
            // Nothing is shown or sent until the saved background is ready.
            media.holdEffects()
            _ui.update { it.copy(effects = saved) }
            applyEffects()
        }
        startEngine()
    }

    fun toggleMic() {
        val on = !_ui.value.micOn
        media.toggleAudio(on)
        _ui.update { it.copy(micOn = on) }
    }

    fun toggleCamera() = setCameraOn(!_ui.value.cameraOn)

    private fun setCameraOn(on: Boolean) {
        media.toggleVideo(on)
        _ui.update { it.copy(cameraOn = on) }
    }

    /** [onDone] runs on any thread once the new camera is running; it is not called if the switch fails. */
    fun switchCamera(onDone: () -> Unit = {}) {
        if (_ui.value.sharing != Sharing.None) return onDone()
        media.switchCamera { front ->
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

    /** Only mutes local playout; the remote side is not notified. */
    abstract fun toggleRemoteAudio()

    /** Stops rendering (and decoding into sinks) remote video locally; the remote side is not notified. */
    abstract fun toggleRemoteVideo()

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
            media.setEffects(null)
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
                media.setEffects(scene)
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
            media.createFileCapture(path)
            _ui.update { it.copy(sharing = Sharing.File) }
        }
    }

    fun stopSharing() {
        val sharing = _ui.value.sharing
        if (sharing == Sharing.None) return
        if (sharing == Sharing.Screen) stopScreenCaptureService()
        media.createDeviceCapture(false, null)
        _ui.update { it.copy(sharing = Sharing.None) }
    }

    /** Opens the chat sheet. */
    open fun openChat() {
        chatVisible = true
        _ui.update { it.copy(unread = 0) }
    }

    fun closeChat() {
        chatVisible = false
    }

    abstract fun sendMessage(text: String)

    fun onForegroundChanged(foreground: Boolean) {
        inForeground = foreground
        if (foreground && needsCameraRestart) {
            needsCameraRestart = false
            media.createDeviceCapture(false, null)
        }
    }

    override fun onCleared() {
        if (_ui.value.sharing == Sharing.Screen) stopScreenCaptureService()
        if (_ui.value.speakerOn) toggleSpeaker()
        releaseEngine()
        ScreenCaptureService.localMediaRef = null
        ScreenCaptureService.mediaProjectionPermissionResultData = null
        eglBase.release()
        super.onCleared()
    }

    // ========== LocalMediaListener (called from WebRTC threads) ==========

    override fun onAddLocalStream(localStream: MediaStream) {
        _localTrack.value = localStream.videoTracks.firstOrNull()
    }

    override fun onRemoveLocalStream(localStream: MediaStream) {
        _localTrack.value = null
    }

    override fun onScreenSharingStopped() {
        viewModelScope.launch(Dispatchers.Main) {
            if (_ui.value.sharing != Sharing.Screen) return@launch
            _ui.update { it.copy(sharing = Sharing.None) }
            stopScreenCaptureService()
            _events.tryEmit("Screen sharing stopped")
            if (inForeground) {
                delay(300)
                media.createDeviceCapture(false, null)
            } else {
                needsCameraRestart = true
            }
        }
    }

    override fun onEffectsFailed() {
        _events.tryEmit("Effects stopped working on this device")
        setEffects(EffectsSelection())
    }

    // ========== Helpers ==========

    protected fun endCall(message: String) {
        _callEnded.value = message
    }

    protected fun appendMessage(text: String, isLocal: Boolean, sender: String? = null) {
        _ui.update {
            it.copy(
                messages = it.messages + ChatMessage(nextMessageId++, text, isLocal, sender = sender),
                unread = if (isLocal || chatVisible) it.unread else it.unread + 1
            )
        }
    }

    private fun stopScreenCaptureService() {
        val app = getApplication<Application>()
        app.stopService(Intent(app, ScreenCaptureService::class.java))
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
