package com.example.myapplication.webrtc.vbg

import android.graphics.Bitmap
import android.graphics.Matrix
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.opengl.GLUtils
import android.os.Handler
import android.os.Looper
import android.util.Log
import org.webrtc.GlRectDrawer
import org.webrtc.GlShader
import org.webrtc.GlTextureFrameBuffer
import org.webrtc.GlUtil
import org.webrtc.RendererCommon
import org.webrtc.TextureBufferImpl
import org.webrtc.VideoFrame
import org.webrtc.VideoProcessor
import org.webrtc.VideoSink
import org.webrtc.YuvConverter
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * Replaces the camera background on the GPU.
 *
 * Runs on the SurfaceTextureHelper thread, whose EGL context is current in [onFrameCaptured]:
 *  1. The OES camera texture is drawn upright into a small FBO and read back for [SelfieSegmenter]
 *     (only when no inference is in flight, so slow inference drops frames instead of stalling capture).
 *  2. A fragment shader mixes the camera texture with the background image using the latest
 *     confidence mask, rendering at full resolution into a pooled RGB texture.
 *  3. The result is emitted as a TextureBuffer frame with rotation 0.
 *
 * Only attach this to the camera VideoSource. [releaseGl] must run on the capture thread before
 * the SurfaceTextureHelper is disposed.
 */
class VirtualBackgroundProcessor : VideoProcessor {

    companion object {
        private const val TAG = "VirtualBgProcessor"
        private const val INFERENCE_LONG_SIDE = 256
        private const val OUTPUT_POOL_SIZE = 4
        private const val MASK_EDGE_LOW = 0.3f
        private const val MASK_EDGE_HIGH = 0.7f

        private const val VERTEX_SHADER = """
attribute vec4 in_pos;
attribute vec2 in_tc;
uniform mat4 u_texMatrix;
varying vec2 v_camTc;
varying vec2 v_uv;
void main() {
  gl_Position = in_pos;
  v_camTc = (u_texMatrix * vec4(in_tc, 0.0, 1.0)).xy;
  v_uv = in_tc;
}
"""

        // v_uv has its origin at the bottom-left of the upright image; mask and background
        // textures are uploaded top-row-first, hence the flipped lookup.
        private const val FRAGMENT_BODY = """
varying vec2 v_camTc;
varying vec2 v_uv;
uniform sampler2D u_mask;
uniform sampler2D u_bg;
uniform vec2 u_bgScale;
uniform vec2 u_edge;
void main() {
  vec3 cam = texture2D(u_cam, v_camTc).rgb;
  vec2 top = vec2(v_uv.x, 1.0 - v_uv.y);
  float person = smoothstep(u_edge.x, u_edge.y, texture2D(u_mask, top).r);
  vec3 bg = texture2D(u_bg, (top - 0.5) * u_bgScale + 0.5).rgb;
  gl_FragColor = vec4(mix(bg, cam, person), 1.0);
}
"""

        private const val FRAGMENT_OES = "#extension GL_OES_EGL_image_external : require\n" +
            "precision mediump float;\nuniform samplerExternalOES u_cam;\n" + FRAGMENT_BODY
        private const val FRAGMENT_RGB = "precision mediump float;\nuniform sampler2D u_cam;\n" + FRAGMENT_BODY

        private val QUAD_POSITIONS = GlUtil.createFloatBuffer(
            floatArrayOf(-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f)
        )
        private val QUAD_TEX_COORDS = GlUtil.createFloatBuffer(
            floatArrayOf(0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f)
        )
    }

    @Volatile
    private var sink: VideoSink? = null

    @Volatile
    private var enabled = false

    @Volatile
    var segmenter: SelfieSegmenter? = null

    @Volatile
    var background: Bitmap? = null

    // Bumped on every enable so the GL thread forgets masks from a previous session.
    @Volatile
    private var enableGeneration = 0

    private var gl: GlState? = null

    fun setEnabled(enable: Boolean) {
        if (enable && !enabled) {
            segmenter?.resetMask()
            enableGeneration++
        }
        enabled = enable
    }

    override fun setSink(sink: VideoSink?) {
        this.sink = sink
    }

    override fun onCapturerStarted(success: Boolean) {}

    override fun onCapturerStopped() {}

    override fun onFrameCaptured(frame: VideoFrame) {
        val sink = sink ?: return
        val segmenter = segmenter
        val background = background
        val buffer = frame.buffer
        val looper = Looper.myLooper()
        if (!enabled || segmenter == null || background == null ||
            buffer !is VideoFrame.TextureBuffer || looper == null
        ) {
            sink.onFrame(frame)
            return
        }

        val state = gl?.takeIf { it.looper == looper } ?: try {
            // A different looper means a new SurfaceTextureHelper/EGL context; the old objects died with it.
            GlState(Handler(looper), background).also { gl = it }
        } catch (e: RuntimeException) {
            Log.e(TAG, "GL init failed, passing frames through", e)
            sink.onFrame(frame)
            return
        }

        val handled = try {
            state.process(frame, buffer, segmenter, sink)
        } catch (e: RuntimeException) {
            Log.e(TAG, "Virtual background render failed", e)
            false
        }
        // Not handled means no mask yet (or a GL error): show the camera unchanged.
        if (!handled) sink.onFrame(frame)
    }

    /** Frees GL objects. Call on the capture thread (the SurfaceTextureHelper handler). */
    fun releaseGl() {
        gl?.release()
        gl = null
    }

    private inner class GlState(private val handler: Handler, private val background: Bitmap) {
        val looper: Looper = handler.looper

        private val oesShader = createShader(FRAGMENT_OES)
        private val rgbShader = createShader(FRAGMENT_RGB)
        private val downscaleDrawer = GlRectDrawer()
        private val inferenceFbo = GlTextureFrameBuffer(GLES20.GL_RGBA)
        private val yuvConverter = YuvConverter()
        private var inferenceBuffer: ByteBuffer? = null

        private val maskTexture = GlUtil.generateTexture(GLES20.GL_TEXTURE_2D)
        private var maskWidth = 0
        private var maskHeight = 0
        private var maskVersion = -1L
        private var hasMask = false
        private var generation = enableGeneration

        private val backgroundTexture = GlUtil.generateTexture(GLES20.GL_TEXTURE_2D).also {
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, it)
            GLUtils.texImage2D(GLES20.GL_TEXTURE_2D, 0, background, 0)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
        }

        private val freeOutputs = ArrayDeque<GlTextureFrameBuffer>()
        private var allocatedOutputs = 0
        private var released = false

        private fun createShader(fragment: String) = GlShader(VERTEX_SHADER, fragment).apply {
            useProgram()
            GLES20.glUniform1i(getUniformLocation("u_cam"), 0)
            GLES20.glUniform1i(getUniformLocation("u_mask"), 1)
            GLES20.glUniform1i(getUniformLocation("u_bg"), 2)
        }

        /**
         * Emits the composited frame to [sink] and returns true, or returns false if the caller
         * should forward the original frame. Frames are dropped (true, nothing emitted) while all
         * output textures are still held by consumers, so the real background never leaks.
         */
        fun process(
            frame: VideoFrame, buffer: VideoFrame.TextureBuffer, segmenter: SelfieSegmenter, sink: VideoSink
        ): Boolean {
            val width = frame.rotatedWidth
            val height = frame.rotatedHeight
            if (width <= 0 || height <= 0) return false

            if (generation != enableGeneration) {
                generation = enableGeneration
                hasMask = false
            }

            if (!segmenter.isBusy) submitForInference(buffer, frame.rotation, width, height, segmenter)
            uploadLatestMask(segmenter)
            if (!hasMask) return false

            val target = obtainOutput(width, height) ?: return true
            composite(buffer, frame.rotation, target, width, height)
            // Consumers (encoder, local renderer) sample this texture from other shared contexts.
            GLES20.glFinish()

            val outBuffer = TextureBufferImpl(
                width, height, VideoFrame.TextureBuffer.Type.RGB, target.textureId, Matrix(),
                handler, yuvConverter
            ) { handler.post { recycleOutput(target) } }
            val output = VideoFrame(outBuffer, 0, frame.timestampNs)
            sink.onFrame(output)
            output.release()
            return true
        }

        private fun submitForInference(
            buffer: VideoFrame.TextureBuffer, rotation: Int, width: Int, height: Int, segmenter: SelfieSegmenter
        ) {
            val scale = INFERENCE_LONG_SIDE.toFloat() / max(width, height)
            val w = max(2, (width * scale).roundToInt())
            val h = max(2, (height * scale).roundToInt())
            inferenceFbo.setSize(w, h)

            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, inferenceFbo.frameBufferId)
            // Flipped so glReadPixels (bottom row first) yields a top-row-first upright image.
            val texMatrix = uprightTexMatrix(buffer, rotation, flipVertical = true)
            if (buffer.type == VideoFrame.TextureBuffer.Type.OES) {
                downscaleDrawer.drawOes(buffer.textureId, texMatrix, w, h, 0, 0, w, h)
            } else {
                downscaleDrawer.drawRgb(buffer.textureId, texMatrix, w, h, 0, 0, w, h)
            }

            val size = w * h * 4
            val pixels = inferenceBuffer?.takeIf { it.capacity() == size }
                ?: ByteBuffer.allocateDirect(size).order(ByteOrder.nativeOrder()).also { inferenceBuffer = it }
            pixels.rewind()
            GLES20.glReadPixels(0, 0, w, h, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, pixels)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)
            GlUtil.checkNoGLES2Error("VirtualBg.readPixels")

            // Safe to reuse: a new submission only happens after the previous inference finished.
            segmenter.submit(pixels, w, h)
        }

        private fun uploadLatestMask(segmenter: SelfieSegmenter) {
            val (mask, version) = segmenter.latestMask(maskVersion) ?: return
            maskVersion = version
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, maskTexture)
            GLES20.glPixelStorei(GLES20.GL_UNPACK_ALIGNMENT, 1)
            mask.data.rewind()
            if (mask.width != maskWidth || mask.height != maskHeight) {
                maskWidth = mask.width
                maskHeight = mask.height
                GLES20.glTexImage2D(
                    GLES20.GL_TEXTURE_2D, 0, GLES20.GL_LUMINANCE, maskWidth, maskHeight, 0,
                    GLES20.GL_LUMINANCE, GLES20.GL_UNSIGNED_BYTE, mask.data
                )
            } else {
                GLES20.glTexSubImage2D(
                    GLES20.GL_TEXTURE_2D, 0, 0, 0, maskWidth, maskHeight,
                    GLES20.GL_LUMINANCE, GLES20.GL_UNSIGNED_BYTE, mask.data
                )
            }
            GLES20.glPixelStorei(GLES20.GL_UNPACK_ALIGNMENT, 4)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
            GlUtil.checkNoGLES2Error("VirtualBg.uploadMask")
            hasMask = true
        }

        private fun composite(
            buffer: VideoFrame.TextureBuffer, rotation: Int, target: GlTextureFrameBuffer, width: Int, height: Int
        ) {
            val isOes = buffer.type == VideoFrame.TextureBuffer.Type.OES
            val shader = if (isOes) oesShader else rgbShader
            shader.useProgram()

            GLES20.glUniformMatrix4fv(
                shader.getUniformLocation("u_texMatrix"), 1, false,
                uprightTexMatrix(buffer, rotation, flipVertical = false), 0
            )
            // Center-crop the background to the output aspect ratio.
            val outAspect = width.toFloat() / height
            val bgAspect = background.width.toFloat() / background.height
            if (outAspect > bgAspect) {
                GLES20.glUniform2f(shader.getUniformLocation("u_bgScale"), 1f, bgAspect / outAspect)
            } else {
                GLES20.glUniform2f(shader.getUniformLocation("u_bgScale"), outAspect / bgAspect, 1f)
            }
            GLES20.glUniform2f(shader.getUniformLocation("u_edge"), MASK_EDGE_LOW, MASK_EDGE_HIGH)

            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(if (isOes) GLES11Ext.GL_TEXTURE_EXTERNAL_OES else GLES20.GL_TEXTURE_2D, buffer.textureId)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE1)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, maskTexture)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE2)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, backgroundTexture)

            shader.setVertexAttribArray("in_pos", 2, QUAD_POSITIONS)
            shader.setVertexAttribArray("in_tc", 2, QUAD_TEX_COORDS)

            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, target.frameBufferId)
            GLES20.glViewport(0, 0, width, height)
            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)

            GLES20.glActiveTexture(GLES20.GL_TEXTURE2)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE1)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(if (isOes) GLES11Ext.GL_TEXTURE_EXTERNAL_OES else GLES20.GL_TEXTURE_2D, 0)
            GlUtil.checkNoGLES2Error("VirtualBg.composite")
        }

        /**
         * Same matrix VideoFrameDrawer uses for texture frames: maps viewport coordinates of the
         * upright (rotated) image to the buffer's texture coordinates.
         */
        private fun uprightTexMatrix(buffer: VideoFrame.TextureBuffer, rotation: Int, flipVertical: Boolean): FloatArray {
            val render = Matrix().apply {
                preTranslate(0.5f, 0.5f)
                preRotate(rotation.toFloat())
                if (flipVertical) preScale(1f, -1f)
                preTranslate(-0.5f, -0.5f)
            }
            val combined = Matrix(buffer.transformMatrix).apply { preConcat(render) }
            return RendererCommon.convertMatrixFromAndroidGraphicsMatrix(combined)
        }

        private fun obtainOutput(width: Int, height: Int): GlTextureFrameBuffer? {
            freeOutputs.removeFirstOrNull()?.let {
                // No-op unless the resolution changed (e.g. camera switch).
                it.setSize(width, height)
                return it
            }
            if (allocatedOutputs >= OUTPUT_POOL_SIZE) return null
            allocatedOutputs++
            return GlTextureFrameBuffer(GLES20.GL_RGBA).apply { setSize(width, height) }
        }

        private fun recycleOutput(target: GlTextureFrameBuffer) {
            if (released) {
                target.release()
            } else {
                freeOutputs.addLast(target)
            }
        }

        fun release() {
            released = true
            freeOutputs.forEach { it.release() }
            freeOutputs.clear()
            oesShader.release()
            rgbShader.release()
            downscaleDrawer.release()
            inferenceFbo.release()
            yuvConverter.release()
            GLES20.glDeleteTextures(2, intArrayOf(maskTexture, backgroundTexture), 0)
        }
    }
}