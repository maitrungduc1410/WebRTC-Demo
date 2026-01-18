package com.example.myapplication.webrtc

import android.content.Context
import android.content.Intent
import android.hardware.camera2.CameraManager
import android.media.projection.MediaProjection
import android.util.Log
import com.example.myapplication.Utils
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
    private val listener: RtcListener,
    host: String,
    private val rootEglBase: EglBase
) {
    private var factory: PeerConnectionFactory? = null
    private val pcConstraints = MediaConstraints()
    private var localStream: MediaStream? = null
    private var videoSource: VideoSource? = null
    private var audioSource: AudioSource? = null
    private var videoCapturer: VideoCapturer? = null
    private var surfaceTextureHelper: SurfaceTextureHelper? = null
    private lateinit var socket: Socket
    private lateinit var signalingHandler: SignalingHandler
    private var peer: WebRtcPeer? = null
    private var useFrontCamera = true

    private var backgroundProcessor: VirtualBackgroundProcessor? = null
    private var isBackgroundEnabled = false

    companion object {
        private const val TAG = "PeerConnectionClient"
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
            getPeer = { peer }
        )

        signalingHandler.setupListeners()
        socket.connect()
    }

    private fun createPeer(): WebRtcPeer {
        peer = WebRtcPeer(
            factory = factory!!,
            localStream = localStream!!,
            pcConstraints = pcConstraints,
            listener = listener,
            signalingHandler = signalingHandler
        )
        return peer!!
    }

    // ========== Public API ==========

    fun start() {
        setupCamera()
    }

    fun switchCamera() {
        if (videoSource != null && videoCapturer?.isScreencast == false) {
            val cameraVideoCapturer = videoCapturer as CameraVideoCapturer
            cameraVideoCapturer.switchCamera(object : CameraVideoCapturer.CameraSwitchHandler {
                override fun onCameraSwitchDone(isFrontCamera: Boolean) {
                    useFrontCamera = isFrontCamera
                }

                override fun onCameraSwitchError(errorDescription: String) {
                    Log.e(TAG, "Error switching camera: $errorDescription")
                }
            })
        }
    }

    fun toggleAudio(enable: Boolean) {
        localStream?.audioTracks?.firstOrNull()?.setEnabled(enable)
    }

    fun toggleVideo(enable: Boolean) {
        localStream?.videoTracks?.firstOrNull()?.setEnabled(enable)
    }

    fun createDataChannel(dataChannelName: String) {
        peer?.createDataChannel(dataChannelName)
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

        Log.d(TAG, "createFileCapture completed")
    }

    fun createDeviceCapture(isScreencast: Boolean, mediaProjectionPermissionResultData: Intent?) {
        Log.d(TAG, "createDeviceCapture: isScreencast=$isScreencast")

        // Remove old tracks from peer connection first
        peer?.getSenders()?.forEach { peer?.removeTrack(it) }

        // Stop and dispose old capturer
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

        // Remove tracks from local stream
        localStream?.videoTracks?.forEach { localStream?.removeTrack(it) }
        localStream?.audioTracks?.forEach { localStream?.removeTrack(it) }

        // Cleanup media resources BEFORE creating new ones
        cleanupMediaResources()

        localStream?.let { listener.onRemoveLocalStream(it) }

        // Get dimensions first (before creating capturer for camera case)
        val (width, height, fps) = if (isScreencast) {
            getScreenCaptureDimensions()
        } else {
            getCameraCaptureDimensions()
        }

        // Create new capturer
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

        // Create new media sources
        videoSource = factory!!.createVideoSource(videoCapturer!!.isScreencast)
        surfaceTextureHelper = SurfaceTextureHelper.create("CaptureThread", rootEglBase.eglBaseContext)

        // Initialize the new capturer
        videoCapturer!!.initialize(surfaceTextureHelper, context, videoSource!!.capturerObserver)

        // Start capture with determined dimensions
        Log.d(TAG, "Starting capture: ${width}x$height @ ${fps}fps")
        videoCapturer!!.startCapture(width, height, fps)

        // Create and add new tracks
        val videoTrack = factory!!.createVideoTrack("LOCAL_MS_VS", videoSource)
        localStream!!.addTrack(videoTrack)

        audioSource = factory!!.createAudioSource(MediaConstraints())
        val audioTrack = factory!!.createAudioTrack("LOCAL_MS_AT", audioSource)
        localStream!!.addTrack(audioTrack)

        // Add tracks to existing peer connection
        peer?.let { p ->
            p.addTrack(audioTrack)
            p.addTrack(videoTrack)
            p.createOffer()
        }

        localStream?.let { listener.onAddLocalStream(it) }
        Log.d(TAG, "createDeviceCapture completed")
    }

    fun onDestroy() {
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

        // Clean up virtual background processor
        backgroundProcessor?.cleanup()
        backgroundProcessor = null

        cleanupMediaResources()

        Log.d(TAG, "Closing peer connection.")
        peer?.dispose()
        peer = null

        Log.d(TAG, "Closing peer connection factory.")
        factory?.dispose()
        factory = null

        PeerConnectionFactory.stopInternalTracingCapture()
        PeerConnectionFactory.shutdownInternalTracer()

        Log.d(TAG, "Cleanup complete.")
    }

    fun toggleVirtualBackground(enable: Boolean) {
        isBackgroundEnabled = enable
        if (enable) {
            if (backgroundProcessor == null) {
                backgroundProcessor = VirtualBackgroundProcessor(context, rootEglBase)
            }
            videoSource?.setVideoProcessor(backgroundProcessor)
            Log.d(TAG, "Virtual background enabled")
        } else {
            videoSource?.setVideoProcessor(null)
            Log.d(TAG, "Virtual background disabled")
        }
    }

    // ========== Private Helper Methods ==========

    private fun setupCamera() {
        localStream = factory!!.createLocalMediaStream("LOCAL_MS")
        videoCapturer = getVideoCapturer()
        videoSource = factory!!.createVideoSource(videoCapturer!!.isScreencast)

        surfaceTextureHelper = SurfaceTextureHelper.create("CaptureThread", rootEglBase.eglBaseContext)
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
        audioSource?.dispose()
        audioSource = null

        videoSource?.dispose()
        videoSource = null

        surfaceTextureHelper?.dispose()
        surfaceTextureHelper = null
    }

}
