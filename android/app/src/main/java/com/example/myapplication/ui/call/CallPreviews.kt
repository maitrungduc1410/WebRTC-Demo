package com.example.myapplication.ui.call

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import com.example.myapplication.R
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.ChatMessage
import com.example.myapplication.call.ChatStatus
import com.example.myapplication.call.ConnectionPhase
import com.example.myapplication.ui.theme.AppTheme
import com.example.myapplication.webrtc.MediaState

/** Stands in for a camera frame in previews. */
@Composable
private fun FakeVideo(colors: List<Color>, modifier: Modifier = Modifier) {
    Box(modifier.background(Brush.linearGradient(colors)), contentAlignment = Alignment.Center) {
        Icon(
            painterResource(R.drawable.ic_person),
            contentDescription = null,
            tint = Color.White.copy(alpha = 0.35f),
            modifier = Modifier.size(160.dp)
        )
    }
}

private val RemoteColors = listOf(Color(0xFF2E3A59), Color(0xFF6B4E71), Color(0xFFC08552))
private val LocalColors = listOf(Color(0xFF1F4E5F), Color(0xFF3C8D93))

private val ConnectedState = CallUiState(
    roomId = "482913",
    e2ee = true,
    phase = ConnectionPhase.Connected,
    connectedSince = System.currentTimeMillis() - 154_000,
    chat = ChatStatus.Open,
    messages = listOf(
        ChatMessage(0, "Can you see my screen?", isLocal = false, timestamp = Long.MAX_VALUE / 2),
        ChatMessage(1, "Yes, loud and clear 👋", isLocal = true, timestamp = Long.MAX_VALUE / 2)
    )
)

@Composable
private fun PreviewCall(ui: CallUiState, hasRemote: Boolean, audioLevel: Float = 0f, sheet: CallSheet = CallSheet.None) {
    AppTheme(darkTheme = true) {
        CallContent(
            ui = ui,
            hasRemote = hasRemote,
            snapshot = null,
            audioLevel = audioLevel,
            actions = CallActions(),
            remoteVideo = { _, modifier -> FakeVideo(RemoteColors, modifier) },
            localVideo = { _, _, modifier -> FakeVideo(LocalColors, modifier) },
            initialSheet = sheet
        )
    }
}

@Preview(name = "Waiting", device = "spec:width=411dp,height=891dp", showSystemUi = false)
@Composable
fun CallWaitingPreview() = PreviewCall(CallUiState(roomId = "482913", e2ee = false), hasRemote = false)

@Preview(name = "Connected", device = "spec:width=411dp,height=891dp")
@Composable
fun CallConnectedPreview() = PreviewCall(ConnectedState, hasRemote = true)

@Preview(name = "Connected landscape", device = "spec:width=891dp,height=411dp,orientation=landscape")
@Composable
fun CallConnectedLandscapePreview() = PreviewCall(ConnectedState, hasRemote = true)

@Preview(name = "Remote camera off", device = "spec:width=411dp,height=891dp")
@Composable
fun CallRemoteVideoOffPreview() = PreviewCall(
    ConnectedState.copy(remote = MediaState(audio = true, video = false), micOn = false, messages = emptyList()),
    hasRemote = true,
    audioLevel = 0.25f
)

@Preview(name = "Peer video hidden", device = "spec:width=411dp,height=891dp")
@Composable
fun CallRemoteVideoHiddenPreview() = PreviewCall(
    ConnectedState.copy(remoteVideoHidden = true, messages = emptyList()),
    hasRemote = true
)

@Preview(name = "More options", device = "spec:width=411dp,height=891dp")
@Composable
fun CallMoreSheetPreview() = PreviewCall(
    ConnectedState.copy(speakerOn = true, remoteVideoHidden = true, messages = emptyList()),
    hasRemote = true,
    sheet = CallSheet.More
)
