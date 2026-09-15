package com.superbryn.phonebridge.transport

import android.net.LocalServerSocket
import android.net.LocalSocket
import android.net.LocalSocketAddress
import android.util.Log
import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream
import kotlin.random.Random

private const val TAG = "BridgeTransport"

/**
 * Which side owns the listening socket. See docs/PROTOCOL.md §1 - the roles are the
 * opposite of the intuitive reading under REVERSE.
 */
enum class TunnelMode {
    /**
     * `adb reverse localabstract:phonebridge tcp:<port>`.
     * adbd on the device listens; this app CONNECTS to it. Preferred, because the PC
     * gets a real connect event.
     */
    REVERSE,

    /**
     * `adb forward tcp:<port> localabstract:phonebridge`.
     * This app LISTENS; the PC connects. Fallback only - the PC cannot distinguish
     * "app ready" from "tunnel exists" in this mode.
     */
    FORWARD
}

/** One established, handshaken channel. */
class BridgeChannel(
    val channel: Int,
    private val socket: LocalSocket
) : Closeable {

    val input: InputStream = socket.inputStream
    val output: OutputStream = socket.outputStream
    val sink = FrameSink(output)

    override fun close() {
        // shutdown* before close so the peer sees EOF promptly rather than waiting for
        // a read timeout; a lingering half-open channel blocks the PC's re-accept.
        runCatching { socket.shutdownInput() }
        runCatching { socket.shutdownOutput() }
        runCatching { socket.close() }
    }
}

/**
 * Opens channels to the desktop over the abstract-namespace socket.
 *
 * Deliberately synchronous and blocking: callers run it on their own dispatcher. The
 * Android socket APIs here have no non-blocking variant worth the wrapper.
 */
class BridgeTransport(
    private val socketName: String = Protocol.SOCKET_NAME,
    var mode: TunnelMode = TunnelMode.REVERSE
) {

    /** Only used in FORWARD mode. Held open across channel accepts. */
    private var serverSocket: LocalServerSocket? = null

    /**
     * Establishes one channel, including the handshake.
     * @throws HandshakeException if the desktop rejects us (check [HandshakeException.isTerminal]).
     * @throws java.io.IOException if the tunnel is absent or the peer vanished.
     */
    fun openChannel(channel: Int): BridgeChannel {
        val socket = when (mode) {
            TunnelMode.REVERSE -> {
                LocalSocket().apply {
                    // Throws immediately if no reverse rule exists, which is exactly the
                    // fast failure we want while the desktop app is not running.
                    connect(LocalSocketAddress(socketName, LocalSocketAddress.Namespace.ABSTRACT))
                }
            }
            TunnelMode.FORWARD -> {
                val server = serverSocket ?: LocalServerSocket(socketName).also { serverSocket = it }
                server.accept()
            }
        }

        return try {
            socket.outputStream.write(Protocol.buildHandshake(channel))
            socket.outputStream.flush()
            Protocol.readReply(socket.inputStream)
            Log.i(TAG, "channel $channel established (${mode.name.lowercase()})")
            BridgeChannel(channel, socket)
        } catch (t: Throwable) {
            runCatching { socket.close() }
            throw t
        }
    }

    /** Releases the listening socket. No-op in REVERSE mode. */
    fun releaseServer() {
        runCatching { serverSocket?.close() }
        serverSocket = null
    }
}

/**
 * Reconnect backoff (FR12). 250ms, 500ms, 1s, 2s, 4s, then steady 4s, each with +/-20%
 * jitter so a reconnect storm after a cable jostle does not synchronise all three
 * channels onto the same retry instant.
 */
class Backoff(
    private val steps: LongArray = longArrayOf(250, 500, 1_000, 2_000, 4_000),
    private val jitterFraction: Double = 0.2
) {
    private var attempt = 0

    fun reset() { attempt = 0 }

    fun nextDelayMs(): Long {
        val base = steps[minOf(attempt, steps.lastIndex)]
        attempt++
        val jitter = (base * jitterFraction).toLong()
        return base + Random.nextLong(-jitter, jitter + 1)
    }
}
