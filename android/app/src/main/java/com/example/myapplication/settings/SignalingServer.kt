package com.example.myapplication.settings

import android.content.Context
import androidx.core.content.edit
import com.example.myapplication.R
import java.net.URI
import java.net.URISyntaxException

/** The signaling server address chosen in the lobby, kept across launches. */
object SignalingServer {
    private const val PREFERENCES = "settings"
    private const val KEY_ADDRESS = "signaling_server"
    private val SCHEME = Regex("^[a-zA-Z][a-zA-Z0-9+.-]*://")

    fun defaultAddress(context: Context): String = context.getString(R.string.serverAddress)

    fun load(context: Context): String =
        preferences(context).getString(KEY_ADDRESS, null)?.let(::normalize) ?: defaultAddress(context)

    fun save(context: Context, address: String) {
        preferences(context).edit {
            if (address == defaultAddress(context)) remove(KEY_ADDRESS) else putString(KEY_ADDRESS, address)
        }
    }

    /**
     * Accepts "192.168.1.10:4000" as well as a full URL and returns "scheme://host[:port]", or null
     * when it is not an http(s) address. A path is dropped because Socket.IO would take it for a namespace.
     */
    fun normalize(input: String): String? {
        val text = input.trim()
        if (text.isEmpty()) return null
        val uri = try {
            URI(if (SCHEME.containsMatchIn(text)) text else "http://$text")
        } catch (_: URISyntaxException) {
            return null
        }
        val scheme = uri.scheme?.lowercase()
        if (scheme != "http" && scheme != "https") return null
        val host = uri.host?.takeIf { it.isNotEmpty() } ?: return null
        return if (uri.port == -1) "$scheme://$host" else "$scheme://$host:${uri.port}"
    }

    private fun preferences(context: Context) = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
}
