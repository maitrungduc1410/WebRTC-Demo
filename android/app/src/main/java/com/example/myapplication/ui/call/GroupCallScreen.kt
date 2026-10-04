package com.example.myapplication.ui.call

import android.graphics.Bitmap
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandHorizontally
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.shrinkHorizontally
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.min
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.example.myapplication.R
import com.example.myapplication.call.CallPerson
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.GroupCallViewModel
import com.example.myapplication.call.GroupTile
import com.example.myapplication.effects.EffectsCatalog
import com.example.myapplication.ui.video.VideoRenderer
import com.example.myapplication.ui.video.VideoSurface
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.emptyFlow

private val TileGap = 8.dp
private val ChromeSpring = spring<Dp>(dampingRatio = 0.85f, stiffness = Spring.StiffnessLow)
private val BadgeEnter = fadeIn() +
    scaleIn(spring(dampingRatio = 0.6f, stiffness = Spring.StiffnessMediumLow)) +
    expandHorizontally(spring(dampingRatio = 0.85f, stiffness = Spring.StiffnessMediumLow))
private val BadgeExit = fadeOut(tween(150)) + scaleOut(tween(150)) + shrinkHorizontally(tween(200))

@Composable
fun GroupCallScreen(
    vm: GroupCallViewModel,
    inPip: Boolean,
    onHangUp: () -> Unit,
    onShareScreen: () -> Unit,
    onShareFromGallery: () -> Unit,
    onShareFromFiles: () -> Unit,
    onEnterPip: (() -> Unit)?
) {
    val ui by vm.ui.collectAsStateWithLifecycle()
    val localTrack by vm.localTrack.collectAsStateWithLifecycle()
    val tiles by vm.tiles.collectAsStateWithLifecycle()
    val activeSpeaker by vm.activeSpeaker.collectAsStateWithLifecycle()
    val audioLevels by vm.audioLevels.collectAsStateWithLifecycle()
    val snapshots by vm.snapshots.collectAsStateWithLifecycle()
    val people by vm.people.collectAsStateWithLifecycle()
    val eglContext = vm.eglBase.eglBaseContext

    val actions = remember(vm) {
        CallActions(
            toggleMic = vm::toggleMic,
            toggleCamera = vm::toggleCamera,
            switchCamera = vm::switchCamera,
            toggleSpeaker = vm::toggleSpeaker,
            toggleRemoteAudio = vm::toggleRemoteAudio,
            toggleRemoteVideo = vm::toggleRemoteVideo,
            setEffects = vm::setEffects,
            stopSharing = vm::stopSharing,
            shareScreen = onShareScreen,
            shareFromGallery = onShareFromGallery,
            shareFromFiles = onShareFromFiles,
            openChat = vm::openChat,
            closeChat = vm::closeChat,
            sendMessage = vm::sendMessage,
            enterPip = onEnterPip,
            hangUp = onHangUp
        )
    }

    GroupCallContent(
        ui = ui,
        tiles = tiles,
        activeSpeaker = activeSpeaker,
        audioLevels = audioLevels,
        snapshots = snapshots,
        people = people,
        events = vm.events,
        actions = actions,
        effectsCatalog = vm.effectsCatalog,
        inPip = inPip,
        micLevel = vm.micLevel,
        tileVideo = { tile, fit, modifier ->
            val id = tile.participant.id
            VideoSurface(
                track = tile.video,
                eglContext = eglContext,
                fit = fit,
                modifier = modifier,
                onSnapshot = { vm.onRemoteSnapshot(id, it) }
            )
        },
        localVideo = { mirror, onFrameSize, modifier ->
            VideoRenderer(
                track = localTrack,
                eglContext = eglContext,
                modifier = modifier,
                mirror = mirror,
                onFrameSize = onFrameSize
            )
        }
    )
}

/**
 * Stateless group call UI: a grid of remote participants behind the same chrome as a 1:1 call.
 * Video is injected through [tileVideo] and [localVideo] so previews can fake it.
 */
@Composable
fun GroupCallContent(
    ui: CallUiState,
    tiles: List<GroupTile>,
    activeSpeaker: String?,
    audioLevels: Map<String, Float>,
    snapshots: Map<String, Bitmap>,
    actions: CallActions,
    /** Everyone in the room, you first, for the people sheet. */
    people: List<CallPerson> = emptyList(),
    tileVideo: @Composable (tile: GroupTile, fit: Boolean, modifier: Modifier) -> Unit,
    localVideo: @Composable (mirror: Boolean, onFrameSize: (IntSize) -> Unit, modifier: Modifier) -> Unit,
    events: Flow<String> = emptyFlow(),
    initialSheet: CallSheet = CallSheet.None,
    effectsCatalog: EffectsCatalog = EffectsCatalog(EffectsCatalog.BUILT_IN, emptyList()),
    inPip: Boolean = false,
    micLevel: StateFlow<Float> = NoMicLevel
) {
    CallLayout(
        ui = ui,
        hasRemote = tiles.isNotEmpty(),
        actions = actions,
        localVideo = localVideo,
        micLevel = micLevel,
        events = events,
        initialSheet = initialSheet,
        effectsCatalog = effectsCatalog,
        inPip = inPip,
        remoteFit = null,
        onToggleFit = {},
        people = people
    ) { onTap, _, chromeVisible ->
        // The tiles grow into the room the top bar and toolbar leave when they hide.
        val gridTop by animateDpAsState(if (chromeVisible) 68.dp else 8.dp, ChromeSpring, label = "gridTop")
        val gridBottom by animateDpAsState(if (chromeVisible) 92.dp else 8.dp, ChromeSpring, label = "gridBottom")
        AnimatedVisibility(
            visible = tiles.isNotEmpty(),
            enter = fadeIn(tween(500)) + scaleIn(initialScale = 1.04f, animationSpec = tween(600)),
            exit = fadeOut(tween(300))
        ) {
            val currentOnTap by rememberUpdatedState(onTap)
            TileGrid(
                count = tiles.size,
                modifier = Modifier
                    .fillMaxSize()
                    // The gaps and margins around the tiles; a tile handles its own taps first.
                    .pointerInput(Unit) { detectTapGestures(onTap = { currentOnTap() }) }
                    .then(
                        if (inPip) Modifier.padding(4.dp)
                        else Modifier
                            .windowInsetsPadding(CallChromeInsets)
                            .padding(start = 8.dp, end = 8.dp, top = gridTop, bottom = gridBottom)
                    )
            ) {
                tiles.forEach { tile ->
                    key(tile.participant.id) {
                        ParticipantTile(
                            tile = tile,
                            speaking = tile.participant.id == activeSpeaker,
                            audioLevel = audioLevels[tile.participant.id] ?: 0f,
                            snapshot = snapshots[tile.participant.id],
                            videoHidden = ui.remoteVideoHidden,
                            compact = inPip,
                            onTap = onTap,
                            video = { fit, modifier -> tileVideo(tile, fit, modifier) }
                        )
                    }
                }
            }
        }
    }
}

/** Columns for [count] tiles: as square as possible, wider rows when the stage is wider than tall. */
internal fun gridColumns(count: Int, landscape: Boolean): Int = when {
    count <= 1 -> 1
    landscape -> when {
        count <= 3 -> count
        count <= 4 -> 2
        count <= 6 -> 3
        else -> 4
    }
    count <= 2 -> 1
    count <= 6 -> 2
    else -> 3
}

/**
 * Equal tiles in rows, the last row centered. One Layout for every tile (instead of a Row per
 * row) so a tile keeps its renderer when someone joins or leaves and it moves to another row.
 */
@Composable
private fun TileGrid(count: Int, modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Layout(content = content, modifier = modifier) { measurables, constraints ->
        val width = constraints.maxWidth
        val height = constraints.maxHeight
        if (count == 0 || measurables.isEmpty()) return@Layout layout(width, height) {}
        val n = measurables.size
        val columns = gridColumns(n, width > height)
        val rows = (n + columns - 1) / columns
        val gap = TileGap.roundToPx()
        val tileWidth = ((width - gap * (columns - 1)) / columns).coerceAtLeast(0)
        val tileHeight = ((height - gap * (rows - 1)) / rows).coerceAtLeast(0)
        val placeables = measurables.map { it.measure(Constraints.fixed(tileWidth, tileHeight)) }
        layout(width, height) {
            placeables.forEachIndexed { index, placeable ->
                val row = index / columns
                val column = index % columns
                val inRow = if (row == rows - 1) n - row * columns else columns
                val rowStart = (width - (inRow * tileWidth + (inRow - 1) * gap)) / 2
                placeable.place(rowStart + column * (tileWidth + gap), row * (tileHeight + gap))
            }
        }
    }
}

@Composable
private fun ParticipantTile(
    tile: GroupTile,
    speaking: Boolean,
    audioLevel: Float,
    snapshot: Bitmap?,
    videoHidden: Boolean,
    compact: Boolean,
    onTap: () -> Unit,
    video: @Composable (fit: Boolean, modifier: Modifier) -> Unit
) {
    val participant = tile.participant
    val state = participant.state
    // Screen shares must never be cropped; double tap switches, until they start or stop sharing.
    var userFit by remember { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(state.screen) { userFit = null }
    val fit = userFit ?: state.screen
    val paused = tile.video == null || !state.video || videoHidden

    val shape = RoundedCornerShape(if (compact) 8.dp else 24.dp)
    val borderColor by animateColorAsState(
        if (speaking) SpeakingGreen else Color.White.copy(alpha = 0.1f),
        label = "speakerBorder"
    )
    val borderWidth by animateDpAsState(if (speaking) 3.dp else 1.dp, label = "speakerBorderWidth")

    BoxWithConstraints(
        Modifier
            .clip(shape)
            .background(Color(0xFF1B1B1F))
            .pointerInput(fit) {
                detectTapGestures(onTap = { onTap() }, onDoubleTap = { userFit = !fit })
            }
    ) {
        if (tile.video != null) video(fit, Modifier.fillMaxSize())
        AnimatedVisibility(visible = paused, enter = fadeIn(tween(350)), exit = fadeOut(tween(350))) {
            PeerPlaceholder(
                snapshot = snapshot,
                seed = "peer-${participant.id}",
                audioLevel = audioLevel,
                avatarSize = (min(maxWidth, maxHeight) * 0.32f).coerceIn(32.dp, 112.dp),
                modifier = Modifier.fillMaxSize()
            )
        }
        if (!compact) {
            Row(
                Modifier
                    .align(Alignment.BottomStart)
                    .padding(8.dp)
                    .background(Color.Black.copy(alpha = 0.55f), CircleShape)
                    .padding(horizontal = 10.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                // The gap lives inside each icon so the label slides over smoothly as one collapses.
                AnimatedVisibility(visible = !state.audio, enter = BadgeEnter, exit = BadgeExit) {
                    Icon(
                        painterResource(R.drawable.ic_mic_off), "Microphone off", tint = MutedRed,
                        modifier = Modifier.padding(end = 6.dp).size(14.dp)
                    )
                }
                AnimatedVisibility(visible = state.screen, enter = BadgeEnter, exit = BadgeExit) {
                    Icon(
                        painterResource(R.drawable.ic_screen_share), "Presenting", tint = Color.White,
                        modifier = Modifier.padding(end = 6.dp).size(14.dp)
                    )
                }
                Text(
                    participant.label,
                    style = MaterialTheme.typography.labelMedium,
                    color = Color.White,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
        // Drawn last so it stays on top of the video.
        Box(Modifier.fillMaxSize().border(borderWidth, borderColor, shape))
    }
}
