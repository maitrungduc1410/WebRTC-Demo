package com.example.myapplication.ui.call

import android.content.res.Configuration
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridScope
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi
import androidx.compose.material3.Icon
import androidx.compose.material3.LoadingIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.example.myapplication.R
import com.example.myapplication.call.CallUiState
import com.example.myapplication.call.EffectsStatus
import com.example.myapplication.call.Sharing
import com.example.myapplication.effects.BackgroundKind
import com.example.myapplication.effects.BackgroundOption
import com.example.myapplication.effects.EffectsCatalog
import com.example.myapplication.effects.EffectsSelection
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Picks a background (blur, picture or video) and a face sticker, with a live preview. */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterial3ExpressiveApi::class)
@Composable
fun EffectsSheet(
    ui: CallUiState,
    catalog: EffectsCatalog,
    onSelect: (EffectsSelection) -> Unit,
    onDismiss: () -> Unit,
    preview: @Composable (Modifier) -> Unit
) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val enabled = ui.sharing == Sharing.None
    val selection = ui.effects
    // Sideways there is no room for the preview above the grid.
    val landscape = LocalConfiguration.current.orientation == Configuration.ORIENTATION_LANDSCAPE

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
    ) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 16.dp)) {
            Text(
                "Backgrounds and effects",
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.padding(start = 8.dp)
            )
            Text(
                "Only your camera changes. Others in the call see what your preview shows.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 8.dp, top = 4.dp, bottom = 12.dp)
            )
            if (!landscape) {
                Box(
                    Modifier
                        .fillMaxWidth()
                        .height(220.dp)
                        .clip(RoundedCornerShape(24.dp))
                        .background(Color.Black),
                    contentAlignment = Alignment.Center
                ) {
                    if (enabled && ui.cameraOn) {
                        preview(Modifier.fillMaxSize())
                    } else {
                        Text(
                            if (enabled) "Your camera is off" else "Effects are paused while you present",
                            color = Color.White.copy(alpha = 0.7f),
                            style = MaterialTheme.typography.bodyMedium
                        )
                    }
                    if (ui.effectsStatus == EffectsStatus.Loading) LoadingIndicator(Modifier.size(48.dp))
                }
                Spacer(Modifier.height(8.dp))
            }

            PrimaryTabRow(selectedTabIndex = tab, containerColor = Color.Transparent) {
                Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text("Backgrounds") })
                Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text("Filters") })
            }

            LazyVerticalGrid(
                columns = GridCells.Adaptive(if (tab == 0) 100.dp else 72.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                modifier = Modifier.fillMaxWidth().heightIn(max = 360.dp).padding(top = 12.dp)
            ) {
                if (tab == 0) {
                    backgroundSection("Blur", catalog.backgrounds.filter { it.kind == BackgroundKind.None || it.kind == BackgroundKind.Blur },
                        selection, enabled, onSelect)
                    backgroundSection("Pictures", catalog.backgrounds.filter { it.kind == BackgroundKind.Image },
                        selection, enabled, onSelect)
                    backgroundSection("Videos", catalog.backgrounds.filter { it.kind == BackgroundKind.Video },
                        selection, enabled, onSelect)
                } else {
                    item(key = "no-sticker") {
                        EffectTile(
                            label = "No filter",
                            selected = selection.sticker == null,
                            enabled = enabled,
                            square = true,
                            onClick = { onSelect(selection.copy(sticker = null)) }
                        ) { Icon(painterResource(R.drawable.ic_block), contentDescription = null) }
                    }
                    items(catalog.stickers, key = { it.id }) { sticker ->
                        EffectTile(
                            label = sticker.name,
                            selected = selection.sticker == sticker.id,
                            enabled = enabled,
                            square = true,
                            onClick = { onSelect(selection.copy(sticker = sticker.id)) }
                        ) { AssetImage(sticker.file, ContentScale.Fit, Modifier.fillMaxSize().padding(8.dp)) }
                    }
                }
            }
        }
    }
}

private fun LazyGridScope.backgroundSection(
    title: String,
    options: List<BackgroundOption>,
    selection: EffectsSelection,
    enabled: Boolean,
    onSelect: (EffectsSelection) -> Unit
) {
    if (options.isEmpty()) return
    item(key = "title-$title", span = { GridItemSpan(maxLineSpan) }) {
        Text(
            title,
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(start = 4.dp, top = 4.dp)
        )
    }
    items(options, key = { it.id }) { option ->
        EffectTile(
            label = option.name,
            selected = selection.background == option.id,
            enabled = enabled,
            square = false,
            onClick = { onSelect(selection.copy(background = option.id)) }
        ) {
            val thumbnail = option.thumbnail
            if (thumbnail != null) {
                AssetImage(thumbnail, ContentScale.Crop, Modifier.fillMaxSize())
                if (option.kind == BackgroundKind.Video) {
                    Icon(
                        painterResource(R.drawable.ic_play_arrow),
                        contentDescription = null,
                        tint = Color.White,
                        modifier = Modifier
                            .align(Alignment.BottomEnd)
                            .padding(6.dp)
                            .size(20.dp)
                            .background(Color.Black.copy(alpha = 0.6f), CircleShape)
                            .padding(2.dp)
                    )
                }
            } else {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(
                        painterResource(
                            when (option.id) {
                                "blur-light" -> R.drawable.ic_blur_medium
                                "blur-strong" -> R.drawable.ic_blur_on
                                else -> R.drawable.ic_block
                            }
                        ),
                        contentDescription = null
                    )
                    Text(
                        option.name,
                        style = MaterialTheme.typography.labelSmall,
                        textAlign = TextAlign.Center,
                        modifier = Modifier.padding(top = 2.dp)
                    )
                }
            }
        }
    }
}

@Composable
private fun EffectTile(
    label: String,
    selected: Boolean,
    enabled: Boolean,
    square: Boolean,
    onClick: () -> Unit,
    content: @Composable androidx.compose.foundation.layout.BoxScope.() -> Unit
) {
    val scale by animateFloatAsState(if (selected) 0.94f else 1f, label = "tileScale")
    val shape = RoundedCornerShape(16.dp)
    Box(
        Modifier
            .fillMaxWidth()
            .aspectRatio(if (square) 1f else 16f / 9f)
            .scale(scale)
            .clip(shape)
            .background(MaterialTheme.colorScheme.surfaceContainerHighest)
            .then(
                if (selected) Modifier.border(BorderStroke(3.dp, MaterialTheme.colorScheme.primary), shape) else Modifier
            )
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .semantics {
                contentDescription = label
                this.selected = selected
            }
            .alpha(if (enabled) 1f else 0.4f),
        contentAlignment = Alignment.Center,
        content = content
    )
}

/** A bundled picture, decoded small and off the main thread. */
@Composable
private fun AssetImage(path: String, contentScale: ContentScale, modifier: Modifier) {
    val context = LocalContext.current
    val bitmap by produceState<ImageBitmap?>(null, path) {
        value = withContext(Dispatchers.IO) { EffectsCatalog.decode(context, path, maxSide = 320)?.asImageBitmap() }
    }
    bitmap?.let { Image(it, contentDescription = null, contentScale = contentScale, modifier = modifier) }
}
