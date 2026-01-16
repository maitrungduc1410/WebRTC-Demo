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
}
