package com.example.myapplication.ui.call

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.example.myapplication.R
import com.example.myapplication.call.CallPerson

/** Everyone in the group call, you first and highlighted, so you know which tile is yours elsewhere. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PeopleSheet(people: List<CallPerson>, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState()) {
        Text(
            "${people.size} in call",
            style = MaterialTheme.typography.titleLarge,
            modifier = Modifier.padding(start = 24.dp, end = 24.dp)
        )
        Text(
            "Others see you by the name on your row.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(start = 24.dp, end = 24.dp, top = 4.dp, bottom = 12.dp)
        )
        LazyColumn(
            Modifier.padding(horizontal = 12.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
            contentPadding = PaddingValues(bottom = 24.dp)
        ) {
            items(people, key = { it.id }) { person -> PersonRow(person, Modifier.animateItem()) }
        }
    }
}

@Composable
private fun PersonRow(person: CallPerson, modifier: Modifier = Modifier) {
    val colors = MaterialTheme.colorScheme
    val shape = RoundedCornerShape(20.dp)
    Row(
        modifier
            .fillMaxWidth()
            .then(
                if (person.isYou) Modifier
                    .background(colors.primaryContainer.copy(alpha = 0.55f), shape)
                    .border(1.dp, colors.primary.copy(alpha = 0.5f), shape)
                else Modifier
            )
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        PersonAvatar(person)
        Text(
            person.label,
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = if (person.isYou) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f)
        )
        if (person.isYou) {
            Surface(shape = CircleShape, color = colors.primary, contentColor = colors.onPrimary) {
                Text(
                    "You",
                    style = MaterialTheme.typography.labelMedium,
                    modifier = Modifier.padding(horizontal = 10.dp, vertical = 3.dp)
                )
            }
        }
        val iconTint = colors.onSurfaceVariant
        if (person.state.screen) {
            Icon(painterResource(R.drawable.ic_screen_share), "Presenting", Modifier.size(18.dp), tint = iconTint)
        } else if (!person.state.video) {
            Icon(painterResource(R.drawable.ic_videocam_off), "Camera off", Modifier.size(18.dp), tint = iconTint)
        }
        if (!person.state.audio) {
            Icon(painterResource(R.drawable.ic_mic_off), "Microphone off", Modifier.size(18.dp), tint = colors.error)
        }
    }
}

/** Same gradient as this person's camera-off placeholder on everyone's screen. */
@Composable
private fun PersonAvatar(person: CallPerson) {
    val (start, end) = remember(person.id) { avatarColors("peer-${person.id}") }
    val ring by animateColorAsState(
        if (person.speaking) SpeakingGreen else Color.Transparent,
        label = "speakingRing"
    )
    Box(
        Modifier
            .size(40.dp)
            .border(2.dp, ring, CircleShape)
            .padding(3.dp)
            .background(Brush.linearGradient(listOf(start, end)), CircleShape),
        contentAlignment = Alignment.Center
    ) {
        Text(
            person.label.take(1),
            style = MaterialTheme.typography.titleSmall,
            color = Color.White
        )
    }
}
