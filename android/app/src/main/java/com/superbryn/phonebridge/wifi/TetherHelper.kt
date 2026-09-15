package com.superbryn.phonebridge.wifi

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.provider.Settings
import android.util.Log
import java.net.NetworkInterface

private const val TAG = "TetherHelper"

/**
 * USB tethering support (PRD §1.4), reduced to what is actually achievable.
 *
 * Tethering CANNOT be toggled programmatically: startTethering() requires
 * TETHER_PRIVILEGED, which is signature|privileged - not grantable and not a runtime
 * permission. No shell command exposes it either, and `svc usb setFunctions rndis` would
 * reconfigure the USB gadget and very likely drop the ADB link this entire transport
 * depends on. See DECISIONS.md D6.
 *
 * So: deep-link the user to the right settings page and detect the result.
 */
object TetherHelper {

    /**
     * Opens the tethering settings page. Tries the documented AOSP action first, then the
     * MIUI component, then the generic wireless-settings page. MIUI moves this screen
     * between releases, so a chain rather than a single intent.
     */
    fun openTetherSettings(context: Context): Boolean {
        val candidates = listOf(
            // AOSP: the canonical tethering screen. Present on most builds including MIUI.
            Intent(Intent.ACTION_MAIN).setComponent(
                ComponentName("com.android.settings", "com.android.settings.TetherSettings")
            ),
            // MIUI's own USB tethering entry point.
            Intent().setComponent(
                ComponentName("com.android.settings", "com.android.settings.Settings\$TetherSettingsActivity")
            ),
            // Last resort: the wireless settings root, one tap away from tethering.
            Intent(Settings.ACTION_WIRELESS_SETTINGS)
        )

        for (intent in candidates) {
            try {
                context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                return true
            } catch (e: Exception) {
                Log.d(TAG, "tether intent rejected (${intent.component ?: intent.action}): ${e.message}")
            }
        }
        Log.w(TAG, "no tethering settings activity could be launched")
        return false
    }

    /** True if any of the deep-link candidates resolves, so the UI can disable a dead button. */
    fun canDeepLink(context: Context): Boolean =
        context.packageManager.resolveActivity(Intent(Settings.ACTION_WIRELESS_SETTINGS), 0) != null

    /**
     * Detects an active USB tether by looking for the gadget network interface.
     *
     * There is no supported API to query this from an app - TetheringManager's
     * introspection is privileged too - so interface-name matching is the available
     * signal. Names vary (rndis0 on RNDIS, ncm0 on CDC-NCM, usb0 on some OEMs), hence
     * the prefix set rather than an exact match.
     */
    fun isTetherActive(): Boolean = try {
        NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().any { nif ->
            nif.isUp && !nif.isLoopback &&
                TETHER_IFACE_PREFIXES.any { nif.name.startsWith(it, ignoreCase = true) }
        }
    } catch (e: Exception) {
        Log.d(TAG, "interface enumeration failed: ${e.message}")
        false
    }

    private val TETHER_IFACE_PREFIXES = listOf("rndis", "ncm", "usb")

    /** Whether the phone currently has upstream internet worth sharing. */
    fun hasUpstream(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
    }
}
