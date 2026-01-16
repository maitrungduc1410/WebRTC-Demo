package com.example.myapplication

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat

class ScreenCaptureService : Service() {
    
    companion object {
        const val CHANNEL_ID = "ScreenCaptureChannel"
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Start the foreground service
        startForeground(1, createNotification())
        
        // Initialize the MediaProjection
        val peerConnectionClient = CallActivity.peerConnectionClientRef?.get()
        println("ScreenCaptureService $peerConnectionClient")
        
        peerConnectionClient?.createDeviceCapture(
            true,
            CallActivity.mediaProjectionPermissionResultData
        )

        return START_STICKY
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
            .setContentTitle("Screen Capture Service")
            .setContentText("Capturing the screen...")
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .build()
    }

    override fun onDestroy() {
        super.onDestroy()
        // Clean up resources
        // If necessary, stop MediaProjection here as well
    }

    override fun onBind(intent: Intent?): IBinder? {
        return null
    }
}
