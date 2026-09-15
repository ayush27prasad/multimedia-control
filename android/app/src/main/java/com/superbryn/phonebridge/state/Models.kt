package com.superbryn.phonebridge.state

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Shared state models. These mirror docs/PROTOCOL.md §4.3 and the C# records in
 * PhoneBridge.Core/Protocol/Models.cs. All three must be changed together.
 *
 * Every field has a default so a snapshot from a newer peer that adds fields still
 * deserialises - forward compatibility matters here because the two apps are built and
 * deployed independently.
 */

// ---- device slice (phone is the authority) ---------------------------------

@Serializable
data class CameraState(
    val streaming: Boolean = false,
    val facing: String = FACING_BACK,
    val width: Int = 1920,
    val height: Int = 1080,
    val fps: Int = 30,
    val bitrateKbps: Int = 25_000,
    val available: List<String> = emptyList(),
    val error: String? = null
) {
    companion object {
        const val FACING_BACK = "back"
        const val FACING_FRONT = "front"
    }
}

@Serializable
data class MicState(
    val streaming: Boolean = false,
    val muted: Boolean = false,
    val source: String = SOURCE_UNPROCESSED,
    val sampleRate: Int = 48_000,
    val channels: Int = 2,
    val error: String? = null
) {
    companion object {
        /** Raw-ish capture, bypassing the AGC/NS colouring MIUI applies to VOICE_* sources. */
        const val SOURCE_UNPROCESSED = "unprocessed"
        const val SOURCE_VOICE_RECOGNITION = "voice_recognition"
        const val SOURCE_MIC = "mic"
        const val SOURCE_CAMCORDER = "camcorder"
    }
}

@Serializable
data class TetherState(
    val active: Boolean = false,
    /**
     * Whether the app can deep-link into the tethering settings page. Tethering cannot be
     * toggled programmatically - TETHER_PRIVILEGED is signature-level. See DECISIONS.md D6.
     */
    val canDeepLink: Boolean = true
)

@Serializable
data class DeviceState(
    val camera: CameraState = CameraState(),
    val mic: MicState = MicState(),
    val tether: TetherState = TetherState()
)

// ---- wifi slice (PC is the authority) --------------------------------------

@Serializable
data class SavedNetwork(
    val networkId: Int = -1,
    val ssid: String = "",
    val security: String = ""
)

@Serializable
data class ScanResult(
    val ssid: String = "",
    val rssi: Int = 0,
    val security: String = "",
    val frequencyMhz: Int = 0
)

/**
 * Authored by the PC from `cmd wifi status` / `list-networks` / `list-scan-results`.
 *
 * The phone renders this but cannot produce it: getConfiguredNetworks() returns an empty
 * list for non-system apps since Android 10. See DECISIONS.md D5.
 *
 * There is deliberately no connect command - `cmd wifi connect-network` requires the
 * passphrase on every call and cannot activate a saved network by id.
 */
@Serializable
data class WifiState(
    val enabled: Boolean = false,
    val connected: Boolean = false,
    val ssid: String? = null,
    val bssid: String? = null,
    val rssi: Int? = null,
    val linkSpeedMbps: Int? = null,
    val frequencyMhz: Int? = null,
    val networkId: Int? = null,
    val savedNetworks: List<SavedNetwork> = emptyList(),
    val scanResults: List<ScanResult> = emptyList(),
    val lastError: String? = null,
    val lastUpdated: Long = 0
)

// ---- link status (local only, never serialised) ----------------------------

enum class LinkStatus { DISCONNECTED, CONNECTING, CONNECTED, INCOMPATIBLE }

data class LinkState(
    val status: LinkStatus = LinkStatus.DISCONNECTED,
    val detail: String? = null,
    val connectedSinceMs: Long? = null,
    val videoChannelUp: Boolean = false,
    val audioChannelUp: Boolean = false
)

// ---- observed-only wifi info the phone CAN read ----------------------------

/**
 * What the app can see natively: current connection via getConnectionInfo() and in-range
 * APs via getScanResults(). Sent to the PC as supplementary telemetry; the PC's ADB-derived
 * view remains authoritative for the wifi slice.
 */
@Serializable
data class ObservedWifi(
    @SerialName("ssid") val ssid: String? = null,
    @SerialName("rssi") val rssi: Int? = null,
    @SerialName("linkSpeedMbps") val linkSpeedMbps: Int? = null,
    @SerialName("frequencyMhz") val frequencyMhz: Int? = null,
    @SerialName("inRange") val inRange: List<ScanResult> = emptyList(),
    @SerialName("locationPermitted") val locationPermitted: Boolean = false
)
