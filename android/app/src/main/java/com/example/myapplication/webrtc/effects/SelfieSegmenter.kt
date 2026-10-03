package com.example.myapplication.webrtc.effects

import android.content.Context
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Log
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.framework.image.ByteBufferImageBuilder
import com.google.mediapipe.framework.image.MPImage
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.core.Delegate
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.imagesegmenter.ImageSegmenter
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Runs MediaPipe selfie segmentation on a dedicated thread.
 *
 * Input is a small, upright, top-row-first RGBA image; the result is a person-confidence mask
 * of the same size stored as one byte per pixel (0 = background, 255 = person).
 * Only one inference is in flight at a time: callers must drop frames while [isBusy].
 */
class SelfieSegmenter(
    private val context: Context,
    private val useGpuDelegate: Boolean = false
) {
    companion object {
        private const val TAG = "SelfieSegmenter"
        private const val MODEL_ASSET = "selfie_segmenter.tflite"
        // Weight of the newest mask in the temporal blend; lowers edge flicker between frames.
        private const val MASK_SMOOTHING = 0.7f
    }

    class Mask(val width: Int, val height: Int, val data: ByteBuffer)

    private val thread = HandlerThread("SegmenterInference").apply { start() }
    private val handler = Handler(thread.looper)
    private val busy = AtomicBoolean(false)
    private var segmenter: ImageSegmenter? = null
    private var input: ByteBuffer? = null

    @Volatile
    var failed = false
        private set
    private var lastTimestampMs = 0L
    private var smoothed: FloatArray? = null
    private var loggedMaskCount = false

    @Volatile
    private var released = false

    private val maskLock = Any()
    private var latestMask: Mask? = null
    private var maskVersion = 0L
    // Bumped by resetMask; results from frames submitted before it are dropped.
    @Volatile
    private var epoch = 0

    init {
        handler.post { segmenter = createSegmenter() }
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
        val submitted = epoch
        val posted = handler.post {
            try {
                runInference(copy, width, height, submitted)
            } finally {
                busy.set(false)
            }
        }
        if (!posted) busy.set(false)
        return posted
    }

    /** Returns the latest mask if it is newer than [sinceVersion], with its version. */
    fun latestMask(sinceVersion: Long): Pair<Mask, Long>? = synchronized(maskLock) {
        val mask = latestMask ?: return null
        if (maskVersion == sinceVersion) null else mask to maskVersion
    }

    fun resetMask() {
        synchronized(maskLock) {
            epoch++
            latestMask = null
            maskVersion++
        }
        handler.post { smoothed = null }
    }

    fun release() {
        released = true
        handler.post {
            segmenter?.close()
            segmenter = null
            thread.quitSafely()
        }
    }

    private fun createSegmenter(): ImageSegmenter? {
        if (useGpuDelegate) {
            try {
                return buildSegmenter(Delegate.GPU).also { Log.d(TAG, "Using GPU delegate") }
            } catch (e: Exception) {
                Log.w(TAG, "GPU delegate unavailable, falling back to CPU", e)
            }
        }
        return try {
            buildSegmenter(Delegate.CPU).also { Log.d(TAG, "Using CPU delegate") }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to create ImageSegmenter", e)
            failed = true
            null
        }
    }

    private fun buildSegmenter(delegate: Delegate): ImageSegmenter {
        val baseOptions = BaseOptions.builder()
            .setModelAssetPath(MODEL_ASSET)
            .setDelegate(delegate)
            .build()
        val options = ImageSegmenter.ImageSegmenterOptions.builder()
            .setBaseOptions(baseOptions)
            .setRunningMode(RunningMode.VIDEO)
            .setOutputConfidenceMasks(true)
            .setOutputCategoryMask(false)
            .build()
        return ImageSegmenter.createFromOptions(context, options)
    }

    private fun runInference(rgba: ByteBuffer, width: Int, height: Int, submitted: Int) {
        val segmenter = segmenter ?: return
        // VIDEO mode requires strictly increasing timestamps.
        val timestampMs = maxOf(SystemClock.elapsedRealtime(), lastTimestampMs + 1)
        lastTimestampMs = timestampMs

        rgba.rewind()
        val image = ByteBufferImageBuilder(rgba, width, height, MPImage.IMAGE_FORMAT_RGBA).build()
        try {
            val result = segmenter.segmentForVideo(image, timestampMs)
            val masks = result.confidenceMasks().orElse(null) ?: return
            if (!loggedMaskCount) {
                loggedMaskCount = true
                Log.d(TAG, "Confidence masks: ${masks.size} (${masks.firstOrNull()?.width}x${masks.firstOrNull()?.height})")
            }
            // Class 0 of selfie_segmenter is the person (the category mask reports 0 for person pixels).
            val personMask = masks.firstOrNull() ?: return
            try {
                if (submitted == epoch) publish(personMask, submitted)
            } finally {
                masks.forEach { it.close() }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Segmentation failed", e)
        } finally {
            image.close()
        }
    }

    private fun publish(mask: MPImage, submitted: Int) {
        val w = mask.width
        val h = mask.height
        val floats = ByteBufferExtractor.extract(mask)
            .order(ByteOrder.nativeOrder())
            .asFloatBuffer()
        val count = w * h
        var prev = smoothed
        if (prev == null || prev.size != count) {
            prev = FloatArray(count)
            floats.get(prev)
            floats.rewind()
            smoothed = prev
        }
        // Low-resolution (model-sized) mask only; full-resolution work happens in the shader.
        val out = ByteBuffer.allocateDirect(count)
        for (i in 0 until count) {
            val v = prev[i] + (floats.get(i) - prev[i]) * MASK_SMOOTHING
            prev[i] = v
            out.put(i, (v.coerceIn(0f, 1f) * 255f + 0.5f).toInt().toByte())
        }
        synchronized(maskLock) {
            if (submitted != epoch) return
            latestMask = Mask(w, h, out)
            maskVersion++
        }
    }
}