package com.example.webrtcdemoandroid;

import android.opengl.EGLContext;
import android.util.Log;
import com.github.nkzawa.emitter.Emitter;
import com.github.nkzawa.socketio.client.IO;
import com.github.nkzawa.socketio.client.Socket;
import org.json.JSONException;
import org.json.JSONObject;
import org.webrtc.AudioSource;
import org.webrtc.DataChannel;
import org.webrtc.IceCandidate;
import org.webrtc.MediaConstraints;
import org.webrtc.MediaStream;
import org.webrtc.PeerConnection;
import org.webrtc.PeerConnectionFactory;
import org.webrtc.SdpObserver;
import org.webrtc.SessionDescription;
import org.webrtc.VideoCapturer;
import org.webrtc.VideoCapturerAndroid;
import org.webrtc.VideoSource;

import java.net.URISyntaxException;
import java.util.LinkedList;

public class PeerConnectionClient {
    private final static String TAG = PeerConnectionClient.class.getCanonicalName();
    private PeerConnectionFactory factory;
    private LinkedList<PeerConnection.IceServer> iceServers = new LinkedList<>();
    private PeerConnectionParameters pcParams;
    private MediaConstraints pcConstraints = new MediaConstraints();
    private MediaStream localMS;
    private VideoSource videoSource;
    private RtcListener mListener;
    private Socket socketClient;
    private Peer peer;
    private String roomId;

    /**
     * Implement this interface to be notified of events.
     */
    public interface RtcListener {
        void onStatusChanged(String newStatus);

        void onLocalStream(MediaStream localStream);

        void onAddRemoteStream(MediaStream remoteStream);

        void onRemoveRemoteStream();

        void onMessage(String message);
    }

    private class MessageHandler {
        private Emitter.Listener onConnect = new Emitter.Listener() {
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

        private Emitter.Listener onDisconnect = args -> Log.d(TAG, "Socket disconnected");

        private Emitter.Listener onMessage = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                JSONObject data = (JSONObject) args[0];
                try {
                    String message = data.getString("message");

                    mListener.onMessage(message);
                } catch (JSONException e) {
                    e.printStackTrace();
                }
            }
        };

        private Emitter.Listener onNewUserJoined = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                peer = new Peer();
                peer.pc.createOffer(peer, pcConstraints);
            }
        };

        private Emitter.Listener onOffer = new Emitter.Listener() {
            @Override
            public void call(Object... args) {
                JSONObject data = (JSONObject) args[0];
                peer = new Peer();
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

        private Emitter.Listener onAnswer = new Emitter.Listener() {
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

        private Emitter.Listener onNewIceCandidate = new Emitter.Listener() {
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

    private class Peer implements SdpObserver, PeerConnection.Observer {
        private PeerConnection pc;

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
            }

            if (iceConnectionState == PeerConnection.IceConnectionState.CONNECTED) {
                Log.d(TAG, "Peers connected");
                mListener.onStatusChanged("CONNECTED");
            }
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
        public void onAddStream(MediaStream mediaStream) {
            Log.d(TAG, "onAddStream " + mediaStream.label());
            // remote streams are displayed from 1 to MAX_PEER (0 is localStream)
            mListener.onAddRemoteStream(mediaStream);
        }

        @Override
        public void onRemoveStream(MediaStream mediaStream) {
            Log.d(TAG, "onRemoveStream " + mediaStream.label());
            mListener.onRemoveRemoteStream();
        }

        @Override
        public void onDataChannel(DataChannel dataChannel) {
        }

        @Override
        public void onRenegotiationNeeded() {

        }

        Peer() {
            Log.d(TAG, "new Peer created");
            this.pc = factory.createPeerConnection(iceServers, pcConstraints, this);

            pc.addStream(localMS); //, new MediaConstraints()
            mListener.onStatusChanged("CONNECTING");
        }
    }

    public PeerConnectionClient(String roomId, RtcListener listener, String host, PeerConnectionParameters params, EGLContext mEGLcontext) {
        this.roomId = roomId;
        mListener = listener;
        pcParams = params;
        PeerConnectionFactory.initializeAndroidGlobals(listener, true, true,
                params.videoCodecHwAcceleration, mEGLcontext);
        factory = new PeerConnectionFactory();
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
        socketClient.on("message", messageHandler.onMessage);

        socketClient.connect();

        iceServers.add(new PeerConnection.IceServer("stun:23.21.150.121"));
        iceServers.add(new PeerConnection.IceServer("stun:stun.l.google.com:19302"));

        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("OfferToReceiveAudio", "true"));
        pcConstraints.mandatory.add(new MediaConstraints.KeyValuePair("OfferToReceiveVideo", "true"));
        pcConstraints.optional.add(new MediaConstraints.KeyValuePair("DtlsSrtpKeyAgreement", "true"));
    }

    /**
     * Call this method in Activity.onPause()
     */
    public void onPause() {
        if (videoSource != null) videoSource.stop();
    }

    /**
     * Call this method in Activity.onResume()
     */
    public void onResume() {
        if (videoSource != null) videoSource.restart();
    }

    /**
     * Call this method in Activity.onDestroy()
     */
    public void onDestroy() {
        android.os.Process.killProcess(android.os.Process.myPid()); // use this code as videoSource.dispose will cause app crash (but if comment that line we cannot start the video source again as access to camera is not released)

//        // Ignore this section
//        if (videoSource != null) {
//            videoSource.dispose();
//        }
        // Ignore the section above

//        factory.dispose();
//        socketClient.disconnect();
//        socketClient.close();
    }

    /**
     * Start the client.
     * <p>
     * Set up the local stream and notify the signaling server.
     * Call this method after onCallReady.
     *
     */
    public void start() {
        setCamera();
    }

    private void setCamera() {
        localMS = factory.createLocalMediaStream("LOCAL_MS");
        if (pcParams.videoCallEnabled) {
            MediaConstraints videoConstraints = new MediaConstraints();
            videoConstraints.mandatory.add(new MediaConstraints.KeyValuePair("maxHeight", Integer.toString(pcParams.videoHeight)));
            videoConstraints.mandatory.add(new MediaConstraints.KeyValuePair("maxWidth", Integer.toString(pcParams.videoWidth)));
            videoConstraints.mandatory.add(new MediaConstraints.KeyValuePair("maxFrameRate", Integer.toString(pcParams.videoFps)));
            videoConstraints.mandatory.add(new MediaConstraints.KeyValuePair("minFrameRate", Integer.toString(pcParams.videoFps)));

            videoSource = factory.createVideoSource(getVideoCapturer(), videoConstraints);
            localMS.addTrack(factory.createVideoTrack("LOCAL_MS_VS", videoSource));
        }

        AudioSource audioSource = factory.createAudioSource(new MediaConstraints());
        localMS.addTrack(factory.createAudioTrack("LOCAL_MS_AT", audioSource));

        mListener.onLocalStream(localMS);
    }

    private VideoCapturer getVideoCapturer() {
        String frontCameraDeviceName = VideoCapturerAndroid.getNameOfFrontFacingDevice();
        return VideoCapturerAndroid.create(frontCameraDeviceName);
    }
}