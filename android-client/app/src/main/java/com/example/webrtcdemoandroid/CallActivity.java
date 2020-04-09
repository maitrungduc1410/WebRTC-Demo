package com.example.webrtcdemoandroid;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;

import android.Manifest;
import android.content.Intent;
import android.graphics.Point;
import android.opengl.GLSurfaceView;
import android.os.Bundle;
import android.view.Window;
import android.widget.Toast;

import org.webrtc.MediaStream;
import org.webrtc.VideoRenderer;
import org.webrtc.VideoRendererGui;
import android.view.WindowManager.LayoutParams;

public class CallActivity extends AppCompatActivity implements PeerConnectionClient.RtcListener {
    private static final String VIDEO_CODEC_VP9 = "VP9";
    private static final String AUDIO_CODEC_OPUS = "opus";
    // Local preview screen position before call is connected.
    private static final int LOCAL_X_CONNECTING = 0;
    private static final int LOCAL_Y_CONNECTING = 0;
    private static final int LOCAL_WIDTH_CONNECTING = 100;
    private static final int LOCAL_HEIGHT_CONNECTING = 100;
    // Local preview screen position after call is connected.
    private static final int LOCAL_X_CONNECTED = 72;
    private static final int LOCAL_Y_CONNECTED = 72;
    private static final int LOCAL_WIDTH_CONNECTED = 25;
    private static final int LOCAL_HEIGHT_CONNECTED = 25;
    // Remote video screen position
    private static final int REMOTE_X = 0;
    private static final int REMOTE_Y = 0;
    private static final int REMOTE_WIDTH = 100;
    private static final int REMOTE_HEIGHT = 100;
    private VideoRendererGui.ScalingType scalingType = VideoRendererGui.ScalingType.SCALE_ASPECT_FILL;
    private GLSurfaceView vsv;
    private VideoRenderer.Callbacks localRender;
    private VideoRenderer.Callbacks remoteRender;
    private PeerConnectionClient peerConnectionClient;
    private String mSocketAddress;
    private String roomId;

    private static final String[] RequiredPermissions = new String[]{Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO};
    protected PermissionChecker permissionChecker = new PermissionChecker();

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(
//                LayoutParams.FLAG_FULLSCREEN
                          LayoutParams.FLAG_KEEP_SCREEN_ON
                        | LayoutParams.FLAG_DISMISS_KEYGUARD
                        | LayoutParams.FLAG_SHOW_WHEN_LOCKED
                        | LayoutParams.FLAG_TURN_SCREEN_ON);
        setContentView(R.layout.activity_call);
        mSocketAddress = getString(R.string.serverAddress);

        vsv = findViewById(R.id.glview_call);
        vsv.setPreserveEGLContextOnPause(true);
        vsv.setKeepScreenOn(true);
        VideoRendererGui.setView(vsv, this::init);

        // local and remote render
        remoteRender = VideoRendererGui.create(
                REMOTE_X, REMOTE_Y,
                REMOTE_WIDTH, REMOTE_HEIGHT, scalingType, false);
        localRender = VideoRendererGui.create(
                LOCAL_X_CONNECTING, LOCAL_Y_CONNECTING,
                LOCAL_WIDTH_CONNECTING, LOCAL_HEIGHT_CONNECTING, scalingType, true);

        final Intent intent = getIntent();
        roomId = intent.getStringExtra(MainActivity.EXTRA_MESSAGE);

        setTitle("RoomID: " + roomId); // set action bar title (this method is built-in provided by class AppCompatActivity)

        checkPermissions();
    }

    private void checkPermissions() {
        permissionChecker.verifyPermissions(this, RequiredPermissions, new PermissionChecker.VerifyPermissionsCallback() {

            @Override
            public void onPermissionAllGranted() {

            }

            @Override
            public void onPermissionDeny(String[] permissions) {
                Toast.makeText(CallActivity.this, "Please grant required permissions.", Toast.LENGTH_LONG).show();
            }
        });
    }

    private void init() {
        Point displaySize = new Point();
        getWindowManager().getDefaultDisplay().getSize(displaySize);
        PeerConnectionParameters params = new PeerConnectionParameters(
                true,
                false,
                displaySize.x,
                displaySize.y,
                30,
                1,
                VIDEO_CODEC_VP9,
                true,
                1,
                AUDIO_CODEC_OPUS,
                true
        );

        peerConnectionClient = new PeerConnectionClient(roomId, this, mSocketAddress, params, VideoRendererGui.getEGLContext());

        if (PermissionChecker.hasPermissions(this, RequiredPermissions)) {
            peerConnectionClient.start();
        }
    }

    @Override
    public void onPause() {
        super.onPause();
        vsv.onPause();
        if (peerConnectionClient != null) {
            peerConnectionClient.onPause();
        }
    }

    @Override
    public void onResume() {
        super.onResume();
        vsv.onResume();
        if (peerConnectionClient != null) {
            peerConnectionClient.onResume();
        }
    }

    @Override
    public void onDestroy() {
        if (peerConnectionClient != null) {
            peerConnectionClient.onDestroy();
        }
        super.onDestroy();
    }

    @Override
    public void onStatusChanged(final String newStatus) {
        runOnUiThread(() -> Toast.makeText(CallActivity.this, newStatus, Toast.LENGTH_SHORT).show());
    }

    @Override
    public void onMessage(final String message) {
        runOnUiThread(() -> {
            AlertDialog.Builder builder = new AlertDialog.Builder(CallActivity.this);
            builder.setTitle("Alert");
            builder.setMessage(message);
            builder.setPositiveButton("Close", (dialog, id) -> {
                dialog.cancel();
            });

            builder.setNegativeButton("Go back", (dialog, id) -> {
                dialog.cancel();
                CallActivity.this.finish();
            });

            AlertDialog alert = builder.create();
            alert.show();
        });
    }

    @Override
    public void onLocalStream(MediaStream localStream) {
        localStream.videoTracks.get(0).addRenderer(new VideoRenderer(localRender));
    }

    @Override
    public void onAddRemoteStream(MediaStream remoteStream) {
        remoteStream.videoTracks.get(0).addRenderer(new VideoRenderer(remoteRender));

        // set remoteRender to fullscreen
        VideoRendererGui.update(remoteRender,
                REMOTE_X, REMOTE_Y,
                REMOTE_WIDTH, REMOTE_HEIGHT, scalingType, false);

        // set localRender to be a small, Picture-in-Picture (in bottom right)
        VideoRendererGui.update(localRender,
                LOCAL_X_CONNECTED, LOCAL_Y_CONNECTED,
                LOCAL_WIDTH_CONNECTED, LOCAL_HEIGHT_CONNECTED,
                scalingType, false);
    }

    @Override
    public void onRemoveRemoteStream() {
        // change position of localRender to fullscreen
        VideoRendererGui.update(localRender,
                LOCAL_X_CONNECTING, LOCAL_Y_CONNECTING,
                LOCAL_WIDTH_CONNECTING, LOCAL_HEIGHT_CONNECTING,
                scalingType, false);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions, @NonNull int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        permissionChecker.onRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}
