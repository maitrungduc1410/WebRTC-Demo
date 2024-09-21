package com.example.myapplication;

import android.app.Notification;
import android.app.Service;
import android.content.Intent;
import android.os.IBinder;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;

import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.os.Build;

public class ScreenCaptureService extends Service {
    public static final String CHANNEL_ID = "ScreenCaptureChannel";

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        // Start the foreground service
        startForeground(1, createNotification());
        System.out.println("ScreenCaptureService+++++" + CallActivity.peerConnectionClient);
        // Initialize the MediaProjection
        CallActivity.peerConnectionClient.createDeviceCapture(true, CallActivity.mediaProjectionPermissionResultData);

        return START_STICKY;
    }

    private Notification createNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "Screen Capture",
                    NotificationManager.IMPORTANCE_LOW
            );
            getSystemService(NotificationManager.class).createNotificationChannel(channel);
        }

        return new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentTitle("Screen Capture Service")
                .setContentText("Capturing the screen...")
                .setSmallIcon(R.drawable.ic_launcher_foreground)
                .build();
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        // Clean up resources
        // If necessary, stop MediaProjection here as well
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}