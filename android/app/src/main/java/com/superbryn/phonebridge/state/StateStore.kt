package com.superbryn.phonebridge.state

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Single source of truth for the phone app.
 *
 * The authority split (docs/PROTOCOL.md §4.2):
 *  - [device] is authored HERE. Mutating it is what makes it true; the PC is then told.
 *  - [wifi] is authored by the PC. This app only ever [applyWifi]s a snapshot it received.
 *    Never mutate it locally - the phone cannot even read most of it (DECISIONS.md D5).
 *
 * The UI never applies an optimistic local change for either slice. It renders this store
 * and nothing else, which is what makes the two apps structurally unable to disagree.
 */
class StateStore {

    private val _device = MutableStateFlow(DeviceState())
    val device: StateFlow<DeviceState> = _device.asStateFlow()

    private val _wifi = MutableStateFlow(WifiState())
    val wifi: StateFlow<WifiState> = _wifi.asStateFlow()

    private val _link = MutableStateFlow(LinkState())
    val link: StateFlow<LinkState> = _link.asStateFlow()

    private val _observedWifi = MutableStateFlow(ObservedWifi())
    val observedWifi: StateFlow<ObservedWifi> = _observedWifi.asStateFlow()

    /**
     * Commands this app has sent to the PC and not yet seen acked, by seq. Drives the
     * pending affordance in the UI so a user tap is visibly in flight rather than
     * appearing to do nothing while it round-trips.
     */
    private val _pending = MutableStateFlow<Set<String>>(emptySet())
    val pending: StateFlow<Set<String>> = _pending.asStateFlow()

    // ---- device slice (we are the authority) -------------------------------

    fun updateCamera(block: (CameraState) -> CameraState) =
        _device.update { it.copy(camera = block(it.camera)) }

    fun updateMic(block: (MicState) -> MicState) =
        _device.update { it.copy(mic = block(it.mic)) }

    fun updateTether(block: (TetherState) -> TetherState) =
        _device.update { it.copy(tether = block(it.tether)) }

    // ---- wifi slice (the PC is the authority) ------------------------------

    fun applyWifi(snapshot: WifiState) { _wifi.value = snapshot }

    /**
     * Cleared on disconnect so a stale network list is never shown as current. Retaining
     * it would be worse than showing nothing: the user could act on a list that no longer
     * reflects the phone.
     */
    fun clearWifi() { _wifi.value = WifiState() }

    // ---- observed (local telemetry, supplementary to the PC's view) --------

    fun applyObserved(observed: ObservedWifi) { _observedWifi.value = observed }

    // ---- link --------------------------------------------------------------

    fun setLink(status: LinkStatus, detail: String? = null) = _link.update {
        it.copy(
            status = status,
            detail = detail,
            connectedSinceMs = when {
                status != LinkStatus.CONNECTED -> null
                it.connectedSinceMs != null -> it.connectedSinceMs
                else -> System.currentTimeMillis()
            }
        )
    }

    fun setMediaChannels(video: Boolean? = null, audio: Boolean? = null) = _link.update {
        it.copy(
            videoChannelUp = video ?: it.videoChannelUp,
            audioChannelUp = audio ?: it.audioChannelUp
        )
    }

    // ---- pending -----------------------------------------------------------

    fun markPending(cmd: String) = _pending.update { it + cmd }
    fun clearPending(cmd: String) = _pending.update { it - cmd }
    fun clearAllPending() { _pending.value = emptySet() }
}
