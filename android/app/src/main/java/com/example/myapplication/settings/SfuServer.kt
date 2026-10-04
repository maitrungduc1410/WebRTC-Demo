package com.example.myapplication.settings

import android.content.Context
import androidx.core.content.edit
import java.net.URI
import java.net.URISyntaxException

/** The group call (SFU) server address chosen in the lobby, kept across launches. */
object SfuServer {
    private const val PREFERENCES = "settings"
    private const val KEY_ADDRESS = "sfu_server"
    const val DEFAULT_PORT = 4001
    private val SCHEME = Regex("^[a-zA-Z][a-zA-Z0-9+.-]*://")

    /** Port 4001 on the host of the default signaling server. */
    fun defaultAddress(context: Context): String =
        fromSignalingAddress(SignalingServer.defaultAddress(context))

    fun load(context: Context): String =
        preferences(context).getString(KEY_ADDRESS, null)?.let(::normalize) ?: defaultAddress(context)

    fun save(context: Context, address: String) {
        preferences(context).edit {
            if (address == defaultAddress(context)) remove(KEY_ADDRESS) else putString(KEY_ADDRESS, address)
        }
    }

    /** The signaling WebSocket endpoint of a normalized address. */
    fun webSocketUrl(address: String): String = "$address/ws"

    /** "ws://host:4001" for the host of [signalingAddress], or "ws://localhost:4001" when it has none. */
    fun fromSignalingAddress(signalingAddress: String): String {
        val host = try {
            URI(signalingAddress.trim()).host
        } catch (_: URISyntaxException) {
            null
        }
        return "ws://${host?.takeIf { it.isNotEmpty() } ?: "localhost"}:$DEFAULT_PORT"
    }

    /**
     * Accepts "192.168.1.10" or "192.168.1.10:4001" as well as a full ws(s) or http(s) URL and
     * returns "ws[s]://host[:port]", or null when it is not such an address. Without a scheme the
     * port defaults to [DEFAULT_PORT]; http(s) maps to ws(s), and the path is dropped.
     */
    fun normalize(input: String): String? {
        val text = input.trim()
        if (text.isEmpty()) return null
        val hasScheme = SCHEME.containsMatchIn(text)
        val uri = try {
            URI(if (hasScheme) text else "ws://$text")
        } catch (_: URISyntaxException) {
            return null
        }
        val scheme = when (uri.scheme?.lowercase()) {
            "ws", "http" -> "ws"
            "wss", "https" -> "wss"
            else -> return null
        }
        val host = uri.host?.takeIf { it.isNotEmpty() } ?: return null
        val port = if (uri.port == -1 && !hasScheme) DEFAULT_PORT else uri.port
        return if (port == -1) "$scheme://$host" else "$scheme://$host:$port"
    }

    private fun preferences(context: Context) = context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)
}
