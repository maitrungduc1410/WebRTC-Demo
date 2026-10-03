package com.example.myapplication.webrtc

import org.webrtc.DataChannel
import org.webrtc.MediaStream

/**
 * Interface to be notified of WebRTC events.
 */
interface RtcListener {
    fun onStatusChanged(newStatus: String)
    fun onAddLocalStream(localStream: MediaStream)
    fun onRemoveLocalStream(localStream: MediaStream)
    fun onAddRemoteStream(remoteStream: MediaStream)
    fun onRemoveRemoteStream()
    fun onDataChannelMessage(message: String)
    fun onDataChannelStateChange(state: DataChannel.State)
    fun onPeersConnectionStatusChange(success: Boolean)
    fun onScreenSharingStopped() // Called when MediaProjection is stopped by system
    fun onRemoteMediaState(state: MediaState)
    /** A model the current effect needs could not run; camera frames are dropped until effects change. */
    fun onEffectsFailed() {}
}

/** What a peer is currently sending; `screen` is true for screen or video file sharing. */
data class MediaState(
    val audio: Boolean = true,
    val video: Boolean = true,
    val screen: Boolean = false
)
