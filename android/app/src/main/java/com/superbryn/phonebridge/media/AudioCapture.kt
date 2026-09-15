package com.superbryn.phonebridge.media

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.os.Build
import android.os.Process
import android.util.Log
import com.superbryn.phonebridge.state.MicState
import com.superbryn.phonebridge.transport.FrameSink
import com.superbryn.phonebridge.transport.Protocol
import java.io.IOException
import kotlin.concurrent.thread

private const val TAG = "AudioCapture"

/** 10 ms at 48 kHz. Small enough to keep latency low, large enough to avoid syscall churn. */
private const val FRAMES_PER_CHUNK = 480

/**
 * Microphone -> raw PCM S16LE -> [FrameSink] (FR4).
 *
 * Uncompressed by choice: 48 kHz stereo 16-bit is 1.536 Mb/s, roughly 1.5% of the link
 * budget, and it buys zero encoder latency, no Windows-side decoder dependency, and
 * sample counts that double as an A/V clock. See DECISIONS.md D3.
 */
class AudioCapture(
    private val sampleRate: Int = 48_000,
    private val requestedChannels: Int = 2,
    private val onError: (String) -> Unit
) {
    @Volatile private var running = false
    @Volatile private var muted = false

    private var record: AudioRecord? = null
    private var worker: Thread? = null

    /** Actual channel count after negotiation - the device may refuse stereo. */
    @Volatile var channels: Int = requestedChannels
        private set

    /**
     * Mute sends silence rather than stopping the stream (FR6).
     *
     * This is deliberate: the PC writes into VB-Cable's render endpoint, and a gap in
     * that stream produces an underrun that some clients hear as a click or interpret as
     * a dead device. Continuous zeroed audio keeps the virtual mic alive and the timeline
     * continuous, so unmute is instant.
     */
    fun setMuted(value: Boolean) { muted = value }

    @SuppressLint("MissingPermission") // RECORD_AUDIO is checked by the caller before start()
    fun start(source: String, sink: FrameSink) {
        stop()

        val audioSource = resolveSource(source)
        var channelConfig =
            if (requestedChannels == 2) AudioFormat.CHANNEL_IN_STEREO else AudioFormat.CHANNEL_IN_MONO
        channels = requestedChannels

        var minBuf = AudioRecord.getMinBufferSize(sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT)
        if (minBuf <= 0 && requestedChannels == 2) {
            // Not all devices expose a stereo capture path; fall back rather than fail.
            Log.w(TAG, "stereo capture unsupported, falling back to mono")
            channelConfig = AudioFormat.CHANNEL_IN_MONO
            channels = 1
            minBuf = AudioRecord.getMinBufferSize(sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT)
        }
        if (minBuf <= 0) {
            onError("device reports no usable capture configuration at ${sampleRate}Hz")
            return
        }

        val bytesPerFrame = 2 * channels
        val chunkBytes = FRAMES_PER_CHUNK * bytesPerFrame
        // Four chunks of slack absorbs scheduler jitter without adding meaningful latency.
        val bufferBytes = maxOf(minBuf, chunkBytes * 4)

        val ar = try {
            AudioRecord(audioSource, sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT, bufferBytes)
        } catch (e: Exception) {
            onError("AudioRecord construction failed: ${e.message}")
            return
        }

        if (ar.state != AudioRecord.STATE_INITIALIZED) {
            ar.release()
            onError("AudioRecord failed to initialise (source=$source)")
            return
        }

        record = ar
        running = true

        worker = thread(name = "pb-audio", isDaemon = true) {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            try {
                ar.startRecording()
                sink.writeRaw(Protocol.audioStreamHeader(sampleRate, channels))

                val buf = ByteArray(chunkBytes)
                val silence = ByteArray(chunkBytes)
                var framesSent = 0L

                while (running) {
                    val n = ar.read(buf, 0, chunkBytes)
                    if (n < 0) {
                        if (running) onError("AudioRecord.read returned $n")
                        break
                    }
                    if (n == 0) continue

                    // PTS derived from the sample count, not the wall clock: the sample
                    // count IS the audio timeline, so this cannot drift against the data.
                    val ptsNs = framesSent * 1_000_000_000L / sampleRate
                    framesSent += n / bytesPerFrame

                    val payload = if (muted) silence else buf
                    sink.writeFrame(
                        Protocol.mediaHeader(ptsNs, n, isConfig = false, isKeyframe = false),
                        payload, 0, n
                    )
                }
            } catch (e: IOException) {
                if (running) onError("audio sink closed: ${e.message}")
            } catch (e: Exception) {
                if (running) onError("audio capture failed: ${e.message}")
            } finally {
                runCatching { ar.stop() }
                runCatching { ar.release() }
            }
        }

        Log.i(TAG, "audio started source=$source rate=$sampleRate ch=$channels")
    }

    fun stop() {
        running = false
        worker?.join(500)
        worker = null
        record = null
    }

    /**
     * UNPROCESSED gives the least-coloured signal but is optional in the HAL, so it is
     * checked at runtime rather than assumed. MIUI applies AGC and noise suppression to
     * the VOICE_* sources, which is audible on a desk mic.
     */
    private fun resolveSource(source: String): Int = when (source) {
        MicState.SOURCE_UNPROCESSED ->
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) MediaRecorder.AudioSource.UNPROCESSED
            else MediaRecorder.AudioSource.VOICE_RECOGNITION
        MicState.SOURCE_VOICE_RECOGNITION -> MediaRecorder.AudioSource.VOICE_RECOGNITION
        MicState.SOURCE_CAMCORDER -> MediaRecorder.AudioSource.CAMCORDER
        else -> MediaRecorder.AudioSource.MIC
    }
}
