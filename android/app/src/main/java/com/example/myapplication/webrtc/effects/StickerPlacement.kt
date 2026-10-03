package com.example.myapplication.webrtc.effects

import com.example.myapplication.effects.StickerAnchor
import com.example.myapplication.effects.StickerOption
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.sin

data class Point(val x: Float, val y: Float)

/** Face points in pixels, y down. The two eyes can come in either order. */
data class FacePoints(val eyeA: Point, val eyeB: Point, val nose: Point, val mouth: Point)

/** A sticker's center, size and rotation (radians, clockwise on screen). */
data class Placement(val x: Float, val y: Float, val width: Float, val height: Float, val angle: Float)

/**
 * Places a sticker on a face, matching the web app. The face's own axes are used, so the sticker
 * tilts with the head: "right" runs from one eye to the other, "up" from the mouth to the eyes.
 */
object StickerPlacement {
    // Eyes to mouth over the distance between the eyes on a face looking at the camera. The larger
    // of the two scales is used so a sticker doesn't shrink when the head turns sideways.
    private const val EYES_TO_MOUTH_RATIO = 1.2f

    fun place(face: FacePoints, sticker: StickerOption, aspect: Float): Placement? {
        val eyes = Point((face.eyeA.x + face.eyeB.x) / 2, (face.eyeA.y + face.eyeB.y) / 2)
        val roughX = eyes.x - face.mouth.x
        val roughY = eyes.y - face.mouth.y
        // Up (0, -1) turns into right (1, 0).
        val forward = (face.eyeB.x - face.eyeA.x) * -roughY + (face.eyeB.y - face.eyeA.y) * roughX >= 0
        val left = if (forward) face.eyeA else face.eyeB
        val right = if (forward) face.eyeB else face.eyeA

        val axisX = right.x - left.x
        val axisY = right.y - left.y
        val iod = hypot(axisX, axisY)
        if (iod < 1f) return null
        val uxX = axisX / iod
        val uxY = axisY / iod
        val upX = uxY
        val upY = -uxX
        val unit = max(iod, hypot(roughX, roughY) / EYES_TO_MOUTH_RATIO)

        val anchor = when (sticker.anchor) {
            StickerAnchor.Nose -> face.nose
            StickerAnchor.Mouth -> face.mouth
            StickerAnchor.Eyes -> eyes
        }
        val width = sticker.width * unit
        return Placement(
            x = anchor.x + unit * (sticker.offsetX * uxX + sticker.offsetY * upX),
            y = anchor.y + unit * (sticker.offsetX * uxY + sticker.offsetY * upY),
            width = width,
            height = sticker.height?.let { it * unit } ?: (width * aspect),
            angle = atan2(uxY, uxX)
        )
    }
}

/** Evens out landmark jitter, and keeps the last placement through a few missed detections. */
class PlacementSmoother(private val amount: Float = 0.4f, private val keepFrames: Int = 6) {
    private var current: Placement? = null
    private var missed = 0

    val placement: Placement? get() = current

    fun update(next: Placement?): Placement? {
        if (next == null) {
            if (++missed > keepFrames) current = null
            return current
        }
        missed = 0
        val prev = current ?: return next.also { current = it }
        val k = 1 - amount
        val delta = (next.angle - prev.angle).let { atan2(sin(it), cos(it)) }
        return Placement(
            x = prev.x + (next.x - prev.x) * k,
            y = prev.y + (next.y - prev.y) * k,
            width = prev.width + (next.width - prev.width) * k,
            height = prev.height + (next.height - prev.height) * k,
            angle = prev.angle + delta * k
        ).also { current = it }
    }

    fun reset() {
        current = null
        missed = 0
    }
}
