package com.example.myapplication.ui.video

import android.content.Context
import android.graphics.Bitmap
import android.graphics.SurfaceTexture
import android.os.Handler
import android.os.Looper
import android.view.TextureView
import org.webrtc.EglBase
import org.webrtc.EglRenderer
import org.webrtc.GlRectDrawer
import org.webrtc.VideoFrame
import org.webrtc.VideoSink
import java.util.concurrent.atomic.AtomicBoolean

/**
 * A [TextureView] backed by an [EglRenderer]. Unlike SurfaceViewRenderer it composes like a normal
 * view, so Compose can clip it to rounded shapes, animate it and stack it without z-order tricks.
 *
 * The renderer always fills the view (center crop). To letterbox, size the view to the frame
 * aspect ratio reported by [onFrameSizeChanged].
 */
class TextureViewRenderer(context: Context) : TextureView(context), TextureView.SurfaceTextureListener, VideoSink {

    private val eglRenderer = EglRenderer("TextureViewRenderer")
    private val mainHandler = Handler(Looper.getMainLooper())
    private var initialized = false
    @Volatile private var frameWidth = 0
    @Volatile private var frameHeight = 0
    private val snapshotPending = AtomicBoolean(false)

    /** Rotated frame size; called on the main thread whenever it changes. */
    var onFrameSizeChanged: ((width: Int, height: Int) -> Unit)? = null

    init {
        surfaceTextureListener = this
        isOpaque = false
    }

    fun init(eglContext: EglBase.Context) {
        if (initialized) return
        initialized = true
        eglRenderer.init(eglContext, EglBase.CONFIG_PLAIN, GlRectDrawer())
        surfaceTexture?.let { onSurfaceTextureAvailable(it, width, height) }
    }

    fun setMirror(mirror: Boolean) = eglRenderer.setMirror(mirror)

    fun clearImage() = eglRenderer.clearImage(0f, 0f, 0f, 0f)

    /**
     * Reads back the next rendered frame, scaled to about [width] pixels wide, and passes it to
     * [onBitmap] on the render thread. Ignored while a previous request is still waiting for a frame.
     */
    fun requestSnapshot(width: Int, onBitmap: (Bitmap) -> Unit) {
        val frameW = frameWidth
        if (!initialized || frameW <= 0 || !snapshotPending.compareAndSet(false, true)) return
        eglRenderer.addFrameListener({ bitmap ->
            snapshotPending.set(false)
            onBitmap(bitmap)
        }, width.toFloat() / frameW)
    }

    fun release() {
        if (!initialized) return
        initialized = false
        onFrameSizeChanged = null
        eglRenderer.release()
    }

    override fun onFrame(frame: VideoFrame) {
        val w = frame.rotatedWidth
        val h = frame.rotatedHeight
        if (w != frameWidth || h != frameHeight) {
            frameWidth = w
            frameHeight = h
            mainHandler.post { onFrameSizeChanged?.invoke(w, h) }
        }
        eglRenderer.onFrame(frame)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        if (w > 0 && h > 0) eglRenderer.setLayoutAspectRatio(w.toFloat() / h)
    }

    override fun onSurfaceTextureAvailable(surface: SurfaceTexture, width: Int, height: Int) {
        if (!initialized) return
        if (width > 0 && height > 0) eglRenderer.setLayoutAspectRatio(width.toFloat() / height)
        eglRenderer.createEglSurface(surface)
    }

    override fun onSurfaceTextureSizeChanged(surface: SurfaceTexture, width: Int, height: Int) {
        if (width > 0 && height > 0) eglRenderer.setLayoutAspectRatio(width.toFloat() / height)
    }

    override fun onSurfaceTextureDestroyed(surface: SurfaceTexture): Boolean {
        // The EGL surface must be gone before the texture is released, so release it ourselves afterwards.
        eglRenderer.releaseEglSurface { mainHandler.post { surface.release() } }
        return false
    }

    override fun onSurfaceTextureUpdated(surface: SurfaceTexture) {}
}
