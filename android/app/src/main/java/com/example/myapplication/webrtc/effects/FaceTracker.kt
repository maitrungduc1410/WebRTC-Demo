package com.example.myapplication.webrtc.effects

import android.content.Context
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import com.google.mediapipe.framework.image.ByteBufferImageBuilder
import com.google.mediapipe.framework.image.MPImage
import com.google.mediapipe.tasks.components.containers.NormalizedLandmark
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.core.Delegate
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.facelandmarker.FaceLandmarker
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Finds one face with MediaPipe's face landmarker on a dedicated thread.
 *
 * Input is a small, upright, top-row-first RGBA image. The result is the few face points stickers
 * are placed by, normalized to 0..1 with the origin at the top left, or null when there is no face.
 * Only one detection is in flight at a time: callers must drop frames while [isBusy].
 */
class FaceTracker(private val context: Context) {
    companion object {
        private const val TAG = "FaceTracker"
        private const val MODEL_ASSET = "face_landmarker.task"
        // Face mesh points: the corners of both eyes, the nose tip and the middle of the lips.
        private val EYE_A = intArrayOf(33, 133)
        private val EYE_B = intArrayOf(362, 263)
        private const val NOSE_TIP = 1
        private val LIPS = intArrayOf(13, 14)
    }

    private val thread = HandlerThread("FaceTracker").apply { start() }
    private val handler = Handler(thread.looper)
    private val busy = AtomicBoolean(false)
    private var landmarker: FaceLandmarker? = null
    private var input: ByteBuffer? = null
    private var lastTimestampMs = 0L

    @Volatile
    var failed = false
        private set

    @Volatile
    private var released = false

    private val lock = Any()
    private var latest: FacePoints? = null
    private var version = 0L

    init {
        handler.post { landmarker = create() }
    }

    val isBusy: Boolean get() = busy.get()

    /** Copies [rgba] (width * height * 4 bytes) and queues it. Returns false while one is running. */
    fun submit(rgba: ByteBuffer, width: Int, height: Int): Boolean {
        if (released || failed || !busy.compareAndSet(false, true)) return false
        val size = width * height * 4
        val copy = input?.takeIf { it.capacity() == size }
            ?: ByteBuffer.allocateDirect(size).order(ByteOrder.nativeOrder()).also { input = it }
        rgba.rewind()
        copy.rewind()
        copy.put(rgba)
        val posted = handler.post {
            try {
                detect(copy, width, height)
            } finally {
                busy.set(false)
            }
        }
        if (!posted) busy.set(false)
        return posted
    }

    /** The newest result if it is newer than [sinceVersion]: the face (or null for none) and its version. */
    fun latest(sinceVersion: Long): Pair<FacePoints?, Long>? = synchronized(lock) {
        if (version == sinceVersion) null else latest to version
    }

    fun reset() {
        handler.post {
            synchronized(lock) {
                latest = null
                version++
            }
        }
    }

    fun release() {
        released = true
        handler.post {
            landmarker?.close()
            landmarker = null
            thread.quitSafely()
        }
    }

    private fun create(): FaceLandmarker? = try {
        val options = FaceLandmarker.FaceLandmarkerOptions.builder()
            .setBaseOptions(BaseOptions.builder().setModelAssetPath(MODEL_ASSET).setDelegate(Delegate.CPU).build())
            .setRunningMode(RunningMode.VIDEO)
            .setNumFaces(1)
            .build()
        FaceLandmarker.createFromOptions(context, options)
    } catch (e: Exception) {
        Log.e(TAG, "Failed to create FaceLandmarker", e)
        failed = true
        null
    }

    private fun detect(rgba: ByteBuffer, width: Int, height: Int) {
        val landmarker = landmarker ?: return
        // VIDEO mode requires strictly increasing timestamps.
        val timestampMs = maxOf(SystemClock.elapsedRealtime(), lastTimestampMs + 1)
        lastTimestampMs = timestampMs
        rgba.rewind()
        val image = ByteBufferImageBuilder(rgba, width, height, MPImage.IMAGE_FORMAT_RGBA).build()
        val face = try {
            landmarker.detectForVideo(image, timestampMs).faceLandmarks().firstOrNull()?.let(::points)
        } catch (e: Exception) {
            Log.e(TAG, "Face tracking failed", e)
            null
        } finally {
            image.close()
        }
        synchronized(lock) {
            latest = face
            version++
        }
    }

    private fun points(landmarks: List<NormalizedLandmark>): FacePoints? {
        if (landmarks.size < 468) return null
        fun at(vararg indices: Int) = Point(
            indices.sumOf { landmarks[it].x().toDouble() }.toFloat() / indices.size,
            indices.sumOf { landmarks[it].y().toDouble() }.toFloat() / indices.size
        )
        return FacePoints(at(*EYE_A), at(*EYE_B), at(NOSE_TIP), at(*LIPS))
    }
}
