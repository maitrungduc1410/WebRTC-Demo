package com.example.myapplication.webrtc

import android.content.Context
import android.content.Intent
import android.hardware.camera2.CameraManager
import android.media.projection.MediaProjection
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.example.myapplication.Utils
import com.example.myapplication.webrtc.effects.EffectsProcessor
import com.example.myapplication.webrtc.effects.EffectsScene
import com.example.myapplication.webrtc.effects.FaceTracker
import com.example.myapplication.webrtc.effects.SelfieSegmenter
import org.webrtc.*
import org.webrtc.audio.JavaAudioDeviceModule

/** Local media events shared by the 1:1 and the group call listeners. */
interface LocalMediaListener {
    fun onAddLocalStream(localStream: MediaStream)
    fun onRemoveLocalStream(localStream: MediaStream)
    fun onScreenSharingStopped() // Called when MediaProjection is stopped by system
    /** A model the current effect needs could not run; camera frames are dropped until effects change. */
    fun onEffectsFailed() {}
}

/**
 * Everything a call sends, independent of how it is signaled: the PeerConnectionFactory, the
 * capturers, sources and tracks, backgrounds and effects, and the E2EE key provider.
 *
 * The engine that owns it (1:1 or group) puts [stream]'s tracks on its senders, sets
 * [replaceVideoTrack] so source switches reach them, and sends [state] when [onStateChange] fires.
 */
class LocalMedia(
    private val context: Context,
    private val rootEglBase: EglBase,
    e2eeEnabled: Boolean,
    private val listener: LocalMediaListener,
    private val onStateChange: () -> Unit
) {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var audioEnabled = true
    private var videoEnabled = true
    private var sharingContent = false

    var factory: PeerConnectionFactory? = null
        private set
    var e2ee: E2eeManager? = null
        private set
    private var localStream: MediaStream? = null
    private var videoSource: VideoSource? = null
    private var audioSource: AudioSource? = null
    private var videoCapturer: VideoCapturer? = null
    private var surfaceTextureHelper: SurfaceTextureHelper? = null
    private var useFrontCamera = true

    /**
     * Puts a new video track on the engine's video sender without a renegotiation. Returns false
     * when a sender still holds the old track, which then has to stay alive.
     */
    var replaceVideoTrack: (VideoTrack) -> Boolean = { true }

    // Backgrounds and effects: the models live for the whole call, the processor per camera VideoSource.
    private var effectsScene: EffectsScene? = null
    private var holdEffectFrames = false
    private var segmenter: SelfieSegmenter? = null
    private var faceTracker: FaceTracker? = null
    private var effectsProcessor: EffectsProcessor? = null

    val micMeter = MicLevelMeter()

    companion object {
        private const val TAG = "LocalMedia"

        // Lets the remote swap to its placeholder before our track turns into black frames.
        private const val MEDIA_STATE_DELAY_MS = 300L
    }

    init {
        initializeWebRTC(e2eeEnabled)
    }

    private fun initializeWebRTC(e2eeEnabled: Boolean) {
        // Initialize WebRTC factory
        val initializationOptions = PeerConnectionFactory.InitializationOptions.builder(context)
            .setEnableInternalTracer(true)
            .createInitializationOptions()
        PeerConnectionFactory.initialize(initializationOptions)

        val options = PeerConnectionFactory.Options()
        val encoderFactory = DefaultVideoEncoderFactory(rootEglBase.eglBaseContext, true, true)
        val decoderFactory = DefaultVideoDecoderFactory(rootEglBase.eglBaseContext)

        // The factory's default module, plus a look at what it records for the local level meter.
        val audioDeviceModule = JavaAudioDeviceModule.builder(context)
            .setSamplesReadyCallback(micMeter::onWebRtcSamples)
            .setAudioRecordStateCallback(object : JavaAudioDeviceModule.AudioRecordStateCallback {
                override fun onWebRtcAudioRecordStart() = micMeter.onWebRtcRecording(true)
                override fun onWebRtcAudioRecordStop() = micMeter.onWebRtcRecording(false)
            })
            .createAudioDeviceModule()
        factory = PeerConnectionFactory.builder()
            .setOptions(options)
            .setAudioDeviceModule(audioDeviceModule)
            .setVideoDecoderFactory(decoderFactory)
            .setVideoEncoderFactory(encoderFactory)
            .createPeerConnectionFactory()
        // The factory holds its own reference.
        audioDeviceModule.release()

        if (e2eeEnabled) {
            e2ee = E2eeManager(factory!!)
            Log.d(TAG, "E2EE enabled")
        }
    }

    // ========== Public API ==========

    /** Null until [start]. */
    val stream: MediaStream? get() = localStream
    val audioTrack: AudioTrack? get() = localStream?.audioTracks?.firstOrNull()
    val videoTrack: VideoTrack? get() = localStream?.videoTracks?.firstOrNull()

    /** What we send; `screen` is true for screen or video file sharing. */
    val state: MediaState get() = MediaState(audioEnabled, videoEnabled, sharingContent)

    val isFrontCamera: Boolean get() = useFrontCamera

    /** Opens the camera and the microphone. */
    fun start() {
        setupCamera()
    }

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
        audioEnabled = enable
        micMeter.muted = !enable
        audioTrack?.setEnabled(enable)
        onStateChange()
    }

    fun toggleVideo(enable: Boolean) {
        videoEnabled = enable
        mainHandler.removeCallbacksAndMessages(null)
        if (enable) {
            videoTrack?.setEnabled(true)
            mainHandler.postDelayed({ onStateChange() }, MEDIA_STATE_DELAY_MS)
        } else {
            onStateChange()
            mainHandler.postDelayed({
                if (!videoEnabled) videoTrack?.setEnabled(false)
            }, MEDIA_STATE_DELAY_MS)
        }
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
        onStateChange()
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
        videoTrack.setEnabled(videoEnabled)
        val replaced = replaceVideoTrack(videoTrack)
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
        onStateChange()
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

    /** First half of the teardown: stops capturing. Close the peer connections next, then [release]. */
    fun stopCapture() {
        mainHandler.removeCallbacksAndMessages(null)
        micMeter.setIdleWanted(false)

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
    }

    /** Second half of the teardown, after every peer connection (and its cryptors) is disposed. */
    fun release() {
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
    }

    // ========== Private Helper Methods ==========

    /** Installs the processor on the current camera VideoSource; frames pass through without effects. */
    private fun attachEffects() {
        val source = videoSource ?: return
        val processor = EffectsProcessor(context).apply {
            segmenter = this@LocalMedia.segmenter
            faceTracker = this@LocalMedia.faceTracker
            setScene(effectsScene)
            holdFrames = holdEffectFrames
            onModelFailed = { mainHandler.post { listener.onEffectsFailed() } }
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
