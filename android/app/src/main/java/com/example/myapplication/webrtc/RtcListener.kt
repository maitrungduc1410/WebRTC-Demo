package com.example.myapplication.webrtc

import org.webrtc.DataChannel
import org.webrtc.MediaStream

/**
 * Interface to be notified of WebRTC events.
 */
interface RtcListener : LocalMediaListener {
    fun onStatusChanged(newStatus: String)
    fun onAddRemoteStream(remoteStream: MediaStream)
    fun onRemoveRemoteStream()
    fun onDataChannelMessage(message: String)
    fun onDataChannelStateChange(state: DataChannel.State)
    fun onPeersConnectionStatusChange(success: Boolean)
    fun onRemoteMediaState(state: MediaState)
    /** The signaling socket closed or the server refused us (room full); the call is over. */
    fun onCallEnded(message: String)
}

/** What a peer is currently sending; `screen` is true for screen or video file sharing. */
data class MediaState(
    val audio: Boolean = true,
    val video: Boolean = true,
    val screen: Boolean = false
)
