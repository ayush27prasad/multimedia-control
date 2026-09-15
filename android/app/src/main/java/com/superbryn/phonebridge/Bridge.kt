package com.superbryn.phonebridge

import com.superbryn.phonebridge.state.StateStore

/**
 * Process-wide handle to the bridge.
 *
 * A single [StateStore] outlives the service so the UI keeps rendering the last known
 * state across a service restart instead of flashing empty. The service registers itself
 * here so the UI can issue commands without binding - this app has exactly one activity
 * and one service, and a Binder round-trip would buy nothing.
 */
object Bridge {
    val store = StateStore()

    @Volatile
    var commandSink: CommandSink? = null
}

/** Implemented by the service. Lets the UI request changes without depending on it. */
interface CommandSink {
    /** A command the phone itself executes (phone is the device authority). */
    fun executeLocal(cmd: String, args: Map<String, Any?> = emptyMap())

    /** A command only the PC can execute (wifi). Forwarded over the control channel. */
    fun forwardToPc(cmd: String, args: Map<String, Any?> = emptyMap())
}
