package com.example.myapplication.ui.call

import android.content.res.Configuration
import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import com.example.myapplication.R
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.ChatMessage
import com.example.myapplication.call.ChatStatus
import com.example.myapplication.call.ConnectionPhase
import java.text.DateFormat
import java.util.Date

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun ChatSheet(ui: CallUiState, onSend: (String) -> Unit, onDismiss: () -> Unit) {
    var draft by rememberSaveable { mutableStateOf("") }
    val listState = rememberLazyListState()
    val ready = ui.chat == ChatStatus.Open

    LaunchedEffect(ui.messages.size) {
        if (ui.messages.isNotEmpty()) listState.animateScrollToItem(ui.messages.lastIndex)
    }

    fun send() {
        if (!ready || draft.isBlank()) return
        onSend(draft)
        draft = ""
    }

    // Sideways there is too little height to leave any of it to the call behind the sheet.
    val landscape = LocalConfiguration.current.orientation == Configuration.ORIENTATION_LANDSCAPE

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .fillMaxHeight(if (landscape) 1f else 0.7f)
                .imePadding()
                .padding(horizontal = 16.dp)
                .padding(bottom = 16.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(start = 8.dp, bottom = 12.dp)) {
                Text("Chat", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                AnimatedContent(targetState = ui.chat to ui.phase, label = "chatStatus") { (chat, phase) ->
                    when {
                        chat == ChatStatus.Open -> Text(
                            "Data channel open",
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.primary
                        )
                        phase != ConnectionPhase.Connected -> Text(
                            "Waiting for a peer",
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        else -> Row(verticalAlignment = Alignment.CenterVertically) {
                            LoadingIndicator(Modifier.size(24.dp))
                            Spacer(Modifier.size(6.dp))
                            Text("Opening…", style = MaterialTheme.typography.labelMedium)
                        }
                    }
                }
            }

            Box(Modifier.weight(1f).fillMaxWidth()) {
                if (ui.messages.isEmpty()) {
                    Text(
                        "Messages travel peer-to-peer over an RTCDataChannel.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.align(Alignment.Center)
                    )
                }
                LazyColumn(
                    state = listState,
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    items(ui.messages, key = { it.id }) { message ->
                        MessageBubble(message, Modifier.animateItem())
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 12.dp)) {
                OutlinedTextField(
                    value = draft,
                    onValueChange = { draft = it },
                    placeholder = { Text(if (ready) "Message" else "Chat isn't connected yet") },
                    enabled = ready,
                    shape = RoundedCornerShape(28.dp),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                    keyboardActions = KeyboardActions(onSend = { send() }),
                    maxLines = 4,
                    modifier = Modifier.weight(1f)
                )
                Spacer(Modifier.size(8.dp))
                FilledIconButton(
                    onClick = { send() },
                    enabled = ready && draft.isNotBlank(),
                    shapes = IconButtonDefaults.shapes(),
                    modifier = Modifier.size(IconButtonDefaults.mediumContainerSize())
                ) {
                    Icon(painterResource(R.drawable.ic_send), contentDescription = "Send")
                }
            }
        }
    }
}

@Composable
private fun MessageBubble(message: ChatMessage, modifier: Modifier = Modifier) {
    val colors = MaterialTheme.colorScheme
    val time = DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(message.timestamp))
    Row(
        modifier.fillMaxWidth(),
        horizontalArrangement = if (message.isLocal) Arrangement.End else Arrangement.Start
    ) {
        Surface(
            shape = if (message.isLocal) RoundedCornerShape(20.dp, 20.dp, 6.dp, 20.dp)
            else RoundedCornerShape(20.dp, 20.dp, 20.dp, 6.dp),
            color = if (message.isLocal) colors.primary else colors.surfaceContainerHighest,
            contentColor = if (message.isLocal) colors.onPrimary else colors.onSurface,
            modifier = Modifier.widthIn(max = 300.dp)
        ) {
            Column(Modifier.padding(horizontal = 14.dp, vertical = 8.dp)) {
                Text(message.text, style = MaterialTheme.typography.bodyLarge)
                Text(
                    time,
                    style = MaterialTheme.typography.labelSmall,
                    color = androidx.compose.material3.LocalContentColor.current.copy(alpha = 0.7f),
                    modifier = Modifier.align(Alignment.End)
                )
            }
        }
    }
}
