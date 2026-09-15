package com.superbryn.phonebridge.service

import android.Manifest
import android.app.Notification
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.LifecycleService
import com.superbryn.phonebridge.Bridge
import com.superbryn.phonebridge.CommandSink
import com.superbryn.phonebridge.PhoneBridgeApp
import com.superbryn.phonebridge.R
import com.superbryn.phonebridge.control.*
import com.superbryn.phonebridge.media.AudioCapture
import com.superbryn.phonebridge.media.CameraController
import com.superbryn.phonebridge.media.VideoConfig
import com.superbryn.phonebridge.state.*
import com.superbryn.phonebridge.transport.*
import com.superbryn.phonebridge.ui.MainActivity
import com.superbryn.phonebridge.wifi.TetherHelper
import com.superbryn.phonebridge.wifi.WifiObserver
import kotlinx.coroutines.*
import kotlinx.serialization.json.*
import java.io.IOException

private const val TAG = "BridgeService"
private const val NOTIFICATION_ID = 1
private const val OBSERVER_INTERVAL_MS = 2_000L

/**
 * Owns the USB link and the device-side hardware.
 *
 * A LifecycleService because CameraX's bindToLifecycle needs a LifecycleOwner that
 * outlives the activity - the stream must survive the user leaving the app.
 */
class BridgeService : LifecycleService(), CommandSink {

    private val store get() = Bridge.store

    private val transport = BridgeTransport()
    private val backoff = Backoff()

    private var controlChannel: ControlChannel? = null
    private var controlConn: BridgeChannel? = null
    private var videoConn: BridgeChannel? = null
    private var audioConn: BridgeChannel? = null

    private lateinit var camera: CameraController
    private var audio: AudioCapture? = null
    private lateinit var wifiObserver: WifiObserver

    private var connectionJob: Job? = null
    private var observerJob: Job? = null

    /** Completed by whatever notices the link died, unblocking the connection loop. */
    private var disconnected: CompletableDeferred<String>? = null

    // ---- lifecycle ---------------------------------------------------------

    override fun onCreate() {
        super.onCreate()
        Bridge.commandSink = this

        camera = CameraController(this, this) { err -> onCameraError(err) }
        camera.initialize {
            store.updateCamera { it.copy(available = camera.availableFacings()) }
            pushDeviceState()
        }

        wifiObserver = WifiObserver(this) { refreshObserved() }
        wifiObserver.start()

        store.updateTether { it.copy(canDeepLink = TetherHelper.canDeepLink(this)) }

        startForegroundCompat()
        startConnectionLoop()
        startObserverLoop()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        // START_STICKY: MIUI kills background services aggressively. Restarting without
        // the original intent is fine - all state is rebuilt from scratch on connect.
        return START_STICKY
    }

    override fun onDestroy() {
        Bridge.commandSink = null
        connectionJob?.cancel()
        observerJob?.cancel()
        stopVideo()
        stopAudio()
        teardownLink("service destroyed")
        transport.releaseServer()
        wifiObserver.stop()
        super.onDestroy()
    }

    // ---- foreground notification ------------------------------------------

    private fun startForegroundCompat() {
        val tap = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val notification: Notification = NotificationCompat.Builder(this, PhoneBridgeApp.CHANNEL_ID)
            .setContentTitle(getString(R.string.app_name))
            .setContentText("USB bridge running")
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setOngoing(true)
            .setContentIntent(tap)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            // From API 34 the declared type must match the permissions actually held, or
            // the start throws. Narrow the type to what has been granted so far.
            var type = 0
            if (hasPermission(Manifest.permission.CAMERA)) {
                type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
            }
            if (hasPermission(Manifest.permission.RECORD_AUDIO)) {
                type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            }
            if (type == 0) {
                // Neither permission granted yet: run as a plain foreground service so the
                // control channel still works, and re-promote once the user grants one.
                startForeground(NOTIFICATION_ID, notification)
            } else {
                startForeground(NOTIFICATION_ID, notification, type)
            }
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private fun hasPermission(p: String) =
        ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED

    // ---- connection loop ---------------------------------------------------

    private fun startConnectionLoop() {
        connectionJob = lifecycleScope.launch(Dispatchers.IO) {
            while (isActive) {
                val reason = try {
                    connectOnce()
                } catch (e: HandshakeException) {
                    if (e.isTerminal) {
                        // Retrying will never help - the desktop build must change.
                        store.setLink(LinkStatus.INCOMPATIBLE, e.message)
                        Log.e(TAG, "terminal handshake failure: ${e.message}")
                        return@launch
                    }
                    e.message ?: "handshake rejected"
                } catch (e: IOException) {
                    // The usual case: desktop app not running, so no tunnel exists.
                    e.message ?: "no tunnel"
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    e.message ?: "unexpected error"
                }

                teardownLink(reason)
                if (!isActive) break
                val wait = backoff.nextDelayMs()
                store.setLink(LinkStatus.DISCONNECTED, reason)
                delay(wait)
            }
        }
    }

    /** @return the reason the link ended. Suspends for as long as the link is healthy. */
    private suspend fun connectOnce(): String {
        store.setLink(LinkStatus.CONNECTING)

        val conn = transport.openChannel(Protocol.CHANNEL_CONTROL)
        controlConn = conn

        val dead = CompletableDeferred<String>()
        disconnected = dead

        val cc = ControlChannel(
            channel = conn,
            scope = lifecycleScope,
            onMessage = { handleMessage(it) },
            onDead = { reason -> if (!dead.isCompleted) dead.complete(reason) }
        )
        controlChannel = cc
        cc.start()

        backoff.reset()
        store.setLink(LinkStatus.CONNECTED)

        cc.sendHello(
            HelloPayload(
                role = Envelope.ORIGIN_PHONE,
                app = "1.0.0",
                protocol = Protocol.VERSION,
                device = "${Build.MANUFACTURER} ${Build.MODEL}",
                android = Build.VERSION.RELEASE,
                caps = listOf("video", "audio", "wifi.observe", "tether.deeplink")
            )
        )
        pushDeviceState()
        refreshObserved()

        return dead.await()
    }

    private fun teardownLink(reason: String) {
        Log.i(TAG, "link down: $reason")
        controlChannel?.stop()
        controlChannel = null
        runCatching { controlConn?.close() }
        controlConn = null
        store.clearWifi()
        store.clearAllPending()
        store.setMediaChannels(video = false, audio = false)
    }

    // ---- inbound commands --------------------------------------------------

    private suspend fun handleMessage(envelope: Envelope) {
        when (envelope.t) {
            Envelope.T_HELLO -> Log.i(TAG, "desktop hello: ${envelope.p}")

            Envelope.T_STATE -> {
                val payload = runCatching {
                    BridgeJson.decodeFromJsonElement(StatePayload.serializer(), envelope.p)
                }.getOrNull() ?: return
                // Only the wifi scope is accepted from the PC. A device-scope snapshot
                // from the peer would mean it is trying to author state it does not own.
                if (payload.scope == StatePayload.SCOPE_WIFI) {
                    payload.wifi?.let { store.applyWifi(it) }
                }
            }

            Envelope.T_CMD -> {
                val payload = runCatching {
                    BridgeJson.decodeFromJsonElement(CmdPayload.serializer(), envelope.p)
                }.getOrNull()
                if (payload == null) {
                    controlChannel?.sendAck(envelope.seq, false, "undecodable command payload")
                    return
                }
                val error = applyCommand(payload)
                controlChannel?.sendAck(envelope.seq, error == null, error)
                if (error == null) pushDeviceState()
            }

            Envelope.T_ACK -> {
                val ack = runCatching {
                    BridgeJson.decodeFromJsonElement(AckPayload.serializer(), envelope.p)
                }.getOrNull() ?: return
                if (!ack.ok) Log.w(TAG, "desktop rejected our command: ${ack.error}")
                store.clearAllPending()
            }
        }
    }

    /** @return null on success, or a human-readable reason for the ack. */
    private suspend fun applyCommand(payload: CmdPayload): String? {
        val args = payload.args
        return when (payload.cmd) {
            Commands.CAMERA_SET_STREAMING ->
                if (args.bool("on") == true) startVideo() else { stopVideo(); null }

            Commands.CAMERA_SET_FACING -> {
                val facing = args.str("facing") ?: return "missing 'facing'"
                if (facing !in listOf(CameraState.FACING_BACK, CameraState.FACING_FRONT))
                    return "unknown facing '$facing'"
                store.updateCamera { it.copy(facing = facing) }
                if (store.device.value.camera.streaming) restartVideo() else null
            }

            Commands.CAMERA_SET_FORMAT -> {
                store.updateCamera {
                    it.copy(
                        width = args.int("width") ?: it.width,
                        height = args.int("height") ?: it.height,
                        fps = args.int("fps") ?: it.fps,
                        bitrateKbps = args.int("bitrateKbps") ?: it.bitrateKbps
                    )
                }
                if (store.device.value.camera.streaming) restartVideo() else null
            }

            Commands.MIC_SET_STREAMING ->
                if (args.bool("on") == true) startAudio() else { stopAudio(); null }

            Commands.MIC_SET_MUTED -> {
                val muted = args.bool("muted") ?: return "missing 'muted'"
                audio?.setMuted(muted)
                store.updateMic { it.copy(muted = muted) }
                null
            }

            Commands.MIC_SET_SOURCE -> {
                val source = args.str("source") ?: return "missing 'source'"
                store.updateMic { it.copy(source = source) }
                if (store.device.value.mic.streaming) restartAudio() else null
            }

            Commands.TETHER_OPEN_SETTINGS ->
                if (TetherHelper.openTetherSettings(this)) null
                else "no tethering settings activity on this ROM"

            else -> "unknown command '${payload.cmd}'"
        }
    }

    // ---- media -------------------------------------------------------------

    private suspend fun startVideo(): String? {
        if (!hasPermission(Manifest.permission.CAMERA)) return "camera permission not granted"
        val cam = store.device.value.camera
        return try {
            val conn = transport.openChannel(Protocol.CHANNEL_VIDEO)
            videoConn = conn
            store.setMediaChannels(video = true)

            val config = VideoConfig(cam.width, cam.height, cam.fps, cam.bitrateKbps)
            withContext(Dispatchers.Main) { camera.start(config, cam.facing, conn.sink) }

            store.updateCamera { it.copy(streaming = true, error = null) }
            // The PC just attached; without this it would wait up to iFrameIntervalSec
            // for its first decodable frame.
            camera.requestKeyframe()
            null
        } catch (e: Exception) {
            stopVideo()
            "video channel failed: ${e.message}"
        }
    }

    private fun stopVideo() {
        lifecycleScope.launch(Dispatchers.Main) { camera.stop() }
        runCatching { videoConn?.close() }
        videoConn = null
        store.setMediaChannels(video = false)
        store.updateCamera { it.copy(streaming = false) }
    }

    private suspend fun restartVideo(): String? { stopVideo(); return startVideo() }

    private fun startAudio(): String? {
        if (!hasPermission(Manifest.permission.RECORD_AUDIO)) return "microphone permission not granted"
        val mic = store.device.value.mic
        return try {
            val conn = transport.openChannel(Protocol.CHANNEL_AUDIO)
            audioConn = conn
            store.setMediaChannels(audio = true)

            val cap = AudioCapture(mic.sampleRate, mic.channels) { err -> onAudioError(err) }
            cap.setMuted(mic.muted)
            cap.start(mic.source, conn.sink)
            audio = cap

            store.updateMic { it.copy(streaming = true, channels = cap.channels, error = null) }
            null
        } catch (e: Exception) {
            stopAudio()
            "audio channel failed: ${e.message}"
        }
    }

    private fun stopAudio() {
        audio?.stop()
        audio = null
        runCatching { audioConn?.close() }
        audioConn = null
        store.setMediaChannels(audio = false)
        store.updateMic { it.copy(streaming = false) }
    }

    private fun restartAudio(): String? { stopAudio(); return startAudio() }

    private fun onCameraError(message: String) {
        Log.e(TAG, "camera: $message")
        store.updateCamera { it.copy(error = message) }
        stopVideo()
        pushDeviceState()
    }

    private fun onAudioError(message: String) {
        Log.e(TAG, "audio: $message")
        store.updateMic { it.copy(error = message) }
        stopAudio()
        pushDeviceState()
    }

    // ---- outbound state ----------------------------------------------------

    private fun pushDeviceState() {
        val cc = controlChannel ?: return
        val d = store.device.value
        lifecycleScope.launch {
            cc.sendState(
                StatePayload(
                    scope = StatePayload.SCOPE_DEVICE,
                    camera = d.camera, mic = d.mic, tether = d.tether
                )
            )
        }
    }

    private fun startObserverLoop() {
        observerJob = lifecycleScope.launch(Dispatchers.IO) {
            while (isActive) {
                refreshObserved()
                delay(OBSERVER_INTERVAL_MS)
            }
        }
    }

    private fun refreshObserved() {
        val observed = wifiObserver.snapshot()
        store.applyObserved(observed)

        val tetherActive = TetherHelper.isTetherActive()
        if (tetherActive != store.device.value.tether.active) {
            store.updateTether { it.copy(active = tetherActive) }
            pushDeviceState()
        }

        val cc = controlChannel ?: return
        lifecycleScope.launch {
            cc.sendState(StatePayload(scope = StatePayload.SCOPE_DEVICE, observedWifi = observed))
        }
    }

    // ---- CommandSink (UI entry points) -------------------------------------

    override fun executeLocal(cmd: String, args: Map<String, Any?>) {
        lifecycleScope.launch {
            val error = applyCommand(CmdPayload(cmd, args.toJsonObject()))
            if (error != null) Log.w(TAG, "local command '$cmd' failed: $error")
            pushDeviceState()
        }
    }

    override fun forwardToPc(cmd: String, args: Map<String, Any?>) {
        val cc = controlChannel ?: return
        store.markPending(cmd)
        lifecycleScope.launch {
            cc.sendCommand(CmdPayload(cmd, args.toJsonObject()))
            // Safety net: if the desktop never acks, the pending affordance must not
            // stick forever. The ack normally clears it well inside this window.
            delay(5_000)
            store.clearPending(cmd)
        }
    }

    companion object {
        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, BridgeService::class.java))
        }
        fun stop(context: Context) {
            context.stopService(Intent(context, BridgeService::class.java))
        }
    }
}

// ---- small JSON helpers ----------------------------------------------------

private fun JsonObject.bool(key: String): Boolean? =
    (this[key] as? JsonPrimitive)?.booleanOrNull

private fun JsonObject.str(key: String): String? =
    (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content

private fun JsonObject.int(key: String): Int? =
    (this[key] as? JsonPrimitive)?.intOrNull

private fun Map<String, Any?>.toJsonObject(): JsonObject = buildJsonObject {
    for ((k, v) in this@toJsonObject) {
        when (v) {
            null -> put(k, JsonNull)
            is Boolean -> put(k, v)
            is Int -> put(k, v)
            is Long -> put(k, v)
            is String -> put(k, v)
            else -> put(k, v.toString())
        }
    }
}
