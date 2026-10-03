package com.example.myapplication.webrtc.effects

import com.example.myapplication.effects.StickerAnchor
import com.example.myapplication.effects.StickerOption
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import kotlin.math.PI

class StickerPlacementTest {

    private fun sticker(anchor: StickerAnchor = StickerAnchor.Eyes, width: Float = 2f, height: Float? = null, offsetX: Float = 0f, offsetY: Float = 0f) =
        StickerOption("s", "S", "s.png", anchor, width, height, offsetX, offsetY)

    // Eyes 100 px apart at y = 100, nose and mouth below: an upright face.
    private val upright = FacePoints(
        eyeA = Point(100f, 100f), eyeB = Point(200f, 100f),
        nose = Point(150f, 150f), mouth = Point(150f, 200f)
    )

    @Test
    fun scalesByTheDistanceBetweenTheEyes() {
        val p = StickerPlacement.place(upright, sticker(width = 2f), aspect = 0.5f)!!
        assertEquals(150f, p.x, 0.01f)
        assertEquals(100f, p.y, 0.01f)
        assertEquals(200f, p.width, 0.01f)
        assertEquals(100f, p.height, 0.01f)
        assertEquals(0f, p.angle, 0.0001f)
    }

    @Test
    fun explicitHeightStretchesTheArtwork() {
        val p = StickerPlacement.place(upright, sticker(width = 2f, height = 3f), aspect = 0.5f)!!
        assertEquals(300f, p.height, 0.01f)
    }

    @Test
    fun positiveOffsetYMovesUpTheFace() {
        val p = StickerPlacement.place(upright, sticker(offsetY = 1f, offsetX = 0.5f), aspect = 1f)!!
        assertEquals(200f, p.x, 0.01f)
        assertEquals(0f, p.y, 0.01f)
    }

    @Test
    fun anchorsOnNoseAndMouth() {
        assertEquals(150f, StickerPlacement.place(upright, sticker(StickerAnchor.Nose), 1f)!!.y, 0.01f)
        assertEquals(200f, StickerPlacement.place(upright, sticker(StickerAnchor.Mouth), 1f)!!.y, 0.01f)
    }

    @Test
    fun eyeOrderDoesNotMatter() {
        val swapped = upright.copy(eyeA = upright.eyeB, eyeB = upright.eyeA)
        assertEquals(StickerPlacement.place(upright, sticker(offsetX = 1f), 1f), StickerPlacement.place(swapped, sticker(offsetX = 1f), 1f))
    }

    @Test
    fun followsAHeadTiltedOnItsSide() {
        // Rotated 90° clockwise on screen: the eyes stack vertically and the mouth is to the left.
        val tilted = FacePoints(
            eyeA = Point(100f, 100f), eyeB = Point(100f, 200f),
            nose = Point(50f, 150f), mouth = Point(0f, 150f)
        )
        val p = StickerPlacement.place(tilted, sticker(offsetY = 1f), aspect = 1f)!!
        assertEquals((PI / 2).toFloat(), p.angle, 0.0001f)
        // "Up" for this face points right on screen.
        assertEquals(200f, p.x, 0.01f)
        assertEquals(150f, p.y, 0.01f)
    }

    @Test
    fun keepsItsSizeWhenTheHeadTurns() {
        // Turned sideways the eyes close up, but eyes to mouth stays 100 px.
        val turned = upright.copy(eyeA = Point(130f, 100f), eyeB = Point(170f, 100f))
        val p = StickerPlacement.place(turned, sticker(width = 1f), aspect = 1f)!!
        assertEquals(100f / 1.2f, p.width, 0.01f)
    }

    @Test
    fun ignoresADegenerateFace() {
        val point = Point(10f, 10f)
        assertNull(StickerPlacement.place(FacePoints(point, point, point, point), sticker(), 1f))
    }

    @Test
    fun smootherHoldsThroughShortGapsThenLetsGo() {
        val smoother = PlacementSmoother(amount = 0.5f, keepFrames = 2)
        val a = Placement(0f, 0f, 10f, 10f, 0f)
        assertEquals(a, smoother.update(a))
        val b = smoother.update(Placement(10f, 0f, 10f, 10f, 0f))
        assertNotNull(b)
        assertEquals(5f, b!!.x, 0.01f)
        assertNotNull(smoother.update(null))
        assertNotNull(smoother.update(null))
        assertNull(smoother.update(null))
    }
}
