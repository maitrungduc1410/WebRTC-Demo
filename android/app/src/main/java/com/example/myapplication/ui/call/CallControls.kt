package com.example.myapplication.ui.call

import androidx.annotation.DrawableRes
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledIconToggleButton
import androidx.compose.material3.FloatingToolbarDefaults
import androidx.compose.material3.HorizontalFloatingToolbar
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.example.myapplication.R
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.ConnectionPhase
import com.example.myapplication.call.EffectsStatus
import com.example.myapplication.call.Sharing

private val HangUpRed = Color(0xFFE5484D)

@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun CallToolbar(
    ui: CallUiState,
    onToggleMic: () -> Unit,
    onToggleCamera: () -> Unit,
    onShare: () -> Unit,
    onChat: () -> Unit,
    onMore: () -> Unit,
    onHangUp: () -> Unit
) {
    HorizontalFloatingToolbar(
        expanded = true,
        floatingActionButton = {
            FloatingToolbarDefaults.VibrantFloatingActionButton(
                onClick = onHangUp,
                containerColor = HangUpRed,
                contentColor = Color.White
            ) {
                Icon(painterResource(R.drawable.ic_call_end), contentDescription = "Leave call")
            }
        },
        colors = FloatingToolbarDefaults.standardFloatingToolbarColors(
            toolbarContainerColor = MaterialTheme.colorScheme.surfaceContainerHigh
        )
    ) {
        ToolbarToggle(
            checked = !ui.micOn,
            onClick = onToggleMic,
            icon = if (ui.micOn) R.drawable.ic_mic else R.drawable.ic_mic_off,
            description = if (ui.micOn) "Mute microphone" else "Unmute microphone"
        )
        ToolbarToggle(
            checked = !ui.cameraOn,
            onClick = onToggleCamera,
            icon = if (ui.cameraOn) R.drawable.ic_videocam else R.drawable.ic_videocam_off,
            description = if (ui.cameraOn) "Turn camera off" else "Turn camera on"
        )
        ToolbarToggle(
            checked = ui.sharing != Sharing.None,
            onClick = onShare,
            icon = if (ui.sharing == Sharing.None) R.drawable.ic_screen_share else R.drawable.ic_stop_screen_share,
            description = if (ui.sharing == Sharing.None) "Share" else "Stop sharing",
            checkedColor = MaterialTheme.colorScheme.primary,
            checkedContentColor = MaterialTheme.colorScheme.onPrimary
        )
        BadgedBox(badge = {
            if (ui.unread > 0) Badge { Text(ui.unread.coerceAtMost(99).toString()) }
        }) {
            IconButton(onClick = onChat, modifier = Modifier.size(48.dp)) {
                Icon(painterResource(R.drawable.ic_chat), contentDescription = "Chat")
            }
        }
        IconButton(onClick = onMore, modifier = Modifier.size(48.dp)) {
            Icon(painterResource(R.drawable.ic_more_horiz), contentDescription = "More options")
        }
    }
}

/** Morphs from a circle to a squircle and fills in when [checked] (e.g. muted). */
@OptIn(ExperimentalMaterial3ExpressiveApi::class)
@Composable
private fun ToolbarToggle(
    checked: Boolean,
    onClick: () -> Unit,
    @DrawableRes icon: Int,
    description: String,
    checkedColor: Color = MaterialTheme.colorScheme.inverseSurface,
    checkedContentColor: Color = MaterialTheme.colorScheme.inverseOnSurface
) {
    FilledIconToggleButton(
        checked = checked,
        onCheckedChange = { onClick() },
        shapes = IconButtonDefaults.toggleableShapes(
            shape = CircleShape,
            pressedShape = RoundedCornerShape(12.dp),
            checkedShape = RoundedCornerShape(16.dp)
        ),
        colors = IconButtonDefaults.filledIconToggleButtonColors(
            containerColor = Color.Transparent,
            contentColor = MaterialTheme.colorScheme.onSurface,
            checkedContainerColor = checkedColor,
            checkedContentColor = checkedContentColor
        ),
        modifier = Modifier.size(48.dp)
    ) {
        AnimatedContent(
            targetState = icon,
            transitionSpec = { scaleIn(initialScale = 0.6f) togetherWith scaleOut(targetScale = 0.6f) },
            label = "toggleIcon"
        ) { res ->
            Icon(painterResource(res), contentDescription = description)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MoreSheet(
    ui: CallUiState,
    remoteFit: Boolean,
    onDismiss: () -> Unit,
    onToggleSpeaker: () -> Unit,
    onToggleRemoteAudio: () -> Unit,
    onToggleRemoteVideo: () -> Unit,
    onOpenEffects: () -> Unit,
    onToggleFit: () -> Unit,
    onSwitchCamera: () -> Unit
) {
    val hasPeer = ui.phase == ConnectionPhase.Connected
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ) {
        // Three rows of tiles are taller than a phone held sideways.
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text(
                "Call options",
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.padding(start = 8.dp, bottom = 16.dp)
            )
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                OptionTile(
                    icon = if (ui.speakerOn) R.drawable.ic_volume_up else R.drawable.ic_phone_in_talk,
                    label = if (ui.speakerOn) "Speaker" else "Earpiece",
                    checked = ui.speakerOn,
                    onClick = onToggleSpeaker,
                    modifier = Modifier.weight(1f)
                )
                OptionTile(
                    icon = R.drawable.ic_auto_awesome,
                    label = "Backgrounds and effects",
                    checked = ui.effectsStatus == EffectsStatus.On,
                    enabled = ui.sharing == Sharing.None,
                    onClick = onOpenEffects,
                    modifier = Modifier.weight(1f)
                )
            }
            Spacer(Modifier.height(12.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                OptionTile(
                    icon = if (ui.remoteAudioMuted) R.drawable.ic_volume_off else R.drawable.ic_volume_up,
                    label = if (ui.remoteAudioMuted) "Peer audio muted" else "Mute peer audio",
                    checked = ui.remoteAudioMuted,
                    enabled = hasPeer,
                    onClick = onToggleRemoteAudio,
                    modifier = Modifier.weight(1f)
                )
                OptionTile(
                    icon = if (ui.remoteVideoHidden) R.drawable.ic_visibility_off else R.drawable.ic_visibility,
                    label = if (ui.remoteVideoHidden) "Peer video hidden" else "Hide peer video",
                    checked = ui.remoteVideoHidden,
                    enabled = hasPeer,
                    onClick = onToggleRemoteVideo,
                    modifier = Modifier.weight(1f)
                )
            }
            Spacer(Modifier.height(12.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                OptionTile(
                    icon = if (remoteFit) R.drawable.ic_fit_screen else R.drawable.ic_crop_free,
                    label = if (remoteFit) "Fit to screen" else "Fill screen",
                    checked = remoteFit,
                    enabled = hasPeer,
                    onClick = onToggleFit,
                    modifier = Modifier.weight(1f)
                )
                OptionTile(
                    icon = R.drawable.ic_cameraswitch,
                    label = if (ui.frontCamera) "Front camera" else "Back camera",
                    checked = false,
                    enabled = ui.sharing == Sharing.None && ui.cameraOn,
                    onClick = onSwitchCamera,
                    modifier = Modifier.weight(1f)
                )
            }
            Text(
                "Muting or hiding the peer only affects this device. Double-tap the video to switch fit and fill.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 8.dp, top = 16.dp, end = 8.dp)
            )
        }
    }
}

/** A large tile whose corners and color animate with the toggle state. */
@Composable
private fun OptionTile(
    @DrawableRes icon: Int,
    label: String,
    checked: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true
) {
    val colors = MaterialTheme.colorScheme
    val container by animateColorAsState(if (checked) colors.primaryContainer else colors.surfaceContainerHighest, label = "tileColor")
    val content by animateColorAsState(if (checked) colors.onPrimaryContainer else colors.onSurface, label = "tileContent")
    val radius by animateDpAsState(if (checked) 32.dp else 20.dp, label = "tileRadius")
    Surface(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(radius),
        color = container,
        contentColor = content,
        modifier = modifier
            .height(96.dp)
            .alpha(if (enabled) 1f else 0.38f)
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.SpaceBetween) {
            Icon(painterResource(icon), contentDescription = null)
            Text(label, style = MaterialTheme.typography.labelLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShareSheet(
    onDismiss: () -> Unit,
    onShareScreen: () -> Unit,
    onShareFromGallery: () -> Unit,
    onShareFromFiles: () -> Unit
) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(
                "Share",
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.padding(start = 8.dp, bottom = 8.dp)
            )
            ShareRow(R.drawable.ic_screen_share, "Screen", "Everything on your screen, even outside the app", onShareScreen)
            ShareRow(R.drawable.ic_video_library, "Video from gallery", "Stream a video as your camera", onShareFromGallery)
            ShareRow(R.drawable.ic_folder_open, "Video file", "Pick an MP4 or WebM from your files", onShareFromFiles)
        }
    }
}

@Composable
private fun ShareRow(@DrawableRes icon: Int, title: String, subtitle: String, onClick: () -> Unit) {
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(24.dp),
        color = MaterialTheme.colorScheme.surfaceContainerHighest,
        modifier = Modifier.fillMaxWidth()
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Surface(shape = RoundedCornerShape(14.dp), color = MaterialTheme.colorScheme.primaryContainer) {
                Icon(
                    painterResource(icon),
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                    modifier = Modifier.padding(10.dp)
                )
            }
            Spacer(Modifier.size(16.dp))
            Column {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
