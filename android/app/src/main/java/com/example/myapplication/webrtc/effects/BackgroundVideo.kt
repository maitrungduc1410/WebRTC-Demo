package com.example.myapplication.webrtc.effects

import android.content.Context
import android.graphics.SurfaceTexture
import android.media.MediaPlayer
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.os.Handler
import android.util.Log
import android.view.Surface
import org.webrtc.GlUtil

/**
 * A looping, silent video background decoded into an OES texture. Create, update and release it
 * on the GL thread that owns [handler]; the player's callbacks arrive on that thread too.
 */
class BackgroundVideo(context: Context, val asset: String, handler: Handler) {
    companion object {
        private const val TAG = "BackgroundVideo"
    }

    val textureId = GlUtil.generateTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES)
    private val surfaceTexture = SurfaceTexture(textureId)
    private val surface = Surface(surfaceTexture)
    private val player = MediaPlayer()
    private var frameAvailable = false

    var width = 0
        private set
    var height = 0
        private set
    var hasFrame = false
        private set

    /** Set when the file can't be played; the caller falls back to something else. */
    var failed = false
        private set

    init {
        surfaceTexture.setOnFrameAvailableListener({ frameAvailable = true }, handler)
        try {
            context.assets.openFd(asset).use { player.setDataSource(it.fileDescriptor, it.startOffset, it.length) }
            player.setSurface(surface)
            player.isLooping = true
            player.setVolume(0f, 0f)
            player.setOnVideoSizeChangedListener { _, w, h ->
                width = w
                height = h
            }
            player.setOnErrorListener { _, what, extra ->
                Log.e(TAG, "Can't play $asset ($what, $extra)")
                failed = true
                true
            }
            player.setOnPreparedListener { it.start() }
            player.prepareAsync()
        } catch (e: Exception) {
            Log.e(TAG, "Can't open $asset", e)
            failed = true
        }
    }

    /** Latches the newest decoded frame and writes its texture transform into [matrix]. */
    fun update(matrix: FloatArray): Boolean {
        if (frameAvailable) {
            frameAvailable = false
            surfaceTexture.updateTexImage()
            hasFrame = true
        }
        surfaceTexture.getTransformMatrix(matrix)
        return hasFrame && width > 0 && height > 0
    }

    fun release() {
        player.release()
        surface.release()
        surfaceTexture.release()
        GLES20.glDeleteTextures(1, intArrayOf(textureId), 0)
    }
}
