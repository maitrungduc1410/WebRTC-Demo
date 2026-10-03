package com.example.myapplication.ui.video

import android.graphics.Bitmap
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.viewinterop.AndroidView
import com.example.myapplication.call.safeAddSink
import com.example.myapplication.call.safeRemoveSink
import org.webrtc.EglBase
import kotlinx.coroutines.delay
import org.webrtc.VideoTrack
import kotlin.math.max
import kotlin.math.min

/**
 * Renders [track] filling the given bounds (center crop). [onSnapshot], when set, receives a
 * [FrameSnapshotter.WIDTH] pixel wide copy of a rendered frame about every
 * [FrameSnapshotter.INTERVAL_MS], on the render thread.
 */
@Composable
fun VideoRenderer(
    track: VideoTrack?,
    eglContext: EglBase.Context,
    modifier: Modifier = Modifier,
    mirror: Boolean = false,
    onFrameSize: (IntSize) -> Unit = {},
    onSnapshot: ((Bitmap) -> Unit)? = null
) {
    var renderer by remember { mutableStateOf<TextureViewRenderer?>(null) }
    val currentOnFrameSize by rememberUpdatedState(onFrameSize)
    val currentOnSnapshot by rememberUpdatedState(onSnapshot)

    AndroidView(
        modifier = modifier,
        factory = { context ->
            TextureViewRenderer(context).apply {
                init(eglContext)
                onFrameSizeChanged = { w, h -> currentOnFrameSize(IntSize(w, h)) }
                renderer = this
            }
        },
        update = { it.setMirror(mirror) },
        onRelease = { it.release() }
    )

    val view = renderer
    DisposableEffect(track, view) {
        if (track != null && view != null) track.safeAddSink(view)
        onDispose {
            if (track != null && view != null) {
                track.safeRemoveSink(view)
                view.clearImage()
            }
        }
    }

    val snapshots = onSnapshot != null
    LaunchedEffect(view, snapshots) {
        if (view == null || !snapshots) return@LaunchedEffect
        while (true) {
            delay(FrameSnapshotter.INTERVAL_MS)
            view.requestSnapshot(FrameSnapshotter.WIDTH) { currentOnSnapshot?.invoke(it) }
        }
    }
}

/**
 * Fills the available space, or letterboxes to the frame's aspect ratio when [fit] is set
 * (screen shares must never be cropped).
 *
 * The renderer is always laid out at the frame's aspect ratio, just large enough to cover the
 * bounds, and fit only scales it down. Switching between the two is then a pure GPU animation:
 * the TextureView never has to be resized mid-animation.
 */
@Composable
fun VideoSurface(
    track: VideoTrack?,
    eglContext: EglBase.Context,
    fit: Boolean,
    modifier: Modifier = Modifier,
    mirror: Boolean = false,
    onFrameSize: (IntSize) -> Unit = {},
    onSnapshot: ((Bitmap) -> Unit)? = null
) {
    var frameSize by remember { mutableStateOf(IntSize.Zero) }
    BoxWithConstraints(modifier.clipToBounds(), contentAlignment = Alignment.Center) {
        val boundsWidth = constraints.maxWidth.toFloat()
        val boundsHeight = constraints.maxHeight.toFloat()
        val known = frameSize.width > 0 && frameSize.height > 0 &&
            constraints.hasBoundedWidth && constraints.hasBoundedHeight && boundsWidth > 0f && boundsHeight > 0f

        val coverScale = if (known) max(boundsWidth / frameSize.width, boundsHeight / frameSize.height) else 1f
        val coverWidth = if (known) frameSize.width * coverScale else boundsWidth
        val coverHeight = if (known) frameSize.height * coverScale else boundsHeight
        val fitScale = if (known) min(boundsWidth / coverWidth, boundsHeight / coverHeight) else 1f

        val scale by animateFloatAsState(
            targetValue = if (fit) fitScale else 1f,
            animationSpec = spring(dampingRatio = 0.86f, stiffness = Spring.StiffnessMediumLow),
            label = "fitScale"
        )

        val sizeModifier = if (known) {
            with(LocalDensity.current) { Modifier.requiredSize(coverWidth.toDp(), coverHeight.toDp()) }
        } else {
            Modifier.fillMaxSize()
        }
        VideoRenderer(
            track = track,
            eglContext = eglContext,
            modifier = sizeModifier.graphicsLayer {
                scaleX = scale
                scaleY = scale
            },
            mirror = mirror,
            onFrameSize = {
                frameSize = it
                onFrameSize(it)
            },
            onSnapshot = onSnapshot
        )
    }
}
