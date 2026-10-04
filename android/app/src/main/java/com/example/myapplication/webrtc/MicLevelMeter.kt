package com.example.myapplication.webrtc

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.webrtc.audio.JavaAudioDeviceModule
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.max

/**
 * How loud our own microphone is, for the three bars on the local tile. While a peer connection
 * sends audio, WebRTC's own recording is measured ([onWebRtcSamples]). A 1:1 call waiting for the
 * other person has no peer connection, so nothing records; [idleWanted] then runs a plain
 * [AudioRecord], always released before WebRTC opens the microphone.
 */
class MicLevelMeter {

    companion object {
        private const val TAG = "MicLevelMeter"
        private const val TICK_MS = 50
        /** WebRTC delivers 10 ms of audio per callback. */
        private const val FRAMES_PER_TICK = TICK_MS / 10
        private const val IDLE_SAMPLE_RATE = 16_000
        /** Per tick, so the bars fall back smoothly after a word instead of dropping. */
        private const val DECAY = 0.75f

        /** -50 dBFS (room noise) to -10 dBFS (loud speech) as 0..1, the same as web and iOS. */
        fun levelFromPeak(peak: Float): Float {
            if (peak <= 0f) return 0f
            return ((20f * log10(peak) + 50f) / 40f).coerceIn(0f, 1f)
        }
    }

    private val _level = MutableStateFlow(0f)
    /** 0..1; 0 while muted or while nothing records. */
    val level: StateFlow<Float> = _level.asStateFlow()

    @Volatile
    var muted = false
        set(value) {
            field = value
            if (value) _level.value = 0f
        }

    private val lock = Any()
    private var idleWanted = false
    private var webRtcRecording = false
    private var idleThread: Thread? = null
    @Volatile
    private var idleRunning = false

    // Touched only on WebRTC's recording thread
    private var tickPeak = 0
    private var tickFrames = 0

    /** 1:1 calls: true while no peer connection exists. Turning it off returns once the microphone is free. */
    fun setIdleWanted(wanted: Boolean) = synchronized(lock) {
        idleWanted = wanted
        update()
    }

    fun onWebRtcRecording(recording: Boolean) = synchronized(lock) {
        webRtcRecording = recording
        if (!recording) _level.value = 0f
        update()
    }

    fun onWebRtcSamples(samples: JavaAudioDeviceModule.AudioSamples) {
        if (samples.audioFormat != AudioFormat.ENCODING_PCM_16BIT) return
        val data = samples.data
        var peak = tickPeak
        var i = 0
        while (i + 1 < data.size) {
            val sample = (data[i].toInt() and 0xFF) or (data[i + 1].toInt() shl 8)
            peak = max(peak, abs(sample.toShort().toInt()))
            i += 2
        }
        tickPeak = peak
        if (++tickFrames < FRAMES_PER_TICK) return
        publish(peak / 32768f)
        tickPeak = 0
        tickFrames = 0
    }

    private fun publish(peak: Float) {
        val level = if (muted) 0f else levelFromPeak(peak)
        _level.value = max(level, _level.value * DECAY)
    }

    private fun update() {
        if (idleWanted && !webRtcRecording) startIdle() else stopIdle()
    }

    @SuppressLint("MissingPermission") // The call only starts once RECORD_AUDIO is granted
    private fun startIdle() {
        if (idleThread != null) return
        val minBuffer = AudioRecord.getMinBufferSize(IDLE_SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuffer <= 0) return
        val record = try {
            AudioRecord(
                MediaRecorder.AudioSource.MIC,
                IDLE_SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                max(minBuffer, IDLE_SAMPLE_RATE / 5 * 2)
            )
        } catch (e: Exception) {
            Log.w(TAG, "Can't open the microphone for the level meter", e)
            return
        }
        if (record.state != AudioRecord.STATE_INITIALIZED) {
            record.release()
            return
        }
        idleRunning = true
        idleThread = Thread({
            val buffer = ShortArray(IDLE_SAMPLE_RATE * TICK_MS / 1000)
            try {
                record.startRecording()
                while (idleRunning) {
                    val read = record.read(buffer, 0, buffer.size)
                    if (read <= 0) break
                    var peak = 0
                    for (index in 0 until read) peak = max(peak, abs(buffer[index].toInt()))
                    if (idleRunning) publish(peak / 32768f)
                }
            } catch (e: IllegalStateException) {
                Log.w(TAG, "Level meter recording failed", e)
            } finally {
                runCatching { record.stop() }
                record.release()
            }
        }, "MicLevelMeter").apply { start() }
    }

    private fun stopIdle() {
        val thread = idleThread ?: return
        idleRunning = false
        idleThread = null
        // One read is at most a tick long; WebRTC must not open the microphone before it is free.
        thread.join(TICK_MS * 4L)
        _level.value = 0f
    }
}
