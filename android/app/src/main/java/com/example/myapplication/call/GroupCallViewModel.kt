package com.example.myapplication.call

import android.app.Application
import android.graphics.Bitmap
import android.os.SystemClock
import androidx.lifecycle.SavedStateHandle
import androidx.lifecycle.viewModelScope
import com.example.myapplication.settings.SfuServer
import com.example.myapplication.ui.video.FrameSnapshotter
import com.example.myapplication.webrtc.LocalMedia
import com.example.myapplication.webrtc.MediaState
import com.example.myapplication.webrtc.sfu.GroupCallClient
import com.example.myapplication.webrtc.sfu.GroupCallListener
import com.example.myapplication.webrtc.sfu.Participant
import com.example.myapplication.webrtc.sfu.participantLabel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.webrtc.VideoTrack

/** One remote participant on the group stage. [video] is null until their track arrives. */
data class GroupTile(val participant: Participant, val video: VideoTrack? = null)

/** A row of the people sheet. [label] is what every client shows on this person's tile. */
data class CallPerson(val id: String, val label: String, val state: MediaState, val isYou: Boolean, val speaking: Boolean = false)

/**
 * Owns a group call through the SFU. The local side (tracks, controls, chat list) works as in a
 * 1:1 call; the remote side is a list of [tiles] plus per-participant audio levels.
 */
class GroupCallViewModel(app: Application, savedState: SavedStateHandle) : BaseCallViewModel(app, savedState), GroupCallListener {

    companion object {
        private const val AUDIO_LEVEL_POLL_MS = 300L
        // audioLevel is linear 0..1; speech is usually well above this, background noise below.
        // Same level as web and iOS, so everyone sees the same person highlighted.
        private const val SPEAKING_LEVEL = 0.03f
        private const val SPEAKER_HOLD_MS = 1_000L
    }

    private val sfuAddress: String = savedState.get<String>(EXTRA_SFU_ADDRESS) ?: SfuServer.load(app)

    private val _participants = MutableStateFlow<List<Participant>>(emptyList())
    private val _videoTracks = MutableStateFlow<Map<String, VideoTrack>>(emptyMap())

    val tiles: StateFlow<List<GroupTile>> = combine(_participants, _videoTracks) { participants, videos ->
        participants.map { GroupTile(it, videos[it.id]) }
    }.stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())

    private val _audioLevels = MutableStateFlow<Map<String, Float>>(emptyMap())
    val audioLevels: StateFlow<Map<String, Float>> = _audioLevels.asStateFlow()

    private val _activeSpeaker = MutableStateFlow<String?>(null)
    val activeSpeaker: StateFlow<String?> = _activeSpeaker.asStateFlow()
    private var activeSpeakerHeardAt = 0L

    /** Our id in the room, from `joined`. */
    private val _selfId = MutableStateFlow<String?>(null)

    private val selfState = _ui
        .map { MediaState(audio = it.micOn, video = it.cameraOn || it.sharing != Sharing.None, screen = it.sharing != Sharing.None) }
        .distinctUntilChanged()

    /** You first, then everyone else in join order; empty until joined. */
    val people: StateFlow<List<CallPerson>> = combine(_selfId, selfState, _participants, _activeSpeaker) { selfId, state, others, speaker ->
        if (selfId == null) return@combine emptyList()
        listOf(CallPerson(selfId, participantLabel(selfId, client.name), state, isYou = true)) +
            others.map { CallPerson(it.id, it.label, it.state, isYou = false, speaking = it.id == speaker) }
    }.stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())

    private val _snapshots = MutableStateFlow<Map<String, Bitmap>>(emptyMap())
    val snapshots: StateFlow<Map<String, Bitmap>> = _snapshots.asStateFlow()

    private val client = GroupCallClient(app, roomId, this, SfuServer.webSocketUrl(sfuAddress), eglBase, e2ee)
    override val media: LocalMedia get() = client.media

    init {
        _ui.update { it.copy(group = true, phase = ConnectionPhase.Connecting) }
        pollAudioLevels()
    }

    override fun startEngine() = client.start()

    override fun releaseEngine() {
        _videoTracks.value = emptyMap()
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
        _videoTracks.value.values.forEach { it.safeSetEnabled(!hidden) }
        _ui.update { it.copy(remoteVideoHidden = hidden) }
    }

    /** Called on the render thread with a small copy of a rendered frame of [participantId]. */
    fun onRemoteSnapshot(participantId: String, bitmap: Bitmap) {
        if (FrameSnapshotter.isUsable(bitmap)) _snapshots.update { it + (participantId to bitmap) }
    }

    override fun sendMessage(text: String) {
        val trimmed = text.trim()
        if (trimmed.isEmpty() || _ui.value.chat != ChatStatus.Open) return
        client.sendChatMessage(trimmed)
        appendMessage(trimmed, isLocal = true)
    }

    // ========== GroupCallListener (main thread) ==========

    override fun onJoined(participantId: String, participants: List<Participant>) {
        _selfId.value = participantId
        _participants.value = participants
        _ui.update { it.copy(chat = ChatStatus.Open) }
        updatePhase()
    }

    override fun onParticipantJoined(participant: Participant) {
        _participants.update { list -> list.filterNot { it.id == participant.id } + participant }
        _events.tryEmit("${participant.name.ifEmpty { "Someone" }} joined")
        updatePhase()
    }

    override fun onParticipantLeft(participantId: String) {
        val left = _participants.value.firstOrNull { it.id == participantId }
        _participants.update { list -> list.filterNot { it.id == participantId } }
        _videoTracks.update { it - participantId }
        _snapshots.update { it - participantId }
        _audioLevels.update { it - participantId }
        if (_activeSpeaker.value == participantId) _activeSpeaker.value = null
        if (left != null) _events.tryEmit("${left.name.ifEmpty { "Someone" }} left")
        updatePhase()
    }

    override fun onParticipantMediaState(participantId: String, state: MediaState) {
        _participants.update { list -> list.map { if (it.id == participantId) it.copy(state = state) else it } }
    }

    override fun onRemoteVideoTrack(participantId: String, track: VideoTrack?) {
        if (track == null) {
            _videoTracks.update { it - participantId }
        } else {
            track.safeSetEnabled(!_ui.value.remoteVideoHidden)
            _videoTracks.update { it + (participantId to track) }
        }
    }

    override fun onChatMessage(participantId: String, name: String, text: String) {
        val sender = name.ifEmpty { _participants.value.firstOrNull { it.id == participantId }?.name ?: "Someone" }
        appendMessage(text, isLocal = false, sender = sender)
    }

    override fun onServerError(message: String) {
        _events.tryEmit(message)
    }

    override fun onCallEnded(message: String) {
        _selfId.value = null
        _participants.value = emptyList()
        _videoTracks.value = emptyMap()
        _ui.update { it.copy(phase = ConnectionPhase.Waiting, connectedSince = null, chat = ChatStatus.Closed) }
        endCall(message)
    }

    // ========== Helpers ==========

    private fun updatePhase() {
        val alone = _participants.value.isEmpty()
        _ui.update {
            if (alone) it.copy(phase = ConnectionPhase.Waiting, connectedSince = null)
            else it.copy(phase = ConnectionPhase.Connected, connectedSince = it.connectedSince ?: System.currentTimeMillis())
        }
    }

    // Highlights the loudest participant above the speaking level, and keeps them for a moment
    // through the pauses between words.
    private fun pollAudioLevels() {
        viewModelScope.launch {
            while (isActive) {
                delay(AUDIO_LEVEL_POLL_MS)
                if (_participants.value.isEmpty() || _ui.value.remoteAudioMuted) {
                    if (_audioLevels.value.isNotEmpty()) _audioLevels.value = emptyMap()
                    _activeSpeaker.value = null
                    continue
                }
                client.getAudioLevels { levels ->
                    _audioLevels.value = levels
                    val now = SystemClock.elapsedRealtime()
                    val loudest = levels.filterValues { it >= SPEAKING_LEVEL }.maxByOrNull { it.value }?.key
                    if (loudest != null) {
                        _activeSpeaker.value = loudest
                        activeSpeakerHeardAt = now
                    } else if (now - activeSpeakerHeardAt > SPEAKER_HOLD_MS) {
                        _activeSpeaker.value = null
                    }
                }
            }
        }
    }
}
