package com.example.myapplication;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.constraintlayout.widget.ConstraintLayout;

import android.Manifest;
import android.content.Context;
import android.content.Intent;
import android.graphics.Point;
import android.media.AudioManager;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.text.InputType;
import android.util.Log;
import android.view.Window;
import android.widget.EditText;
import android.widget.ImageButton;
import android.widget.Toast;

import org.webrtc.DataChannel;
import org.webrtc.EglBase;
import org.webrtc.MediaStream;
import org.webrtc.RendererCommon;
import org.webrtc.SurfaceViewRenderer;
import org.webrtc.VideoTrack;

import android.view.WindowManager.LayoutParams;

public class CallActivity extends AppCompatActivity implements PeerConnectionClient.RtcListener {
    private final static String TAG = CallActivity.class.getCanonicalName();

    public static PeerConnectionClient peerConnectionClient;
    private String mSocketAddress;
    private String roomId;

    private static final String[] RequiredPermissions = new String[]{Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO};
    protected PermissionChecker permissionChecker = new PermissionChecker();

    private SurfaceViewRenderer localView;
    private int localViewWidth = 150;
    private int localViewHeight = 150;

    private int remoteViewWidth = 150;
    private int remoteViewHeight = 150;
    private SurfaceViewRenderer remoteView;
    private EglBase eglBase;
    private boolean videoEnabled = true;
    private boolean audioEnabled = true;
    private boolean isSpeakerOn = false;
    private boolean dataChannelReady = false;

    private static final int SCREEN_CAPTURE_REQUEST_CODE = 100;
    private MediaProjectionManager mediaProjectionManager;
    public static Intent mediaProjectionPermissionResultData;

    private boolean isScreenSharing = false;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);
        getWindow().addFlags(
                LayoutParams.FLAG_KEEP_SCREEN_ON
                        | LayoutParams.FLAG_DISMISS_KEYGUARD
                        | LayoutParams.FLAG_SHOW_WHEN_LOCKED
                        | LayoutParams.FLAG_TURN_SCREEN_ON);
        setContentView(R.layout.activity_call);

        // Disable the native back button (up button)
        getSupportActionBar().setDisplayHomeAsUpEnabled(false);

        mSocketAddress = getString(R.string.serverAddress);

        localView = findViewById(R.id.local_view);
        remoteView = findViewById(R.id.remote_view);

        eglBase = EglBase.create();

        localView.init(eglBase.getEglBaseContext(), null);
        localView.setScalingType(RendererCommon.ScalingType.SCALE_ASPECT_FIT);
        localView.setMirror(false);
        localView.setZOrderMediaOverlay(true);
        localView.setEnableHardwareScaler(true); // Enable hardware scaler for efficiency

        remoteView.init(eglBase.getEglBaseContext(), null);
        remoteView.setScalingType(RendererCommon.ScalingType.SCALE_ASPECT_FILL);
        remoteView.setMirror(false);
//        remoteView.setZOrderMediaOverlay(true);
        remoteView.setEnableHardwareScaler(true);

        final Intent intent = getIntent();
        roomId = intent.getStringExtra(MainActivity.EXTRA_MESSAGE);

        setTitle("RoomID: " + roomId);

        checkPermissions();

        init();
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

        peerConnectionClient = new PeerConnectionClient(roomId, this, mSocketAddress,  eglBase);

        if (PermissionChecker.hasPermissions(this, RequiredPermissions)) {
            peerConnectionClient.start();
        }

        ImageButton switchCamera = findViewById(R.id.switch_camera);
        switchCamera.setOnClickListener(v -> {
            peerConnectionClient.switchCamera();
        });

        ImageButton toggleVideo = findViewById(R.id.toggle_video);
        toggleVideo.setOnClickListener(v -> {
            peerConnectionClient.toggleVideo(!videoEnabled);
            videoEnabled = !videoEnabled;
            toggleVideo.setImageResource(videoEnabled ? R.drawable.video_slash_fill : R.drawable.video_fill);
        });

        ImageButton toggleAudio = findViewById(R.id.toggle_audio);
        toggleAudio.setOnClickListener(v -> {
            peerConnectionClient.toggleAudio(!audioEnabled);
            audioEnabled = !audioEnabled;

            toggleAudio.setImageResource(audioEnabled ? R.drawable.mic_slash_fill : R.drawable.mic_fill);
        });

        ImageButton toggleSpeaker = findViewById(R.id.toggle_speaker);
        AudioManager audioManager = (AudioManager) getSystemService(Context.AUDIO_SERVICE);

        toggleSpeaker.setOnClickListener(v -> {
            isSpeakerOn = !isSpeakerOn;

            if (isSpeakerOn) {
                audioManager.setMode(AudioManager.MODE_IN_COMMUNICATION);
                audioManager.setSpeakerphoneOn(true);
            } else {
                audioManager.setSpeakerphoneOn(false);
                audioManager.setMode(AudioManager.MODE_NORMAL);
            }

            toggleSpeaker.setImageResource(isSpeakerOn ? R.drawable.speaker_slash_fill : R.drawable.speaker_wave_3_fill);
        });

        ImageButton hangUp = findViewById(R.id.hang_up);
        hangUp.setOnClickListener(v -> onBackPressed());

        ImageButton toggleMessage = findViewById(R.id.toggle_message);
        toggleMessage.setEnabled(false);
        toggleMessage.setOnClickListener(v -> {
            if (dataChannelReady) {
                AlertDialog.Builder builder = new AlertDialog.Builder(this);
                builder.setTitle("Send message");

                final EditText input = new EditText(this);
                input.setInputType(InputType.TYPE_CLASS_TEXT);
                input.setHint("Message...");
                builder.setView(input);

                builder.setPositiveButton("Send", (dialog, which) -> peerConnectionClient.sendDataChannelMessage(input.getText().toString()));
                builder.setNegativeButton("Close", (dialog, which) -> dialog.cancel());

                builder.show();
            } else {
                peerConnectionClient.createDataChannel(getString(R.string.dataChannelName));
            }
        });

        ImageButton shareScreen = findViewById(R.id.share_screen);
        shareScreen.setOnClickListener(v -> {
            if (isScreenSharing) {
                // stop screen capture
                Intent stopIntent = new Intent(this, ScreenCaptureService.class);
                stopService(stopIntent);

                peerConnectionClient.createDeviceCapture(false, null);
                isScreenSharing = false;
                shareScreen.setImageResource(R.drawable.tv_fill);

                findViewById(R.id.switch_camera).setEnabled(true);
//                switchCamera.setEnabled(true);

                onStatusChanged("Screen sharing closed");
            } else {
                // Initialize MediaProjectionManager
                mediaProjectionManager = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);

                // Request screen capture permission
                Intent screenCaptureIntent = mediaProjectionManager.createScreenCaptureIntent();
                startActivityForResult(screenCaptureIntent, SCREEN_CAPTURE_REQUEST_CODE);
            }
        });

        localView.addFrameListener(bitmap -> {
            Log.d(TAG, "localView size: " + bitmap.getWidth() + " " + bitmap.getHeight());
            // this will give exact size which remote peer will see
            localViewWidth = bitmap.getWidth();
            localViewHeight = bitmap.getHeight();
        }, 1);

        remoteView.addFrameListener(bitmap -> {
            Log.d(TAG, "remoteView size: " + bitmap.getWidth() + " " + bitmap.getHeight());
            // this will give exact size which remote peer will see
            remoteViewWidth = bitmap.getWidth();
            remoteViewHeight = bitmap.getHeight();
        }, 1);
    }

    @Override
    public void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode == SCREEN_CAPTURE_REQUEST_CODE && resultCode == RESULT_OK) {
            mediaProjectionPermissionResultData = data;
            // Start the foreground service and pass MediaProjection to it
            Intent serviceIntent = new Intent(this, ScreenCaptureService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                startForegroundService(serviceIntent); // Android O and above
            } else {
                startService(serviceIntent); // For earlier versions
            }

            isScreenSharing = true;

            ImageButton shareScreen = findViewById(R.id.share_screen);
            shareScreen.setImageResource(R.drawable.tv_slash);
            findViewById(R.id.switch_camera).setEnabled(false);
            onStatusChanged("Screen sharing started");
        }
    }

//    @Override
//    public void onPause() {
//        super.onPause();
////        vsv.onPause();
//        if (peerConnectionClient != null) {
//            peerConnectionClient.onPause();
//        }
//    }
//
//    @Override
//    public void onResume() {
//        super.onResume();
//        if (peerConnectionClient != null) {
//            peerConnectionClient.onResume();
//        }
//    }

    @Override
    public void onDestroy() {
        System.out.println("1111111 1CallActivity onDestroy " + peerConnectionClient);
        if (peerConnectionClient != null) {
            System.out.println("1111111 2 CallActivity onDestroy");
            peerConnectionClient.onDestroy();
        }

        localView.release();
        remoteView.release();
        eglBase.release();

        super.onDestroy();
    }

    @Override
    public void onStatusChanged(final String newStatus) {
        runOnUiThread(() -> Toast.makeText(CallActivity.this, newStatus, Toast.LENGTH_SHORT).show());
    }

    @Override
    public void onDataChannelMessage(final String message) {
        onStatusChanged("Received message: " + message);
    }

    @Override
    public void onAddLocalStream(MediaStream localStream) {
        Log.d(TAG, "onAddLocalStream");

        VideoTrack videoTrack = localStream.videoTracks.get(0);
        videoTrack.setEnabled(true);
        localStream.videoTracks.get(0).addSink(localView);
    }

    @Override
    public void onRemoveLocalStream(MediaStream localStream) {
        Log.d(TAG, "onRemoveLocalStream");
        localStream.videoTracks.get(0).removeSink(localView);
    }

    @Override
    public void onAddRemoteStream(MediaStream remoteStream) {
        Log.d(TAG, "onAddRemoteStream " + remoteStream.getId() + " " + remoteStream.videoTracks.size() + " " + remoteStream.audioTracks.size());

        if (!remoteStream.videoTracks.isEmpty()) {
            VideoTrack remoteVideoTrack = remoteStream.videoTracks.get(0);
            remoteVideoTrack.addSink(remoteView);

            runOnUiThread(() -> {
                /// Calculate the new height based on the aspect ratio, fix width to 100dp
                int newWidth = (int) getResources().getDisplayMetrics().density * 100;
                int newHeight = (int) getResources().getDisplayMetrics().density * 100 *  localViewHeight / localViewWidth;

                ConstraintLayout.LayoutParams params = (ConstraintLayout.LayoutParams) localView.getLayoutParams();
                params.width = newWidth;
                params.height = newHeight;
                params.rightMargin = 16;
                params.topMargin = 16;
                params.topToTop = ConstraintLayout.LayoutParams.PARENT_ID;
                params.rightToRight = ConstraintLayout.LayoutParams.PARENT_ID;
                localView.setLayoutParams(params);


                // Get the screen width
                Point displaySize = new Point();
                getWindowManager().getDefaultDisplay().getSize(displaySize);
                int screenWidth = displaySize.x;

                // Calculate the new height based on the aspect ratio
                int newRemoteHeight = (int) (screenWidth * (remoteViewHeight / (float) remoteViewWidth));

                // Update the layout parameters for remoteView
                ConstraintLayout.LayoutParams remoteParams = (ConstraintLayout.LayoutParams) remoteView.getLayoutParams();
//                remoteParams.width = screenWidth; // no need to set width explicily, we'll use layout constraints below
                remoteParams.height = newRemoteHeight;
                remoteParams.topToTop = ConstraintLayout.LayoutParams.PARENT_ID;    // Align top to parent
                remoteParams.bottomToBottom = ConstraintLayout.LayoutParams.PARENT_ID; // Align bottom to parent
                remoteParams.startToStart = ConstraintLayout.LayoutParams.PARENT_ID;   // Align start to parent
                remoteParams.endToEnd = ConstraintLayout.LayoutParams.PARENT_ID; // Align end to parent
                remoteParams.verticalBias = 0.5f;  // Center vertically

                remoteView.setLayoutParams(remoteParams);
            });
        }
    }

    @Override
    public void onRemoveRemoteStream() {
        Log.d(TAG, "onRemoveRemoteStream");
        runOnUiThread(() -> {
            remoteView.clearImage();

            // Revert localView to full screen
            ConstraintLayout.LayoutParams params = (ConstraintLayout.LayoutParams) localView.getLayoutParams();
            params.width = ConstraintLayout.LayoutParams.MATCH_PARENT;
            params.height = ConstraintLayout.LayoutParams.MATCH_PARENT;
            params.rightMargin = 0;
            params.bottomMargin = 0;
            params.topToBottom = ConstraintLayout.LayoutParams.UNSET;
            params.bottomToBottom = ConstraintLayout.LayoutParams.UNSET;
            params.endToEnd = ConstraintLayout.LayoutParams.UNSET;
            params.startToStart = ConstraintLayout.LayoutParams.UNSET;
            params.horizontalBias = 0.5f; // Center horizontally
            params.verticalBias = 0.5f; // Center vertically

            localView.setLayoutParams(params);
        });
    }

    @Override
    public void onDataChannelStateChange(DataChannel.State state) {
        if (state == DataChannel.State.OPEN) {
            dataChannelReady = true;
            onStatusChanged("Data channel ready");
        } else {
            dataChannelReady = false;
            onStatusChanged("Data channel closed");
        }

        ImageButton toggleMessage = findViewById(R.id.toggle_message);
        toggleMessage.setImageResource(dataChannelReady ? R.drawable.checkmark_bubble_fill : R.drawable.exclamationmark_bubble_fill);
    }

    @Override
    public void onPeersConnectionStatusChange(boolean success) {
        runOnUiThread(() -> {
            findViewById(R.id.toggle_message).setEnabled(success);
        });
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions, @NonNull int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        permissionChecker.onRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}