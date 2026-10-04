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
import com.example.myapplication.call.CallPerson
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.ChatMessage
import com.example.myapplication.call.ChatStatus
import com.example.myapplication.call.ConnectionPhase
import com.example.myapplication.call.GroupTile
import com.example.myapplication.ui.theme.AppTheme
import com.example.myapplication.webrtc.MediaState
import com.example.myapplication.webrtc.sfu.Participant
import com.example.myapplication.webrtc.sfu.participantLabel

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

private val GroupTiles = listOf(
    GroupTile(Participant("a1b2c3", "Web", MediaState())),
    GroupTile(Participant("x9y8z7", "iOS · iPhone", MediaState(audio = false))),
    GroupTile(Participant("k5l6m7", "Android · Pixel 8", MediaState(video = false))),
    GroupTile(Participant("p0q1r2", "Web", MediaState(screen = true)))
)

private val GroupTileColors = listOf(
    listOf(Color(0xFF2E3A59), Color(0xFF6B4E71)),
    listOf(Color(0xFF3D5A40), Color(0xFF9CAF88)),
    listOf(Color(0xFF5B3A29), Color(0xFFC08552)),
    listOf(Color(0xFF1F2A44), Color(0xFF4A6FA5))
)

private fun previewPeople(tiles: List<GroupTile>) =
    listOf(CallPerson("f4e3d2", participantLabel("f4e3d2", "Android · Pixel 9"), MediaState(), isYou = true)) +
        tiles.mapIndexed { i, tile -> CallPerson(tile.participant.id, tile.participant.label, tile.participant.state, isYou = false, speaking = i == 0) }

@Composable
private fun PreviewGroupCall(
    tiles: List<GroupTile>,
    ui: CallUiState = ConnectedState.copy(group = true),
    sheet: CallSheet = CallSheet.None
) {
    AppTheme(darkTheme = true) {
        GroupCallContent(
            ui = ui,
            tiles = tiles,
            activeSpeaker = tiles.firstOrNull()?.participant?.id,
            audioLevels = mapOf("k5l6m7" to 0.3f),
            snapshots = emptyMap(),
            actions = CallActions(),
            people = previewPeople(tiles),
            initialSheet = sheet,
            tileVideo = { tile, _, modifier ->
                FakeVideo(GroupTileColors[tiles.indexOf(tile) % GroupTileColors.size], modifier)
            },
            localVideo = { _, _, modifier -> FakeVideo(LocalColors, modifier) }
        )
    }
}

@Preview(name = "Group call", device = "spec:width=411dp,height=891dp")
@Composable
fun GroupCallPreview() = PreviewGroupCall(GroupTiles)

@Preview(name = "Group call, people", device = "spec:width=411dp,height=891dp")
@Composable
fun GroupCallPeoplePreview() = PreviewGroupCall(GroupTiles, sheet = CallSheet.People)

@Preview(name = "Group call, three", device = "spec:width=411dp,height=891dp")
@Composable
fun GroupCallThreePreview() = PreviewGroupCall(GroupTiles.take(3))

@Preview(name = "Group call landscape", device = "spec:width=891dp,height=411dp,orientation=landscape")
@Composable
fun GroupCallLandscapePreview() = PreviewGroupCall(GroupTiles)

@Preview(name = "Group call, alone", device = "spec:width=411dp,height=891dp")
@Composable
fun GroupCallAlonePreview() = PreviewGroupCall(
    emptyList(),
    CallUiState(roomId = "482913", e2ee = true, group = true, chat = ChatStatus.Open)
)
