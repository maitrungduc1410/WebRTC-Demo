package com.example.myapplication.webrtc

import android.content.Context
import android.graphics.*
import android.util.Log
import com.example.myapplication.R
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.imagesegmenter.ImageSegmenter
import org.webrtc.*
import java.nio.ByteBuffer

/**
 * VirtualBackgroundProcessor applies virtual background effect using MediaPipe.
 * 
 * Key insight from MediaPipe selfie_segmenter model:
 * - Mask value 0 = person (foreground)
 * - Mask value 255 = background
 */
class VirtualBackgroundProcessor(
    context: Context,
    private val rootEglBase: EglBase
) : VideoProcessor {
    
    companion object {
        private const val TAG = "VirtualBgProcessor"
        private const val SEGMENTATION_WIDTH = 256  // Downscale for fast inference
        private const val INFERENCE_INTERVAL = 3  // Process every Nth frame for segmentation
    }
    
    private var imageSegmenter: ImageSegmenter? = null
    private var sink: VideoSink? = null
    private val backgroundBitmap: Bitmap = BitmapFactory.decodeResource(
        context.resources, 
        R.drawable.virtual_background
    )

    private val maskLock = Any()
    private var safeMaskBuffer: ByteBuffer? = null
    private var maskWidth = 0
    private var maskHeight = 0
    
    // Frame counter for skipping inference
    private var frameCount = 0
    
    // Pre-scaled background for faster compositing
    private var scaledBackgroundCache: Bitmap? = null
    private var cachedWidth = 0
    private var cachedHeight = 0

    init {
        try {
            val options = ImageSegmenter.ImageSegmenterOptions.builder()
                .setBaseOptions(
                    BaseOptions.builder()
                        .setModelAssetPath("selfie_segmenter.tflite")
                        .build()
                )
                .setRunningMode(RunningMode.LIVE_STREAM)
                .setOutputCategoryMask(true)
                .setOutputConfidenceMasks(false)
                .setResultListener { result, _ ->
                    try {
                        val maskImage = result.categoryMask().get()
                        val extracted = ByteBufferExtractor.extract(maskImage)

                        synchronized(maskLock) {
                            if (safeMaskBuffer == null || safeMaskBuffer!!.capacity() < extracted.capacity()) {
                                safeMaskBuffer = ByteBuffer.allocateDirect(extracted.capacity())
                            }
                            safeMaskBuffer!!.clear()
                            extracted.rewind()
                            safeMaskBuffer!!.put(extracted)
                            safeMaskBuffer!!.rewind()
                            
                            maskWidth = maskImage.width
                            maskHeight = maskImage.height
                        }
                    } catch (e: Exception) {
                        Log.e(TAG, "Error in segmentation result", e)
                    }
                }
                .setErrorListener { error ->
                    Log.e(TAG, "MediaPipe error: ${error.message}", error)
                }
                .build()
            imageSegmenter = ImageSegmenter.createFromOptions(context, options)
            Log.d(TAG, "MediaPipe initialized successfully")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize MediaPipe", e)
        }
    }

    override fun onFrameCaptured(frame: VideoFrame) {
        try {
            frameCount++
            
            // Only run segmentation on every Nth frame to save CPU
            if (frameCount % INFERENCE_INTERVAL == 0) {
                runSegmentation(frame)
            }

            // Apply background if mask ready
            synchronized(maskLock) {
                if (safeMaskBuffer != null && safeMaskBuffer!!.capacity() > 0) {
                    val processedFrame = applyVirtualBackgroundFast(frame)
                    sink?.onFrame(processedFrame)
                    processedFrame.release()
                } else {
                    // No mask yet (first frames), pass through
                    sink?.onFrame(frame)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error processing frame", e)
            sink?.onFrame(frame)
        }
    }
    
    private fun runSegmentation(frame: VideoFrame) {
        try {
            val aspectRatio = frame.buffer.height.toFloat() / frame.buffer.width
            val inferenceHeight = (SEGMENTATION_WIDTH * aspectRatio).toInt()
            
            val scaledBuffer = frame.buffer.cropAndScale(
                0, 0,
                frame.buffer.width,
                frame.buffer.height,
                SEGMENTATION_WIDTH,
                inferenceHeight
            )
            val i420Buffer = scaledBuffer.toI420()

            val inferenceBitmap = i420BufferToBitmap(i420Buffer!!)
            i420Buffer.release()
            scaledBuffer.release()

            // Trigger MediaPipe async segmentation
            val mpImage = BitmapImageBuilder(inferenceBitmap).build()
            imageSegmenter?.segmentAsync(mpImage, frame.timestampNs / 1000000)
            inferenceBitmap.recycle()
        } catch (e: Exception) {
            Log.e(TAG, "Error in segmentation", e)
        }
    }

    private fun applyVirtualBackgroundFast(originalFrame: VideoFrame): VideoFrame {
        // Convert to full-res bitmap for quality
        val fullI420 = originalFrame.buffer.toI420()
        val width = fullI420?.width ?: 0
        val height = fullI420?.height ?: 0
        
        val personBitmap = i420BufferToBitmap(fullI420!!)
        fullI420.release()

        // Output bitmap
        val outputBitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(outputBitmap)

        // Use cached scaled background if size matches
        if (scaledBackgroundCache == null || cachedWidth != width || cachedHeight != height) {
            scaledBackgroundCache?.recycle()
            scaledBackgroundCache = Bitmap.createScaledBitmap(backgroundBitmap, width, height, true)
            cachedWidth = width
            cachedHeight = height
        }
        
        // Draw pre-scaled background (faster than scaling on-demand)
        canvas.drawBitmap(scaledBackgroundCache!!, 0f, 0f, null)

        // Get and scale mask
        val maskBitmap = synchronized(maskLock) {
            val rawMask = Bitmap.createBitmap(maskWidth, maskHeight, Bitmap.Config.ALPHA_8)
            safeMaskBuffer?.rewind()
            rawMask.copyPixelsFromBuffer(safeMaskBuffer!!)
            
            val scaledMask = Bitmap.createScaledBitmap(rawMask, width, height, true)
            rawMask.recycle()
            scaledMask
        }

        // Create person layer
        // CRITICAL: selfie_segmenter outputs 0=person, 255=background
        // We need to invert for proper masking
        val personLayer = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val layerCanvas = Canvas(personLayer)
        layerCanvas.drawBitmap(personBitmap, 0f, 0f, null)

        // Invert mask: 0 (person) -> 255 (opaque), 255 (bg) -> 0 (transparent)
        val invertedMask = invertMaskBitmap(maskBitmap)
        maskBitmap.recycle()

        // Apply mask to keep only person
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        paint.xfermode = PorterDuffXfermode(PorterDuff.Mode.DST_IN)
        layerCanvas.drawBitmap(invertedMask, 0f, 0f, paint)
        invertedMask.recycle()

        // Composite person on background
        canvas.drawBitmap(personLayer, 0f, 0f, null)

        personLayer.recycle()
        personBitmap.recycle()

        // Convert back to VideoFrame
        val processedBuffer = bitmapToI420Buffer(outputBitmap)
        outputBitmap.recycle()
        
        return VideoFrame(processedBuffer, originalFrame.rotation, originalFrame.timestampNs)
    }

    private fun invertMaskBitmap(mask: Bitmap): Bitmap {
        val width = mask.width
        val height = mask.height
        val inverted = Bitmap.createBitmap(width, height, Bitmap.Config.ALPHA_8)
        
        val pixels = ByteBuffer.allocate(width * height)
        mask.copyPixelsToBuffer(pixels)
        pixels.rewind()
        
        val invertedPixels = ByteBuffer.allocate(width * height)
        for (i in 0 until width * height) {
            val value = pixels.get(i).toInt() and 0xFF
            // Invert: 0 (person) -> 255, 255 (bg) -> 0
            invertedPixels.put((255 - value).toByte())
        }
        invertedPixels.rewind()
        
        inverted.copyPixelsFromBuffer(invertedPixels)
        return inverted
    }

    private fun bitmapToI420Buffer(bitmap: Bitmap): VideoFrame.I420Buffer {
        val width = bitmap.width
        val height = bitmap.height
        
        val argbBuffer = ByteBuffer.allocateDirect(width * height * 4)
        bitmap.copyPixelsToBuffer(argbBuffer)
        argbBuffer.rewind()

        val i420Buffer = JavaI420Buffer.allocate(width, height)
        
        YuvHelper.ABGRToI420(
            argbBuffer, width * 4,
            i420Buffer.dataY, i420Buffer.strideY,
            i420Buffer.dataU, i420Buffer.strideU,
            i420Buffer.dataV, i420Buffer.strideV,
            width, height
        )
        
        return i420Buffer
    }

    private fun i420BufferToBitmap(i420Buffer: VideoFrame.I420Buffer): Bitmap {
        val width = i420Buffer.width
        val height = i420Buffer.height
        val pixels = IntArray(width * height)

        val yBuf = i420Buffer.dataY
        val uBuf = i420Buffer.dataU
        val vBuf = i420Buffer.dataV

        for (i in 0 until height) {
            for (j in 0 until width) {
                val yIndex = i * i420Buffer.strideY + j
                val uvIndex = (i / 2) * i420Buffer.strideU + (j / 2)
                
                val y = (yBuf.get(yIndex).toInt() and 0xFF)
                val u = (uBuf.get(uvIndex).toInt() and 0xFF) - 128
                val v = (vBuf.get(uvIndex).toInt() and 0xFF) - 128

                val r = (y + 1.370705 * v).toInt().coerceIn(0, 255)
                val g = (y - 0.337633 * u - 0.698001 * v).toInt().coerceIn(0, 255)
                val b = (y + 1.732446 * u).toInt().coerceIn(0, 255)
                
                pixels[i * width + j] = Color.rgb(r, g, b)
            }
        }

        return Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888)
    }

    override fun setSink(sink: VideoSink?) {
        this.sink = sink
    }

    override fun onCapturerStarted(success: Boolean) {
        Log.d(TAG, "Capturer started: $success")
    }

    override fun onCapturerStopped() {
        Log.d(TAG, "Capturer stopped")
    }
    
    fun cleanup() {
        imageSegmenter?.close()
        imageSegmenter = null
        if (!backgroundBitmap.isRecycled) {
            backgroundBitmap.recycle()
        }
        scaledBackgroundCache?.recycle()
        scaledBackgroundCache = null
        Log.d(TAG, "Cleanup complete")
    }
}