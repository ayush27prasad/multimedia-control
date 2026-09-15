package com.superbryn.phonebridge.transport

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/**
 * Wire constants and frame encoding. The authoritative specification is
 * docs/PROTOCOL.md; this file must not drift from it.
 *
 * All multi-byte integers are big-endian.
 */
object Protocol {

    const val VERSION = 1

    /** Abstract-namespace socket name. Same name in both tunnel modes; only the role differs. */
    const val SOCKET_NAME = "phonebridge"

    const val CHANNEL_VIDEO = 1
    const val CHANNEL_AUDIO = 2
    const val CHANNEL_CONTROL = 3

    val MAGIC_CLIENT = "PBRG".toByteArray(Charsets.US_ASCII)
    val MAGIC_OK = "PBOK".toByteArray(Charsets.US_ASCII)
    val MAGIC_NO = "PBNO".toByteArray(Charsets.US_ASCII)

    const val STATUS_OK = 0
    const val STATUS_BAD_MAGIC = 1
    const val STATUS_VERSION_MISMATCH = 2
    const val STATUS_CHANNEL_BUSY = 3

    const val HANDSHAKE_LEN = 10
    const val REPLY_LEN = 6
    const val FRAME_HEADER_LEN = 12

    // Frame header, byte 0.
    private const val FLAG_MEDIA = 0x80
    private const val FLAG_CONFIG = 0x40
    private const val FLAG_KEYFRAME = 0x20

    /** Max control line, matching the PC's guard. Anything larger is a desync, not a message. */
    const val MAX_CONTROL_LINE = 256 * 1024

    // ---- handshake ---------------------------------------------------------

    fun buildHandshake(channel: Int): ByteArray {
        val b = ByteArray(HANDSHAKE_LEN)
        MAGIC_CLIENT.copyInto(b, 0)
        b[4] = VERSION.toByte()
        b[5] = channel.toByte()
        // bytes 6..9 reserved, already zero
        return b
    }

    /**
     * Reads and validates the PC's reply.
     * @throws HandshakeException with a status the caller can branch on.
     */
    fun readReply(input: InputStream) {
        val r = ByteArray(REPLY_LEN)
        readFully(input, r, 0, REPLY_LEN)

        val ok = r.regionMatches(0, MAGIC_OK)
        val no = r.regionMatches(0, MAGIC_NO)
        if (!ok && !no) throw HandshakeException(STATUS_BAD_MAGIC, "desktop sent an unrecognised magic")

        val status = r[5].toInt() and 0xFF
        if (!ok || status != STATUS_OK) {
            throw HandshakeException(
                status,
                when (status) {
                    STATUS_VERSION_MISMATCH ->
                        "protocol mismatch: phone speaks v$VERSION, desktop speaks v${r[4].toInt() and 0xFF}"
                    STATUS_CHANNEL_BUSY -> "channel already open on the desktop"
                    STATUS_BAD_MAGIC -> "desktop rejected the handshake magic"
                    else -> "desktop rejected the handshake (status $status)"
                }
            )
        }
    }

    // ---- stream headers ----------------------------------------------------

    fun videoStreamHeader(width: Int, height: Int): ByteArray {
        val b = ByteArray(16)
        "PBV1".toByteArray(Charsets.US_ASCII).copyInto(b, 0)
        "h264".toByteArray(Charsets.US_ASCII).copyInto(b, 4)
        putInt(b, 8, width)
        putInt(b, 12, height)
        return b
    }

    fun audioStreamHeader(sampleRate: Int, channels: Int): ByteArray {
        val b = ByteArray(20)
        "PBA1".toByteArray(Charsets.US_ASCII).copyInto(b, 0)
        putInt(b, 4, 1)          // 1 = PCM_S16LE
        putInt(b, 8, sampleRate)
        putInt(b, 12, channels)
        putInt(b, 16, 16)        // bits per sample
        return b
    }

    // ---- frame headers -----------------------------------------------------

    /**
     * Media packet header. [ptsNanos] is truncated to 61 bits, which overflows after
     * ~73 years of uptime and is therefore not worth guarding.
     */
    fun mediaHeader(ptsNanos: Long, length: Int, isConfig: Boolean, isKeyframe: Boolean): ByteArray {
        val b = ByteArray(FRAME_HEADER_LEN)
        val pts = ptsNanos and 0x1FFF_FFFF_FFFF_FFFFL

        var flags = FLAG_MEDIA
        if (isConfig) flags = flags or FLAG_CONFIG
        if (isKeyframe) flags = flags or FLAG_KEYFRAME

        // Top 5 bits of the 61-bit PTS share byte 0 with the flags.
        b[0] = (flags or ((pts ushr 56).toInt() and 0x1F)).toByte()
        for (i in 1..7) {
            b[i] = (pts ushr (8 * (7 - i))).toByte()
        }
        putInt(b, 8, length)
        return b
    }

    /** Session packet: video geometry changed. Byte 0 bit 7 clear distinguishes it. */
    fun sessionHeader(width: Int, height: Int): ByteArray {
        val b = ByteArray(FRAME_HEADER_LEN)
        putInt(b, 4, width)
        putInt(b, 8, height)
        return b
    }

    // ---- helpers -----------------------------------------------------------

    fun putInt(b: ByteArray, off: Int, v: Int) {
        b[off] = (v ushr 24).toByte()
        b[off + 1] = (v ushr 16).toByte()
        b[off + 2] = (v ushr 8).toByte()
        b[off + 3] = v.toByte()
    }

    fun readFully(input: InputStream, buf: ByteArray, offset: Int, length: Int) {
        var read = 0
        while (read < length) {
            val n = input.read(buf, offset + read, length - read)
            if (n < 0) throw EOFException("stream closed after $read of $length bytes")
            read += n
        }
    }

    private fun ByteArray.regionMatches(offset: Int, other: ByteArray): Boolean {
        if (size < offset + other.size) return false
        for (i in other.indices) if (this[offset + i] != other[i]) return false
        return true
    }
}

class HandshakeException(val status: Int, message: String) : Exception(message) {
    /** A version mismatch will never resolve by retrying; the desktop build must change. */
    val isTerminal: Boolean get() = status == Protocol.STATUS_VERSION_MISMATCH
}

/**
 * Serialises writes to one channel. MediaCodec's callback thread and the control
 * coroutine can both reach a sink, and a torn frame header desynchronises the stream
 * permanently — there is no resync marker in the framing.
 */
class FrameSink(private val out: OutputStream) {
    private val lock = Any()

    fun writeRaw(bytes: ByteArray) = synchronized(lock) {
        out.write(bytes)
        out.flush()
    }

    fun writeFrame(header: ByteArray, payload: ByteArray, payloadOffset: Int, payloadLength: Int) =
        synchronized(lock) {
            out.write(header)
            out.write(payload, payloadOffset, payloadLength)
            out.flush()
        }
}
