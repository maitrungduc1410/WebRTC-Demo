package com.example.myapplication.webrtc

import android.content.Context
import android.content.Intent
import android.hardware.camera2.CameraManager
import android.media.projection.MediaProjection
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.example.myapplication.R
import com.example.myapplication.Utils
import com.example.myapplication.webrtc.effects.EffectsProcessor
import com.example.myapplication.webrtc.effects.EffectsScene
import com.example.myapplication.webrtc.effects.FaceTracker
import com.example.myapplication.webrtc.effects.SelfieSegmenter
import io.socket.client.IO
import io.socket.client.Socket
import org.webrtc.*
import java.net.URISyntaxException

/**
 * Main WebRTC client that manages peer connections, media capture, and signaling.
 */
class PeerConnectionClient(
    private val context: Context,
    private val roomId: String,
    private val callbacks: RtcListener,
    host: String,
    private val rootEglBase: EglBase,
    private val e2eeEnabled: Boolean = false
) {
    // Re-announces the local media state whenever a peer (re)connects.
    private val listener = object : RtcListener by callbacks {
        override fun onPeersConnectionStatusChange(success: Boolean) {
            if (success) sendMediaState()
            callbacks.onPeersConnectionStatusChange(success)
        }
    }
    private val mainHandler = Handler(Looper.getMainLooper())
    private var localAudioEnabled = true
    private var localVideoEnabled = true
    private var sharingContent = false

    private var factory: PeerConnectionFactory? = null
    private val pcConstraints = MediaConstraints()
    private var localStream: MediaStream? = null
    private var videoSource: VideoSource? = null
    private var audioSource: AudioSource? = null
    private var videoCapturer: VideoCapturer? = null
    private var surfaceTextureHelper: SurfaceTextureHelper? = null
    private lateinit var socket: Socket
    private lateinit var signalingHandler: SignalingHandler
    // Replaced from socket threads, used from the UI thread
    @Volatile
    private var peer: WebRtcPeer? = null
    private var useFrontCamera = true
    // Written on the UI thread, read when a peer is created from a socket callback
    @Volatile
    private var remoteAudioEnabled = true
    private var e2ee: E2eeManager? = null

    // Backgrounds and effects: the models live for the whole call, the processor per camera VideoSource.
    private var effectsScene: EffectsScene? = null
    private var holdEffectFrames = false
    private var segmenter: SelfieSegmenter? = null
    private var faceTracker: FaceTracker? = null
    private var effectsProcessor: EffectsProcessor? = null

    companion object {
        private const val TAG = "PeerConnectionClient"

        // Lets the remote swap to its placeholder before our track turns into black frames.
        private const val MEDIA_STATE_DELAY_MS = 300L
    }

    init {
        initializeWebRTC()
        setupSignaling(host)
    }

    private fun initializeWebRTC() {
        // Initialize WebRTC factory
        val initializationOptions = PeerConnectionFactory.InitializationOptions.builder(context)
            .setEnableInternalTracer(true)
            .createInitializationOptions()
        PeerConnectionFactory.initialize(initializationOptions)

        val options = PeerConnectionFactory.Options()
        val encoderFactory = DefaultVideoEncoderFactory(rootEglBase.eglBaseContext, true, true)
        val decoderFactory = DefaultVideoDecoderFactory(rootEglBase.eglBaseContext)

        factory = PeerConnectionFactory.builder()
            .setOptions(options)
            .setVideoDecoderFactory(decoderFactory)
            .setVideoEncoderFactory(encoderFactory)
            .createPeerConnectionFactory()

        if (e2eeEnabled) {
            e2ee = E2eeManager(factory!!)
            Log.d(TAG, "E2EE enabled")
        }

        // Setup peer connection constraints
        pcConstraints.mandatory.apply {
            add(MediaConstraints.KeyValuePair("OfferToReceiveAudio", "true"))
            add(MediaConstraints.KeyValuePair("OfferToReceiveVideo", "true"))
            add(MediaConstraints.KeyValuePair("maxHeight", "1080"))
            add(MediaConstraints.KeyValuePair("maxWidth", "2400"))
            add(MediaConstraints.KeyValuePair("maxFrameRate", "30"))
            add(MediaConstraints.KeyValuePair("minFrameRate", "30"))
        }
        pcConstraints.optional.add(MediaConstraints.KeyValuePair("DtlsSrtpKeyAgreement", "true"))
    }

    private fun setupSignaling(host: String) {
        try {
            socket = IO.socket(host)
        } catch (e: URISyntaxException) {
            e.printStackTrace()
        }

        signalingHandler = SignalingHandler(
            socket = socket,
            roomId = roomId,
            onPeerCreated = { createPeer() },
            // A peer disposes itself when ICE disconnects; a later offer must start a new one.
            getPeer = { peer?.takeUnless { it.isDisposed } },
            onReconnected = { dropPeer() },
            onRemoteMediaState = { listener.onRemoteMediaState(it) },
            e2ee = e2ee
        )

        signalingHandler.setupListeners()
    }

    private fun createPeer(): WebRtcPeer {
        // "new user joined" or an offer for a new call while one exists: the remote restarted it.
        dropPeer()
        peer = WebRtcPeer(
            factory = factory!!,
            localStream = localStream!!,
            pcConstraints = pcConstraints,
            listener = listener,
            signalingHandler = signalingHandler,
            dataChannelLabel = context.getString(R.string.dataChannelName),
            e2ee = e2ee,
            remoteAudioEnabled = remoteAudioEnabled
        )
        return peer!!
    }

    /** Ends the current call but keeps local media running. */
    private fun dropPeer() {
        val old = peer ?: return
        peer = null
        if (old.isDisposed) return
        old.dispose()
        listener.onRemoveRemoteStream()
        listener.onPeersConnectionStatusChange(false)
    }

    // ========== Public API ==========

    /**
     * Creates the local media, then joins the room. The order matters: a peer already in the room
     * sends its offer as soon as we join, and answering it needs [localStream].
     */
    fun start() {
        setupCamera()
        socket.connect()
    }

    val isFrontCamera: Boolean get() = useFrontCamera

    /** [onDone] runs on the camera thread with the new facing. */
    fun switchCamera(onDone: (isFrontCamera: Boolean) -> Unit = {}) {
        if (videoSource != null && videoCapturer?.isScreencast == false) {
            val cameraVideoCapturer = videoCapturer as CameraVideoCapturer
            cameraVideoCapturer.switchCamera(object : CameraVideoCapturer.CameraSwitchHandler {
                override fun onCameraSwitchDone(isFrontCamera: Boolean) {
                    effectsProcessor?.invalidateAnalysis()
                    useFrontCamera = isFrontCamera
                    onDone(isFrontCamera)
                }

                override fun onCameraSwitchError(errorDescription: String) {
                    Log.e(TAG, "Error switching camera: $errorDescription")
                }
            })
        }
    }

    fun toggleAudio(enable: Boolean) {
        localAudioEnabled = enable
        localStream?.audioTracks?.firstOrNull()?.setEnabled(enable)
        sendMediaState()
    }

    fun toggleVideo(enable: Boolean) {
        localVideoEnabled = enable
        mainHandler.removeCallbacksAndMessages(null)
        if (enable) {
            localStream?.videoTracks?.firstOrNull()?.setEnabled(true)
            mainHandler.postDelayed({ sendMediaState() }, MEDIA_STATE_DELAY_MS)
        } else {
            sendMediaState()
            mainHandler.postDelayed({
                if (!localVideoEnabled) localStream?.videoTracks?.firstOrNull()?.setEnabled(false)
            }, MEDIA_STATE_DELAY_MS)
        }
    }

    /** Reads the remote `audioLevel` (0..1) from the inbound audio RTP stats. */
    fun getRemoteAudioLevel(callback: (Float) -> Unit) {
        val peer = peer ?: return
        peer.getStats { report ->
            val level = report.statsMap.values
                .firstOrNull { it.type == "inbound-rtp" && it.members["kind"] == "audio" }
                ?.members?.get("audioLevel") as? Double
            callback((level ?: 0.0).toFloat())
        }
    }

    private fun sendMediaState() {
        signalingHandler.sendMediaState(MediaState(localAudioEnabled, localVideoEnabled, sharingContent))
    }

    fun toggleRemoteAudio(enable: Boolean) {
        remoteAudioEnabled = enable
        peer?.setRemoteAudioEnabled(enable)
    }

    /** Usually a no-op: the offerer creates the chat channel with the first offer. */
    fun ensureDataChannel() {
        peer?.ensureDataChannel()
    }

    fun sendDataChannelMessage(message: String) {
        peer?.sendDataChannelMessage(message)
    }
    
    fun createFileCapture(videoFilePath: String) {
        Log.d(TAG, "createFileCapture: videoFilePath=$videoFilePath")

        // Stop and dispose old capturer
        videoCapturer?.let {
            try {
                Log.d(TAG, "Stopping old capturer")
                it.stopCapture()
            } catch (e: InterruptedException) {
                Log.e(TAG, "Error stopping capture", e)
            }
            it.dispose()
            Log.d(TAG, "Old capturer disposed")
        }

        // Effects are camera-only; the file capturer reuses this VideoSource.
        detachEffects()

        // 2. CRITICAL: Dispose the old helper and create a NEW one.
        // This provides a fresh, unconnected Surface for the MediaCodec.
        surfaceTextureHelper?.dispose()
        surfaceTextureHelper = SurfaceTextureHelper.create("FileCaptureThread", rootEglBase.eglBaseContext)

        // Create Mp4VideoCapturer with the file path
        videoCapturer = Mp4VideoCapturer(videoFilePath)

        // Reuse existing video source and surface texture helper
        // Just reinitialize with the new capturer
        videoCapturer!!.initialize(surfaceTextureHelper, context, videoSource!!.capturerObserver)

        // Start capture - dimensions will be determined by the video file
        Log.d(TAG, "Starting file capture: $videoFilePath")
        videoCapturer!!.startCapture(1280, 720, 30) // These will be overridden by the actual video

        sharingContent = true
        sendMediaState()
        Log.d(TAG, "createFileCapture completed")
    }

    /**
     * Switches the outgoing video between the camera and the screen. The new track replaces the old
     * one on the existing sender instead of renegotiating: a renegotiation recreates the video
     * decoders on both sides, which leaves the remote video frozen on Android. Audio is untouched.
     */
    fun createDeviceCapture(isScreencast: Boolean, mediaProjectionPermissionResultData: Intent?) {
        Log.d(TAG, "createDeviceCapture: isScreencast=$isScreencast")

        videoCapturer?.let {
            try {
                Log.d(TAG, "Stopping old capturer")
                it.stopCapture()
            } catch (e: InterruptedException) {
                Log.e(TAG, "Error stopping capture", e)
            }
            it.dispose()
            videoCapturer = null
            Log.d(TAG, "Old capturer disposed")
        }
        detachEffects()
        surfaceTextureHelper?.dispose()

        val (width, height, fps) = if (isScreencast) {
            getScreenCaptureDimensions()
        } else {
            getCameraCaptureDimensions()
        }

        videoCapturer = if (isScreencast) {
            ScreenCapturerAndroid(
                mediaProjectionPermissionResultData,
                object : MediaProjection.Callback() {
                    override fun onStop() {
                        Log.d(TAG, "MediaProjection stopped by system")
                        // Notify activity that screen sharing was stopped by system (stop when outside app)
                        listener.onScreenSharingStopped()
                    }
                }
            )
        } else {
            getVideoCapturer()
        }

        // A new source, because only a screencast source adapts by frame rate instead of resolution.
        val oldSource = videoSource
        videoSource = factory!!.createVideoSource(videoCapturer!!.isScreencast)
        surfaceTextureHelper = SurfaceTextureHelper.create("CaptureThread", rootEglBase.eglBaseContext)
        if (!isScreencast) attachEffects()
        videoCapturer!!.initialize(surfaceTextureHelper, context, videoSource!!.capturerObserver)
        Log.d(TAG, "Starting capture: ${width}x$height @ ${fps}fps")
        videoCapturer!!.startCapture(width, height, fps)

        val videoTrack = factory!!.createVideoTrack("LOCAL_MS_VS", videoSource)
        videoTrack.setEnabled(localVideoEnabled)
        val replaced = peer?.takeUnless { it.isDisposed }?.replaceVideoTrack(videoTrack) ?: true
        if (!replaced) Log.w(TAG, "No video sender to replace the track on")

        val stream = localStream!!
        val oldTrack = stream.videoTracks.firstOrNull()
        listener.onRemoveLocalStream(stream)
        oldTrack?.let { stream.removeTrack(it) }
        stream.addTrack(videoTrack)
        listener.onAddLocalStream(stream)

        // A sender that still holds the old track must keep it alive.
        if (replaced) {
            oldTrack?.dispose()
            oldSource?.dispose()
        }

        sharingContent = isScreencast
        sendMediaState()
        Log.d(TAG, "createDeviceCapture completed")
    }

    /**
     * A screen share keeps the size it started with, so after a rotation the peer would get the
     * new screen letterboxed inside the old frame. Resizes the virtual display to match.
     */
    fun onDisplayChanged() {
        val capturer = videoCapturer as? ScreenCapturerAndroid ?: return
        val (width, height, fps) = getScreenCaptureDimensions()
        capturer.changeCaptureFormat(width, height, fps)
    }

    fun onDestroy() {
        mainHandler.removeCallbacksAndMessages(null)
        signalingHandler.disconnect()

        Log.d(TAG, "Stopping capture.")
        videoCapturer?.let {
            try {
                it.stopCapture()
            } catch (e: InterruptedException) {
                throw RuntimeException(e)
            }
            it.dispose()
            videoCapturer = null
        }

        cleanupMediaResources()

        Log.d(TAG, "Closing peer connection.")
        peer?.dispose()
        peer = null

        // After every cryptor (owned by the peer) is gone.
        e2ee?.dispose()
        e2ee = null

        segmenter?.release()
        segmenter = null
        faceTracker?.release()
        faceTracker = null

        Log.d(TAG, "Closing peer connection factory.")
        factory?.dispose()
        factory = null

        PeerConnectionFactory.stopInternalTracingCapture()
        PeerConnectionFactory.shutdownInternalTracer()

        Log.d(TAG, "Cleanup complete.")
    }

    /**
     * Drops camera frames until the next [setEffects], so a saved background is in place before
     * the room is shown or sent. Call before [start].
     */
    fun holdEffects() {
        holdEffectFrames = true
        effectsProcessor?.holdFrames = true
    }

    /** Applies a background and sticker to the camera; null (or an empty scene) turns them off. */
    fun setEffects(scene: EffectsScene?) {
        val active = scene?.takeIf { it.isActive }
        if (active?.needsMask == true && segmenter == null) segmenter = SelfieSegmenter(context)
        if (active?.needsFace == true && faceTracker == null) faceTracker = FaceTracker(context)
        effectsScene = active
        holdEffectFrames = false
        effectsProcessor?.let {
            it.segmenter = segmenter
            it.faceTracker = faceTracker
            it.setScene(active)
        }
        Log.d(TAG, "Effects: ${active?.background?.id ?: "none"} / ${active?.sticker?.id ?: "no sticker"}")
    }

    // ========== Private Helper Methods ==========

    /** Installs the processor on the current camera VideoSource; frames pass through without effects. */
    private fun attachEffects() {
        val source = videoSource ?: return
        val processor = EffectsProcessor(context).apply {
            segmenter = this@PeerConnectionClient.segmenter
            faceTracker = this@PeerConnectionClient.faceTracker
            setScene(effectsScene)
            holdFrames = holdEffectFrames
            onModelFailed = { mainHandler.post { callbacks.onEffectsFailed() } }
        }
        source.setVideoProcessor(processor)
        effectsProcessor = processor
    }

    /** Must run while the SurfaceTextureHelper the processor rendered on is still alive. */
    private fun detachEffects() {
        val processor = effectsProcessor ?: return
        effectsProcessor = null
        videoSource?.setVideoProcessor(null)
        surfaceTextureHelper?.handler?.let { handler ->
            ThreadUtils.invokeAtFrontUninterruptibly(handler) { processor.releaseGl() }
        }
    }

    private fun setupCamera() {
        localStream = factory!!.createLocalMediaStream("LOCAL_MS")
        videoCapturer = getVideoCapturer()
        videoSource = factory!!.createVideoSource(videoCapturer!!.isScreencast)

        surfaceTextureHelper = SurfaceTextureHelper.create("CaptureThread", rootEglBase.eglBaseContext)
        attachEffects()
        videoCapturer!!.initialize(surfaceTextureHelper, context, videoSource!!.capturerObserver)

        val (width, height, fps) = getCameraCaptureDimensions()
        println("camera capture granted: ${width}x${height} @ ${fps}fps")
        videoCapturer!!.startCapture(width, height, fps)

        localStream!!.addTrack(factory!!.createVideoTrack("LOCAL_MS_VS", videoSource))
        audioSource = factory!!.createAudioSource(MediaConstraints())
        localStream!!.addTrack(factory!!.createAudioTrack("LOCAL_MS_AT", audioSource))

        listener.onAddLocalStream(localStream!!)
    }

    private fun getVideoCapturer(): VideoCapturer {
        val enumerator: CameraEnumerator = if (Camera2Enumerator.isSupported(context)) {
            Camera2Enumerator(context)
        } else {
            Camera1Enumerator(true)
        }

        return createCapturer(enumerator, useFrontCamera)!!
    }

    private fun createCapturer(enumerator: CameraEnumerator, frontFacing: Boolean): VideoCapturer? {
        val deviceNames = enumerator.deviceNames
        for (deviceName in deviceNames) {
            if (enumerator.isFrontFacing(deviceName) == frontFacing) {
                val videoCapturer = enumerator.createCapturer(deviceName, null)
                if (videoCapturer != null) {
                    return videoCapturer
                }
            }
        }
        return null
    }

    private fun getCameraId(frontFacing: Boolean): String? {
        val enumerator: CameraEnumerator = if (Camera2Enumerator.isSupported(context)) {
            Camera2Enumerator(context)
        } else {
            Camera1Enumerator(true)
        }

        val deviceNames = enumerator.deviceNames
        for (deviceName in deviceNames) {
            if (enumerator.isFrontFacing(deviceName) == frontFacing) {
                return deviceName
            }
        }
        return null
    }

    private fun getCameraCaptureDimensions(): Triple<Int, Int, Int> {
        val cameraDeviceName = getCameraId(useFrontCamera)
        val targetFps = 60

        val maxSize = when (videoCapturer) {
            is Camera1Capturer -> {
                val cameraIndex = Camera1Helper.getCameraId(cameraDeviceName)
                Camera1Helper.getMaxCaptureFormat(cameraIndex)
            }
            is Camera2Capturer -> {
                Camera2Helper.getMaxCaptureFormat(
                    context.getSystemService(Context.CAMERA_SERVICE) as CameraManager,
                    cameraDeviceName
                )
            }
            else -> null
        }

        /**
         * an important note on target resolution: getSupportedFormats in Camera1Helper and
         * Camera2Helper return a list of supported formats, in landscape mode (width > height).
         * So when querying for closest format, we should provide width > height values to get
         * correct results, even if your intent is to capture in portrait mode.
         * webrtc renderer will handle rotation automatically based on the camera sensor orientation for us
         *
         * If you try to set targetWidth < targetHeight, you may end up with lower resolution or even incorrect one
         */
        val width = maxSize?.width ?: 1920
        val height = maxSize?.height ?: 1080

        Log.d(TAG, "Camera capture (max): ${width}x$height @ ${targetFps}fps")
        return Triple(width, height, targetFps)
    }

    private fun getScreenCaptureDimensions(): Triple<Int, Int, Int> {
        val dimensions = Utils.getScreenDimentions(context)
        val fps = Utils.getFps(context)
        Log.d(TAG, "Screen capture: ${dimensions.screenWidth}x${dimensions.screenHeight} @ ${fps}fps")
        return Triple(dimensions.screenWidth, dimensions.screenHeight, fps)
    }

    private fun cleanupMediaResources() {
        detachEffects()

        audioSource?.dispose()
        audioSource = null

        videoSource?.dispose()
        videoSource = null

        surfaceTextureHelper?.dispose()
        surfaceTextureHelper = null
    }

}