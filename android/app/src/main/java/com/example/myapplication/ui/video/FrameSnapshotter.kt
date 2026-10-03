package com.example.myapplication.ui.video

import android.graphics.Bitmap

/**
 * Tiny copies of the remote video, used as the blurred backdrop when it is hidden or turned off.
 *
 * Snapshots are read back by the renderer after it draws a frame ([TextureViewRenderer.requestSnapshot]).
 * Never convert frames in a track sink instead: `toI420()` on a decoder texture blocks the frame
 * delivery thread on the decoder's texture thread, and stalls the remote video when the decoder
 * is recreated (for example when the peer renegotiates to open the chat).
 */
object FrameSnapshotter {
    const val WIDTH = 36
    const val INTERVAL_MS = 500L

    // Disabled tracks deliver black frames; keep the last real picture instead.
    private const val MIN_AVERAGE_LUMA = 10

    fun isUsable(bitmap: Bitmap): Boolean {
        val pixels = IntArray(bitmap.width * bitmap.height)
        if (pixels.isEmpty()) return false
        bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
        var lumaSum = 0L
        for (pixel in pixels) {
            val r = (pixel shr 16) and 0xFF
            val g = (pixel shr 8) and 0xFF
            val b = pixel and 0xFF
            lumaSum += (77 * r + 150 * g + 29 * b) shr 8
        }
        return lumaSum / pixels.size >= MIN_AVERAGE_LUMA
    }
}
