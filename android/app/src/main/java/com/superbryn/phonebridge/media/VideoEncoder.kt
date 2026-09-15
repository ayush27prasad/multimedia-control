package com.superbryn.phonebridge.media

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.Surface
import com.superbryn.phonebridge.transport.FrameSink
import com.superbryn.phonebridge.transport.Protocol
import java.io.IOException
import java.nio.ByteBuffer

private const val TAG = "VideoEncoder"
private const val MIME = MediaFormat.MIMETYPE_VIDEO_AVC

data class VideoConfig(
    val width: Int = 1920,
    val height: Int = 1080,
    val fps: Int = 30,
    val bitrateKbps: Int = 25_000,
    /**
     * Seconds between IDR frames. 1-2s keeps mid-stream join latency low without
     * meaningfully hurting bitrate efficiency; combined with prepended headers it means
     * the PC can always start decoding within this window.
     */
    val iFrameIntervalSec: Int = 2
)

/**
 * Camera frames -> H.264 Annex-B -> [FrameSink], using the hardware encoder.
 *
 * Input is a [Surface] (COLOR_FormatSurface), so frames go camera -> encoder without ever
 * being copied through the app's heap.
 */
class VideoEncoder(
    private val config: VideoConfig,
    private val sink: FrameSink,
    private val onError: (String) -> Unit
) {
    private var codec: MediaCodec? = null
    private var inputSurface: Surface? = null

    @Volatile private var running = false
    @Volatile private var headerSent = false

    /** Wall-clock ns at first frame; PTS is emitted relative to it so the stream starts near zero. */
    private var ptsBaseNs = -1L

    /**
     * Creates and starts the encoder.
     * @return the Surface to hand to CameraX. Caller must not release it; [stop] does.
     */
    fun start(): Surface {
        val encoderName = selectHardwareEncoder()
            ?: throw IOException("no H.264 encoder available on this device")

        val format = MediaFormat.createVideoFormat(MIME, config.width, config.height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, config.bitrateKbps * 1000)
            setInteger(MediaFormat.KEY_FRAME_RATE, config.fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, config.iFrameIntervalSec)

            // CBR: on a link with ample fixed headroom, predictable bitrate beats VBR's
            // quality-per-bit. Buffer sizing on the PC side becomes trivial.
            setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                // B-frames require reordering on the decoder, which costs exactly the
                // latency this whole design exists to avoid.
                setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
                // SPS/PPS ride along with every IDR, so the PC can join or recover
                // mid-stream without out-of-band configuration.
                setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                // Ask the encoder not to hold a lookahead queue.
                setInteger(MediaFormat.KEY_LATENCY, 1)
            }
        }

        val mc = MediaCodec.createByCodecName(encoderName)
        mc.setCallback(callback)
        mc.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        inputSurface = mc.createInputSurface()
        mc.start()

        codec = mc
        running = true
        headerSent = false
        ptsBaseNs = -1L

        Log.i(TAG, "encoder $encoderName started: ${config.width}x${config.height}@${config.fps} ${config.bitrateKbps}kbps")
        return inputSurface!!
    }

    fun stop() {
        running = false
        runCatching { codec?.stop() }
        runCatching { codec?.release() }
        runCatching { inputSurface?.release() }
        codec = null
        inputSurface = null
    }

    /**
     * Forces an IDR. Called when the PC (re)attaches the video channel, so it does not
     * have to wait up to [VideoConfig.iFrameIntervalSec] for a decodable frame.
     */
    fun requestKeyframe() {
        runCatching {
            codec?.setParameters(Bundle().apply {
                putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
            })
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(c: MediaCodec, index: Int) {
            // Surface input - the codec pulls frames itself, so this is never called.
        }

        override fun onOutputBufferAvailable(c: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (!running) {
                runCatching { c.releaseOutputBuffer(index, false) }
                return
            }
            try {
                val buf = c.getOutputBuffer(index)
                if (buf != null && info.size > 0) {
                    emit(buf, info)
                }
            } catch (e: Exception) {
                onError("encoder output failed: ${e.message}")
            } finally {
                runCatching { c.releaseOutputBuffer(index, false) }
            }
        }

        override fun onError(c: MediaCodec, e: MediaCodec.CodecException) {
            Log.e(TAG, "codec error (recoverable=${e.isRecoverable} transient=${e.isTransient})", e)
            this@VideoEncoder.onError("encoder error: ${e.message}")
        }

        override fun onOutputFormatChanged(c: MediaCodec, format: MediaFormat) {
            Log.i(TAG, "output format: $format")
        }
    }

    private fun emit(buf: ByteBuffer, info: MediaCodec.BufferInfo) {
        val isConfig = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0
        val isKey = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0

        val nowNs = info.presentationTimeUs * 1_000L
        if (ptsBaseNs < 0) ptsBaseNs = nowNs
        val pts = (nowNs - ptsBaseNs).coerceAtLeast(0)

        buf.position(info.offset)
        buf.limit(info.offset + info.size)
        val payload = ByteArray(info.size)
        buf.get(payload)

        try {
            // The stream header must precede the first packet, and the encoder's real
            // output format is only known now - hence emitting it here rather than at start().
            if (!headerSent) {
                sink.writeRaw(Protocol.videoStreamHeader(config.width, config.height))
                headerSent = true
            }
            sink.writeFrame(
                Protocol.mediaHeader(pts, payload.size, isConfig, isKey),
                payload, 0, payload.size
            )
        } catch (e: IOException) {
            // The PC went away. Not an encoder fault; the reconnect path handles it.
            onError("video sink closed: ${e.message}")
        }
    }

    /**
     * Software H.264 encoders cannot sustain 1080p30 at usable latency on this class of
     * device, so a hardware codec is a hard requirement rather than a preference.
     */
    private fun selectHardwareEncoder(): String? {
        val list = MediaCodecList(MediaCodecList.REGULAR_CODECS)
        var fallback: String? = null

        for (info in list.codecInfos) {
            if (!info.isEncoder) continue
            if (info.supportedTypes.none { it.equals(MIME, ignoreCase = true) }) continue

            val isHardware = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                info.isHardwareAccelerated
            } else {
                // Pre-Q has no isHardwareAccelerated; the OMX.google.* prefix is the
                // documented marker for the software reference encoders.
                !info.name.startsWith("OMX.google.", ignoreCase = true) &&
                    !info.name.startsWith("c2.android.", ignoreCase = true)
            }

            if (isHardware) return info.name
            if (fallback == null) fallback = info.name
        }

        if (fallback != null) Log.w(TAG, "no hardware H.264 encoder; falling back to $fallback - expect poor latency")
        return fallback
    }
}
