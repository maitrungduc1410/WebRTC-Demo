package com.example.myapplication.webrtc

import android.content.Context
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.view.Surface
import org.webrtc.*

/**
 * A VideoCapturer that plays video from an MP4 file in a loop.
 *
 * Note: This implementation uses MediaCodec and MediaExtractor to decode video frames
 * and render them to a SurfaceTexture provided by WebRTC's SurfaceTextureHelper.
 *
 * Because builtin WebRTC FileVideoCapturer is limited to .y4m files, this custom capturer
 * allows using more common MP4 files as video sources.
 *
 * Note that only video is shared, audio inside video is not shared due to complexity of implementing it (A/V Sync, Resampling, Microphone Conflict...).
 */
class Mp4VideoCapturer(private val videoFilePath: String) : VideoCapturer {
    private var surfaceTextureHelper: SurfaceTextureHelper? = null
    private var capturerObserver: CapturerObserver? = null
    private var isStopped = false
    private var decodeThread: Thread? = null

    override fun initialize(
        helper: SurfaceTextureHelper,
        context: Context,
        observer: CapturerObserver
    ) {
        this.surfaceTextureHelper = helper
        this.capturerObserver = observer
    }

    override fun startCapture(width: Int, height: Int, framerate: Int) {
        isStopped = false
        decodeThread = Thread { runDecodeLoop() }.apply {
            name = "VideoFileDecoderThread"
            start()
        }
    }

    private fun runDecodeLoop() {
        val extractor = MediaExtractor()
        try {
            // Use a FileDescriptor if possible for better compatibility with Android 13+ picker
            val file = java.io.File(videoFilePath)
            if (file.exists()) {
                val fis = java.io.FileInputStream(file)
                extractor.setDataSource(fis.fd)
                fis.close()
            } else {
                extractor.setDataSource(videoFilePath)
            }

            val trackIndex = selectVideoTrack(extractor)
            if (trackIndex < 0) return

            extractor.selectTrack(trackIndex)
            val format = extractor.getTrackFormat(trackIndex)
            val mime = format.getString(MediaFormat.KEY_MIME) ?: return
            val width = format.getInteger(MediaFormat.KEY_WIDTH)
            val height = format.getInteger(MediaFormat.KEY_HEIGHT)

            // print width and height
            Logging.d("Mp4Capturer", "Video size: ${width}x${height}")

            // 1. Tell the SurfaceTexture the buffer size
            surfaceTextureHelper?.surfaceTexture?.setDefaultBufferSize(width, height)

            // 2. CRITICAL: Tell WebRTC the texture size or it will ignore frames
            surfaceTextureHelper?.setTextureSize(width, height)

            // 3. Start listening
            surfaceTextureHelper?.startListening { frame ->
                capturerObserver?.onFrameCaptured(frame)
            }

            val surface = Surface(surfaceTextureHelper?.surfaceTexture)
            val decoder = MediaCodec.createDecoderByType(mime)

            decoder.configure(format, surface, null, 0)
            decoder.start()

            renderLoop(extractor, decoder)

            decoder.stop()
            decoder.release()
            surface.release()
        } catch (e: Exception) {
            Logging.e("Mp4Capturer", "Error during decoding: ${e.message}")
            e.printStackTrace()
        } finally {
            extractor.release()
        }
    }

    private fun renderLoop(extractor: MediaExtractor, decoder: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        var startWallTimeNs: Long = -1
        var startVideoTimeUs: Long = -1

        while (!isStopped) {
            // 1. Feed the decoder
            val inputBufferIndex = decoder.dequeueInputBuffer(10000)
            if (inputBufferIndex >= 0) {
                val inputBuffer = decoder.getInputBuffer(inputBufferIndex)!!
                val sampleSize = extractor.readSampleData(inputBuffer, 0)

                if (sampleSize < 0) {
                    // LOOP DETECTED
                    extractor.seekTo(0, MediaExtractor.SEEK_TO_CLOSEST_SYNC)

                    // 1. Clear the decoder internal buffers so old frames don't leak into new loop
                    decoder.flush()

                    // 2. Reset timing variables immediately
                    startVideoTimeUs = -1
                    startWallTimeNs = -1

                    Logging.d("Mp4Capturer", "Video looped and decoder flushed")
                    continue // Restart loop to feed new data immediately
                } else {
                    decoder.queueInputBuffer(inputBufferIndex, 0, sampleSize, extractor.sampleTime, 0)
                    extractor.advance()
                }
            }

            // 2. Drain the decoder
            var outputBufferIndex = decoder.dequeueOutputBuffer(info, 10000)
            while (outputBufferIndex >= 0) {
                // Initialize clocks on the first frame of every loop
                if (startVideoTimeUs == -1L) {
                    startVideoTimeUs = info.presentationTimeUs
                    startWallTimeNs = System.nanoTime()
                }

                val videoElapsedUs = info.presentationTimeUs - startVideoTimeUs
                val wallElapsedUs = (System.nanoTime() - startWallTimeNs) / 1000
                val sleepMs = (videoElapsedUs - wallElapsedUs) / 1000

                if (sleepMs > 5) {
                    try {
                        Thread.sleep(sleepMs)
                    } catch (e: InterruptedException) {
                        Thread.currentThread().interrupt()
                        break
                    }
                }

                decoder.releaseOutputBuffer(outputBufferIndex, true)
                outputBufferIndex = decoder.dequeueOutputBuffer(info, 0)
            }
        }
    }

    private fun selectVideoTrack(extractor: MediaExtractor): Int {
        for (i in 0 until extractor.trackCount) {
            val format = extractor.getTrackFormat(i)
            if (format.getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true) return i
        }
        return -1
    }

    override fun stopCapture() {
        Logging.d("Mp4Capturer", "Stopping capture")
        isStopped = true
        surfaceTextureHelper?.stopListening()
    }

    override fun changeCaptureFormat(width: Int, height: Int, framerate: Int) {}
    override fun dispose() {
        Logging.d("Mp4Capturer", "Disposing capturer")
        stopCapture()
    }
    override fun isScreencast(): Boolean = false
}