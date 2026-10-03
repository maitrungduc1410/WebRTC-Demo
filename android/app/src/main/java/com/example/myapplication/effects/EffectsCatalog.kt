package com.example.myapplication.effects

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import androidx.core.content.edit
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException

enum class BackgroundKind { None, Blur, Image, Video }

data class BackgroundOption(
    val id: String,
    val name: String,
    val kind: BackgroundKind,
    /** Blur radius as a fraction of the frame width. */
    val blur: Float = 0f,
    /** Asset path, e.g. "effects/backgrounds/beach.jpg". */
    val file: String? = null,
    val thumbnail: String? = null
)

enum class StickerAnchor { Eyes, Nose, Mouth }

/** Sizes and offsets are in units of the distance between the eyes; positive offsetY is up. */
data class StickerOption(
    val id: String,
    val name: String,
    val file: String,
    val anchor: StickerAnchor,
    val width: Float,
    /** Stretches the artwork; null keeps the picture's own aspect ratio. */
    val height: Float? = null,
    val offsetX: Float = 0f,
    val offsetY: Float = 0f
)

data class EffectsSelection(val background: String = NONE, val sticker: String? = null) {
    companion object {
        const val NONE = "none"
    }
}

/**
 * The backgrounds and stickers bundled from the repository's effects folder, which the build
 * copies to assets/effects (see app/build.gradle.kts). The web and iOS apps read the same files.
 */
class EffectsCatalog(val backgrounds: List<BackgroundOption>, val stickers: List<StickerOption>) {

    fun background(id: String): BackgroundOption = backgrounds.firstOrNull { it.id == id } ?: backgrounds.first()

    fun sticker(id: String?): StickerOption? = id?.let { wanted -> stickers.firstOrNull { it.id == wanted } }

    fun hasEffects(selection: EffectsSelection): Boolean =
        background(selection.background).kind != BackgroundKind.None || sticker(selection.sticker) != null

    /** Drops choices that are no longer bundled. */
    fun sanitize(selection: EffectsSelection) =
        EffectsSelection(background(selection.background).id, sticker(selection.sticker)?.id)

    companion object {
        private const val TAG = "EffectsCatalog"
        const val ROOT = "effects"

        val BUILT_IN = listOf(
            BackgroundOption(EffectsSelection.NONE, "None", BackgroundKind.None),
            BackgroundOption("blur-light", "Slight blur", BackgroundKind.Blur, blur = 0.008f),
            BackgroundOption("blur-strong", "Blur", BackgroundKind.Blur, blur = 0.02f)
        )

        fun load(context: Context): EffectsCatalog {
            val assets = context.assets
            val files = buildSet {
                for (dir in listOf("backgrounds", "thumbnails", "stickers")) {
                    assets.list("$ROOT/$dir")?.forEach { add("$dir/$it") }
                }
            }
            fun read(name: String): JSONObject? = try {
                assets.open("$ROOT/$name").bufferedReader().use { JSONObject(it.readText()) }
            } catch (e: Exception) {
                Log.w(TAG, "Couldn't read $name", e)
                null
            }
            return EffectsCatalog(
                parseBackgrounds(read("backgrounds.json"), files),
                parseStickers(read("stickers.json"), files)
            )
        }

        private fun parseBackgrounds(json: JSONObject?, files: Set<String>): List<BackgroundOption> {
            val ids = BUILT_IN.mapTo(mutableSetOf()) { it.id }
            val assets = json?.optJSONArray("backgrounds").objects().mapNotNull { entry ->
                val id = entry.optString("id")
                val file = entry.optString("file")
                val kind = when (entry.optString("type")) {
                    "image" -> BackgroundKind.Image
                    "video" -> BackgroundKind.Video
                    else -> null
                }
                if (id.isEmpty() || kind == null || file !in files || !ids.add(id)) return@mapNotNull null
                val thumbnail = entry.optString("thumbnail").takeIf { it in files }
                BackgroundOption(
                    id = id,
                    name = entry.optString("name").ifEmpty { id },
                    kind = kind,
                    file = "$ROOT/$file",
                    thumbnail = (thumbnail ?: file.takeIf { kind == BackgroundKind.Image })?.let { "$ROOT/$it" }
                )
            }
            return BUILT_IN + assets
        }

        private fun parseStickers(json: JSONObject?, files: Set<String>): List<StickerOption> =
            json?.optJSONArray("stickers").objects().mapNotNull { entry ->
                val id = entry.optString("id")
                val file = entry.optString("file")
                if (id.isEmpty() || file !in files) return@mapNotNull null
                StickerOption(
                    id = id,
                    name = entry.optString("name").ifEmpty { id },
                    file = "$ROOT/$file",
                    anchor = when (entry.optString("anchor")) {
                        "nose" -> StickerAnchor.Nose
                        "mouth" -> StickerAnchor.Mouth
                        else -> StickerAnchor.Eyes
                    },
                    width = entry.optDouble("width", 1.0).toFloat(),
                    height = entry.optDouble("height").takeUnless { it.isNaN() }?.toFloat(),
                    offsetX = entry.optDouble("offsetX", 0.0).toFloat(),
                    offsetY = entry.optDouble("offsetY", 0.0).toFloat()
                )
            }

        private fun JSONArray?.objects(): List<JSONObject> =
            if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }

        /** Decodes an asset no larger than [maxSide] on its longer side; null if it can't be read. */
        fun decode(context: Context, path: String, maxSide: Int): Bitmap? = try {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            context.assets.open(path).use { BitmapFactory.decodeStream(it, null, bounds) }
            var sample = 1
            while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= maxSide) sample *= 2
            val options = BitmapFactory.Options().apply { inSampleSize = sample }
            context.assets.open(path).use { BitmapFactory.decodeStream(it, null, options) }
        } catch (e: IOException) {
            Log.w(TAG, "Couldn't decode $path", e)
            null
        }
    }
}

/** The last background and sticker the user picked, kept across calls. */
object EffectsStore {
    private const val PREFERENCES = "settings"
    private const val KEY_BACKGROUND = "effects_background"
    private const val KEY_STICKER = "effects_sticker"

    fun load(context: Context, catalog: EffectsCatalog): EffectsSelection {
        val prefs = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
        return catalog.sanitize(
            EffectsSelection(
                prefs.getString(KEY_BACKGROUND, null) ?: EffectsSelection.NONE,
                prefs.getString(KEY_STICKER, null)
            )
        )
    }

    fun save(context: Context, selection: EffectsSelection) {
        context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE).edit {
            putString(KEY_BACKGROUND, selection.background)
            if (selection.sticker == null) remove(KEY_STICKER) else putString(KEY_STICKER, selection.sticker)
        }
    }
}
