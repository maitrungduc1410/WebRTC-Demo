package com.example.myapplication.settings

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SignalingServerTest {

    @Test
    fun addsHttpWhenTheSchemeIsMissing() {
        assertEquals("http://192.168.1.10:4000", SignalingServer.normalize("192.168.1.10:4000"))
    }

    @Test
    fun keepsHttpsAndTrimsWhitespace() {
        assertEquals("https://signal.example.com", SignalingServer.normalize("  https://signal.example.com  "))
    }

    @Test
    fun dropsThePath() {
        assertEquals("http://10.0.0.2:4000", SignalingServer.normalize("http://10.0.0.2:4000/socket/"))
    }

    @Test
    fun webSocketUrlMapsTheSchemeAndAddsTheEndpointPath() {
        assertEquals("ws://192.168.1.10:4000/ws", SignalingServer.webSocketUrl("http://192.168.1.10:4000"))
        assertEquals("wss://signal.example.com/ws", SignalingServer.webSocketUrl("https://signal.example.com"))
    }

    @Test
    fun lowercasesTheScheme() {
        assertEquals("http://localhost:4000", SignalingServer.normalize("HTTP://localhost:4000"))
    }

    @Test
    fun rejectsOtherSchemesAndGarbage() {
        assertNull(SignalingServer.normalize(""))
        assertNull(SignalingServer.normalize("ws://localhost:4000"))
        assertNull(SignalingServer.normalize("http://"))
        assertNull(SignalingServer.normalize("not a url"))
    }
}
