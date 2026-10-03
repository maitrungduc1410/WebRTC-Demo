package com.example.myapplication.ui.lobby

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialShapes
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.toShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.tooling.preview.Preview
import com.example.myapplication.R
import com.example.myapplication.settings.SignalingServer
import com.example.myapplication.ui.theme.AppTheme
import kotlinx.coroutines.launch
import kotlin.random.Random

private fun randomRoomId(): String = Random.nextInt(100000, 1_000_000).toString()

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun LobbyScreen(
    serverAddress: String,
    defaultServerAddress: String,
    onServerAddressChange: (String) -> Unit,
    onJoin: (roomId: String, e2ee: Boolean) -> Unit
) {
    var roomId by rememberSaveable { mutableStateOf(randomRoomId()) }
    var e2ee by rememberSaveable { mutableStateOf(false) }
    var editingServer by rememberSaveable { mutableStateOf(false) }
    val haptics = LocalHapticFeedback.current
    val scope = rememberCoroutineScope()
    val shuffleRotation = remember { Animatable(0f) }
    val colors = MaterialTheme.colorScheme

    fun join() {
        if (roomId.isBlank()) return
        haptics.performHapticFeedback(HapticFeedbackType.Confirm)
        onJoin(roomId.trim(), e2ee)
    }

    val header: @Composable () -> Unit = {
        HeroBadge()
        Spacer(Modifier.height(28.dp))
        Text(
            "WebRTC Demo",
            style = MaterialTheme.typography.displaySmall,
            fontWeight = FontWeight.SemiBold,
            textAlign = TextAlign.Center
        )
        Spacer(Modifier.height(8.dp))
        Text(
            "Peer-to-peer video calls, chat and screen sharing",
            style = MaterialTheme.typography.bodyLarge,
            color = colors.onSurfaceVariant,
            textAlign = TextAlign.Center
        )
    }
    val form: @Composable () -> Unit = {
        Surface(
            shape = RoundedCornerShape(32.dp),
            color = colors.surfaceContainerHigh,
            modifier = Modifier.widthIn(max = 480.dp).fillMaxWidth()
        ) {
            Column(Modifier.padding(20.dp)) {
                OutlinedTextField(
                    value = roomId,
                    onValueChange = { value -> roomId = value.filter { it.isLetterOrDigit() }.take(32) },
                    label = { Text("Room ID") },
                    singleLine = true,
                    shape = RoundedCornerShape(20.dp),
                    textStyle = MaterialTheme.typography.titleLarge,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number, imeAction = ImeAction.Go),
                    keyboardActions = KeyboardActions(onGo = { join() }),
                    trailingIcon = {
                        IconButton(onClick = {
                            haptics.performHapticFeedback(HapticFeedbackType.SegmentTick)
                            roomId = randomRoomId()
                            scope.launch {
                                shuffleRotation.animateTo(shuffleRotation.value + 360f, spring(dampingRatio = 0.6f, stiffness = 300f))
                            }
                        }) {
                            Icon(
                                painterResource(R.drawable.ic_shuffle),
                                contentDescription = "Random room",
                                modifier = Modifier.graphicsLayer { rotationZ = shuffleRotation.value }
                            )
                        }
                    },
                    modifier = Modifier.fillMaxWidth()
                )

                Spacer(Modifier.height(12.dp))

                val e2eeContainer by animateColorAsState(
                    if (e2ee) colors.primaryContainer else colors.surfaceContainerHighest,
                    label = "e2eeContainer"
                )
                Surface(
                    onClick = { e2ee = !e2ee },
                    shape = RoundedCornerShape(24.dp),
                    color = e2eeContainer,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(painterResource(R.drawable.ic_lock), contentDescription = null)
                        Spacer(Modifier.size(16.dp))
                        Column(Modifier.weight(1f)) {
                            Text("End-to-end encryption", style = MaterialTheme.typography.titleMedium)
                            Text(
                                "Encrypt every frame with a key only the two of you share",
                                style = MaterialTheme.typography.bodySmall,
                                color = colors.onSurfaceVariant
                            )
                        }
                        Spacer(Modifier.size(12.dp))
                        Switch(
                            checked = e2ee,
                            onCheckedChange = { e2ee = it },
                            thumbContent = if (e2ee) {
                                { Icon(painterResource(R.drawable.ic_lock), null, Modifier.size(SwitchDefaults.IconSize)) }
                            } else null
                        )
                    }
                }

                Spacer(Modifier.height(20.dp))

                val height = ButtonDefaults.MediumContainerHeight
                Button(
                    onClick = { join() },
                    enabled = roomId.isNotBlank(),
                    shapes = ButtonDefaults.shapes(),
                    contentPadding = ButtonDefaults.contentPaddingFor(height, hasEndIcon = true),
                    modifier = Modifier.fillMaxWidth().height(height)
                ) {
                    Text("Join room", style = ButtonDefaults.textStyleFor(height))
                    Spacer(Modifier.size(ButtonDefaults.iconSpacingFor(height)))
                    Icon(
                        painterResource(R.drawable.ic_arrow_forward),
                        contentDescription = null,
                        modifier = Modifier.size(ButtonDefaults.iconSizeFor(height))
                    )
                }
            }
    }

    Spacer(Modifier.height(16.dp))
    Surface(
        onClick = { editingServer = true },
        shape = RoundedCornerShape(50),
        color = Color.Transparent,
        contentColor = colors.onSurfaceVariant
    ) {
        Row(
            Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "Signaling server · $serverAddress",
                style = MaterialTheme.typography.labelMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f, fill = false)
            )
            Spacer(Modifier.size(6.dp))
            Icon(painterResource(R.drawable.ic_edit), contentDescription = "Change signaling server", Modifier.size(14.dp))
        }
    }
    }

    if (editingServer) {
        ServerAddressDialog(
            current = serverAddress,
            default = defaultServerAddress,
            onDismiss = { editingServer = false },
            onSave = { address ->
                onServerAddressChange(address)
                editingServer = false
            }
        )
    }

    Surface(color = colors.surface, modifier = Modifier.fillMaxSize()) {
        Backdrop()

        BoxWithConstraints(Modifier.fillMaxSize().safeDrawingPadding().imePadding()) {
            val viewport = maxHeight
            // A phone on its side has room for two panes but not for the stacked layout.
            if (maxWidth > maxHeight && maxWidth >= 560.dp) {
                Row(
                    Modifier.fillMaxSize().padding(horizontal = 32.dp),
                    horizontalArrangement = Arrangement.spacedBy(40.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    LobbyPane(viewport, Modifier.weight(1f)) { header() }
                    LobbyPane(viewport, Modifier.weight(1f)) { form() }
                }
            } else {
                LobbyPane(viewport, Modifier.fillMaxSize().padding(horizontal = 24.dp)) {
                    header()
                    Spacer(Modifier.height(32.dp))
                    form()
                }
            }
        }
    }
}

/** Edits the signaling server address; [onSave] gets it normalized to "scheme://host[:port]". */
@Composable
private fun ServerAddressDialog(current: String, default: String, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    var text by rememberSaveable { mutableStateOf(current) }
    var invalid by rememberSaveable { mutableStateOf(false) }

    fun save() {
        val address = SignalingServer.normalize(text)
        if (address == null) invalid = true else onSave(address)
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(painterResource(R.drawable.ic_edit), contentDescription = null) },
        title = { Text("Signaling server") },
        text = {
            Column {
                Text(
                    "The address printed when you start the signaling server. It is saved on this device.",
                    style = MaterialTheme.typography.bodyMedium
                )
                Spacer(Modifier.height(16.dp))
                OutlinedTextField(
                    value = text,
                    onValueChange = {
                        text = it
                        invalid = false
                    },
                    label = { Text("Address") },
                    placeholder = { Text("http://192.168.1.10:4000") },
                    isError = invalid,
                    supportingText = if (invalid) {
                        { Text("Use an address like http://192.168.1.10:4000") }
                    } else null,
                    singleLine = true,
                    shape = RoundedCornerShape(16.dp),
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        imeAction = ImeAction.Done,
                        autoCorrectEnabled = false
                    ),
                    keyboardActions = KeyboardActions(onDone = { save() }),
                    modifier = Modifier.fillMaxWidth()
                )
                if (text.trim() != default) {
                    TextButton(
                        onClick = {
                            text = default
                            invalid = false
                        },
                        modifier = Modifier.padding(top = 4.dp)
                    ) {
                        Text("Use default · $default", maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = { save() }) { Text("Save") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } }
    )
}

/** Scrolls when its content is taller than [viewport], and centers it otherwise. */
@Composable
private fun LobbyPane(viewport: Dp, modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier
            .verticalScroll(rememberScrollState())
            .heightIn(min = viewport)
            .padding(vertical = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
        content = content
    )
}

/** A slowly turning cookie shape behind the app glyph. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun HeroBadge() {
    val colors = MaterialTheme.colorScheme
    val transition = rememberInfiniteTransition(label = "hero")
    val rotation by transition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(tween(24_000, easing = LinearEasing)),
        label = "heroRotation"
    )
    val pulse by transition.animateFloat(
        initialValue = 0.94f,
        targetValue = 1.06f,
        animationSpec = infiniteRepeatable(tween(2_400), RepeatMode.Reverse),
        label = "heroPulse"
    )
    val burst = MaterialShapes.SoftBurst.toShape()
    val cookie = MaterialShapes.Cookie9Sided.toShape()

    Box(Modifier.size(156.dp), contentAlignment = Alignment.Center) {
        ShapeBlob(burst, 156.dp, colors.tertiaryContainer, Modifier.graphicsLayer {
            rotationZ = -rotation
            scaleX = pulse
            scaleY = pulse
        })
        ShapeBlob(cookie, 112.dp, colors.primary, Modifier.graphicsLayer { rotationZ = rotation * 1.5f })
        Icon(
            painterResource(R.drawable.ic_videocam),
            contentDescription = null,
            tint = colors.onPrimary,
            modifier = Modifier.size(48.dp)
        )
    }
}

/** Oversized, translucent Material shapes drifting behind the content instead of a photo. */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun Backdrop() {
    val colors = MaterialTheme.colorScheme
    val rotation by rememberInfiniteTransition(label = "backdrop").animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(tween(90_000, easing = LinearEasing)),
        label = "backdropRotation"
    )
    val clover = MaterialShapes.Clover8Leaf.toShape()
    val sunny = MaterialShapes.Sunny.toShape()

    Box(Modifier.fillMaxSize()) {
        ShapeBlob(clover, 360.dp, colors.primaryContainer.copy(alpha = 0.55f), Modifier
            .align(Alignment.TopEnd)
            .offset(x = 140.dp, y = (-110).dp)
            .graphicsLayer { rotationZ = rotation })
        ShapeBlob(sunny, 300.dp, colors.tertiaryContainer.copy(alpha = 0.45f), Modifier
            .align(Alignment.BottomStart)
            .offset(x = (-130).dp, y = 90.dp)
            .graphicsLayer { rotationZ = -rotation })
    }
}

@Preview(name = "Lobby", device = "spec:width=411dp,height=891dp")
@Composable
fun LobbyPreview() = AppTheme(darkTheme = false) { LobbyScreen("http://192.168.0.4:4000", "http://192.168.0.4:4000", {}) { _, _ -> } }

@Preview(name = "Lobby dark", device = "spec:width=411dp,height=891dp")
@Composable
fun LobbyDarkPreview() = AppTheme(darkTheme = true) { LobbyScreen("http://192.168.0.4:4000", "http://192.168.0.4:4000", {}) { _, _ -> } }

@Preview(name = "Lobby landscape", device = "spec:width=891dp,height=411dp,orientation=landscape")
@Composable
fun LobbyLandscapePreview() = AppTheme(darkTheme = false) { LobbyScreen("http://192.168.0.4:4000", "http://192.168.0.4:4000", {}) { _, _ -> } }

@Preview(name = "Signaling server dialog")
@Composable
fun ServerAddressDialogPreview() = AppTheme(darkTheme = false) {
    ServerAddressDialog("http://10.0.0.5:4000", "http://192.168.0.4:4000", {}, {})
}

@Composable
private fun ShapeBlob(shape: Shape, size: Dp, color: Color, modifier: Modifier = Modifier) {
    Box(modifier.size(size).background(color, shape))
}
