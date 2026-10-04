package com.example.myapplication.settings

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SfuServerTest {

    @Test
    fun addsWsAndTheDefaultPortWhenOnlyAHostIsGiven() {
        assertEquals("ws://192.168.1.10:4001", SfuServer.normalize("192.168.1.10"))
    }

    @Test
    fun keepsAnExplicitPort() {
        assertEquals("ws://192.168.1.10:5000", SfuServer.normalize("192.168.1.10:5000"))
    }

    @Test
    fun keepsWssAndTrimsWhitespace() {
        assertEquals("wss://sfu.example.com", SfuServer.normalize("  wss://sfu.example.com  "))
    }

    @Test
    fun mapsHttpSchemesToWebSocketSchemes() {
        assertEquals("ws://10.0.0.2:4001", SfuServer.normalize("http://10.0.0.2:4001"))
        assertEquals("wss://sfu.example.com", SfuServer.normalize("https://sfu.example.com"))
    }

    @Test
    fun dropsThePath() {
        assertEquals("ws://10.0.0.2:4001", SfuServer.normalize("ws://10.0.0.2:4001/ws"))
    }

    @Test
    fun lowercasesTheScheme() {
        assertEquals("ws://localhost:4001", SfuServer.normalize("WS://localhost:4001"))
    }

    @Test
    fun rejectsOtherSchemesAndGarbage() {
        assertNull(SfuServer.normalize(""))
        assertNull(SfuServer.normalize("ftp://localhost:4001"))
        assertNull(SfuServer.normalize("ws://"))
        assertNull(SfuServer.normalize("not a url"))
    }

    @Test
    fun defaultUsesTheSignalingHostOnPort4001() {
        assertEquals("ws://192.168.0.4:4001", SfuServer.fromSignalingAddress("http://192.168.0.4:4000"))
        assertEquals("ws://localhost:4001", SfuServer.fromSignalingAddress("garbage"))
    }

    @Test
    fun webSocketUrlAddsTheEndpointPath() {
        assertEquals("ws://192.168.0.4:4001/ws", SfuServer.webSocketUrl("ws://192.168.0.4:4001"))
    }
}
