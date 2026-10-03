package com.example.myapplication

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.res.Configuration
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.example.myapplication.webrtc.PeerConnectionClient
import java.lang.ref.WeakReference

/**
 * Foreground service required by Android 10+ while a MediaProjection is active. The capturer can only
 * be created after startForeground, so the service starts it on the call's client.
 */
class ScreenCaptureService : Service() {
    
    companion object {
        const val CHANNEL_ID = "ScreenCaptureChannel"

        // Set by the call screen right before starting the service.
        var mediaProjectionPermissionResultData: Intent? = null

        // WeakReference so the service never outlives the call's client.
        var peerConnectionClientRef: WeakReference<PeerConnectionClient>? = null
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(1, createNotification())

        peerConnectionClientRef?.get()?.createDeviceCapture(true, mediaProjectionPermissionResultData)

        return START_NOT_STICKY
    }

    private fun createNotification(): Notification {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Screen Capture",
                NotificationManager.IMPORTANCE_LOW
            )
            getSystemService(NotificationManager::class.java)?.createNotificationChannel(channel)
        }

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Sharing your screen")
            .setContentText("The other participant can see your screen")
            .setSmallIcon(R.drawable.ic_screen_share)
            .build()
    }

    // The service keeps running while the user shares another app, unlike the call activity,
    // so it is the one that hears about rotations.
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        peerConnectionClientRef?.get()?.onDisplayChanged()
    }

    override fun onBind(intent: Intent?): IBinder? {
        return null
    }
}
