package com.example.myapplication.ui.call

import android.graphics.Bitmap
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.example.myapplication.R
import kotlin.math.absoluteValue

private val AvatarPalettes = listOf(
    Color(0xFF7C4DFF) to Color(0xFF448AFF),
    Color(0xFFFF6E40) to Color(0xFFFF4081),
    Color(0xFF00BFA5) to Color(0xFF00B0FF),
    Color(0xFFFFAB00) to Color(0xFFFF5252),
    Color(0xFF651FFF) to Color(0xFFD500F9),
    Color(0xFF00C853) to Color(0xFF64DD17),
)

/** Stable avatar colors for [seed], so the same participant keeps the same look. */
fun avatarColors(seed: String): Pair<Color, Color> =
    AvatarPalettes[seed.hashCode().absoluteValue % AvatarPalettes.size]

/**
 * Messenger-style "video off" state: the last frame blurred behind a gradient avatar whose ring
 * pulses with the participant's audio level.
 */
@Composable
fun PeerPlaceholder(
    snapshot: Bitmap?,
    seed: String,
    modifier: Modifier = Modifier,
    audioLevel: Float = 0f,
    avatarSize: Dp = 112.dp,
    title: String? = null,
    subtitle: String? = null,
    action: (@Composable () -> Unit)? = null
) {
    val (start, end) = remember(seed) { avatarColors(seed) }
    Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
        AnimatedContent(
            targetState = snapshot,
            transitionSpec = { fadeIn(tween(400)) togetherWith fadeOut(tween(400)) },
            label = "snapshot"
        ) { bitmap ->
            if (bitmap != null) {
                Image(
                    bitmap = remember(bitmap) { bitmap.asImageBitmap() },
                    contentDescription = null,
                    contentScale = ContentScale.Crop,
                    filterQuality = FilterQuality.Low,
                    modifier = Modifier.fillMaxSize().blur(32.dp)
                )
            } else {
                Box(
                    Modifier.fillMaxSize().background(
                        Brush.linearGradient(listOf(start.copy(alpha = 0.55f), Color.Black, end.copy(alpha = 0.45f)))
                    )
                )
            }
        }
        Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.35f)))

        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            SpeakingAvatar(start, end, audioLevel, avatarSize)
            if (title != null) {
                Spacer(Modifier.height(20.dp))
                Text(
                    title,
                    style = MaterialTheme.typography.titleMedium,
                    color = Color.White,
                    textAlign = TextAlign.Center
                )
            }
            if (subtitle != null) {
                Spacer(Modifier.height(4.dp))
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodyMedium,
                    color = Color.White.copy(alpha = 0.75f),
                    textAlign = TextAlign.Center
                )
            }
            if (action != null) {
                Spacer(Modifier.height(16.dp))
                action()
            }
        }
    }
}

@Composable
private fun SpeakingAvatar(start: Color, end: Color, audioLevel: Float, size: Dp) {
    val bouncyLevel by animateFloatAsState(
        targetValue = (audioLevel * 3f).coerceIn(0f, 1f),
        animationSpec = spring(dampingRatio = Spring.DampingRatioMediumBouncy, stiffness = Spring.StiffnessMediumLow),
        label = "level"
    )
    val level = bouncyLevel.coerceAtLeast(0f)
    val breathing by rememberInfiniteTransition(label = "breathing").animateFloat(
        initialValue = 1f,
        targetValue = 1.06f,
        animationSpec = infiniteRepeatable(tween(1600, easing = LinearEasing), RepeatMode.Reverse),
        label = "breathingScale"
    )

    Box(Modifier.size(size * 1.7f), contentAlignment = Alignment.Center) {
        Box(
            Modifier
                .size(size)
                .graphicsLayer {
                    val scale = breathing + level * 0.55f
                    scaleX = scale
                    scaleY = scale
                    alpha = 0.18f + level * 0.5f
                }
                .background(end, CircleShape)
        )
        Box(
            Modifier
                .size(size)
                .graphicsLayer {
                    val scale = 1f + level * 0.25f
                    scaleX = scale
                    scaleY = scale
                    alpha = 0.3f + level * 0.4f
                }
                .background(start, CircleShape)
        )
        Box(
            Modifier
                .size(size)
                .clip(CircleShape)
                .background(Brush.linearGradient(listOf(start, end))),
            contentAlignment = Alignment.Center
        ) {
            Icon(
                painterResource(R.drawable.ic_person),
                contentDescription = null,
                tint = Color.White,
                modifier = Modifier.size(size * 0.62f)
            )
        }
    }
}
