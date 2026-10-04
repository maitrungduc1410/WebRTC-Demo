package com.example.myapplication.webrtc

import android.os.Handler
import android.os.Looper
import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONException
import org.json.JSONObject
import java.util.concurrent.TimeUnit

/**
 * A signaling socket of either server (1:1 signaling server or SFU): plain JSON text frames, each
 * with a "type", over a WebSocket. Every callback is delivered on the main thread, in the order
 * the frames arrived. There is no reconnect; the call ends with the socket.
 */
class SignalingSocket(private val url: String, private val events: Events) {

    interface Events {
        fun onOpen()
        fun onMessage(type: String, message: JSONObject)
        /** Called once, unless [close] was called first. [opened] is false when the connection never succeeded. */
        fun onClosed(opened: Boolean)
    }

    companion object {
        private const val TAG = "SignalingSocket"

        // The servers ping; the read timeout must not end an idle but healthy socket.
        private val client = OkHttpClient.Builder()
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()
    }

    private val mainHandler = Handler(Looper.getMainLooper())
    private var socket: WebSocket? = null
    @Volatile
    private var closed = false
    @Volatile
    private var opened = false

    fun connect() {
        val request = Request.Builder().url(url).build()
        socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                opened = true
                onMain { events.onOpen() }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                val message = try {
                    JSONObject(text)
                } catch (e: JSONException) {
                    Log.w(TAG, "Ignoring a frame that is not JSON")
                    return
                }
                val type = message.optString("type")
                onMain { events.onMessage(type, message) }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                Log.d(TAG, "Socket closed: $code $reason")
                onMain { events.onClosed(opened) }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Log.w(TAG, "Socket failure", t)
                onMain { events.onClosed(opened) }
            }
        })
    }

    /** Thread-safe; drops the message once the socket is closed. */
    fun send(message: JSONObject) {
        if (closed) return
        socket?.send(message.toString())
    }

    fun send(type: String, build: JSONObject.() -> Unit = {}) {
        send(JSONObject().put("type", type).apply(build))
    }

    /** Sends `leave` and closes the socket; no more events are delivered. */
    fun close() {
        if (closed) return
        send("leave")
        closed = true
        socket?.close(1000, "leave")
        socket = null
    }

    private fun onMain(block: () -> Unit) {
        mainHandler.post { if (!closed) block() }
    }
}
