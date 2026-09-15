package com.superbryn.phonebridge.control

import com.superbryn.phonebridge.state.DeviceState
import com.superbryn.phonebridge.state.ObservedWifi
import com.superbryn.phonebridge.state.WifiState
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject

/** Control-channel envelope. docs/PROTOCOL.md §4.1. */
@Serializable
data class Envelope(
    val t: String,
    val seq: Long = 0,
    val origin: String = ORIGIN_PHONE,
    val ts: Long = 0,
    val p: JsonObject = JsonObject(emptyMap())
) {
    companion object {
        const val ORIGIN_PHONE = "phone"
        const val ORIGIN_PC = "pc"

        const val T_HELLO = "hello"
        const val T_STATE = "state"
        const val T_CMD = "cmd"
        const val T_ACK = "ack"
        const val T_PING = "ping"
        const val T_PONG = "pong"
    }
}

@Serializable
data class HelloPayload(
    val role: String = Envelope.ORIGIN_PHONE,
    val app: String = "1.0.0",
    val protocol: Int = 1,
    val device: String = "",
    val android: String = "",
    val caps: List<String> = emptyList()
)

@Serializable
data class StatePayload(
    val scope: String,
    val camera: com.superbryn.phonebridge.state.CameraState? = null,
    val mic: com.superbryn.phonebridge.state.MicState? = null,
    val tether: com.superbryn.phonebridge.state.TetherState? = null,
    val wifi: WifiState? = null,
    val observedWifi: ObservedWifi? = null
) {
    companion object {
        const val SCOPE_DEVICE = "device"
        const val SCOPE_WIFI = "wifi"
    }
}

@Serializable
data class CmdPayload(
    val cmd: String,
    val args: JsonObject = JsonObject(emptyMap())
)

@Serializable
data class AckPayload(
    @SerialName("ackSeq") val ackSeq: Long,
    val ok: Boolean,
    val error: String? = null
)

@Serializable
data class PingPayload(val nonce: String)

/** Command names. Mirrors docs/PROTOCOL.md §4.3 and Commands.cs. */
object Commands {
    // Targeted at the PC (wifi authority).
    const val WIFI_SET_ENABLED = "wifi.setEnabled"
    const val WIFI_REFRESH = "wifi.refresh"
    const val WIFI_SCAN = "wifi.scan"

    // Targeted at the phone (device authority).
    const val CAMERA_SET_STREAMING = "camera.setStreaming"
    const val CAMERA_SET_FACING = "camera.setFacing"
    const val CAMERA_SET_FORMAT = "camera.setFormat"
    const val MIC_SET_STREAMING = "mic.setStreaming"
    const val MIC_SET_MUTED = "mic.setMuted"
    const val MIC_SET_SOURCE = "mic.setSource"
    const val TETHER_OPEN_SETTINGS = "tether.openSettings"
}

val BridgeJson = Json {
    ignoreUnknownKeys = true      // a newer peer may add fields
    encodeDefaults = true         // the PC expects a full snapshot, not a sparse one
    explicitNulls = false
    isLenient = false
}

fun envelopeOf(type: String, seq: Long, payload: JsonObject = buildJsonObject { }): Envelope =
    Envelope(t = type, seq = seq, origin = Envelope.ORIGIN_PHONE, ts = System.currentTimeMillis(), p = payload)
