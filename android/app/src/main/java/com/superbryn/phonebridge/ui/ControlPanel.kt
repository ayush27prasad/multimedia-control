package com.superbryn.phonebridge.ui

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.superbryn.phonebridge.control.Commands
import com.superbryn.phonebridge.state.*

/**
 * The mirrored control surface (FR14). Every control here has a counterpart on the
 * desktop, and both render the same authoritative state.
 *
 * Wi-Fi is presented read-only apart from the radio toggle, because that is the honest
 * shape of what is achievable - see DECISIONS.md D5.
 */
@Composable
fun ControlPanel(
    link: LinkState,
    device: DeviceState,
    wifi: WifiState,
    observed: ObservedWifi,
    pending: Set<String>,
    onLocal: (String, Map<String, Any?>) -> Unit,
    onForward: (String, Map<String, Any?>) -> Unit,
    onRequestPermissions: () -> Unit,
    hasCameraPermission: Boolean,
    hasMicPermission: Boolean
) {
    val connected = link.status == LinkStatus.CONNECTED

    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        LinkHeader(link)

        if (!hasCameraPermission || !hasMicPermission) {
            PermissionPrompt(hasCameraPermission, hasMicPermission, onRequestPermissions)
        }

        CameraSection(device.camera, connected, hasCameraPermission, onLocal)
        MicSection(device.mic, connected, hasMicPermission, onLocal)
        WifiSection(wifi, observed, connected, pending, onForward)
        TetherSection(device.tether, onLocal)

        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun LinkHeader(link: LinkState) {
    Section(
        title = "USB link",
        trailing = { StatusDot(link.status) }
    ) {
        Text(
            when (link.status) {
                LinkStatus.CONNECTED -> "Connected to desktop"
                LinkStatus.CONNECTING -> "Connecting..."
                LinkStatus.INCOMPATIBLE -> "Incompatible desktop version"
                LinkStatus.DISCONNECTED -> "Waiting for desktop app"
            },
            style = MaterialTheme.typography.titleMedium,
            color = when (link.status) {
                LinkStatus.CONNECTED -> Palette.TextPrimary
                LinkStatus.INCOMPATIBLE -> Palette.Danger
                else -> Palette.TextSecondary
            }
        )
        link.detail?.let {
            Spacer(Modifier.height(4.dp))
            Text(it, style = MaterialTheme.typography.bodySmall, color = Palette.TextMuted)
        }
        if (link.status == LinkStatus.DISCONNECTED) {
            Spacer(Modifier.height(10.dp))
            InfoNote(
                "Start PhoneBridge on the PC with the cable plugged in. The phone dials out " +
                    "through the ADB tunnel, so nothing happens until the desktop app creates it."
            )
        }
        if (link.status == LinkStatus.CONNECTED) {
            Spacer(Modifier.height(8.dp))
            MetricLine("video", if (link.videoChannelUp) "streaming" else "idle")
            MetricLine("audio", if (link.audioChannelUp) "streaming" else "idle")
        }
    }
}

@Composable
private fun PermissionPrompt(camera: Boolean, mic: Boolean, onRequest: () -> Unit) {
    val missing = buildList {
        if (!camera) add("Camera")
        if (!mic) add("Microphone")
    }.joinToString(" and ")

    Section(title = "Permissions needed") {
        Text(
            "$missing access is required before streaming can start.",
            style = MaterialTheme.typography.bodyMedium,
            color = Palette.TextSecondary
        )
        Spacer(Modifier.height(12.dp))
        Button(
            onClick = onRequest,
            colors = ButtonDefaults.buttonColors(
                containerColor = Palette.Primary, contentColor = Palette.OnPrimary
            )
        ) { Text("Grant permissions") }
    }
}

@Composable
private fun CameraSection(
    camera: CameraState,
    connected: Boolean,
    hasPermission: Boolean,
    onLocal: (String, Map<String, Any?>) -> Unit
) {
    Section(title = "Camera") {
        camera.error?.let { ErrorNote(it); Spacer(Modifier.height(10.dp)) }

        ControlRow(
            label = "Stream to PC",
            subtitle = if (!hasPermission) "Camera permission required"
                       else if (!connected) "Requires an active link"
                       else "${camera.width}x${camera.height} @ ${camera.fps}fps, ${camera.bitrateKbps / 1000} Mb/s",
            enabled = connected && hasPermission
        ) {
            BridgeSwitch(camera.streaming, connected && hasPermission) { on ->
                onLocal(Commands.CAMERA_SET_STREAMING, mapOf("on" to on))
            }
        }

        // Only offer facings the hardware actually reports.
        val facings = camera.available.ifEmpty { listOf(CameraState.FACING_BACK) }
        if (facings.size > 1) {
            Spacer(Modifier.height(4.dp))
            ControlRow(label = "Source", enabled = connected) {
                SegmentedChoice(
                    options = facings.map { it to if (it == CameraState.FACING_FRONT) "Front" else "Rear" },
                    selected = camera.facing,
                    enabled = connected
                ) { onLocal(Commands.CAMERA_SET_FACING, mapOf("facing" to it)) }
            }
        }

        Spacer(Modifier.height(4.dp))
        ControlRow(label = "Quality", enabled = connected) {
            SegmentedChoice(
                options = listOf("720" to "720p", "1080" to "1080p"),
                selected = if (camera.height >= 1080) "1080" else "720",
                enabled = connected
            ) { choice ->
                // Bitrate scales with pixel count; these targets sit well inside the
                // link budget while keeping quality the priority (FR3).
                val (w, h, kbps) = if (choice == "1080") Triple(1920, 1080, 25_000)
                                   else Triple(1280, 720, 12_000)
                onLocal(Commands.CAMERA_SET_FORMAT, mapOf("width" to w, "height" to h, "bitrateKbps" to kbps))
            }
        }
    }
}

@Composable
private fun MicSection(
    mic: MicState,
    connected: Boolean,
    hasPermission: Boolean,
    onLocal: (String, Map<String, Any?>) -> Unit
) {
    Section(title = "Microphone") {
        mic.error?.let { ErrorNote(it); Spacer(Modifier.height(10.dp)) }

        ControlRow(
            label = "Stream to PC",
            subtitle = if (!hasPermission) "Microphone permission required"
                       else "PCM ${mic.sampleRate / 1000}kHz ${if (mic.channels == 2) "stereo" else "mono"}",
            enabled = connected && hasPermission
        ) {
            BridgeSwitch(mic.streaming, connected && hasPermission) { on ->
                onLocal(Commands.MIC_SET_STREAMING, mapOf("on" to on))
            }
        }

        Spacer(Modifier.height(4.dp))
        ControlRow(
            label = "Mute",
            subtitle = if (mic.muted) "Sending silence" else null,
            enabled = mic.streaming
        ) {
            BridgeSwitch(mic.muted, mic.streaming) { muted ->
                onLocal(Commands.MIC_SET_MUTED, mapOf("muted" to muted))
            }
        }

        Spacer(Modifier.height(4.dp))
        ControlRow(label = "Processing", enabled = connected) {
            SegmentedChoice(
                options = listOf(
                    MicState.SOURCE_UNPROCESSED to "Raw",
                    MicState.SOURCE_VOICE_RECOGNITION to "Clean"
                ),
                selected = mic.source,
                enabled = connected
            ) { onLocal(Commands.MIC_SET_SOURCE, mapOf("source" to it)) }
        }
    }
}

@Composable
private fun WifiSection(
    wifi: WifiState,
    observed: ObservedWifi,
    connected: Boolean,
    pending: Set<String>,
    onForward: (String, Map<String, Any?>) -> Unit
) {
    Section(
        title = "Wi-Fi",
        trailing = {
            if (Commands.WIFI_SET_ENABLED in pending) {
                CircularProgressIndicator(
                    Modifier.size(14.dp), strokeWidth = 2.dp, color = Palette.Primary
                )
            }
        }
    ) {
        ControlRow(
            label = "Wi-Fi radio",
            subtitle = if (!connected) "Requires an active link" else null,
            enabled = connected
        ) {
            BridgeSwitch(wifi.enabled, connected && Commands.WIFI_SET_ENABLED !in pending) { on ->
                // Forwarded, never applied locally: setWifiEnabled() returns false for
                // apps targeting Android 10+, so only the PC can actually do this.
                onForward(Commands.WIFI_SET_ENABLED, mapOf("enabled" to on))
            }
        }

        Spacer(Modifier.height(8.dp))

        // Prefer the phone's own reading of the current network: it updates on a
        // NetworkCallback, whereas the PC's view waits for its next ADB poll.
        val currentSsid = observed.ssid ?: wifi.ssid
        if (currentSsid != null) {
            MetricLine("connected", currentSsid)
            (observed.rssi ?: wifi.rssi)?.let { MetricLine("signal", "$it dBm") }
            (observed.linkSpeedMbps ?: wifi.linkSpeedMbps)?.let { MetricLine("link", "$it Mb/s") }
            (observed.frequencyMhz ?: wifi.frequencyMhz)?.let {
                MetricLine("band", if (it > 4000) "5 GHz" else "2.4 GHz")
            }
        } else if (wifi.enabled) {
            MetricLine("connected", "not connected")
        }

        wifi.lastError?.let { Spacer(Modifier.height(10.dp)); ErrorNote(it) }

        if (wifi.savedNetworks.isNotEmpty()) {
            Spacer(Modifier.height(14.dp))
            Text("SAVED NETWORKS", style = MaterialTheme.typography.labelSmall, color = Palette.TextMuted)
            Spacer(Modifier.height(6.dp))
            // Strongest in-range first, so the list matches what the user would pick.
            val strength = observed.inRange.associate { it.ssid to it.rssi }
            for (net in wifi.savedNetworks.sortedByDescending { strength[it.ssid] ?: Int.MIN_VALUE }) {
                val rssi = strength[net.ssid]
                Row(Modifier.fillMaxWidth().padding(vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        net.ssid,
                        style = MaterialTheme.typography.bodyMedium,
                        color = if (net.ssid == currentSsid) Palette.Success else Palette.TextPrimary,
                        modifier = Modifier.weight(1f)
                    )
                    Text(
                        when {
                            net.ssid == currentSsid -> "connected"
                            rssi != null -> "$rssi dBm"
                            else -> "out of range"
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = if (rssi != null || net.ssid == currentSsid) Palette.TextSecondary else Palette.TextMuted
                    )
                }
            }
        }

        Spacer(Modifier.height(12.dp))
        InfoNote(
            "Tap a network in Android's own Wi-Fi settings to connect. ADB can list saved " +
                "networks and toggle the radio, but cannot join a saved network without its " +
                "password, so connecting is deliberately left to the system UI."
        )

        if (!observed.locationPermitted) {
            Spacer(Modifier.height(8.dp))
            InfoNote("Grant location permission to see signal strength for nearby networks.")
        }
    }
}

@Composable
private fun TetherSection(tether: TetherState, onLocal: (String, Map<String, Any?>) -> Unit) {
    Section(
        title = "USB tethering",
        trailing = {
            StatusDot(if (tether.active) LinkStatus.CONNECTED else LinkStatus.DISCONNECTED)
        }
    ) {
        Text(
            if (tether.active) "Active - the PC can route through this phone"
            else "Not active",
            style = MaterialTheme.typography.titleMedium,
            color = if (tether.active) Palette.Success else Palette.TextSecondary
        )
        Spacer(Modifier.height(12.dp))
        OutlinedButton(
            onClick = { onLocal(Commands.TETHER_OPEN_SETTINGS, emptyMap()) },
            enabled = tether.canDeepLink,
            colors = ButtonDefaults.outlinedButtonColors(contentColor = Palette.Primary)
        ) { Text("Open tethering settings") }

        Spacer(Modifier.height(10.dp))
        InfoNote(
            "Android does not let apps switch tethering on. This opens the system page " +
                "so it is one tap away; the state above updates once it is running."
        )
    }
}
