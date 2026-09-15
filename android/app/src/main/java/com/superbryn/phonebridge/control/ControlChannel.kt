package com.superbryn.phonebridge.control

import android.util.Log
import com.superbryn.phonebridge.BuildConfig
import com.superbryn.phonebridge.transport.BridgeChannel
import com.superbryn.phonebridge.transport.Protocol
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.encodeToJsonElement
import java.io.BufferedReader
import java.io.IOException
import java.util.UUID
import java.util.concurrent.atomic.AtomicLong

private const val TAG = "ControlChannel"
private const val PING_INTERVAL_MS = 1_000L
private const val MISSED_PINGS_BEFORE_DEAD = 3

/**
 * NDJSON control channel. One JSON object per line, bidirectional.
 *
 * Ownership: the caller supplies an established [BridgeChannel] and is responsible for
 * closing it. This class owns only the read loop and the heartbeat.
 */
class ControlChannel(
    private val channel: BridgeChannel,
    private val scope: CoroutineScope,
    private val onMessage: suspend (Envelope) -> Unit,
    private val onDead: (String) -> Unit
) {
    private val seq = AtomicLong(0)

    /** Pings sent whose pong has not come back. Reset on any inbound traffic. */
    @Volatile private var outstandingPings = 0

    private var readerJob: Job? = null
    private var heartbeatJob: Job? = null

    fun start() {
        readerJob = scope.launch(Dispatchers.IO) { readLoop() }
        heartbeatJob = scope.launch(Dispatchers.IO) { heartbeatLoop() }
    }

    fun stop() {
        readerJob?.cancel()
        heartbeatJob?.cancel()
    }

    // ---- outbound ----------------------------------------------------------

    fun nextSeq(): Long = seq.incrementAndGet()

    suspend fun send(envelope: Envelope) = withContext(Dispatchers.IO) {
        val line = BridgeJson.encodeToString(Envelope.serializer(), envelope) + "\n"
        if (BuildConfig.TRACE_TRANSPORT) Log.v(TAG, "TX $line")
        try {
            channel.sink.writeRaw(line.toByteArray(Charsets.UTF_8))
        } catch (e: IOException) {
            onDead("write failed: ${e.message}")
        }
    }

    suspend fun sendState(payload: StatePayload) = send(
        envelopeOf(
            Envelope.T_STATE,
            nextSeq(),
            BridgeJson.encodeToJsonElement(payload) as JsonObject
        )
    )

    suspend fun sendHello(payload: HelloPayload) = send(
        envelopeOf(
            Envelope.T_HELLO,
            nextSeq(),
            BridgeJson.encodeToJsonElement(payload) as JsonObject
        )
    )

    /** @return the seq used, so the caller can correlate the ack. */
    suspend fun sendCommand(payload: CmdPayload): Long {
        val s = nextSeq()
        send(envelopeOf(Envelope.T_CMD, s, BridgeJson.encodeToJsonElement(payload) as JsonObject))
        return s
    }

    suspend fun sendAck(ackSeq: Long, ok: Boolean, error: String? = null) = send(
        envelopeOf(
            Envelope.T_ACK,
            nextSeq(),
            BridgeJson.encodeToJsonElement(AckPayload(ackSeq, ok, error)) as JsonObject
        )
    )

    // ---- inbound -----------------------------------------------------------

    private suspend fun readLoop() {
        try {
            val reader = BufferedReader(channel.input.reader(Charsets.UTF_8), 16 * 1024)
            while (scope.isActive) {
                val line = reader.readLine() ?: run {
                    onDead("desktop closed the control channel")
                    return
                }
                if (line.isBlank()) continue

                // A line beyond the cap means the stream has desynchronised. There is no
                // resync marker in NDJSON, so recovering in place is not possible -
                // drop the channel and let the reconnect path rebuild it.
                if (line.length > Protocol.MAX_CONTROL_LINE) {
                    onDead("control line exceeded ${Protocol.MAX_CONTROL_LINE} bytes - stream desynchronised")
                    return
                }

                if (BuildConfig.TRACE_TRANSPORT) Log.v(TAG, "RX $line")
                outstandingPings = 0

                val envelope = try {
                    BridgeJson.decodeFromString(Envelope.serializer(), line)
                } catch (e: Exception) {
                    // One malformed message is not fatal; a peer bug should not take the
                    // link down. Log and keep reading.
                    Log.w(TAG, "undecodable control message, skipping: ${e.message}")
                    continue
                }

                if (envelope.t == Envelope.T_PING) {
                    val nonce = runCatching {
                        BridgeJson.decodeFromJsonElement(PingPayload.serializer(), envelope.p).nonce
                    }.getOrDefault("")
                    send(
                        envelopeOf(
                            Envelope.T_PONG,
                            nextSeq(),
                            BridgeJson.encodeToJsonElement(PingPayload(nonce)) as JsonObject
                        )
                    )
                    continue
                }
                if (envelope.t == Envelope.T_PONG) continue

                onMessage(envelope)
            }
        } catch (e: IOException) {
            onDead("control read failed: ${e.message}")
        } catch (e: Exception) {
            onDead("control loop error: ${e.message}")
        }
    }

    private suspend fun heartbeatLoop() {
        while (scope.isActive) {
            delay(PING_INTERVAL_MS)
            if (outstandingPings >= MISSED_PINGS_BEFORE_DEAD) {
                onDead("no response to $MISSED_PINGS_BEFORE_DEAD pings")
                return
            }
            outstandingPings++
            send(
                envelopeOf(
                    Envelope.T_PING,
                    nextSeq(),
                    BridgeJson.encodeToJsonElement(PingPayload(UUID.randomUUID().toString().take(8))) as JsonObject
                )
            )
        }
    }
}
