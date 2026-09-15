package com.superbryn.phonebridge.wifi

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.util.Log
import androidx.core.content.ContextCompat
import com.superbryn.phonebridge.state.ObservedWifi
import com.superbryn.phonebridge.state.ScanResult as PbScanResult

private const val TAG = "WifiObserver"

/**
 * The app's own read-only view of Wi-Fi.
 *
 * Scope is narrow by necessity, not by choice (DECISIONS.md D5):
 *  - saved networks   -> getConfiguredNetworks() returns [] for non-system apps since
 *                        Android 10. The PC supplies these over the control channel.
 *  - radio toggle     -> setWifiEnabled() returns false for apps targeting Android 10+.
 *  - connect          -> every app-side API is either advisory (suggestions) or requires
 *                        a user tap (network request / settings panel).
 *
 * What remains is genuinely useful: current connection and in-range APs, pushed to the PC
 * as supplementary telemetry that arrives faster than the PC's ADB polling cycle.
 */
class WifiObserver(
    private val context: Context,
    private val onChanged: () -> Unit
) {
    private val wifiManager =
        context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    private val connectivityManager =
        context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

    private var callback: ConnectivityManager.NetworkCallback? = null

    /** Both getScanResults() and an un-redacted SSID require fine location. */
    fun hasLocationPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.ACCESS_FINE_LOCATION) ==
            PackageManager.PERMISSION_GRANTED

    fun start() {
        if (callback != null) return
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = onChanged()
            override fun onLost(network: Network) = onChanged()
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = onChanged()
        }
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .build()
        runCatching { connectivityManager.registerNetworkCallback(request, cb) }
            .onSuccess { callback = cb }
            .onFailure { Log.w(TAG, "could not register network callback: ${it.message}") }
    }

    fun stop() {
        callback?.let { runCatching { connectivityManager.unregisterNetworkCallback(it) } }
        callback = null
    }

    @Suppress("DEPRECATION") // getConnectionInfo() is the only path that also works pre-S
    fun snapshot(): ObservedWifi {
        val permitted = hasLocationPermission()

        val info = runCatching { wifiManager.connectionInfo }.getOrNull()
        // Android returns the literal string "<unknown ssid>" rather than null when the
        // caller lacks location permission, and quotes the SSID when it has it.
        val rawSsid = info?.ssid
        val ssid = rawSsid
            ?.removeSurrounding("\"")
            ?.takeIf { it.isNotBlank() && it != WifiManager.UNKNOWN_SSID }

        val inRange = if (permitted) {
            runCatching {
                wifiManager.scanResults.orEmpty()
                    .filter { it.SSID.isNotBlank() }
                    .map {
                        PbScanResult(
                            ssid = it.SSID,
                            rssi = it.level,
                            security = securityOf(it.capabilities),
                            frequencyMhz = it.frequency
                        )
                    }
                    // Same SSID on multiple bands/APs collapses to its strongest sighting;
                    // the user cares about the network, not each radio.
                    .groupBy { it.ssid }
                    .map { (_, v) -> v.maxBy { it.rssi } }
                    .sortedByDescending { it.rssi }
            }.getOrElse {
                Log.w(TAG, "scanResults unavailable: ${it.message}")
                emptyList()
            }
        } else emptyList()

        return ObservedWifi(
            ssid = ssid,
            rssi = info?.rssi?.takeIf { it != 0 },
            linkSpeedMbps = info?.linkSpeed?.takeIf { it > 0 },
            frequencyMhz = info?.frequency?.takeIf { it > 0 },
            inRange = inRange,
            locationPermitted = permitted
        )
    }

    /** Normalised to the same vocabulary `cmd wifi list-networks` uses, so both lists read alike. */
    private fun securityOf(capabilities: String): String = when {
        capabilities.contains("WPA3") || capabilities.contains("SAE") -> "wpa3"
        capabilities.contains("WPA2") || capabilities.contains("RSN") -> "wpa2"
        capabilities.contains("WPA") -> "wpa"
        capabilities.contains("WEP") -> "wep"
        capabilities.contains("OWE") -> "owe"
        else -> "open"
    }
}
