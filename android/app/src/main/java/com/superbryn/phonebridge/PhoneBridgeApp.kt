package com.superbryn.phonebridge

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build

class PhoneBridgeApp : Application() {

    override fun onCreate() {
        super.onCreate()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(NotificationManager::class.java)
            nm.createNotificationChannel(
                NotificationChannel(
                    CHANNEL_ID,
                    getString(R.string.channel_bridge_name),
                    // LOW: this notification exists because a foreground service requires
                    // one, not because it is news. IMPORTANCE_DEFAULT would buzz on every
                    // reconnect.
                    NotificationManager.IMPORTANCE_LOW
                ).apply {
                    description = getString(R.string.channel_bridge_desc)
                    setShowBadge(false)
                }
            )
        }
    }

    companion object {
        const val CHANNEL_ID = "phonebridge.bridge"
    }
}
