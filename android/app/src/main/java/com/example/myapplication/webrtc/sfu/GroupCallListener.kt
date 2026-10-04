package com.example.myapplication.webrtc.sfu

import com.example.myapplication.webrtc.LocalMediaListener
import com.example.myapplication.webrtc.MediaState
import org.webrtc.VideoTrack

/** Another participant in the group room, as announced by the SFU. */
data class Participant(
    val id: String,
    val name: String,
    val state: MediaState = MediaState()
) {
    /** What every client shows for this participant: several devices can share a name. */
    val label: String get() = participantLabel(id, name)
}

fun participantLabel(id: String, name: String): String = "${name.ifEmpty { "Guest" }} · ${id.take(4)}"

/** Group call events. Everything except the [LocalMediaListener] callbacks arrives on the main thread. */
interface GroupCallListener : LocalMediaListener {
    /** We are in the room; [participants] are the others already there. */
    fun onJoined(participantId: String, participants: List<Participant>)
    fun onParticipantJoined(participant: Participant)
    /** Drop the tile right away; its tracks may still be reported as removed later. */
    fun onParticipantLeft(participantId: String)
    fun onParticipantMediaState(participantId: String, state: MediaState)
    /** The participant's video track, or null once it is gone. May arrive before [onParticipantJoined]. */
    fun onRemoteVideoTrack(participantId: String, track: VideoTrack?)
    fun onChatMessage(participantId: String, name: String, text: String)
    /** A non-fatal error reported by the server. */
    fun onServerError(message: String)
    /** The server closed the call (fatal error, socket closed, media connection lost). */
    fun onCallEnded(message: String)
}
