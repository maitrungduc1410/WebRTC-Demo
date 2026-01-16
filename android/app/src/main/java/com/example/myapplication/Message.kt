package com.example.myapplication

data class Message(
    val sender: String,
    val text: String,
    val timestamp: Long = System.currentTimeMillis(),
    val isLocal: Boolean = false
)
