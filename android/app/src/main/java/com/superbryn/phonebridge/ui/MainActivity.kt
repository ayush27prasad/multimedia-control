package com.superbryn.phonebridge.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.superbryn.phonebridge.Bridge
import com.superbryn.phonebridge.service.BridgeService

class MainActivity : ComponentActivity() {

    private var hasCamera by mutableStateOf(false)
    private var hasMic by mutableStateOf(false)

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { granted ->
        refreshPermissions()
        // Re-promote the foreground service: on API 34+ its declared type must match the
        // permissions actually held, so a service started before the grant is running
        // without the camera/microphone types.
        if (granted.values.any { it }) BridgeService.start(this)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)

        refreshPermissions()
        BridgeService.start(this)

        setContent {
            PhoneBridgeTheme {
                val link by Bridge.store.link.collectAsStateWithLifecycle()
                val device by Bridge.store.device.collectAsStateWithLifecycle()
                val wifi by Bridge.store.wifi.collectAsStateWithLifecycle()
                val observed by Bridge.store.observedWifi.collectAsStateWithLifecycle()
                val pending by Bridge.store.pending.collectAsStateWithLifecycle()

                Box(
                    Modifier
                        .fillMaxSize()
                        .background(Palette.Background)
                        .windowInsetsPadding(WindowInsets.systemBars)
                ) {
                    ControlPanel(
                        link = link,
                        device = device,
                        wifi = wifi,
                        observed = observed,
                        pending = pending,
                        onLocal = { cmd, args -> Bridge.commandSink?.executeLocal(cmd, args) },
                        onForward = { cmd, args -> Bridge.commandSink?.forwardToPc(cmd, args) },
                        onRequestPermissions = ::requestPermissions,
                        hasCameraPermission = hasCamera,
                        hasMicPermission = hasMic
                    )
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        // The user may have changed permissions in system settings while we were away.
        refreshPermissions()
    }

    private fun refreshPermissions() {
        hasCamera = granted(Manifest.permission.CAMERA)
        hasMic = granted(Manifest.permission.RECORD_AUDIO)
    }

    private fun granted(p: String) =
        ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED

    private fun requestPermissions() {
        val wanted = buildList {
            add(Manifest.permission.CAMERA)
            add(Manifest.permission.RECORD_AUDIO)
            // Needed for getScanResults() and an un-redacted SSID.
            add(Manifest.permission.ACCESS_FINE_LOCATION)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                // From API 33 the foreground-service notification is itself gated.
                add(Manifest.permission.POST_NOTIFICATIONS)
            }
        }.filterNot { granted(it) }

        if (wanted.isNotEmpty()) permissionLauncher.launch(wanted.toTypedArray())
    }
}
