package com.example.myapplication.webrtc.effects

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Matrix
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.opengl.GLUtils
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.example.myapplication.effects.BackgroundKind
import com.example.myapplication.effects.BackgroundOption
import com.example.myapplication.effects.StickerOption
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
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin

/** What the processor draws. Immutable; [EffectsProcessor.setScene] swaps it as a whole. */
class EffectsScene(
    val background: BackgroundOption,
    val backgroundBitmap: Bitmap? = null,
    val sticker: StickerOption? = null,
    val stickerBitmap: Bitmap? = null
) {
    val needsMask: Boolean get() = background.kind != BackgroundKind.None
    val needsFace: Boolean get() = sticker != null && stickerBitmap != null
    val isActive: Boolean get() = needsMask || needsFace
}

/**
 * Applies the background and sticker on the GPU.
 *
 * Runs on the SurfaceTextureHelper thread, whose EGL context is current in [onFrameCaptured]:
 *  1. The camera texture is drawn upright into a small FBO and read back for [SelfieSegmenter]
 *     and [FaceTracker] (only for an idle model, so slow inference drops work instead of frames).
 *  2. The background is prepared: a picture, a blurred copy of the camera, or a video frame.
 *  3. A fragment shader mixes camera and background through the latest person mask, rendering at
 *     full resolution into a pooled RGB texture; the sticker is blended on top.
 *  4. The result is emitted as a TextureBuffer frame with rotation 0.
 *
 * Only attach this to the camera VideoSource. [releaseGl] must run on the capture thread before
 * the SurfaceTextureHelper is disposed.
 */
class EffectsProcessor(private val context: Context) : VideoProcessor {

    companion object {
        private const val TAG = "EffectsProcessor"
        private const val MASK_LONG_SIDE = 256
        // The face landmarker needs more pixels than segmentation to find a face across the room.
        private const val FACE_LONG_SIDE = 384
        private const val OUTPUT_POOL_SIZE = 4
        private const val MASK_EDGE_LOW = 0.3f
        private const val MASK_EDGE_HIGH = 0.7f
        // Blur is done on a copy scaled down until the radius is this many pixels.
        private const val BLUR_WORKING_RADIUS = 3f
        private const val BLUR_PASSES = 2

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

        // v_uv has its origin at the bottom-left of the upright image. The mask and pictures are
        // uploaded top-row-first, hence the flipped lookup; rendered backgrounds are not flipped.
        private const val FRAGMENT_BODY = """
varying vec2 v_camTc;
varying vec2 v_uv;
uniform sampler2D u_mask;
uniform sampler2D u_bg;
uniform vec2 u_bgScale;
uniform vec2 u_edge;
uniform float u_useMask;
uniform float u_bgFlip;
void main() {
  vec3 cam = texture2D(u_cam, v_camTc).rgb;
  if (u_useMask < 0.5) {
    gl_FragColor = vec4(cam, 1.0);
  } else {
    vec2 top = vec2(v_uv.x, 1.0 - v_uv.y);
    float person = smoothstep(u_edge.x, u_edge.y, texture2D(u_mask, top).r);
    vec2 bgUv = mix(v_uv, top, u_bgFlip);
    vec3 bg = texture2D(u_bg, (bgUv - 0.5) * u_bgScale + 0.5).rgb;
    gl_FragColor = vec4(mix(bg, cam, person), 1.0);
  }
}
"""

        private const val FRAGMENT_OES = "#extension GL_OES_EGL_image_external : require\n" +
            "precision mediump float;\nuniform samplerExternalOES u_cam;\n" + FRAGMENT_BODY
        private const val FRAGMENT_RGB = "precision mediump float;\nuniform sampler2D u_cam;\n" + FRAGMENT_BODY

        private const val PLAIN_VERTEX_SHADER = """
attribute vec4 in_pos;
attribute vec2 in_tc;
varying vec2 v_tc;
void main() {
  gl_Position = in_pos;
  v_tc = in_tc;
}
"""

        // Nine taps folded into five bilinear samples of a Gaussian.
        private const val BLUR_FRAGMENT = """
precision mediump float;
varying vec2 v_tc;
uniform sampler2D u_tex;
uniform vec2 u_step;
void main() {
  vec4 c = texture2D(u_tex, v_tc) * 0.2270270270;
  c += texture2D(u_tex, v_tc + u_step * 1.3846153846) * 0.3162162162;
  c += texture2D(u_tex, v_tc - u_step * 1.3846153846) * 0.3162162162;
  c += texture2D(u_tex, v_tc + u_step * 3.2307692308) * 0.0702702703;
  c += texture2D(u_tex, v_tc - u_step * 3.2307692308) * 0.0702702703;
  gl_FragColor = c;
}
"""

        private const val STICKER_FRAGMENT = """
precision mediump float;
varying vec2 v_tc;
uniform sampler2D u_tex;
void main() {
  gl_FragColor = texture2D(u_tex, v_tc);
}
"""

        private val QUAD_POSITIONS = GlUtil.createFloatBuffer(
            floatArrayOf(-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f)
        )
        private val QUAD_TEX_COORDS = GlUtil.createFloatBuffer(
            floatArrayOf(0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f)
        )
        // Top-left, top-right, bottom-left, bottom-right of a bitmap uploaded top-row-first.
        private val STICKER_TEX_COORDS = GlUtil.createFloatBuffer(
            floatArrayOf(0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f)
        )
    }

    @Volatile
    private var sink: VideoSink? = null

    @Volatile
    var segmenter: SelfieSegmenter? = null

    @Volatile
    var faceTracker: FaceTracker? = null

    @Volatile
    private var scene: EffectsScene? = null

    /** Set while a saved choice is still loading: frames are dropped instead of sent unprocessed. */
    @Volatile
    var holdFrames = false

    /** Called on the capture thread, once per scene, when a model it needs has failed. */
    var onModelFailed: (() -> Unit)? = null

    @Volatile
    private var failureReported = false

    // Bumped whenever a mask or face from before can no longer be trusted.
    @Volatile
    private var maskGeneration = 0

    @Volatile
    private var faceGeneration = 0

    private var gl: GlState? = null

    fun setScene(next: EffectsScene?) {
        val current = scene
        if (next?.needsMask == true && current?.needsMask != true) {
            segmenter?.resetMask()
            maskGeneration++
        }
        if (next?.needsFace == true && current?.sticker?.id != next.sticker?.id) {
            faceTracker?.reset()
            faceGeneration++
        }
        scene = next
        failureReported = false
        holdFrames = false
    }

    override fun setSink(sink: VideoSink?) {
        this.sink = sink
    }

    override fun onCapturerStarted(success: Boolean) {}

    override fun onCapturerStopped() {}

    override fun onFrameCaptured(frame: VideoFrame) {
        val sink = sink ?: return
        val scene = scene
        val buffer = frame.buffer
        val looper = Looper.myLooper()
        if (scene == null || !scene.isActive) {
            // A video background must not keep decoding once nothing uses it.
            gl?.takeIf { it.looper == looper }?.releaseVideo()
            if (!holdFrames) sink.onFrame(frame)
            return
        }
        val segmenter = segmenter.takeIf { scene.needsMask }
        val tracker = faceTracker.takeIf { scene.needsFace }
        val unusable = (scene.needsMask && (segmenter == null || segmenter.failed)) ||
            (scene.needsFace && (tracker == null || tracker.failed)) ||
            buffer !is VideoFrame.TextureBuffer || looper == null
        if (unusable) {
            // Sending the raw camera would show what the user chose to hide.
            reportFailure()
            return
        }

        val state = gl?.takeIf { it.looper == looper } ?: try {
            // A different looper means a new SurfaceTextureHelper/EGL context; the old objects died with it.
            GlState(Handler(looper)).also { gl = it }
        } catch (e: RuntimeException) {
            Log.e(TAG, "GL init failed", e)
            reportFailure()
            return
        }

        val handled = try {
            state.process(frame, buffer, scene, segmenter, tracker, sink)
        } catch (e: RuntimeException) {
            // Dropped rather than sent unprocessed.
            Log.e(TAG, "Effects render failed", e)
            true
        }
        if (!handled) sink.onFrame(frame)
    }

    /** The next camera frames show another scene (camera switch): drop old masks and faces. */
    fun invalidateAnalysis() {
        maskGeneration++
        faceGeneration++
        segmenter?.resetMask()
        faceTracker?.reset()
    }

    private fun reportFailure() {
        if (failureReported) return
        failureReported = true
        onModelFailed?.invoke()
    }

    /** Frees GL objects. Call on the capture thread (the SurfaceTextureHelper handler). */
    fun releaseGl() {
        gl?.release()
        gl = null
    }

    private class Background(val textureId: Int, val width: Int, val height: Int, val flipped: Boolean)

    private inner class GlState(private val handler: Handler) {
        val looper: Looper = handler.looper

        private val oesShader = createCompositeShader(FRAGMENT_OES)
        private val rgbShader = createCompositeShader(FRAGMENT_RGB)
        private val blurShader = GlShader(PLAIN_VERTEX_SHADER, BLUR_FRAGMENT)
        private val stickerShader = GlShader(PLAIN_VERTEX_SHADER, STICKER_FRAGMENT)
        private val drawer = GlRectDrawer()
        private val inferenceFbo = GlTextureFrameBuffer(GLES20.GL_RGBA)
        private val blurFbos = arrayOf(GlTextureFrameBuffer(GLES20.GL_RGBA), GlTextureFrameBuffer(GLES20.GL_RGBA))
        private val videoFbo = GlTextureFrameBuffer(GLES20.GL_RGBA)
        private val yuvConverter = YuvConverter()
        private var inferenceBuffer: ByteBuffer? = null
        private val videoMatrix = FloatArray(16)
        private val stickerCorners = ByteBuffer.allocateDirect(8 * 4).order(ByteOrder.nativeOrder()).asFloatBuffer()

        private val maskTexture = GlUtil.generateTexture(GLES20.GL_TEXTURE_2D)
        private var maskWidth = 0
        private var maskHeight = 0
        private var maskVersion = -1L
        private var hasMask = false
        private var maskGen = maskGeneration

        private var faceVersion = -1L
        private var faceGen = faceGeneration
        private val smoother = PlacementSmoother()

        private val pictureTexture = GlUtil.generateTexture(GLES20.GL_TEXTURE_2D)
        private var picture: Bitmap? = null
        private val stickerTexture = GlUtil.generateTexture(GLES20.GL_TEXTURE_2D)
        private var stickerBitmap: Bitmap? = null
        private var video: BackgroundVideo? = null

        private val freeOutputs = ArrayDeque<GlTextureFrameBuffer>()
        private var allocatedOutputs = 0
        private var released = false

        private fun createCompositeShader(fragment: String) = GlShader(VERTEX_SHADER, fragment).apply {
            useProgram()
            GLES20.glUniform1i(getUniformLocation("u_cam"), 0)
            GLES20.glUniform1i(getUniformLocation("u_mask"), 1)
            GLES20.glUniform1i(getUniformLocation("u_bg"), 2)
        }

        /**
         * Emits the processed frame to [sink] and returns true, or returns false if the caller
         * should forward the original frame. Frames are dropped (true, nothing emitted) until the
         * first mask arrives and while all output textures are held by consumers, so the real
         * background never leaks.
         */
        fun process(
            frame: VideoFrame, buffer: VideoFrame.TextureBuffer, scene: EffectsScene,
            segmenter: SelfieSegmenter?, tracker: FaceTracker?, sink: VideoSink
        ): Boolean {
            val width = frame.rotatedWidth
            val height = frame.rotatedHeight
            if (width <= 0 || height <= 0) return false

            if (maskGen != maskGeneration) {
                maskGen = maskGeneration
                hasMask = false
            }
            if (faceGen != faceGeneration) {
                faceGen = faceGeneration
                faceVersion = -1L
                smoother.reset()
            }
            if (scene.background.kind != BackgroundKind.Video) releaseVideo()

            val segmenterIdle = segmenter != null && !segmenter.isBusy
            val trackerIdle = tracker != null && !tracker.isBusy
            if (segmenterIdle || trackerIdle) {
                val longSide = if (tracker != null) FACE_LONG_SIDE else MASK_LONG_SIDE
                val pixels = readBack(buffer, frame.rotation, width, height, longSide)
                if (segmenterIdle) segmenter.submit(pixels, inferenceFbo.width, inferenceFbo.height)
                if (trackerIdle) tracker.submit(pixels, inferenceFbo.width, inferenceFbo.height)
            }

            val background = if (segmenter != null) {
                uploadLatestMask(segmenter)
                if (!hasMask) return true
                prepareBackground(scene, buffer, frame.rotation, width, height)
            } else {
                null
            }
            val placement = if (tracker != null) updateFace(tracker, scene, width, height) else null

            val target = obtainOutput(width, height) ?: return true
            composite(buffer, frame.rotation, target, width, height, background)
            if (placement != null) drawSticker(target, scene.stickerBitmap!!, placement, width, height)
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

        /** An upright, top-row-first RGBA copy of the frame, [longSide] pixels on its longer side. */
        private fun readBack(buffer: VideoFrame.TextureBuffer, rotation: Int, width: Int, height: Int, longSide: Int): ByteBuffer {
            val scale = longSide.toFloat() / max(width, height)
            val w = max(2, (width * scale).roundToInt())
            val h = max(2, (height * scale).roundToInt())
            inferenceFbo.setSize(w, h)

            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, inferenceFbo.frameBufferId)
            // Flipped so glReadPixels (bottom row first) yields a top-row-first upright image.
            drawCamera(buffer, uprightTexMatrix(buffer, rotation, flipVertical = true), w, h)

            val size = w * h * 4
            val pixels = inferenceBuffer?.takeIf { it.capacity() == size }
                ?: ByteBuffer.allocateDirect(size).order(ByteOrder.nativeOrder()).also { inferenceBuffer = it }
            pixels.rewind()
            GLES20.glReadPixels(0, 0, w, h, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, pixels)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)
            GlUtil.checkNoGLES2Error("Effects.readPixels")
            return pixels
        }

        private fun drawCamera(buffer: VideoFrame.TextureBuffer, texMatrix: FloatArray, w: Int, h: Int) {
            if (buffer.type == VideoFrame.TextureBuffer.Type.OES) {
                drawer.drawOes(buffer.textureId, texMatrix, w, h, 0, 0, w, h)
            } else {
                drawer.drawRgb(buffer.textureId, texMatrix, w, h, 0, 0, w, h)
            }
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
            GlUtil.checkNoGLES2Error("Effects.uploadMask")
            hasMask = true
        }

        private fun prepareBackground(
            scene: EffectsScene, buffer: VideoFrame.TextureBuffer, rotation: Int, width: Int, height: Int
        ): Background = when (scene.background.kind) {
            BackgroundKind.Image -> scene.backgroundBitmap?.let { bitmap ->
                if (picture !== bitmap) {
                    upload(pictureTexture, bitmap)
                    picture = bitmap
                }
                Background(pictureTexture, bitmap.width, bitmap.height, flipped = true)
            } ?: blur(buffer, rotation, width, height, scene.background.blur)
            // Until the first video frame, and if the file can't be played, the room is blurred.
            BackgroundKind.Video -> videoFrame(scene.background.file)
                ?: blur(buffer, rotation, width, height, 0.02f)
            else -> blur(buffer, rotation, width, height, scene.background.blur)
        }

        private fun blur(buffer: VideoFrame.TextureBuffer, rotation: Int, width: Int, height: Int, fraction: Float): Background {
            val radius = max(2f, fraction * width)
            val scale = min(1f, BLUR_WORKING_RADIUS / radius)
            val w = max(2, (width * scale).roundToInt())
            val h = max(2, (height * scale).roundToInt())
            val (a, b) = blurFbos
            a.setSize(w, h)
            b.setSize(w, h)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, a.frameBufferId)
            drawCamera(buffer, uprightTexMatrix(buffer, rotation, flipVertical = false), w, h)

            blurShader.useProgram()
            blurShader.setVertexAttribArray("in_pos", 2, QUAD_POSITIONS)
            blurShader.setVertexAttribArray("in_tc", 2, QUAD_TEX_COORDS)
            GLES20.glUniform1i(blurShader.getUniformLocation("u_tex"), 0)
            val step = blurShader.getUniformLocation("u_step")
            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glViewport(0, 0, w, h)
            repeat(BLUR_PASSES) {
                GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, b.frameBufferId)
                GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, a.textureId)
                GLES20.glUniform2f(step, 1f / w, 0f)
                GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
                GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, a.frameBufferId)
                GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, b.textureId)
                GLES20.glUniform2f(step, 0f, 1f / h)
                GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            }
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)
            GlUtil.checkNoGLES2Error("Effects.blur")
            return Background(a.textureId, w, h, flipped = false)
        }

        private fun videoFrame(asset: String?): Background? {
            asset ?: return null
            val player = video?.takeIf { it.asset == asset } ?: run {
                releaseVideo()
                BackgroundVideo(context, asset, handler).also { video = it }
            }
            if (player.failed || !player.update(videoMatrix)) return null
            val w = player.width
            val h = player.height
            videoFbo.setSize(w, h)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, videoFbo.frameBufferId)
            drawer.drawOes(player.textureId, videoMatrix, w, h, 0, 0, w, h)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)
            GlUtil.checkNoGLES2Error("Effects.video")
            return Background(videoFbo.textureId, w, h, flipped = false)
        }

        fun releaseVideo() {
            video?.release()
            video = null
        }

        private fun updateFace(tracker: FaceTracker, scene: EffectsScene, width: Int, height: Int): Placement? {
            val sticker = scene.sticker ?: return null
            val bitmap = scene.stickerBitmap ?: return null
            tracker.latest(faceVersion)?.let { (face, version) ->
                faceVersion = version
                val placed = face?.let {
                    StickerPlacement.place(
                        FacePoints(it.eyeA.scaled(width, height), it.eyeB.scaled(width, height),
                            it.nose.scaled(width, height), it.mouth.scaled(width, height)),
                        sticker, bitmap.height.toFloat() / bitmap.width
                    )
                }
                smoother.update(placed)
            }
            return smoother.placement
        }

        private fun Point.scaled(width: Int, height: Int) = Point(x * width, y * height)

        private fun composite(
            buffer: VideoFrame.TextureBuffer, rotation: Int, target: GlTextureFrameBuffer,
            width: Int, height: Int, background: Background?
        ) {
            val isOes = buffer.type == VideoFrame.TextureBuffer.Type.OES
            val shader = if (isOes) oesShader else rgbShader
            shader.useProgram()

            GLES20.glUniformMatrix4fv(
                shader.getUniformLocation("u_texMatrix"), 1, false,
                uprightTexMatrix(buffer, rotation, flipVertical = false), 0
            )
            GLES20.glUniform1f(shader.getUniformLocation("u_useMask"), if (background != null) 1f else 0f)
            if (background != null) {
                // Center-crop the background to the output aspect ratio.
                val outAspect = width.toFloat() / height
                val bgAspect = background.width.toFloat() / background.height
                if (outAspect > bgAspect) {
                    GLES20.glUniform2f(shader.getUniformLocation("u_bgScale"), 1f, bgAspect / outAspect)
                } else {
                    GLES20.glUniform2f(shader.getUniformLocation("u_bgScale"), outAspect / bgAspect, 1f)
                }
                GLES20.glUniform2f(shader.getUniformLocation("u_edge"), MASK_EDGE_LOW, MASK_EDGE_HIGH)
                GLES20.glUniform1f(shader.getUniformLocation("u_bgFlip"), if (background.flipped) 1f else 0f)
            }

            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(if (isOes) GLES11Ext.GL_TEXTURE_EXTERNAL_OES else GLES20.GL_TEXTURE_2D, buffer.textureId)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE1)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, maskTexture)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE2)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, background?.textureId ?: 0)

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
            GlUtil.checkNoGLES2Error("Effects.composite")
        }

        private fun drawSticker(target: GlTextureFrameBuffer, bitmap: Bitmap, placement: Placement, width: Int, height: Int) {
            if (stickerBitmap !== bitmap) {
                upload(stickerTexture, bitmap)
                stickerBitmap = bitmap
            }
            // Corners in pixels (y down), turned with the face, then into clip space (y up).
            val c = cos(placement.angle)
            val s = sin(placement.angle)
            val hw = placement.width / 2
            val hh = placement.height / 2
            stickerCorners.clear()
            for ((dx, dy) in arrayOf(-hw to -hh, hw to -hh, -hw to hh, hw to hh)) {
                val x = placement.x + dx * c - dy * s
                val y = placement.y + dx * s + dy * c
                stickerCorners.put(x / width * 2f - 1f)
                stickerCorners.put(1f - y / height * 2f)
            }
            stickerCorners.rewind()

            stickerShader.useProgram()
            stickerShader.setVertexAttribArray("in_pos", 2, stickerCorners)
            stickerShader.setVertexAttribArray("in_tc", 2, STICKER_TEX_COORDS)
            GLES20.glUniform1i(stickerShader.getUniformLocation("u_tex"), 0)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, stickerTexture)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, target.frameBufferId)
            GLES20.glViewport(0, 0, width, height)
            // Android bitmaps are uploaded with premultiplied alpha.
            GLES20.glEnable(GLES20.GL_BLEND)
            GLES20.glBlendFunc(GLES20.GL_ONE, GLES20.GL_ONE_MINUS_SRC_ALPHA)
            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            GLES20.glDisable(GLES20.GL_BLEND)
            GLES20.glBindFramebuffer(GLES20.GL_FRAMEBUFFER, 0)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
            GlUtil.checkNoGLES2Error("Effects.sticker")
        }

        private fun upload(texture: Int, bitmap: Bitmap) {
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, texture)
            GLUtils.texImage2D(GLES20.GL_TEXTURE_2D, 0, bitmap, 0)
            GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, 0)
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
            releaseVideo()
            freeOutputs.forEach { it.release() }
            freeOutputs.clear()
            oesShader.release()
            rgbShader.release()
            blurShader.release()
            stickerShader.release()
            drawer.release()
            inferenceFbo.release()
            blurFbos.forEach { it.release() }
            videoFbo.release()
            yuvConverter.release()
            GLES20.glDeleteTextures(3, intArrayOf(maskTexture, pictureTexture, stickerTexture), 0)
        }
    }
}
