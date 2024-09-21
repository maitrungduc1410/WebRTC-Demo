package com.example.myapplication;

import android.content.Context;
import android.content.Intent;
import android.media.projection.MediaProjection;
import android.util.Log;

import androidx.annotation.Nullable;

import org.json.JSONException;
import org.json.JSONObject;
import org.webrtc.AudioSource;
import org.webrtc.AudioTrack;
import org.webrtc.Camera1Enumerator;
import org.webrtc.Camera2Enumerator;
import org.webrtc.CameraEnumerationAndroid;
import org.webrtc.CameraEnumerator;
import org.webrtc.CameraVideoCapturer;
import org.webrtc.DataChannel;
import org.webrtc.DefaultVideoDecoderFactory;
import org.webrtc.DefaultVideoEncoderFactory;
import org.webrtc.EglBase;
import org.webrtc.IceCandidate;
import org.webrtc.MediaConstraints;
import org.webrtc.MediaStream;
import org.webrtc.PeerConnection;
import org.webrtc.PeerConnectionFactory;
import org.webrtc.RtpSender;
import org.webrtc.SdpObserver;
import org.webrtc.SessionDescription;
import org.webrtc.Size;
import org.webrtc.SurfaceTextureHelper;
import org.webrtc.VideoCapturer;
import org.webrtc.VideoDecoderFactory;
import org.webrtc.VideoEncoderFactory;
import org.webrtc.VideoSource;
import org.webrtc.VideoTrack;
import org.webrtc.ScreenCapturerAndroid;

import java.net.URISyntaxException;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import io.socket.client.IO;
import io.socket.client.Socket;
import io.socket.emitter.Emitter;

public class PeerConnectionClient {
    private final static String TAG = PeerConnectionClient.class.getCanonicalName();
    private PeerConnectionFactory factory;
    private final MediaConstraints pcConstraints = new MediaConstraints();
    private MediaStream localMS;
    private VideoSource videoSource;
    private AudioSource audioSource;
    private VideoCapturer videoCapturer;
    private SurfaceTextureHelper surfaceTextureHelper;
    private final RtcListener mListener;
    private Socket socketClient;
    private Peer peer;
    private final String roomId;
    private final EglBase rootEglBase;
    private boolean useFrontCamera = true;
    private DataChannel mDataChannel;


    /**
     * Implement this interface to be notified of events.
     */
    public interface RtcListener {
        void onStatusChanged(String newStatus);

        void onAddLocalStream(MediaStream localStream);

        void onRemoveLocalStream(MediaStream localStream);

        void onAddRemoteStream(MediaStream remoteStream);

        void onRemoveRemoteStream();

        void onDataChannelMessage(String message);

        void onDataChannelStateChange(DataChannel.State state);

        void onPeersConnectionStatusChange(boolean success);
    }

    private class MessageHandler {
        private final Emitter.Listener onConnect = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                JSONObject obj = new JSONObject();
                try {
                    obj.put("roomId", roomId);
                    socketClient.emit("join room", obj);
                } catch (JSONException e) {
                    e.printStackTrace();
                }
            }
        };

        private final Emitter.Listener onDisconnect = args -> Log.d(TAG, "Socket disconnected");

        private final Emitter.Listener onNewUserJoined = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                peer = new Peer();
                peer.pc.createOffer(peer, pcConstraints);
            }
        };

        private final Emitter.Listener onOffer = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                JSONObject data = (JSONObject) args[0];

                // no need to recreate peer connection if we already set
                // can happen in case of creating data channel where remote peer sends another offer/answer
                if (peer == null) {
                    peer = new Peer();
                }

                try {
                    JSONObject offer = data.getJSONObject("offer");

                    SessionDescription sdp = new SessionDescription(
                            SessionDescription.Type.fromCanonicalForm(offer.getString("type")),
                            offer.getString("sdp")
                    );
                    peer.pc.setRemoteDescription(peer, sdp);
                    peer.pc.createAnswer(peer, pcConstraints);
                } catch (JSONException e) {
                    e.printStackTrace();
                }
            }
        };

        private final Emitter.Listener onAnswer = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                JSONObject data = (JSONObject) args[0];
                try {
                    JSONObject answer = data.getJSONObject("answer");

                    SessionDescription sdp = new SessionDescription(
                            SessionDescription.Type.fromCanonicalForm(answer.getString("type")),
                            answer.getString("sdp")
                    );
                    peer.pc.setRemoteDescription(peer, sdp);
                } catch (JSONException e) {
                    e.printStackTrace();
                }
            }
        };

        private final Emitter.Listener onNewIceCandidate = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                JSONObject data = (JSONObject) args[0];
                try {
                    JSONObject iceCandidate = data.getJSONObject("iceCandidate");

                    if (peer.pc.getRemoteDescription() != null) {
                        IceCandidate candidate = new IceCandidate(
                                iceCandidate.getString("sdpMid"),
                                iceCandidate.getInt("sdpMLineIndex"),
                                iceCandidate.getString("candidate")
                        );
                        peer.pc.addIceCandidate(candidate);
                    }
                } catch (JSONException e) {
                    e.printStackTrace();
                }
            }
        };
    }

    private class Peer implements SdpObserver, PeerConnection.Observer, DataChannel.Observer {
        private final PeerConnection pc;

        @Override
        public void onCreateSuccess(final SessionDescription sdp) {
            try {
                pc.setLocalDescription(Peer.this, sdp);

                JSONObject payload = new JSONObject();

                JSONObject desc = new JSONObject();
                desc.put("type", sdp.type.canonicalForm()); // sdp.type.canonicalForm() returns: 'offer' or 'answer'
                desc.put("sdp", sdp.description);

                payload.put(sdp.type.canonicalForm(), desc);
                payload.put("roomId", roomId);

                socketClient.emit(sdp.type.canonicalForm(), payload);
            } catch (JSONException e) {
                e.printStackTrace();
            }
        }

        @Override
        public void onSetSuccess() {
        }

        @Override
        public void onCreateFailure(String s) {
        }

        @Override
        public void onSetFailure(String s) {
        }

        @Override
        public void onSignalingChange(PeerConnection.SignalingState signalingState) {
        }

        @Override
        public void onIceConnectionChange(PeerConnection.IceConnectionState iceConnectionState) {
            if (iceConnectionState == PeerConnection.IceConnectionState.DISCONNECTED) {
                mListener.onStatusChanged("DISCONNECTED");
                mListener.onRemoveRemoteStream();

                if (mDataChannel != null) {
                    mDataChannel.unregisterObserver();
                    mDataChannel.dispose();
                    mDataChannel = null;
                }

                pc.dispose();

                mListener.onPeersConnectionStatusChange(false);
            } else if (iceConnectionState == PeerConnection.IceConnectionState.CONNECTED) {
                Log.d(TAG, "Peers connected");
                mListener.onStatusChanged("CONNECTED");
                mListener.onPeersConnectionStatusChange(true);
            }
        }

        @Override
        public void onIceConnectionReceivingChange(boolean b) {

        }

        @Override
        public void onIceGatheringChange(PeerConnection.IceGatheringState iceGatheringState) {
        }

        @Override
        public void onIceCandidate(final IceCandidate candidate) {
            try {
                JSONObject payload = new JSONObject();
                JSONObject iceCandidate = new JSONObject();

                iceCandidate.put("sdpMLineIndex", candidate.sdpMLineIndex);
                iceCandidate.put("sdpMid", candidate.sdpMid);
                iceCandidate.put("candidate", candidate.sdp);

                payload.put("iceCandidate", iceCandidate);
                payload.put("roomId", roomId);

                socketClient.emit("new ice candidate", payload);
            } catch (JSONException e) {
                e.printStackTrace();
            }
        }

        @Override
        public void onIceCandidatesRemoved(IceCandidate[] iceCandidates) {
            peer.pc.removeIceCandidates(iceCandidates);
        }

        @Override
        public void onAddStream(MediaStream mediaStream) {
            Log.d(TAG, "onAddStream " + mediaStream.getId());
            // remote streams are displayed from 1 to MAX_PEER (0 is localStream)
            mListener.onAddRemoteStream(mediaStream);
        }

        @Override
        public void onRemoveStream(MediaStream mediaStream) {
            Log.d(TAG, "onRemoveStream " + mediaStream.getId());
            mListener.onRemoveRemoteStream();
        }

        @Override
        public void onDataChannel(DataChannel dataChannel) {
            Log.d(TAG, "onDataChannel " + dataChannel.state());

            mDataChannel = dataChannel;
            mDataChannel.registerObserver(this);
        }

        @Override
        public void onRenegotiationNeeded() {
        }

        Peer() {
            Log.d(TAG, "11111new Peer created");
            PeerConnection.RTCConfiguration rtcConfig = new PeerConnection.RTCConfiguration(new ArrayList<>());
            rtcConfig.iceServers.add(PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer());

            this.pc = factory.createPeerConnection(rtcConfig, this);

            pc.addTrack(localMS.audioTracks.get(0), Collections.singletonList("ARDAMS"));
            pc.addTrack(localMS.videoTracks.get(0), Collections.singletonList("ARDAMS"));

            mListener.onStatusChanged("CONNECTING");
        }


        @Override
        public void onBufferedAmountChange(long l) {

        }

        @Override
        public void onStateChange() {
            Log.d(TAG, "11111 " + mDataChannel.state());
            mListener.onDataChannelStateChange(mDataChannel.state());
        }

        @Override
        public void onMessage(DataChannel.Buffer buffer) {
            // Handle incoming message
            if (buffer.binary) {
                // Handle binary message
                Log.d(TAG, "Binary message");
            } else {
                // Convert buffer data to string and handle text message
                ByteBuffer data = buffer.data;
                byte[] bytes = new byte[data.remaining()];
                data.get(bytes);
                String message = new String(bytes);
                mListener.onDataChannelMessage(message);
            }
        }
    }

    public PeerConnectionClient(String roomId, RtcListener listener, String host, EglBase rootEglBase) {
        this.roomId = roomId;
        mListener = listener;
        this.rootEglBase = rootEglBase;

        // Initialize WebRTC
        PeerConnectionFactory.InitializationOptions initializationOptions =
                PeerConnectionFactory.InitializationOptions.builder((Context) listener)
                        .setEnableInternalTracer(true)
                        .createInitializationOptions();
        PeerConnectionFactory.initialize(initializationOptions);

        PeerConnectionFactory.Options options = new PeerConnectionFactory.Options();
        // This is very important: https://stackoverflow.com/a/69983765/7569705
        /*
            Without encoder/decoder we won't be able to send/receive remote stream

            The error message was triggered due to the offer containing H264 codecs whilst the Android Client was not anticipating H264 and was not setup to encode and/or decode this particular hardware encoded stream.
         */
        VideoEncoderFactory encoderFactory = new DefaultVideoEncoderFactory(rootEglBase.getEglBaseContext(), true, true);
        VideoDecoderFactory decoderFactory = new DefaultVideoDecoderFactory(rootEglBase.getEglBaseContext());

        factory = PeerConnectionFactory.builder()
                .setOptions(options)
                .setVideoDecoderFactory(decoderFactory)
                .setVideoEncoderFactory(encoderFactory)
                .createPeerConnectionFactory();

        MessageHandler messageHandler = new MessageHandler();

        try {
            socketClient = IO.socket(host);
        } catch (URISyntaxException e) {
            e.printStackTrace();
        }
        socketClient.on(Socket.EVENT_CONNECT, messageHandler.onConnect);
        socketClient.on("new user joined", messageHandler.onNewUserJoined);
        socketClient.on("offer", messageHandler.onOffer);
        socketClient.on("answer", messageHandler.onAnswer);
        socketClient.on("new ice candidate", messageHandler.onNewIceCandidate);
        socketClient.on(Socket.EVENT_DISCONNECT, messageHandler.onDisconnect);

        socketClient.connect();

        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("OfferToReceiveAudio", "true"));
        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("OfferToReceiveVideo", "true"));
        pcConstraints.optional.add(new MediaConstraints.KeyValuePair("DtlsSrtpKeyAgreement", "true"));


        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("maxHeight", Integer.toString(1080)));
        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("maxWidth", Integer.toString(2400)));
        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("maxFrameRate", Integer.toString(30)));
        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("minFrameRate", Integer.toString(30)));
    }

    public void onPause() {
        if (videoSource != null) videoSource.dispose();
    }

    public void onResume() {
        if (videoSource != null) videoSource.dispose();
    }

    public void onDestroy() {
        socketClient.close();

        Log.d(TAG, "Closing peer connection.");
        if (mDataChannel != null) {
            mDataChannel.unregisterObserver();
            mDataChannel.dispose();
            mDataChannel = null;
        }

        Log.d(TAG, "Stopping capture.");
        if (videoCapturer != null) {
            try {
                videoCapturer.stopCapture();
            } catch (InterruptedException e) {
                throw new RuntimeException(e);
            }
            videoCapturer.dispose();
            videoCapturer = null;
        }

        Log.d(TAG, "Closing video source.");
        if (videoSource != null) {
            videoSource.dispose();
            videoSource = null;
        }

        Log.d(TAG, "Closing audio source.");
        if (audioSource != null) {
            audioSource.dispose();
            audioSource = null;
        }

        Log.d(TAG, "Closing surface texture helper.");
        if (surfaceTextureHelper != null) {
            surfaceTextureHelper.dispose();
            surfaceTextureHelper = null;
        }

        Log.d(TAG, "Closing peer connection.");
        if (peer != null && peer.pc != null) {
            peer.pc.dispose();
            peer = null;
        }

        Log.d(TAG, "Closing peer connection factory.");
        if (factory != null) {
            factory.dispose();
            factory = null;
        }

        PeerConnectionFactory.stopInternalTracingCapture();
        PeerConnectionFactory.shutdownInternalTracer();

        Log.d(TAG, "Cleanup complete.");
    }

    public void start() {
        setCamera();
    }

    private void setCamera() {
        localMS = factory.createLocalMediaStream("LOCAL_MS");
        videoCapturer = getVideoCapturer();
        videoSource = factory.createVideoSource(videoCapturer.isScreencast());

        surfaceTextureHelper = SurfaceTextureHelper.create("CaptureThread", rootEglBase.getEglBaseContext());
        videoCapturer.initialize(surfaceTextureHelper,
                (Context) mListener, videoSource.getCapturerObserver());

        Utils.ScreenDimensions dimensions = Utils.getScreenDimentions((Context) mListener);
        int fps = Utils.getFps((Context) mListener);

        Log.d(TAG, "FPSSSS: " + fps + " " + dimensions.screenWidth + " " + dimensions.screenHeight);

        videoCapturer.startCapture(dimensions.screenWidth, dimensions.screenHeight, fps);

        localMS.addTrack(factory.createVideoTrack("LOCAL_MS_VS", videoSource));

        audioSource = factory.createAudioSource(new MediaConstraints());
        localMS.addTrack(factory.createAudioTrack("LOCAL_MS_AT", audioSource));

        mListener.onAddLocalStream(localMS);
    }

    private VideoCapturer getVideoCapturer() {
        VideoCapturer videoCapturer;
        CameraEnumerator enumerator;

        if (Camera2Enumerator.isSupported((Context) mListener)) {
            enumerator = new Camera2Enumerator((Context) mListener);
        } else {
            enumerator = new Camera1Enumerator(true);
        }

        // Switch the camera based on the current state
        videoCapturer = createCapturer(enumerator, useFrontCamera);

        // Toggle the camera for the next switch
//        useFrontCamera = !useFrontCamera;

        return videoCapturer;
    }

    private VideoCapturer createCapturer(CameraEnumerator enumerator, boolean frontFacing) {
        final String[] deviceNames = enumerator.getDeviceNames();
        for (String deviceName : deviceNames) {
            if (enumerator.isFrontFacing(deviceName) == frontFacing) {
                VideoCapturer videoCapturer = enumerator.createCapturer(deviceName, null);
                if (videoCapturer != null) {
                    return videoCapturer;
                }
            }
        }
        return null;
    }

    public void switchCamera() {
        if (videoSource != null && !videoCapturer.isScreencast()) {
            CameraVideoCapturer cameraVideoCapturer = (CameraVideoCapturer) videoCapturer;
            cameraVideoCapturer.switchCamera(new CameraVideoCapturer.CameraSwitchHandler() {
                @Override
                public void onCameraSwitchDone(boolean isFrontCamera) {
                    useFrontCamera = isFrontCamera;
                }

                @Override
                public void onCameraSwitchError(String errorDescription) {
                    Log.e(TAG, "Error switching camera: " + errorDescription);
                }
            });
        }
    }

    public void toggleAudio(boolean enable) {
        if (localMS != null && !localMS.audioTracks.isEmpty()) {
            AudioTrack audioTrack = localMS.audioTracks.get(0);
            audioTrack.setEnabled(enable);
        }
    }

    public void toggleVideo(boolean enable) {
        if (localMS != null && !localMS.videoTracks.isEmpty()) {
            VideoTrack videoTrack = localMS.videoTracks.get(0);
            videoTrack.setEnabled(enable);
        }
    }

    public void createDataChannel(String dataChannelName) {
        Log.d(TAG, "11111 createDataChannel: " + dataChannelName);
        DataChannel.Init init = new DataChannel.Init();
        mDataChannel = peer.pc.createDataChannel(dataChannelName, init);

        peer.pc.createOffer(peer, pcConstraints);

        mDataChannel.registerObserver(peer);
    }

    public void sendDataChannelMessage(String message) {
        if (mDataChannel.state() == DataChannel.State.OPEN) {
            ByteBuffer buffer = ByteBuffer.wrap(message.getBytes());
            DataChannel.Buffer dataBuffer = new DataChannel.Buffer(buffer, false); // false means it's a text message
            mDataChannel.send(dataBuffer);
        }
    }

    public void createDeviceCapture(boolean isScreencast, @Nullable Intent mediaProjectionPermissionResultData) {
        if (videoCapturer != null) {
            try {
                videoCapturer.stopCapture();
            } catch (InterruptedException e) {
                throw new RuntimeException(e);
            }
        }

        if (audioSource != null) {
            audioSource.dispose();
            audioSource = null;
        }

        if (videoSource != null) {
            videoSource.dispose(); // Dispose of the old video source
            videoSource = null;
        }

        if (surfaceTextureHelper != null) {
            surfaceTextureHelper.dispose();
            surfaceTextureHelper = null;
        }

        mListener.onRemoveLocalStream(localMS);

        // Remove the old tracks (video + audio)
        if (peer != null) {
            for (RtpSender rtpSender : peer.pc.getSenders()) {
                peer.pc.removeTrack(rtpSender);
            }
        }

        for (VideoTrack videoTrack : localMS.videoTracks) {
            localMS.removeTrack(videoTrack);
        }
        for (AudioTrack audioTrack : localMS.audioTracks) {
            localMS.removeTrack(audioTrack);
        }

        if (isScreencast) {
            videoCapturer = new ScreenCapturerAndroid(
                    mediaProjectionPermissionResultData, new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    Log.d(TAG, "Screen sharing stopped.");
//                reportError("User revoked permission to capture the screen.");
                }
            });
        } else {
            videoCapturer = getVideoCapturer();
        }

        videoSource = factory.createVideoSource(videoCapturer.isScreencast());

        surfaceTextureHelper = SurfaceTextureHelper.create("CaptureThread", rootEglBase.getEglBaseContext());
        videoCapturer.initialize(surfaceTextureHelper,
                (Context) mListener, videoSource.getCapturerObserver());


        Utils.ScreenDimensions dimensions = Utils.getScreenDimentions((Context) mListener);
        int fps = Utils.getFps((Context) mListener);

        Log.d(TAG, "FPSSSS: " + fps + " " + dimensions.screenWidth + " " + dimensions.screenHeight);

        videoCapturer.startCapture(dimensions.screenWidth, dimensions.screenHeight, fps);

        localMS.addTrack(factory.createVideoTrack("LOCAL_MS_VS", videoSource));

        audioSource = factory.createAudioSource(new MediaConstraints());
        localMS.addTrack(factory.createAudioTrack("LOCAL_MS_AT", audioSource));

        if (peer != null) {
            peer.pc.addTrack(localMS.audioTracks.get(0), Collections.singletonList("ARDAMS"));
            peer.pc.addTrack(localMS.videoTracks.get(0), Collections.singletonList("ARDAMS"));

            // Important: since media tracks changed, we have to renegotiate by sending a new offer
            peer.pc.createOffer(peer, pcConstraints);
        }

        mListener.onAddLocalStream(localMS);
    }

    @Nullable
    public List<CameraEnumerationAndroid.CaptureFormat> getSupportedFormats(@Nullable String cameraId) {
        Camera2Enumerator enumerator = new Camera2Enumerator((Context) mListener);
        return enumerator.getSupportedFormats(cameraId);
    }

    public Size findClosestCaptureFormat(@Nullable String cameraId, int width, int height) {
        List<CameraEnumerationAndroid.CaptureFormat> formats = getSupportedFormats(cameraId);

        List<Size> sizes = new ArrayList<>();
        if (formats != null) {
            for (CameraEnumerationAndroid.CaptureFormat format : formats) {
                sizes.add(new Size(format.width, format.height));
            }
        }

        return CameraEnumerationAndroid.getClosestSupportedSize(sizes, width, height);
    }
}