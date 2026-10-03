package com.example.myapplication.ui.call

import android.content.ClipData
import android.content.res.Configuration
import android.graphics.Bitmap
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutLinearInEasing
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.lerp
import androidx.compose.ui.util.lerp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.example.myapplication.R
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.CallViewModel
import com.example.myapplication.call.ChatMessage
import com.example.myapplication.call.ConnectionPhase
import com.example.myapplication.call.Sharing
import com.example.myapplication.effects.EffectsCatalog
import com.example.myapplication.effects.EffectsSelection
import com.example.myapplication.ui.video.VideoRenderer
import com.example.myapplication.ui.video.VideoSurface
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.math.roundToInt

enum class CallSheet { None, More, Share, Chat, Effects }

private const val CONTROLS_AUTO_HIDE_MS = 5_000L
private const val BUBBLE_LIFETIME_MS = 6_000L
private const val MAX_RECENT_MESSAGES = 3
private const val CAMERA_SWITCH_TIMEOUT_MS = 1_500L

/** Everything the call UI can ask for; [CallScreen] wires these to the [CallViewModel]. */
class CallActions(
    val toggleMic: () -> Unit = {},
    val toggleCamera: () -> Unit = {},
    val switchCamera: (onDone: () -> Unit) -> Unit = { it() },
    val toggleSpeaker: () -> Unit = {},
    val toggleRemoteAudio: () -> Unit = {},
    val toggleRemoteVideo: () -> Unit = {},
    val setEffects: (EffectsSelection) -> Unit = {},
    val stopSharing: () -> Unit = {},
    val shareScreen: () -> Unit = {},
    val shareFromGallery: () -> Unit = {},
    val shareFromFiles: () -> Unit = {},
    val openChat: () -> Unit = {},
    val closeChat: () -> Unit = {},
    val sendMessage: (String) -> Unit = {},
    /** Null when the device has no picture-in-picture support. */
    val enterPip: (() -> Unit)? = null,
    val hangUp: () -> Unit = {}
)

@Composable
fun CallScreen(
    vm: CallViewModel,
    inPip: Boolean,
    onHangUp: () -> Unit,
    onShareScreen: () -> Unit,
    onShareFromGallery: () -> Unit,
    onShareFromFiles: () -> Unit,
    onEnterPip: (() -> Unit)?,
    onRemoteFrameSize: (IntSize) -> Unit
) {
    val ui by vm.ui.collectAsStateWithLifecycle()
    val localTrack by vm.localTrack.collectAsStateWithLifecycle()
    val remoteTrack by vm.remoteTrack.collectAsStateWithLifecycle()
    val snapshot by vm.remoteSnapshot.collectAsStateWithLifecycle()
    val audioLevel by vm.remoteAudioLevel.collectAsStateWithLifecycle()
    val eglContext = vm.eglBase.eglBaseContext
    var remoteFrameSize by remember(remoteTrack) { mutableStateOf(IntSize.Zero) }

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

    CallContent(
        ui = ui,
        inPip = inPip,
        hasRemote = remoteTrack != null,
        snapshot = snapshot,
        audioLevel = audioLevel,
        events = vm.events,
        actions = actions,
        effectsCatalog = vm.effectsCatalog,
        remoteFrameSize = remoteFrameSize,
        remoteVideo = { fit, modifier ->
            VideoSurface(
                track = remoteTrack,
                eglContext = eglContext,
                fit = fit,
                modifier = modifier,
                onFrameSize = {
                    remoteFrameSize = it
                    onRemoteFrameSize(it)
                },
                onSnapshot = vm::onRemoteSnapshot
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
 * Stateless call UI. Video is injected through [remoteVideo] and [localVideo] so previews and
 * screenshot tests can stand in for the GL renderers.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CallContent(
    ui: CallUiState,
    hasRemote: Boolean,
    snapshot: Bitmap?,
    audioLevel: Float,
    actions: CallActions,
    remoteVideo: @Composable (fit: Boolean, modifier: Modifier) -> Unit,
    localVideo: @Composable (mirror: Boolean, onFrameSize: (IntSize) -> Unit, modifier: Modifier) -> Unit,
    events: Flow<String> = emptyFlow(),
    initialSheet: CallSheet = CallSheet.None,
    effectsCatalog: EffectsCatalog = EffectsCatalog(EffectsCatalog.BUILT_IN, emptyList()),
    /** The system picture-in-picture window: only the video, no controls. */
    inPip: Boolean = false,
    /** Rotated size of the peer's frames, or zero before the first one. */
    remoteFrameSize: IntSize = IntSize.Zero
) {
    val haptics = LocalHapticFeedback.current

    var controlsVisible by rememberSaveable { mutableStateOf(true) }
    var sheet by rememberSaveable { mutableStateOf(initialSheet) }
    var userFit by remember { mutableStateOf<Boolean?>(null) }
    var interactions by remember { mutableIntStateOf(0) }
    val snackbarHostState = remember { SnackbarHostState() }
    val cameraSwitches = remember { Channel<Unit>(Channel.CONFLATED) }

    fun switchCamera() {
        haptics.performHapticFeedback(HapticFeedbackType.ContextClick)
        interactions++
        cameraSwitches.trySend(Unit)
    }

    // Filling a landscape screen with a portrait peer, or the other way round, would crop most of
    // the picture away. A rotation starts over from the default, as a new screen share does.
    val landscape = LocalConfiguration.current.orientation == Configuration.ORIENTATION_LANDSCAPE
    val crossOrientation = remoteFrameSize.width > 0 && remoteFrameSize.height > 0 &&
        (remoteFrameSize.width > remoteFrameSize.height) != landscape
    val defaultFit = ui.remote.screen || crossOrientation
    val remoteFit = userFit ?: defaultFit

    LaunchedEffect(defaultFit) { userFit = null }
    LaunchedEffect(events) { events.collect { snackbarHostState.showSnackbar(it) } }
    LaunchedEffect(controlsVisible, hasRemote, sheet, interactions) {
        if (controlsVisible && hasRemote && sheet == CallSheet.None) {
            delay(CONTROLS_AUTO_HIDE_MS)
            controlsVisible = false
        }
    }
    LaunchedEffect(sheet) { if (sheet == CallSheet.Chat) actions.openChat() else actions.closeChat() }
    LaunchedEffect(inPip) {
        if (inPip) {
            sheet = CallSheet.None
            controlsVisible = false
        }
    }
    val showChrome = !inPip && (controlsVisible || !hasRemote)

    fun toggle(on: Boolean, action: () -> Unit) {
        haptics.performHapticFeedback(if (on) HapticFeedbackType.ToggleOff else HapticFeedbackType.ToggleOn)
        interactions++
        action()
    }

    BoxWithConstraints(
        Modifier
            .fillMaxSize()
            .background(Color.Black)
    ) {
        // Remote participant
        AnimatedVisibility(
            visible = hasRemote,
            enter = fadeIn(tween(500)) + scaleIn(initialScale = 1.08f, animationSpec = tween(600)),
            exit = fadeOut(tween(300))
        ) {
            Box(
                Modifier
                    .fillMaxSize()
                    .pointerInput(Unit) {
                        detectTapGestures(
                            onTap = { controlsVisible = !controlsVisible },
                            onDoubleTap = { userFit = !remoteFit }
                        )
                    }
            ) {
                remoteVideo(remoteFit, Modifier.fillMaxSize())
                AnimatedVisibility(
                    visible = ui.remoteVideoPaused,
                    enter = fadeIn(tween(350)),
                    exit = fadeOut(tween(350))
                ) {
                    PeerPlaceholder(
                        snapshot = snapshot,
                        seed = "peer-${ui.roomId}",
                        audioLevel = audioLevel,
                        modifier = Modifier.fillMaxSize(),
                        title = if (ui.remoteVideoHidden) "You hid their video" else "Camera is off",
                        subtitle = if (!ui.remote.audio) "Microphone muted" else null,
                        action = if (ui.remoteVideoHidden && !inPip) {
                            {
                                FilledTonalButton(onClick = { toggle(false, actions.toggleRemoteVideo) }) {
                                    Icon(painterResource(R.drawable.ic_visibility), null, Modifier.size(18.dp))
                                    Spacer(Modifier.size(8.dp))
                                    Text("Show video")
                                }
                            }
                        } else null
                    )
                }
            }
        }

        // Hidden rather than removed in the PiP window, so the tile keeps its corner.
        val stageWidth = maxWidth
        val stageHeight = maxHeight
        Box(Modifier.fillMaxSize().graphicsLayer { alpha = if (inPip && hasRemote) 0f else 1f }) {
            LocalTile(
                ui = ui,
                pip = hasRemote,
                maxWidth = stageWidth,
                maxHeight = stageHeight,
                controlsVisible = controlsVisible,
                cameraSwitches = cameraSwitches,
                onTap = { controlsVisible = !controlsVisible },
                onRequestSwitch = ::switchCamera,
                onSwitchCamera = actions.switchCamera,
                video = localVideo
            )
        }

        // Waiting card
        AnimatedVisibility(
            visible = !hasRemote && !inPip,
            enter = fadeIn() + slideInVertically { it / 3 },
            exit = fadeOut() + slideOutVertically { it / 3 },
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(CallChromeInsets.only(WindowInsetsSides.Horizontal + WindowInsetsSides.Bottom))
                .padding(bottom = 112.dp, start = 24.dp, end = 24.dp)
        ) {
            WaitingCard(ui)
        }

        // Top bar
        AnimatedVisibility(
            visible = showChrome,
            enter = fadeIn() + slideInVertically { -it },
            exit = fadeOut() + slideOutVertically { -it },
            modifier = Modifier.align(Alignment.TopCenter)
        ) {
            TopBar(ui, onSwitchCamera = ::switchCamera, onEnterPip = actions.enterPip?.takeIf { hasRemote })
        }

        RecentMessages(
            messages = ui.messages,
            visible = sheet != CallSheet.Chat && !inPip,
            modifier = Modifier
                .align(Alignment.BottomStart)
                .windowInsetsPadding(CallChromeInsets.only(WindowInsetsSides.Horizontal + WindowInsetsSides.Bottom))
                .padding(start = 16.dp, bottom = if (controlsVisible) 108.dp else 24.dp)
        )

        // Bottom controls
        AnimatedVisibility(
            visible = showChrome,
            enter = fadeIn() + slideInVertically { it },
            exit = fadeOut() + slideOutVertically { it },
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(CallChromeInsets.only(WindowInsetsSides.Horizontal + WindowInsetsSides.Bottom))
                .padding(bottom = 16.dp)
        ) {
            CallToolbar(
                ui = ui,
                onToggleMic = { toggle(ui.micOn, actions.toggleMic) },
                onToggleCamera = { toggle(ui.cameraOn, actions.toggleCamera) },
                onShare = {
                    interactions++
                    if (ui.sharing != Sharing.None) actions.stopSharing() else sheet = CallSheet.Share
                },
                onChat = { sheet = CallSheet.Chat },
                onMore = { sheet = CallSheet.More },
                onHangUp = {
                    haptics.performHapticFeedback(HapticFeedbackType.Confirm)
                    actions.hangUp()
                }
            )
        }

        // Kept composed in PiP: the host is what times a snackbar out and lets the next one show.
        SnackbarHost(
            snackbarHostState,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .windowInsetsPadding(CallChromeInsets.only(WindowInsetsSides.Horizontal + WindowInsetsSides.Bottom))
                .padding(bottom = 96.dp)
                .graphicsLayer { alpha = if (inPip) 0f else 1f }
        )
    }

    when (sheet) {
        CallSheet.More -> MoreSheet(
            ui = ui,
            remoteFit = remoteFit,
            onDismiss = { sheet = CallSheet.None },
            onToggleSpeaker = { toggle(ui.speakerOn, actions.toggleSpeaker) },
            onToggleRemoteAudio = { toggle(!ui.remoteAudioMuted, actions.toggleRemoteAudio) },
            onToggleRemoteVideo = { toggle(!ui.remoteVideoHidden, actions.toggleRemoteVideo) },
            onOpenEffects = { sheet = CallSheet.Effects },
            onToggleFit = { userFit = !remoteFit },
            onSwitchCamera = ::switchCamera
        )
        CallSheet.Share -> ShareSheet(
            onDismiss = { sheet = CallSheet.None },
            onShareScreen = { sheet = CallSheet.None; actions.shareScreen() },
            onShareFromGallery = { sheet = CallSheet.None; actions.shareFromGallery() },
            onShareFromFiles = { sheet = CallSheet.None; actions.shareFromFiles() }
        )
        CallSheet.Chat -> ChatSheet(
            ui = ui,
            onSend = actions.sendMessage,
            onDismiss = { sheet = CallSheet.None }
        )
        CallSheet.Effects -> EffectsSheet(
            ui = ui,
            catalog = effectsCatalog,
            onSelect = actions.setEffects,
            onDismiss = { sheet = CallSheet.None },
            preview = { modifier -> localVideo(ui.frontCamera && ui.sharing == Sharing.None, {}, modifier) }
        )
        CallSheet.None -> {}
    }
}

/**
 * What the call chrome must stay clear of. The keyboard is left out: it only opens over the chat
 * sheet, and the controls behind it should not jump. In landscape the cutout and the navigation
 * bar move to the sides.
 */
private val CallChromeInsets: WindowInsets
    @Composable get() = WindowInsets.systemBars.union(WindowInsets.displayCutout)

private enum class Corner { TopStart, TopEnd, BottomStart, BottomEnd }

/**
 * The local preview: full screen while alone, then it shrinks into a draggable picture-in-picture
 * that snaps to the nearest corner.
 */
@Composable
private fun LocalTile(
    ui: CallUiState,
    pip: Boolean,
    maxWidth: Dp,
    maxHeight: Dp,
    controlsVisible: Boolean,
    cameraSwitches: ReceiveChannel<Unit>,
    onTap: () -> Unit,
    onRequestSwitch: () -> Unit,
    onSwitchCamera: (onDone: () -> Unit) -> Unit,
    video: @Composable (mirror: Boolean, onFrameSize: (IntSize) -> Unit, modifier: Modifier) -> Unit
) {
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current
    var frameSize by remember { mutableStateOf(IntSize(3, 4)) }
    var corner by rememberSaveable { mutableStateOf(Corner.TopEnd) }
    val offset = remember { Animatable(Offset.Unspecified, Offset.VectorConverter) }
    val flip = remember { Animatable(0f) }
    val currentUi by rememberUpdatedState(ui)
    val currentOnSwitchCamera by rememberUpdatedState(onSwitchCamera)

    // Turn the tile edge-on, switch while it is invisible, then show the new camera from the other
    // side. Waiting for the switch keeps the old camera's last frames off the back face.
    LaunchedEffect(cameraSwitches) {
        for (request in cameraSwitches) {
            val visible = currentUi.cameraOn && currentUi.sharing == Sharing.None
            if (!visible) {
                currentOnSwitchCamera {}
                continue
            }
            flip.animateTo(90f, tween(170, easing = FastOutLinearInEasing))
            val switched = CompletableDeferred<Unit>()
            currentOnSwitchCamera { switched.complete(Unit) }
            withTimeoutOrNull(CAMERA_SWITCH_TIMEOUT_MS) { switched.await() }
            flip.snapTo(-90f)
            flip.animateTo(0f, spring(dampingRatio = 0.72f, stiffness = Spring.StiffnessMediumLow))
        }
    }

    val progress by animateFloatAsState(
        targetValue = if (pip) 1f else 0f,
        animationSpec = spring(dampingRatio = 0.82f, stiffness = Spring.StiffnessLow),
        label = "pip"
    )

    val aspect = (frameSize.width.toFloat() / frameSize.height).coerceIn(0.4f, 2f)
    val pipWidth = if (aspect >= 1f) 168.dp else 116.dp
    val pipHeight = pipWidth / aspect

    val insets = CallChromeInsets.asPaddingValues()
    val layoutDirection = LocalLayoutDirection.current
    val margin = 16.dp
    val leftLimit = insets.calculateLeftPadding(layoutDirection) + margin
    val rightLimit = insets.calculateRightPadding(layoutDirection) + margin
    val topLimit = insets.calculateTopPadding() + if (controlsVisible) 72.dp else margin
    val bottomLimit = insets.calculateBottomPadding() + if (controlsVisible) 104.dp else margin

    fun cornerOffset(c: Corner): Offset = with(density) {
        val left = leftLimit.toPx()
        val right = (maxWidth - pipWidth - rightLimit).toPx()
        val top = topLimit.toPx()
        val bottom = (maxHeight - pipHeight - bottomLimit).toPx()
        when (c) {
            Corner.TopStart -> Offset(left, top)
            Corner.TopEnd -> Offset(right, top)
            Corner.BottomStart -> Offset(left, bottom)
            Corner.BottomEnd -> Offset(right, bottom)
        }
    }

    LaunchedEffect(corner, pipWidth, pipHeight, leftLimit, rightLimit, topLimit, bottomLimit, maxWidth, maxHeight) {
        val target = cornerOffset(corner)
        if (offset.value == Offset.Unspecified) offset.snapTo(target)
        else offset.animateTo(target, spring(dampingRatio = 0.7f, stiffness = Spring.StiffnessMediumLow))
    }

    val pipOffset = if (offset.value == Offset.Unspecified) cornerOffset(corner) else offset.value
    val width = lerp(maxWidth, pipWidth, progress)
    val height = lerp(maxHeight, pipHeight, progress)
    // The spring overshoots past 0 and 1; corner sizes, elevations and alphas must stay in range.
    val decoration = progress.coerceIn(0f, 1f)
    val shape = RoundedCornerShape(lerp(0.dp, 24.dp, decoration))

    Box(
        Modifier
            .offset { IntOffset(lerp(0f, pipOffset.x, progress).roundToInt(), lerp(0f, pipOffset.y, progress).roundToInt()) }
            .size(width, height)
            .graphicsLayer {
                rotationY = flip.value
                cameraDistance = 16f * density.density
            }
            .shadow(lerp(0.dp, 16.dp, decoration), shape)
            .clip(shape)
            .background(Color(0xFF1B1B1F))
            .border(1.dp, Color.White.copy(alpha = 0.28f * decoration), shape)
            .pointerInput(pip) {
                detectTapGestures(
                    onTap = { onTap() },
                    onDoubleTap = if (pip) {
                        { onRequestSwitch() }
                    } else null
                )
            }
            .then(
                if (pip) Modifier
                    .pointerInput(corner, pipWidth, pipHeight, maxWidth, maxHeight, leftLimit, rightLimit, topLimit, bottomLimit) {
                        detectDragGestures(
                            onDragEnd = {
                                val center = offset.value + Offset(pipWidth.toPx() / 2, pipHeight.toPx() / 2)
                                val startSide = center.x < maxWidth.toPx() / 2
                                val topSide = center.y < maxHeight.toPx() / 2
                                val next = when {
                                    topSide && startSide -> Corner.TopStart
                                    topSide -> Corner.TopEnd
                                    startSide -> Corner.BottomStart
                                    else -> Corner.BottomEnd
                                }
                                haptics.performHapticFeedback(HapticFeedbackType.GestureEnd)
                                if (next == corner) {
                                    scope.launch { offset.animateTo(cornerOffset(next), spring(0.7f, Spring.StiffnessMediumLow)) }
                                }
                                corner = next
                            }
                        ) { change, drag ->
                            change.consume()
                            scope.launch { offset.snapTo(offset.value + drag) }
                        }
                    }
                else Modifier
            )
    ) {
        val presenting = ui.sharing == Sharing.Screen
        if (!presenting) {
            video(ui.frontCamera && ui.sharing == Sharing.None, { frameSize = it }, Modifier.fillMaxSize())
        }
        AnimatedVisibility(visible = !ui.cameraOn || presenting, enter = fadeIn(), exit = fadeOut()) {
            Box(Modifier.fillMaxSize().background(Color(0xFF1B1B1F)), contentAlignment = Alignment.Center) {
                if (presenting) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(painterResource(R.drawable.ic_screen_share), null, tint = Color.White, modifier = Modifier.size(28.dp))
                        Spacer(Modifier.height(6.dp))
                        Text("Presenting", style = MaterialTheme.typography.labelMedium, color = Color.White)
                    }
                } else {
                    PeerPlaceholder(
                        snapshot = null,
                        seed = "you",
                        avatarSize = lerp(112.dp, 48.dp, decoration),
                        title = if (pip) null else "Your camera is off",
                        modifier = Modifier.fillMaxSize()
                    )
                }
            }
        }
        if (!ui.micOn && pip) {
            Box(
                Modifier
                    .align(Alignment.BottomStart)
                    .padding(8.dp)
                    .size(26.dp)
                    .background(Color.Black.copy(alpha = 0.55f), CircleShape),
                contentAlignment = Alignment.Center
            ) {
                Icon(painterResource(R.drawable.ic_mic_off), "Microphone off", tint = Color.White, modifier = Modifier.size(16.dp))
            }
        }
    }
}

@Composable
private fun TopBar(ui: CallUiState, onSwitchCamera: () -> Unit, onEnterPip: (() -> Unit)?) {
    var switches by remember { mutableIntStateOf(0) }
    val iconRotation by animateFloatAsState(
        targetValue = switches * 180f,
        animationSpec = spring(dampingRatio = 0.6f, stiffness = Spring.StiffnessLow),
        label = "switchIcon"
    )
    Row(
        Modifier
            .fillMaxWidth()
            .background(
                Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.55f), Color.Transparent))
            )
            .windowInsetsPadding(CallChromeInsets.only(WindowInsetsSides.Horizontal + WindowInsetsSides.Top))
            .padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        RoomPill(ui)
        if (ui.phase == ConnectionPhase.Connected && !ui.remote.audio) {
            StatusChip(R.drawable.ic_mic_off, "Muted")
        }
        if (ui.phase == ConnectionPhase.Connected && ui.remote.screen) {
            StatusChip(R.drawable.ic_screen_share, "Presenting")
        }
        Spacer(Modifier.weight(1f))
        AnimatedVisibility(
            visible = onEnterPip != null,
            enter = fadeIn() + scaleIn(),
            exit = fadeOut() + scaleOut()
        ) {
            FilledIconButton(
                onClick = { onEnterPip?.invoke() },
                colors = IconButtonDefaults.filledIconButtonColors(
                    containerColor = Color.Black.copy(alpha = 0.45f),
                    contentColor = Color.White
                ),
                modifier = Modifier.size(44.dp)
            ) {
                Icon(
                    painterResource(R.drawable.ic_picture_in_picture),
                    contentDescription = "Picture in picture",
                    modifier = Modifier.size(22.dp)
                )
            }
        }
        AnimatedVisibility(
            visible = ui.sharing == Sharing.None,
            enter = fadeIn() + scaleIn(),
            exit = fadeOut() + scaleOut()
        ) {
            FilledIconButton(
                onClick = {
                    switches++
                    onSwitchCamera()
                },
                colors = IconButtonDefaults.filledIconButtonColors(
                    containerColor = Color.Black.copy(alpha = 0.45f),
                    contentColor = Color.White
                ),
                modifier = Modifier.size(44.dp)
            ) {
                Icon(
                    painterResource(R.drawable.ic_cameraswitch),
                    contentDescription = "Switch camera",
                    modifier = Modifier
                        .size(22.dp)
                        .graphicsLayer { rotationZ = iconRotation }
                )
            }
        }
    }
}

@Composable
private fun RoomPill(ui: CallUiState) {
    val elapsed by produceState(0L, ui.connectedSince) {
        val since = ui.connectedSince ?: return@produceState
        while (true) {
            value = (System.currentTimeMillis() - since) / 1000
            delay(1000)
        }
    }
    val status = when (ui.phase) {
        ConnectionPhase.Waiting -> "Waiting"
        ConnectionPhase.Connecting -> "Connecting…"
        ConnectionPhase.Connected -> "%d:%02d".format(elapsed / 60, elapsed % 60)
    }
    val dotColor = when (ui.phase) {
        ConnectionPhase.Connected -> Color(0xFF4ADE80)
        ConnectionPhase.Connecting -> Color(0xFFFBBF24)
        ConnectionPhase.Waiting -> Color.White.copy(alpha = 0.6f)
    }
    val pulse by rememberInfiniteTransition(label = "dot").animateFloat(
        initialValue = 0.35f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(900), RepeatMode.Reverse),
        label = "dotAlpha"
    )

    Surface(shape = CircleShape, color = Color.Black.copy(alpha = 0.45f), contentColor = Color.White) {
        Row(
            Modifier.padding(horizontal = 14.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Box(
                Modifier
                    .size(8.dp)
                    .graphicsLayer { alpha = if (ui.phase == ConnectionPhase.Connected) 1f else pulse }
                    .background(dotColor, CircleShape)
            )
            if (ui.e2ee) {
                Icon(painterResource(R.drawable.ic_lock), "End-to-end encrypted", Modifier.size(14.dp), tint = Color(0xFF4ADE80))
            }
            Text("Room ${ui.roomId}", style = MaterialTheme.typography.labelLarge)
            Text("·", color = Color.White.copy(alpha = 0.6f))
            Text(status, style = MaterialTheme.typography.labelLarge, color = Color.White.copy(alpha = 0.8f))
        }
    }
}

@Composable
private fun StatusChip(icon: Int, label: String) {
    Surface(shape = CircleShape, color = Color.Black.copy(alpha = 0.45f), contentColor = Color.White) {
        Row(
            Modifier.padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Icon(painterResource(icon), null, Modifier.size(14.dp))
            Text(label, style = MaterialTheme.typography.labelMedium)
        }
    }
}

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun WaitingCard(ui: CallUiState) {
    val clipboard = LocalClipboard.current
    val scope = rememberCoroutineScope()
    var copied by remember { mutableStateOf(false) }
    LaunchedEffect(copied) { if (copied) { delay(1500); copied = false } }

    Surface(
        shape = RoundedCornerShape(32.dp),
        color = Color(0xFF1B1B1F).copy(alpha = 0.85f),
        contentColor = Color.White,
        border = BorderStroke(1.dp, Color.White.copy(alpha = 0.12f)),
        modifier = Modifier.widthIn(max = 420.dp)
    ) {
        Row(
            Modifier.padding(start = 12.dp, end = 12.dp, top = 12.dp, bottom = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            LoadingIndicator(Modifier.size(48.dp))
            Spacer(Modifier.size(12.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    if (ui.phase == ConnectionPhase.Connecting) "Connecting…" else "Waiting for others",
                    style = MaterialTheme.typography.titleMedium
                )
                Text(
                    "Join room ${ui.roomId} from another device",
                    style = MaterialTheme.typography.bodyMedium,
                    color = Color.White.copy(alpha = 0.7f)
                )
            }
            FilledTonalButton(onClick = {
                scope.launch {
                    clipboard.setClipEntry(
                        ClipEntry(ClipData.newPlainText("Room ID", ui.roomId))
                    )
                    copied = true
                }
            }) {
                Icon(painterResource(R.drawable.ic_content_copy), null, Modifier.size(18.dp))
                Spacer(Modifier.size(6.dp))
                Text(if (copied) "Copied" else "Copy")
            }
        }
    }
}

@Composable
private fun RecentMessages(messages: List<ChatMessage>, visible: Boolean, modifier: Modifier = Modifier) {
    val now by produceState(System.currentTimeMillis()) {
        while (true) {
            delay(500)
            value = System.currentTimeMillis()
        }
    }
    // A few bubbles past the visible ones stay composed so they can animate out instead of popping.
    val window = messages.takeLast(MAX_RECENT_MESSAGES + 2)
    val firstShown = window.size - MAX_RECENT_MESSAGES
    Column(modifier.widthIn(max = 280.dp)) {
        window.forEachIndexed { index, message ->
            key(message.id) {
                val state = remember { MutableTransitionState(false) }
                state.targetState = visible && index >= firstShown && now - message.timestamp < BUBBLE_LIFETIME_MS
                AnimatedVisibility(
                    visibleState = state,
                    enter = fadeIn(bubbleSpring()) + slideInHorizontally(bubbleSpring()) { -it / 2 } +
                        expandVertically(bubbleSpring(), expandFrom = Alignment.Top),
                    exit = fadeOut(bubbleSpring()) + shrinkVertically(bubbleSpring(), shrinkTowards = Alignment.Top)
                ) {
                    RecentBubble(message, Modifier.padding(top = 6.dp))
                }
            }
        }
    }
}

private fun <T> bubbleSpring() = spring<T>(dampingRatio = 0.8f, stiffness = Spring.StiffnessMediumLow)

@Composable
private fun RecentBubble(message: ChatMessage, modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier,
        shape = RoundedCornerShape(20.dp),
        color = if (message.isLocal) MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.9f)
        else Color.Black.copy(alpha = 0.55f),
        contentColor = if (message.isLocal) MaterialTheme.colorScheme.onPrimaryContainer else Color.White
    ) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 8.dp)) {
            Text(
                if (message.isLocal) "You" else "Peer",
                style = MaterialTheme.typography.labelSmall,
                color = LocalContentColor.current.copy(alpha = 0.7f)
            )
            Text(message.text, style = MaterialTheme.typography.bodyMedium)
        }
    }
}
